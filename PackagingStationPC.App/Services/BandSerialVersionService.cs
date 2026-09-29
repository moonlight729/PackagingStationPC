using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

public interface IBandSerialVersionService
{
    Task<VersionComponentResult> QueryAsync(CancellationToken cancellationToken = default);
}

public sealed partial class BandSerialVersionService : IBandSerialVersionService
{
    internal const int BaudRate = 2_000_000;

    // 手环刚上电/刚插入时 USB CDC 往往还没就绪，第一次探测会整体落空、
    // 必须扫第二遍才成功。这里放宽等待窗口并加一轮补测，让第一遍就能读到。
    private const int HandQueryAttempts = 3;
    private const int SnQueryAttempts = 2;
    private const int VersionQueryAttempts = 2;

    // 候选串口并行探测，避免"每个口串行等待满超时"把整轮采集拖到几十秒。
    private const int MaxConcurrentProbes = 4;

    // 三层超时闸，保证任何异常（驱动卡死、手环无响应）都不会无限等待：
    // TotalQueryBudget 整轮硬上限（含补测）、RoundBudget 单轮软上限、
    // MinRetryBudget 剩余时间不足时直接放弃补测、RoundGrace 单轮收尾宽限。
    private static readonly TimeSpan TotalQueryBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RoundBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinRetryBudget = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan RoundGrace = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan OpenSettleDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan HandTimeout = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan SnTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ColdStartRetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly AppLogger? _logger;

    public BandSerialVersionService(AppLogger? logger = null) => _logger = logger;

    // 响应命中后不再死等固定尾部窗口，改为"收到最后一字节后再静默这么久"即可收尾。
    private static readonly TimeSpan ResponseQuietWindow = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MaxTrailingWindow = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DrainQuietWindow = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan MaxDrainWindow = TimeSpan.FromMilliseconds(300);
    private static readonly byte[] ConsoleResync = "\r\n"u8.ToArray();

    public async Task<VersionComponentResult> QueryAsync(CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();
        var availablePorts = SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PortNumber)
            .ThenBy(port => port, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (availablePorts.Length == 0)
        {
            await LogAsync("WARN", "Query failed: 电脑未检测到可用串口。");
            return Failure("电脑未检测到可用串口。");
        }

        var identifiedBandPorts = ReadBandPortsFromRegistry()
            .Intersect(availablePorts, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var usedUsbFilter = identifiedBandPorts.Length > 0;
        var candidates = usedUsbFilter ? identifiedBandPorts : availablePorts;
        // 注册表只认出 0 或 1 个口时，"一左一右"必然凑不齐，直接退化为全量探测，省掉一轮往返。
        if (usedUsbFilter && candidates.Length < 2 && availablePorts.Length > candidates.Length)
        {
            await LogAsync(
                "WARN",
                $"USB filter matched only [{string.Join(", ", candidates)}] of [{string.Join(", ", availablePorts)}], falling back to all ports.");
            candidates = availablePorts;
            usedUsbFilter = false;
        }

        // USB 过滤只用来排优先级，绝不用来排除：注册表经常漏掉刚插入、挂在 Hub 上或 PID 不同的那一只手环，
        // 只探测命中口就会"必然缺一只手环"。被过滤掉的口留作兜底，需要时一起补测。
        var filteredOutPorts = availablePorts
            .Where(port => !candidates.Contains(port, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        await LogAsync(
            "INFO",
            $"Query start ports=[{string.Join(", ", availablePorts)}], " +
            $"filter={(usedUsbFilter ? "usb 303A" : "all")}, " +
            $"candidates=[{string.Join(", ", candidates)}], " +
            $"filteredOut=[{string.Join(", ", filteredOutPorts)}], budget={TotalQueryBudget.TotalSeconds:0.#}s");

        var probes = await ProbePortsAsync(candidates, RoundBudget, cancellationToken);
        await LogProbesAsync("round1", probes);

        // 补测分两种成因：
        // 1) 冷启动：刚上电的手环第一次开串口还在吐启动日志，没识别出手环或没读到 SN 的口再跑一轮；
        // 2) 漏口兜底：只要还凑不齐"一左一右"，就把被 USB 过滤排除掉的口一并补上。
        var retryPorts = RetryCandidates(probes);
        if (filteredOutPorts.Length > 0 && NeedsMoreProbes(probes))
        {
            await LogAsync(
                "WARN",
                $"Filtered-out ports=[{string.Join(", ", filteredOutPorts)}] will be probed: 左右手环未凑齐。");
            retryPorts = retryPorts
                .Concat(filteredOutPorts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        if (retryPorts.Count > 0)
        {
            var remaining = TotalQueryBudget - total.Elapsed;
            if (remaining >= MinRetryBudget)
            {
                await LogAsync(
                    "WARN",
                    $"Retry ports=[{string.Join(", ", retryPorts)}], remaining={remaining.TotalSeconds:0.#}s");
                await Task.Delay(ColdStartRetryDelay, cancellationToken);
                var retried = await ProbePortsAsync(
                    retryPorts,
                    TotalQueryBudget - total.Elapsed,
                    cancellationToken);
                await LogProbesAsync("round2", retried);
                probes = Merge(probes, retried);
            }
            else
            {
                await LogAsync(
                    "WARN",
                    $"Retry skipped ports=[{string.Join(", ", retryPorts)}], remaining={remaining.TotalSeconds:0.#}s < {MinRetryBudget.TotalSeconds:0.#}s");
            }
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var sourcePorts = new List<string>();
        foreach (var hand in new[] { "left", "right" })
        {
            var matches = probes
                .Where(probe => string.Equals(probe.Hand, hand, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length > 1)
            {
                AddUnique(errors, $"检测到 {matches.Length} 个{HandDisplayName(hand)}手手环：{string.Join(", ", matches.Select(item => item.PortName))}");
            }

            foreach (var failed in matches.Where(item => !string.IsNullOrWhiteSpace(item.Error)))
            {
                AddUnique(errors, $"{failed.PortName}: {failed.Error}");
            }

            var selected = matches.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Serial));
            if (selected is null)
            {
                AddUnique(errors, $"未读取到{HandDisplayName(hand)}手手环 SN");
                values[$"{hand}Sn"] = string.Empty;
                values[$"{hand}Version"] = string.Empty;
                continue;
            }

            values[$"{hand}Sn"] = selected.Serial!;
            values[$"{hand}Version"] = selected.Version ?? string.Empty;
            sourcePorts.Add(selected.PortName);
        }

        var unrecognized = probes.Where(probe => probe.Hand is null).ToArray();
        if (usedUsbFilter || values.Count == 0)
        {
            foreach (var probe in unrecognized)
            {
                AddUnique(errors, $"{probe.PortName}: {probe.Error}");
            }
        }

        // 必须是"一左一右"两只不同的手环才允许上报：任一侧缺失、任一侧读到多只、
        // 或左右 SN 相同（同一只手环被当成左右两只）都判为不合法，并清空左右 SN 阻断上报。
        var pairingError = ValidateHandPairing(
            CountHand(probes, "left"),
            CountHand(probes, "right"),
            values.GetValueOrDefault("leftSn"),
            values.GetValueOrDefault("rightSn"));
        if (pairingError is not null)
        {
            AddUnique(errors, pairingError);
            foreach (var key in new[] { "leftSn", "leftVersion", "rightSn", "rightVersion" })
            {
                values[key] = string.Empty;
            }

            sourcePorts.Clear();
        }

        var hasBothSerials = HasValue(values, "leftSn") && HasValue(values, "rightSn");
        var source = sourcePorts.Count > 0
            ? $"本机串口 {BaudRate} 8N1: {string.Join(", ", sourcePorts)}"
            : $"本机串口 {BaudRate} 8N1";
        var state = errors.Count == 0
            ? QueryState.Success
            : hasBothSerials ? QueryState.Warning : QueryState.Failed;
        await LogAsync(
            state is QueryState.Success ? "INFO" : "ERROR",
            $"Query done elapsed={total.ElapsedMilliseconds} ms, state={state}, " +
            $"values=[{string.Join(", ", values.Select(item => $"{item.Key}={item.Value}"))}], " +
            $"source={source}, errors=[{string.Join("; ", errors)}]");
        return new VersionComponentResult
        {
            State = state,
            Component = "band",
            DisplayName = "手环",
            Required = true,
            Source = source,
            Message = string.Join("; ", errors),
            Values = values
        };
    }

    /// <summary>
    /// 校验左右手环配对：有且仅有一只左手环和一只右手环，且两者 SN 不同。
    /// 返回 null 表示配对合法，否则返回阻断上报的原因。
    /// </summary>
    internal static string? ValidateHandPairing(int leftCount, int rightCount, string? leftSn, string? rightSn)
    {
        if (leftCount == 0 || string.IsNullOrWhiteSpace(leftSn))
        {
            return "未读取到左手手环 SN";
        }

        if (rightCount == 0 || string.IsNullOrWhiteSpace(rightSn))
        {
            return "未读取到右手手环 SN";
        }

        if (leftCount > 1 || rightCount > 1)
        {
            return $"左右手环未正确配对：检测到左手环 {leftCount} 只、右手环 {rightCount} 只";
        }

        return string.Equals(leftSn!.Trim(), rightSn!.Trim(), StringComparison.OrdinalIgnoreCase)
            ? $"左手环与右手环 SN 相同（{leftSn.Trim()}），可能只接了一只手环"
            : null;
    }

    private static int CountHand(IEnumerable<BandProbe> probes, string hand) =>
        probes.Count(probe => string.Equals(probe.Hand, hand, StringComparison.OrdinalIgnoreCase));

    private static bool HasValue(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// 是否还需要继续找手环：左右没凑齐，或某一侧识别出手环却没读到 SN。
    /// 一左一右且都读到 SN 时返回 false，不做任何多余探测，成功路径的耗时不受影响。
    /// </summary>
    internal static bool NeedsMoreProbes(IEnumerable<BandProbe> probes)
    {
        var list = probes.ToArray();
        return CountHand(list, "left") == 0
            || CountHand(list, "right") == 0
            || list.Any(probe => probe.Hand is not null && string.IsNullOrWhiteSpace(probe.Serial));
    }

    /// <summary>
    /// 需要补测的串口：没识别出手环，或识别出手环但没读到 SN。
    /// 打不开（被占用/不存在）的口再试一次也不会变好，直接排除，避免浪费补测预算。
    /// </summary>
    internal static List<string> RetryCandidates(IEnumerable<BandProbe> probes) =>
        probes
            .Where(probe => probe.Retryable &&
                            (probe.Hand is null || string.IsNullOrWhiteSpace(probe.Serial)))
            .Select(probe => probe.PortName)
            .ToList();

    /// <summary>用补测结果覆盖第一轮：同一个口保留"信息更完整"的那次探测。</summary>
    internal static List<BandProbe> Merge(IEnumerable<BandProbe> first, IEnumerable<BandProbe> second)
    {
        var merged = new Dictionary<string, BandProbe>(StringComparer.OrdinalIgnoreCase);
        foreach (var probe in first)
        {
            merged[probe.PortName] = probe;
        }

        foreach (var probe in second)
        {
            if (!merged.TryGetValue(probe.PortName, out var existing) || Score(probe) > Score(existing))
            {
                merged[probe.PortName] = probe;
            }
        }

        return merged.Values
            .OrderBy(probe => PortNumber(probe.PortName))
            .ThenBy(probe => probe.PortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // 读到 SN 的探测结果最优先，其次是识别出手环的，最后才是失败。
    private static int Score(BandProbe probe) =>
        !string.IsNullOrWhiteSpace(probe.Serial) ? 2 : probe.Hand is not null ? 1 : 0;

    private async Task LogProbesAsync(string round, IEnumerable<BandProbe> probes)
    {
        foreach (var probe in probes)
        {
            await LogAsync(
                string.IsNullOrWhiteSpace(probe.Error) ? "INFO" : "WARN",
                $"Probe {round} port={probe.PortName}, hand={probe.Hand ?? "-"}, sn={probe.Serial ?? "-"}, " +
                $"version={probe.Version ?? "-"}, elapsed={probe.ElapsedMilliseconds} ms, " +
                $"error={probe.Error ?? "-"}");
        }
    }

    private Task LogAsync(string level, string message) =>
        _logger?.WriteAsync(level, $"[band] {message}") ?? Task.CompletedTask;

    /// <summary>
    /// 并行探测所有候选串口。手环在 2Mbps 上响应是毫秒级的，真正的耗时来自"没有手环的口也要等满超时"，
    /// 并行后整轮耗时约等于单个口的最坏耗时，而不再随串口数量线性增长。
    /// 整轮受 budget 约束：到点后未完成的口直接放弃，驱动卡在 Open 上也不会把采集拖死。
    /// </summary>
    private async Task<List<BandProbe>> ProbePortsAsync(
        IReadOnlyCollection<string> candidates,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        // 刻意不用 using：宽限期到点后可能还有探测任务在跑（例如驱动卡在 Open 上），
        // 提前释放 CTS / 信号量会让这些任务抛 ObjectDisposedException 或永久挂起，
        // 不释放则它们拿到取消信号后能自己收尾并关闭串口。两者都是纯托管对象，交给 GC 即可。
        var roundCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        roundCts.CancelAfter(budget);
        var roundToken = roundCts.Token;

        var gate = new SemaphoreSlim(Math.Min(MaxConcurrentProbes, candidates.Count));

        async Task<BandProbe> RunAsync(string portName)
        {
            await gate.WaitAsync(roundToken).ConfigureAwait(false);
            try
            {
                return await ProbeAsync(portName, roundToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new BandProbe(portName, null, null, $"本轮探测超时（预算 {budget.TotalSeconds:0.#}s），已放弃该串口");
            }
            catch (Exception ex)
            {
                return new BandProbe(portName, null, null, $"串口查询异常：{ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }

        var pending = candidates
            .Select(portName => (PortName: portName, Task: RunAsync(portName)))
            .ToArray();
        try
        {
            await Task.WhenAll(pending.Select(item => item.Task))
                .WaitAsync(budget + RoundGrace, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 极少数驱动会在 Open 上卡死。宽限期到点后放弃未完成的口，保证整轮一定返回。
            await LogAsync(
                "ERROR",
                $"Probe round exceeded budget={budget.TotalSeconds:0.#}s + grace={RoundGrace.TotalMilliseconds}ms, " +
                "dropping unfinished ports.");
        }

        return pending
            .Select(item => item.Task.IsCompletedSuccessfully
                ? item.Task.Result
                : new BandProbe(item.PortName, null, null, "打开串口无响应，已跳过该串口"))
            .OrderBy(probe => PortNumber(probe.PortName))
            .ThenBy(probe => probe.PortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<BandProbe> ProbeAsync(string portName, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 100,
            WriteTimeout = 1000,
            Encoding = Encoding.UTF8,
            NewLine = "\r\n"
        };

        try
        {
            await Task.Run(port.Open, cancellationToken);
            await Task.Delay(OpenSettleDelay, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 端口被占用或不存在：重试也不会变好，标记为不可重试，别浪费补测预算。
            await LogAsync("WARN", $"Command port={portName}, open failed: {ex.Message}");
            return new BandProbe(
                portName,
                null,
                null,
                $"打开串口失败：{ex.Message}",
                Retryable: false);
        }

        string handResponse = string.Empty;
        string? hand = null;
        for (var attempt = 1; attempt <= HandQueryAttempts && hand is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            port.DiscardInBuffer();
            await ResyncConsoleAsync(port, cancellationToken);
            port.DiscardInBuffer();
            var attemptWatch = Stopwatch.StartNew();
            await WriteLinePacedAsync(port, "hand", cancellationToken);
            handResponse = await ReadUntilAsync(port, "hand=", HandTimeout, cancellationToken);
            hand = DeviceVersionParser.ParseBandHand(handResponse);
            if (hand is null)
            {
                await LogAsync(
                    "WARN",
                    $"Command port={portName}, cmd=hand, attempt={attempt}/{HandQueryAttempts}, " +
                    $"elapsed={attemptWatch.ElapsedMilliseconds} ms{ResponseSuffix(handResponse)}");
                if (attempt < HandQueryAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }
        }

        if (hand is null)
        {
            return new BandProbe(
                portName,
                null,
                null,
                $"hand 命令重试 {HandQueryAttempts} 次仍无有效响应{ResponseSuffix(handResponse)}",
                ElapsedMilliseconds: watch.ElapsedMilliseconds);
        }

        string snResponse = string.Empty;
        string? sn = null;
        for (var attempt = 1; attempt <= SnQueryAttempts && sn is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            port.DiscardInBuffer();
            var attemptWatch = Stopwatch.StartNew();
            await WriteLinePacedAsync(port, "sn", cancellationToken);
            snResponse = await ReadUntilAsync(port, "sn=", SnTimeout, cancellationToken);
            sn = DeviceVersionParser.ParseBandSerial(snResponse);
            if (sn is null)
            {
                await LogAsync(
                    "WARN",
                    $"Command port={portName}, cmd=sn, attempt={attempt}/{SnQueryAttempts}, " +
                    $"elapsed={attemptWatch.ElapsedMilliseconds} ms{ResponseSuffix(snResponse)}");
                if (attempt < SnQueryAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }
        }

        string versionResponse = string.Empty;
        string? version = null;
        for (var attempt = 1; attempt <= VersionQueryAttempts && version is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            port.DiscardInBuffer();
            var attemptWatch = Stopwatch.StartNew();
            await WriteLinePacedAsync(port, "version", cancellationToken);
            versionResponse = await ReadUntilAsync(port, "version=", VersionTimeout, cancellationToken);
            version = DeviceVersionParser.ParseBandVersion(versionResponse);
            if (version is null)
            {
                await LogAsync(
                    "WARN",
                    $"Command port={portName}, cmd=version, attempt={attempt}/{VersionQueryAttempts}, " +
                    $"elapsed={attemptWatch.ElapsedMilliseconds} ms{ResponseSuffix(versionResponse)}");
                if (attempt < VersionQueryAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }
        }

        if (sn is null)
        {
            return new BandProbe(
                portName,
                hand,
                version,
                $"sn 命令重试 {SnQueryAttempts} 次仍无 SN{ResponseSuffix(snResponse)}",
                ElapsedMilliseconds: watch.ElapsedMilliseconds);
        }

        return version is not null
            ? new BandProbe(
                portName,
                hand,
                version,
                null,
                sn,
                ElapsedMilliseconds: watch.ElapsedMilliseconds)
            : new BandProbe(
                portName,
                hand,
                null,
                $"version 命令重试 {VersionQueryAttempts} 次仍无版本号{ResponseSuffix(versionResponse)}",
                sn,
                ElapsedMilliseconds: watch.ElapsedMilliseconds);
    }

    private static async Task ResyncConsoleAsync(SerialPort port, CancellationToken cancellationToken)
    {
        port.Write(ConsoleResync, 0, ConsoleResync.Length);
        await DrainInputAsync(port, MaxDrainWindow, DrainQuietWindow, cancellationToken);
    }

    private static async Task WriteLinePacedAsync(
        SerialPort port,
        string command,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        var oneByte = new byte[1];
        foreach (var value in bytes)
        {
            oneByte[0] = value;
            port.Write(oneByte, 0, 1);
            await Task.Delay(1, cancellationToken);
        }
    }

    /// <summary>
    /// 排空输入缓冲：一直接收到"连续 quietDuration 没有新字节"为止，最长不超过 maxDuration。
    /// 端口本来就没有输出时（绝大多数候选口）只需等一个静默窗口，而不是固定 200ms。
    /// </summary>
    private static async Task DrainInputAsync(
        SerialPort port,
        TimeSpan maxDuration,
        TimeSpan quietDuration,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var lastDataAt = TimeSpan.Zero;
        var buffer = new byte[512];
        while (elapsed.Elapsed < maxDuration && elapsed.Elapsed - lastDataAt < quietDuration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = port.BytesToRead;
            if (available > 0)
            {
                port.Read(buffer, 0, Math.Min(available, buffer.Length));
                lastDataAt = elapsed.Elapsed;
            }
            else
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 读取直到出现 marker：命中后再等到"最后一字节后静默 ResponseQuietWindow"即可收尾，
    /// 最多再等 MaxTrailingWindow，避免每条命令都固定多等 250~350ms。
    /// </summary>
    private static async Task<string> ReadUntilAsync(
        SerialPort port,
        string marker,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryStream();
        var elapsed = Stopwatch.StartNew();
        TimeSpan? markerSeenAt = null;
        var lastDataAt = TimeSpan.Zero;
        var buffer = new byte[1024];
        while (elapsed.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = port.BytesToRead;
            if (available > 0)
            {
                var count = port.Read(buffer, 0, Math.Min(available, buffer.Length));
                response.Write(buffer, 0, count);
                lastDataAt = elapsed.Elapsed;
                if (markerSeenAt is null &&
                    Encoding.UTF8.GetString(response.GetBuffer(), 0, checked((int)response.Length))
                        .Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    markerSeenAt = lastDataAt;
                }
            }
            else
            {
                await Task.Delay(PollInterval, cancellationToken);
            }

            if (markerSeenAt is not null &&
                (elapsed.Elapsed - lastDataAt >= ResponseQuietWindow ||
                 elapsed.Elapsed - markerSeenAt.Value >= MaxTrailingWindow))
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(response.ToArray());
    }

    private static IReadOnlyCollection<string> ReadBandPortsFromRegistry()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb is null)
            {
                return result;
            }

            // 只认 Espressif 的 VID 303A，不再限定 PID：现场同一只手环在不同固件/启动模式下
            // PID 会变，限定 PID 会漏掉其中一只。匹配放宽最多是多探几个口，不影响正确性。
            foreach (var deviceName in usb.GetSubKeyNames()
                         .Where(name => name.StartsWith("VID_303A", StringComparison.OrdinalIgnoreCase)))
            {
                using var device = usb.OpenSubKey(deviceName);
                if (device is null)
                {
                    continue;
                }

                foreach (var instanceName in device.GetSubKeyNames())
                {
                    using var parameters = device.OpenSubKey($@"{instanceName}\Device Parameters");
                    if (parameters?.GetValue("PortName") is string portName &&
                        !string.IsNullOrWhiteSpace(portName))
                    {
                        result.Add(portName);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }

        return result;
    }

    private static void AddUnique(List<string> errors, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error) && !errors.Contains(error, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(error);
        }
    }

    private static string HandDisplayName(string hand) => hand == "left" ? "左" : "右";

    private static int PortNumber(string portName)
    {
        var match = ComPortNumberRegex().Match(portName);
        return match.Success && int.TryParse(match.Groups["number"].Value, out var number)
            ? number
            : int.MaxValue;
    }

    private static string ResponseSuffix(string response)
    {
        var summary = string.Join(
            " ",
            response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (summary.Length > 160)
        {
            summary = summary[^160..];
        }
        return summary.Length == 0 ? string.Empty : $"，收到：{summary}";
    }

    private static VersionComponentResult Failure(string message) => new()
    {
        Component = "band",
        DisplayName = "手环",
        Required = true,
        State = QueryState.Failed,
        Source = $"本机串口 {BaudRate} 8N1",
        Message = message
    };

    internal sealed record BandProbe(
        string PortName,
        string? Hand,
        string? Version,
        string? Error,
        string? Serial = null,
        bool Retryable = true,
        long ElapsedMilliseconds = 0);

    [GeneratedRegex("^COM(?<number>\\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComPortNumberRegex();
}
