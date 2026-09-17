using System.Text.RegularExpressions;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

public sealed class DeviceVersionService
{
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
        var device = await _adbClient.ResolveDeviceAsync(selectedSerial, cancellationToken);
        var useSudo = await RequiresSudoAsync(device.Serial, cancellationToken);
        var components = new List<VersionComponentResult>();

        ProgressChanged?.Invoke("正在读取盒子设备 SN...");
        var box = await CaptureAsync(
            () => QueryBoxAsync(device.Serial, cancellationToken),
            "box",
            "盒子",
            required: true);
        components.Add(box);

        ProgressChanged?.Invoke("正在按 UART 协议查询眼镜版本（直接读写 ACM 串口）...");
        components.Add(await CaptureAsync(
            () => _glassesAcmService.QueryAsync(device.Serial, useSudo, cancellationToken),
            "glasses",
            "眼镜",
            required: true));

        ProgressChanged?.Invoke("正在探测本机串口并查询左右手环版本...");
        components.Add(await CaptureAsync(
            () => _bandSerialVersionService.QueryAsync(cancellationToken),
            "band",
            "手环",
            required: true));

        ProgressChanged?.Invoke("版本读取完成。");
        return new DeviceVersionSnapshot
        {
            AdbSerial = device.Serial,
            DeviceSerial = box.Values.GetValueOrDefault("sn", string.Empty),
            CollectedAt = DateTimeOffset.Now,
            Components = components
        };
    }

    private async Task<bool> RequiresSudoAsync(string serial, CancellationToken cancellationToken)
    {
        var identity = await _adbClient.ShellAsync(serial, "id -u", cancellationToken);
        if (identity.Success && identity.StandardOutput.Trim() == "0")
        {
            return false;
        }

        var sudo = await _adbClient.ShellAsync(serial, "sudo -n true", cancellationToken);
        return sudo.Success;
    }

    private async Task<VersionComponentResult> QueryBoxAsync(
        string serial,
        CancellationToken cancellationToken)
    {
        var result = await _adbClient.ShellAsync(
            serial,
            "sudo vendor_storage -r VENDOR_SN_ID -t string",
            cancellationToken);
        EnsureSuccess(result, "读取盒子设备 SN");
        var match = Regex.Match(
            result.StandardOutput,
            @"VENDOR_SN_ID\s*:\s*(?<sn>[^\s\x1B]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException("vendor_storage 输出中没有 VENDOR_SN_ID。");
        }

        var versionResult = await _adbClient.ShellAsync(serial, "version", cancellationToken);
        EnsureSuccess(versionResult, "读取盒子版本");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sn"] = match.Groups["sn"].Value.Trim()
        };
        foreach (Match versionMatch in Regex.Matches(
                     versionResult.StandardOutput,
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

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.Success)
        {
            return;
        }

        var detail = result.TimedOut
            ? "执行超时"
            : string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        throw new InvalidOperationException($"{operation}失败: {detail}");
    }

}
