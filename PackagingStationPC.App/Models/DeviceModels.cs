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
