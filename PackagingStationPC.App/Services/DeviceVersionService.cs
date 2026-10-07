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
    private const string AppExitMarker = "__BOX_APP_RC__=";
    private const string SwitchExitMarker = "__BOX_SW_RC__=";

    // 段的顺序就是脚本中命令的顺序：SN、version、gen1-app、wlan0_bt_switch。
    private static readonly string[] BoxSectionKeys = ["sn", "version", "app", "switch"];
    private static readonly string[] BoxSectionMarkers =
        [SnExitMarker, VersionExitMarker, AppExitMarker, SwitchExitMarker];

    // 合并后一条 shell 要跑完 vendor_storage、version、dpkg-query 和 cat，超时给到单次默认值的 1.5 倍。
    private static readonly TimeSpan BoxShellTimeout = TimeSpan.FromSeconds(15);

    /// <summary>盒子上的蓝牙/无线切换开关配置文件，读到什么值由配置段决定。</summary>
    private const string SwitchConfigPath = "/oem/config/wlan0_bt_switch";

    /// <summary>该开关值在采集结果里的字段名。</summary>
    public const string SwitchField = "wlan0BtSwitch";

    private readonly AdbClient _adbClient;
    private readonly IBandSerialVersionService _bandSerialVersionService;
    private readonly GlassesAcmService _glassesAcmService;
    private readonly VersionCheckConfiguration _boxVersion;
    private readonly VersionCheckConfiguration _glassesVersion;
    private readonly VersionCheckConfiguration _bandVersion;

    public DeviceVersionService(
        AdbClient adbClient,
        IBandSerialVersionService bandSerialVersionService,
        GlassesAcmService glassesAcmService,
        AppConfiguration? configuration = null)
    {
        _adbClient = adbClient;
        _bandSerialVersionService = bandSerialVersionService;
        _glassesAcmService = glassesAcmService;
        _boxVersion = configuration?.BoxVersion ?? new VersionCheckConfiguration();
        _glassesVersion = configuration?.GlassesVersion ?? new VersionCheckConfiguration();
        _bandVersion = configuration?.BandVersion ?? new VersionCheckConfiguration();
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

        // 全部版本号读完之后才做的独立动作：按配置项比对各组件版本。采集结果本身不受影响。
        ApplyVersionChecks(components);

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

    /// <summary>
    /// 采集结束后的独立动作：把每个组件读到的版本逐项与它自己配置段里的期望（最低）版本比对。
    /// 盒子、眼镜、手环各用各的配置段与开关，互不影响；采集过程本身不动。
    /// </summary>
    private void ApplyVersionChecks(VersionComponentResult[] components)
    {
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            var configuration = ChecksFor(component.Component);
            if (configuration is null)
            {
                continue;
            }

            var result = VersionChecker.Check(component, configuration);
            if (result.Passed)
            {
                continue;
            }

            // 失败项的值清空（与手环左右配对失效时的处理一致），让既有的"必需字段非空"门禁
            // 一起拦住，避免带着不合格的版本继续绑定和上报；
            // SN 类字段不清空，保证 DeviceSerial 与绑定关系始终可用。
            var values = new Dictionary<string, string>(component.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var field in result.Fields.Where(field => values.ContainsKey(field) && !IsSerialField(field)))
            {
                values[field] = string.Empty;
            }

            var message = $"{VersionChecker.FailurePrefix}{result.Message}";
            components[index] = new VersionComponentResult
            {
                Component = component.Component,
                DisplayName = component.DisplayName,
                Required = component.Required,
                State = result.Blocking ? QueryState.Failed : QueryState.Warning,
                Source = component.Source,
                Message = message,
                Values = values
            };

            ProgressChanged?.Invoke($"{component.DisplayName}{message}");
        }
    }

    private VersionCheckConfiguration? ChecksFor(string component) => component switch
    {
        "box" => _boxVersion,
        "glasses" => _glassesVersion,
        "band" => _bandVersion,
        _ => null
    };

    private static bool IsSerialField(string field) =>
        field.Equals("sn", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("leftSn", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("rightSn", StringComparison.OrdinalIgnoreCase);

    private async Task<VersionComponentResult> QueryBoxAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        // 单次 shell 取回 SN、version、gen1-app 版本和 wlan0_bt_switch 开关：
        // sudo 权限在脚本内自判（root 直接跑，非 root 走 sudo -n），
        // 四条命令的分段与各自的退出码用标记行带回，省掉多次 adb 往返和一次权限探测。
        // 注意：标记常量自身已以 '=' 结尾，脚本里写成 echo 标记$?，不能再补一个 '='。
        // dpkg-query 只查询包数据库，不需要 root。
        var script =
            "if [ \"$(id -u)\" = \"0\" ]; then SUDO=; else SUDO=\"sudo -n\"; fi; " +
            $"$SUDO vendor_storage -r VENDOR_SN_ID -t string 2>&1; echo {SnExitMarker}$?; " +
            $"version 2>&1; echo {VersionExitMarker}$?; " +
            $"dpkg-query -W gen1-app 2>&1; echo {AppExitMarker}$?; " +
            $"cat {SwitchConfigPath} 2>&1; echo {SwitchExitMarker}$?";
        var result = await _adbClient.ShellAsync(serial, script, BoxShellTimeout, cancellationToken);
        var sections = SplitBoxOutput(result.StandardOutput);
        var snOutput = sections["sn"].Output;
        var snExitCode = sections["sn"].ExitCode;
        var versionOutput = sections["version"].Output;
        var versionExitCode = sections["version"].ExitCode;
        var appOutput = sections["app"].Output;
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

        if (sections["app"].ExitCode != 0)
        {
            throw new InvalidOperationException($"读取盒子 gen1-app 版本失败: {Describe(appOutput, result)}");
        }

        var appMatch = Regex.Match(
            appOutput,
            @"gen1-app\s+(?<value>[^\s\x1B]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!appMatch.Success)
        {
            throw new InvalidOperationException("dpkg-query 输出中没有 gen1-app 版本。");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sn"] = match.Groups["sn"].Value.Trim(),
            ["gen1App"] = appMatch.Groups["value"].Value.Trim()
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

        // 开关文件在部分盒子上可能不存在或不可读：这里不抛异常，留空交给校验配置去判，
        // 免得一个配置项把整轮采集（连 SN 一起）拖成失败。
        values[SwitchField] = ReadFirstLine(sections["switch"]);

        return new VersionComponentResult
        {
            Component = "box",
            DisplayName = "盒子",
            Required = true,
            State = QueryState.Success,
            Source = "adb shell: vendor_storage + version + dpkg-query + wlan0_bt_switch",
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
    /// 按标记行把单次 shell 的输出切成 SN、version、gen1-app、wlan0_bt_switch 四段，并取出各自的退出码。
    /// 标记必须按脚本顺序出现，最后一个标记之后的文本归到最后一段。
    /// </summary>
    private static Dictionary<string, BoxSection> SplitBoxOutput(string standardOutput)
    {
        var outputs = BoxSectionKeys.Select(_ => new StringBuilder()).ToArray();
        var exitCodes = Enumerable.Repeat(-1, BoxSectionKeys.Length).ToArray();
        var index = 0;
        foreach (var line in standardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (index < BoxSectionMarkers.Length && TryReadExitCode(line, BoxSectionMarkers[index], out var code))
            {
                exitCodes[index] = code;
                index++;
                continue;
            }

            outputs[Math.Min(index, outputs.Length - 1)].AppendLine(line);
        }

        var sections = new Dictionary<string, BoxSection>(StringComparer.Ordinal);
        for (var i = 0; i < BoxSectionKeys.Length; i++)
        {
            sections[BoxSectionKeys[i]] = new BoxSection(outputs[i].ToString(), exitCodes[i]);
        }

        return sections;
    }

    /// <summary>单次 shell 中某一条命令的输出与退出码。</summary>
    private readonly record struct BoxSection(string Output, int ExitCode);

    /// <summary>取开关文件输出的首行；命令失败或没有内容时返回空串，由校验配置决定怎么判。</summary>
    private static string ReadFirstLine(BoxSection section) =>
        section.ExitCode == 0
            ? section.Output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty
            : string.Empty;

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
