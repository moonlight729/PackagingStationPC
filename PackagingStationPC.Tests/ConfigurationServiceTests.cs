using PackagingStationPC.Services;

namespace PackagingStationPC.Tests;

public sealed class ConfigurationServiceTests
{
    [Fact]
    public void Load_ReadsVersionChecksForEveryComponent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"appsettings-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            """
            {
              // 版本配置校验：全部版本号读完之后才执行，不参与采集过程。
              "boxVersion": {
                "enabled": true,
                "checks": [
                  { "field": "gen1App", "minVersion": "0.5.0", "enabled": true, "blockOnFail": true }
                ]
              },
              "glassesVersion": {
                "enabled": false,
                "checks": [
                  { "field": "app", "minVersion": "0.1.0" }
                ]
              },
              "bandVersion": {
                "enabled": true,
                "checks": [
                  { "field": "leftVersion", "minVersion": "1.0.0", "allowMissing": true },
                  { "field": "rightVersion", "minVersion": "1.0.0", "enabled": false, "blockOnFail": false }
                ]
              }
            }
            """);
        try
        {
            var configuration = ConfigurationService.Load(path);

            Assert.True(configuration.BoxVersion.Enabled);
            Assert.Equal("gen1App", configuration.BoxVersion.Checks[0].Field);
            Assert.Equal("0.5.0", configuration.BoxVersion.Checks[0].MinVersion);
            Assert.True(configuration.BoxVersion.Checks[0].BlockOnFail);
            Assert.False(configuration.BoxVersion.Checks[0].AllowMissing);

            Assert.False(configuration.GlassesVersion.Enabled);
            Assert.Equal("app", configuration.GlassesVersion.Checks[0].Field);

            Assert.True(configuration.BandVersion.Enabled);
            Assert.True(configuration.BandVersion.Checks[0].AllowMissing);
            Assert.False(configuration.BandVersion.Checks[1].Enabled);
            Assert.False(configuration.BandVersion.Checks[1].BlockOnFail);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LeavesEveryComponentDisabledWhenTheSectionsAreAbsent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"appsettings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "stationCode": "PACKING-01" }""");
        try
        {
            var configuration = ConfigurationService.Load(path);

            Assert.False(configuration.BoxVersion.Enabled);
            Assert.False(configuration.GlassesVersion.Enabled);
            Assert.False(configuration.BandVersion.Enabled);
            Assert.Empty(configuration.BoxVersion.Checks);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
