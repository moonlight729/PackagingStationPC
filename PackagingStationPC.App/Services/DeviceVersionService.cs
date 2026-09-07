using System.Text.Json;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

public sealed class DeviceVersionService
{
    private readonly AdbClient _adbClient;
    private readonly IBandSerialVersionService _bandSerialVersionService;

    public DeviceVersionService(
        AdbClient adbClient,
        IBandSerialVersionService bandSerialVersionService)
    {
        _adbClient = adbClient;
        _bandSerialVersionService = bandSerialVersionService;
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

        ProgressChanged?.Invoke("正在按 UART 协议查询眼镜版本（最多约 12 秒）...");
        components.Add(await CaptureAsync(
            () => QueryGlassesAsync(device.Serial, useSudo, cancellationToken),
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

    private async Task<VersionComponentResult> QueryGlassesAsync(
        string serial,
        bool useSudo,
        CancellationToken cancellationToken)
    {
        var result = await _adbClient.RunEmbeddedPythonAsync(
            serial,
            "glasses_version_query.py",
            useSudo,
            cancellationToken);
        EnsureSuccess(result, "执行眼镜 UART 查询");

        using var document = JsonDocument.Parse(LastNonEmptyLine(result.StandardOutput));
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            throw new InvalidOperationException(GetString(root, "error", "眼镜版本查询失败。"));
        }

        var values = ReadStringMap(root.GetProperty("versions"));
        var source = GetString(root, "source", "direct_uart");
        var warning = root.TryGetProperty("warning", out var warningValue) && warningValue.GetBoolean();
        return new VersionComponentResult
        {
            Component = "glasses",
            DisplayName = "眼镜",
            Required = true,
            State = warning ? QueryState.Warning : QueryState.Success,
            Source = source,
            Message = warning ? "直连未取到响应，当前值来自 camera10 历史日志。" : "",
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

    private static string LastNonEmptyLine(string value) => value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault() ?? throw new FormatException("设备查询没有返回数据。");

    private static Dictionary<string, string> ReadStringMap(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        return result;
    }

    private static string GetString(JsonElement element, string property, string fallback)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.GetRawText();
    }

}
