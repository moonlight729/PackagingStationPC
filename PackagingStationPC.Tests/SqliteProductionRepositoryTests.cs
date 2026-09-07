using PackagingStationPC.Models;
using PackagingStationPC.Services;

namespace PackagingStationPC.Tests;

public sealed class SqliteProductionRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PackagingStationPCTests", Guid.NewGuid().ToString("N"));
    private SqliteProductionRepository _repository = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _repository = new SqliteProductionRepository(Path.Combine(_directory, "test.db"), new SnGenerator());
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Reserve_IncrementsSequenceAndReturnsExistingDeviceRecord()
    {
        var firstSnapshot = Snapshot("device-1");
        var first = await _repository.ReserveAsync(
            firstSnapshot,
            "Origin Kit Gen1",
            "PACKING-01",
            new SnParts("G1", "F", 26, 32, 1));

        var next = await _repository.GetNextSequenceAsync(new SnParts("G1", "F", 26, 32, 1), 1);
        var repeated = await _repository.ReserveAsync(
            firstSnapshot,
            "Origin Kit Gen1",
            "PACKING-01",
            new SnParts("G1", "F", 26, 32, next));

        Assert.Equal("G1F2632000001", first.Record.PackageSn);
        Assert.Equal(2, next);
        Assert.True(repeated.WasExisting);
        Assert.Equal(first.Record.Id, repeated.Record.Id);
    }

    [Fact]
    public async Task UploadAndPrintStatuses_AreIndependent()
    {
        var reserved = await _repository.ReserveAsync(
            Snapshot("device-2"),
            "Origin Kit Gen1",
            "PACKING-01",
            new SnParts("G1", "F", 26, 32, 1));

        await _repository.UpdateUploadAsync(reserved.Record.Id, new IntegrationResult(true, "uploaded"));
        var afterUpload = await _repository.GetByIdAsync(reserved.Record.Id);

        Assert.Equal(IntegrationStatus.Succeeded, afterUpload!.UploadStatus);
        Assert.Equal(IntegrationStatus.Pending, afterUpload.PrintStatus);

        await _repository.UpdatePrintAsync(reserved.Record.Id, new IntegrationResult(false, "offline"));
        var afterPrint = await _repository.GetByIdAsync(reserved.Record.Id);

        Assert.Equal(IntegrationStatus.Succeeded, afterPrint!.UploadStatus);
        Assert.Equal(IntegrationStatus.Failed, afterPrint.PrintStatus);
    }

    [Fact]
    public async Task SetNextSequence_CannotRewindPastUsedSequence()
    {
        await _repository.ReserveAsync(
            Snapshot("device-3"),
            "Origin Kit Gen1",
            "PACKING-01",
            new SnParts("G1", "F", 26, 32, 5));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.SetNextSequenceAsync(
                new SnParts("G1", "F", 26, 32, 1),
                4,
                "test rewind"));

        Assert.Contains("不能回退复用", error.Message);
    }

    private static DeviceVersionSnapshot Snapshot(string serial) => new()
    {
        AdbSerial = serial,
        CollectedAt = DateTimeOffset.Now,
        Components =
        [
            new VersionComponentResult
            {
                Component = "band",
                DisplayName = "手环",
                Required = true,
                State = QueryState.Success,
                Values = new Dictionary<string, string> { ["left"] = "3.0.3.3", ["right"] = "3.0.3.2" }
            }
        ]
    };
}
