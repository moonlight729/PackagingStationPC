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
    private const int HandQueryAttempts = 2;
    private const int SnQueryAttempts = 2;
    private const int VersionQueryAttempts = 2;

    // 候选串口并行探测，避免"每个口串行等待满超时"把整轮采集拖到几十秒。
    private const int MaxConcurrentProbes = 4;

    private static readonly TimeSpan OpenSettleDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan HandTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan SnTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    // 响应命中后不再死等固定尾部窗口，改为"收到最后一字节后再静默这么久"即可收尾。
    private static readonly TimeSpan ResponseQuietWindow = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MaxTrailingWindow = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DrainQuietWindow = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan MaxDrainWindow = TimeSpan.FromMilliseconds(300);
    private static readonly byte[] ConsoleResync = "\r\n"u8.ToArray();

    public async Task<VersionComponentResult> QueryAsync(CancellationToken cancellationToken = default)
    {
        var availablePorts = SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PortNumber)
            .ThenBy(port => port, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (availablePorts.Length == 0)
        {
            return Failure("电脑未检测到可用串口。");
        }

        var identifiedBandPorts = ReadBandPortsFromRegistry()
            .Intersect(availablePorts, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var usedUsbFilter = identifiedBandPorts.Length > 0;
        var candidates = usedUsbFilter ? identifiedBandPorts : availablePorts;
        var probes = await ProbePortsAsync(candidates, cancellationToken);

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
        return new VersionComponentResult
        {
            Component = "band",
            DisplayName = "手环",
            Required = true,
            State = errors.Count == 0
                ? QueryState.Success
                : hasBothSerials ? QueryState.Warning : QueryState.Failed,
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
    /// 并行探测所有候选串口。手环在 2Mbps 上响应是毫秒级的，真正的耗时来自"没有手环的口也要等满超时"，
    /// 并行后整轮耗时约等于单个口的最坏耗时，而不再随串口数量线性增长。
    /// </summary>
    private static async Task<List<BandProbe>> ProbePortsAsync(
        IReadOnlyCollection<string> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        using var gate = new SemaphoreSlim(Math.Min(MaxConcurrentProbes, candidates.Count));
        var tasks = candidates.Select(async portName =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ProbeAsync(portName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new BandProbe(portName, null, null, $"串口查询异常：{ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        return (await Task.WhenAll(tasks).ConfigureAwait(false))
            .OrderBy(probe => PortNumber(probe.PortName))
            .ThenBy(probe => probe.PortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<BandProbe> ProbeAsync(string portName, CancellationToken cancellationToken)
    {
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
            return new BandProbe(portName, null, null, $"打开串口失败：{ex.Message}");
        }

        string handResponse = string.Empty;
        string? hand = null;
        for (var attempt = 1; attempt <= HandQueryAttempts && hand is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            port.DiscardInBuffer();
            await ResyncConsoleAsync(port, cancellationToken);
            port.DiscardInBuffer();
            await WriteLinePacedAsync(port, "hand", cancellationToken);
            handResponse = await ReadUntilAsync(port, "hand=", HandTimeout, cancellationToken);
            hand = DeviceVersionParser.ParseBandHand(handResponse);
            if (hand is null && attempt < HandQueryAttempts)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }

        if (hand is null)
        {
            return new BandProbe(
                portName,
                null,
                null,
                $"hand 命令重试 {HandQueryAttempts} 次仍无有效响应{ResponseSuffix(handResponse)}");
        }

        string snResponse = string.Empty;
        string? sn = null;
        for (var attempt = 1; attempt <= SnQueryAttempts && sn is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            port.DiscardInBuffer();
            await WriteLinePacedAsync(port, "sn", cancellationToken);
            snResponse = await ReadUntilAsync(port, "sn=", SnTimeout, cancellationToken);
            sn = DeviceVersionParser.ParseBandSerial(snResponse);
            if (sn is null && attempt < SnQueryAttempts)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }

        string versionResponse = string.Empty;
        string? version = null;
        for (var attempt = 1; attempt <= VersionQueryAttempts && version is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            port.DiscardInBuffer();
            await WriteLinePacedAsync(port, "version", cancellationToken);
            versionResponse = await ReadUntilAsync(port, "version=", VersionTimeout, cancellationToken);
            version = DeviceVersionParser.ParseBandVersion(versionResponse);
            if (version is null && attempt < VersionQueryAttempts)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }

        if (sn is null)
        {
            return new BandProbe(
                portName,
                hand,
                version,
                $"sn 命令重试 {SnQueryAttempts} 次仍无 SN{ResponseSuffix(snResponse)}");
        }

        return version is not null
            ? new BandProbe(portName, hand, version, null, sn)
            : new BandProbe(
                portName,
                hand,
                null,
                $"version 命令重试 {VersionQueryAttempts} 次仍无版本号{ResponseSuffix(versionResponse)}",
                sn);
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

            foreach (var deviceName in usb.GetSubKeyNames()
                         .Where(name => name.StartsWith("VID_303A&PID_1001", StringComparison.OrdinalIgnoreCase)))
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

    private sealed record BandProbe(
        string PortName,
        string? Hand,
        string? Version,
        string? Error,
        string? Serial = null);

    [GeneratedRegex("^COM(?<number>\\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComPortNumberRegex();
}
