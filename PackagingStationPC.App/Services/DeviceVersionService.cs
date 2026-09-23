using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

public sealed class DeviceVersionService
{
    private const string SnExitMarker = "__BOX_SN_RC__=";
    private const string VersionExitMarker = "__BOX_VER_RC__=";

    private readonly AdbClient _adbClient;
    private readonly IBandSerialVersionService _bandSerialVersionService;
    private readonly GlassesAcmService _glassesAcmService;

    public DeviceVersionService(
        AdbClient adbClient,
        IBandSerialVersionService bandSerialVersionService,
        GlassesAcmService glassesAcmService)
    {
        _adbClient = adbClient;
        _bandSerialVersionService = bandSerialVersionService;
        _glassesAcmService = glassesAcmService;
    }

    public event Action<string>? ProgressChanged;

    public Task<IReadOnlyList<AdbDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        _adbClient.GetDevicesAsync(cancellationToken);

    public async Task<DeviceVersionSnapshot> CollectAsync(
        string? selectedSerial,
        CancellationToken cancellationToken = default)
    {
        ProgressChanged?.Invoke("正在确认 ADB 设备...");
        var total = Stopwatch.StartNew();
        var device = await _adbClient.ResolveDeviceAsync(selectedSerial, cancellationToken);

        // 盒子走 ADB、眼镜走盒子上的 ACM、手环走本机串口，三条链路互不依赖，
        // 串行执行会把各自的等待时间累加；并行后整轮耗时约等于最慢的一条。
        ProgressChanged?.Invoke("正在并行读取盒子、眼镜与左右手环版本...");
        var timings = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        async Task<VersionComponentResult> TimedAsync(
            string displayName,
            string component,
            Func<Task<VersionComponentResult>> operation)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                return await CaptureAsync(operation, component, displayName, required: true);
            }
            finally
            {
                timings[displayName] = watch.ElapsedMilliseconds;
            }
        }

        var boxTask = TimedAsync("盒子", "box", () => QueryBoxAsync(device.Serial, cancellationToken));
        var glassesTask = TimedAsync("眼镜", "glasses", () => _glassesAcmService.QueryAsync(device.Serial, cancellationToken));
        var bandTask = TimedAsync("手环", "band", () => _bandSerialVersionService.QueryAsync(cancellationToken));

        var components = await Task.WhenAll(boxTask, glassesTask, bandTask);
        var breakdown = string.Join(" / ", new[] { "盒子", "眼镜", "手环" }
            .Select(name => $"{name} {timings.GetValueOrDefault(name)} ms"));
        ProgressChanged?.Invoke($"版本读取完成，耗时 {total.ElapsedMilliseconds} ms（{breakdown}）。");
        return new DeviceVersionSnapshot
        {
            AdbSerial = device.Serial,
            DeviceSerial = components[0].Values.GetValueOrDefault("sn", string.Empty),
            CollectedAt = DateTimeOffset.Now,
            Components = components
        };
    }

    private async Task<VersionComponentResult> QueryBoxAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        // 单次 shell 取回 SN 与 version：sudo 权限在脚本内自判（root 直接跑，非 root 走 sudo -n），
        // 两条命令的分段与各自的退出码用标记行带回，省掉一次 adb 往返和一次权限探测。
        var script =
            "if [ \"$(id -u)\" = \"0\" ]; then SUDO=; else SUDO=\"sudo -n\"; fi; " +
            $"$SUDO vendor_storage -r VENDOR_SN_ID -t string 2>&1; echo {SnExitMarker}=$?; " +
            $"version 2>&1; echo {VersionExitMarker}=$?";
        var result = await _adbClient.ShellAsync(serial, script, cancellationToken);
        var (snOutput, snExitCode, versionOutput, versionExitCode) = SplitBoxOutput(result.StandardOutput);
        if (snExitCode != 0)
        {
            throw new InvalidOperationException($"读取盒子设备 SN失败: {Describe(snOutput, result)}");
        }

        var match = Regex.Match(
            snOutput,
            @"VENDOR_SN_ID\s*:\s*(?<sn>[^\s\x1B]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException("vendor_storage 输出中没有 VENDOR_SN_ID。");
        }

        if (versionExitCode != 0)
        {
            throw new InvalidOperationException($"读取盒子版本失败: {Describe(versionOutput, result)}");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sn"] = match.Groups["sn"].Value.Trim()
        };
        foreach (Match versionMatch in Regex.Matches(
                     versionOutput,
                     @"(?<name>uboot|kernel|rootfs)\s+version\s*:\s*(?<value>[^\r\n\x1B]+)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            values[versionMatch.Groups["name"].Value.ToLowerInvariant()] =
                versionMatch.Groups["value"].Value.Trim();
        }

        if (!values.ContainsKey("uboot") || !values.ContainsKey("kernel") || !values.ContainsKey("rootfs"))
        {
            throw new InvalidOperationException("盒子 version 输出缺少 uboot、kernel 或 rootfs 版本。");
        }

        return new VersionComponentResult
        {
            Component = "box",
            DisplayName = "盒子",
            Required = true,
            State = QueryState.Success,
            Source = "adb shell: vendor_storage + version",
            Values = values
        };
    }

    private static async Task<VersionComponentResult> CaptureAsync(
        Func<Task<VersionComponentResult>> operation,
        string component,
        string displayName,
        bool required)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex)
        {
            return new VersionComponentResult
            {
                Component = component,
                DisplayName = displayName,
                Required = required,
                State = QueryState.Failed,
                Message = ex.Message
            };
        }
    }

    /// <summary>
    /// 按标记行把单次 shell 的输出切成 SN 段与 version 段，并取出各自的退出码。
    /// </summary>
    private static (string SnOutput, int SnExitCode, string VersionOutput, int VersionExitCode) SplitBoxOutput(
        string standardOutput)
    {
        var sn = new StringBuilder();
        var version = new StringBuilder();
        StringBuilder? target = sn;
        var snExitCode = -1;
        var versionExitCode = -1;
        foreach (var line in standardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryReadExitCode(line, SnExitMarker, out var snCode))
            {
                snExitCode = snCode;
                target = version;
                continue;
            }

            if (TryReadExitCode(line, VersionExitMarker, out var versionCode))
            {
                versionExitCode = versionCode;
                target = null;
                continue;
            }

            target?.AppendLine(line);
        }

        return (sn.ToString(), snExitCode, version.ToString(), versionExitCode);
    }

    private static bool TryReadExitCode(string line, string marker, out int exitCode)
    {
        exitCode = -1;
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 &&
               int.TryParse(
                   line[(index + marker.Length)..].Trim(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out exitCode);
    }

    private static string Describe(string section, ProcessResult result)
    {
        var detail = string.Join(
            ' ',
            section.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (detail.Length > 0)
        {
            return detail;
        }

        return !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError.Trim()
            : result.TimedOut ? "执行超时" : "无输出";
    }

}
