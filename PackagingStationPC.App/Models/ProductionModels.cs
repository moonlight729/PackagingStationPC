namespace PackagingStationPC.Models;

public enum IntegrationStatus
{
    Pending,
    Succeeded,
    Failed
}

public sealed class ProductionRecord
{
    public long Id { get; init; }
    public string PackageSn { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public string StationCode { get; init; } = string.Empty;
    public string FactoryCode { get; init; } = string.Empty;
    public int PackageYear { get; init; }
    public int PackageWeek { get; init; }
    public int Sequence { get; init; }
    public string DeviceSerial { get; init; } = string.Empty;
    public DeviceVersionSnapshot VersionSnapshot { get; init; } = new();
    public IntegrationStatus UploadStatus { get; init; } = IntegrationStatus.Pending;
    public string UploadMessage { get; init; } = string.Empty;
    public IntegrationStatus PrintStatus { get; init; } = IntegrationStatus.Pending;
    public string PrintMessage { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UploadedAt { get; init; }
    public DateTimeOffset? PrintedAt { get; init; }
}

public sealed record SnParts(string ProductCode, string FactoryCode, int Year, int Week, int Sequence);

public sealed record ReservationResult(ProductionRecord Record, bool WasExisting);

public sealed record IntegrationResult(bool Success, string Message, string ResponseBody = "");
