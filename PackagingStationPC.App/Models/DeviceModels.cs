namespace PackagingStationPC.Models;

public sealed record AdbDeviceInfo(string Serial, string State, string Description)
{
    public bool IsOnline => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => string.IsNullOrWhiteSpace(Description)
        ? $"{Serial} ({State})"
        : $"{Serial} ({Description})";
}

public enum QueryState
{
    NotRun,
    Success,
    Warning,
    Failed
}

public sealed class VersionComponentResult
{
    public string Component { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool Required { get; init; }
    public QueryState State { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 采集过程中对该设备执行过的动作（例如写 LAN MAC）。只用于界面呈现与门禁判定，
    /// 不是采集字段，不进 Values，也就不参与上报。
    /// </summary>
    public IReadOnlyList<DeviceActionResult> Actions { get; init; } = [];
}

/// <summary>
/// 采集时顺带执行的一个设备动作及其结果。动作本身不产出上报字段，
/// 成功与否只影响该组件是否判失败（由调用方决定）与界面上的展示。
/// </summary>
public sealed class DeviceActionResult
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>实际下发的命令，界面上原样显示，方便现场核对。</summary>
    public string Command { get; init; } = string.Empty;

    public bool Success { get; init; }

    /// <summary>结果说明：成功写"已执行"，失败写出命令带回的输出或错误。</summary>
    public string Message { get; init; } = string.Empty;
}

public sealed class DeviceVersionSnapshot
{
    public string AdbSerial { get; init; } = string.Empty;
    public string DeviceSerial { get; init; } = string.Empty;
    public DateTimeOffset CollectedAt { get; init; }
    public IReadOnlyList<VersionComponentResult> Components { get; init; } = [];

    public bool IsComplete => Components
        .Where(component => component.Required)
        .All(component => component.State == QueryState.Success);
}
