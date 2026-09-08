using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

public interface IProductionRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<int> GetNextSequenceAsync(SnParts key, int initialSequence, CancellationToken cancellationToken = default);
    Task SetNextSequenceAsync(SnParts key, int nextSequence, string reason, CancellationToken cancellationToken = default);
    Task<ReservationResult> ReserveAsync(
        DeviceVersionSnapshot snapshot,
        string productName,
        string stationCode,
        SnParts requestedParts,
        CancellationToken cancellationToken = default);
    Task<ReservationResult> BindScannedPackageAsync(
        string packageCode,
        DeviceVersionSnapshot snapshot,
        string productCode,
        string productName,
        string stationCode,
        CancellationToken cancellationToken = default);
    Task<ProductionRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
    Task UpdateUploadAsync(long id, IntegrationResult result, CancellationToken cancellationToken = default);
    Task UpdatePrintAsync(long id, IntegrationResult result, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProductionRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);
}
