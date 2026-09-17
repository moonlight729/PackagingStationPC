using PackagingStationPC.Models;
using PackagingStationPC.Services;

namespace PackagingStationPC.Tests;

public sealed class AdbClientTests
{
    private const string BoxLine =
        "58d9c065d549552d       device product:occam model:Nexus_4 device:mako transport_id:15";

    [Fact]
    public async Task ResolveDevice_FallsBackToOnlyOnlineDeviceWhenSelectionIsStale()
    {
        var client = new AdbClient(
            new AdbConfiguration { Path = "adb" },
            new StubProcessRunner($"List of devices attached\n{BoxLine}\n"));

        var device = await client.ResolveDeviceAsync("ead039604bc4a32b");

        Assert.Equal("58d9c065d549552d", device.Serial);
    }

    [Fact]
    public async Task ResolveDevice_RejectsStaleSelectionWhenSeveralDevicesAreOnline()
    {
        var client = new AdbClient(
            new AdbConfiguration { Path = "adb" },
            new StubProcessRunner(
                $"List of devices attached\n{BoxLine}\n" +
                "ead039604bc4a32b       device product:occam model:Nexus_4 device:mako transport_id:16\n"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ResolveDeviceAsync("1111111111111111"));

        Assert.Contains("58d9c065d549552d, ead039604bc4a32b", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveDevice_WithoutSelectionPicksSingleOnlineDevice()
    {
        var client = new AdbClient(
            new AdbConfiguration { Path = "adb" },
            new StubProcessRunner($"List of devices attached\n{BoxLine}\n"));

        var device = await client.ResolveDeviceAsync(null);

        Assert.Equal("58d9c065d549552d", device.Serial);
    }

    [Fact]
    public async Task ResolveDevice_ReportsOfflineDevicesInsteadOfEmptyMessage()
    {
        var client = new AdbClient(
            new AdbConfiguration { Path = "adb" },
            new StubProcessRunner("List of devices attached\n58d9c065d549552d       offline\n"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ResolveDeviceAsync(null));

        Assert.Contains("58d9c065d549552d(offline)", exception.Message, StringComparison.Ordinal);
    }

    private sealed class StubProcessRunner(string stdout) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(0, stdout, string.Empty, false));
    }
}
