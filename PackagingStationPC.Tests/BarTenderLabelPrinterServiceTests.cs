using PackagingStationPC.Models;
using PackagingStationPC.Services;

namespace PackagingStationPC.Tests;

public sealed class BarTenderLabelPrinterServiceTests : IDisposable
{
    private readonly string _templatePath = Path.Combine(
        Path.GetTempPath(),
        $"packaging-station-{Guid.NewGuid():N}.btw");

    public BarTenderLabelPrinterServiceTests()
    {
        File.WriteAllText(_templatePath, "test template placeholder");
    }

    [Fact]
    public async Task Print_PassesCurrentSnAndConfigurationToAdapter()
    {
        var configuration = Configuration();
        var adapter = new RecordingAdapter();
        var service = new BarTenderLabelPrinterService(configuration, adapter);

        var result = await service.PrintAsync(new ProductionRecord { PackageSn = "G1F2632000001" });

        Assert.True(result.Success, result.Message);
        Assert.Equal("G1F2632000001", adapter.PackageSn);
        Assert.Same(configuration, adapter.Configuration);
    }

    [Fact]
    public async Task Test_RejectsMissingTemplateBeforeStartingBarTender()
    {
        var configuration = Configuration();
        configuration.TemplatePath += ".missing";
        var adapter = new RecordingAdapter();
        var service = new BarTenderLabelPrinterService(configuration, adapter);

        var result = await service.TestAsync();

        Assert.False(result.Success);
        Assert.Contains("模板不存在", result.Message);
        Assert.Null(adapter.Configuration);
    }

    [Fact]
    public void Factory_SelectsBarTenderMode()
    {
        var service = LabelPrinterServiceFactory.Create(Configuration());

        Assert.IsType<BarTenderLabelPrinterService>(service);
        Assert.Equal("bartender", service.Mode);
    }

    public void Dispose()
    {
        File.Delete(_templatePath);
    }

    private PrinterConfiguration Configuration() => new()
    {
        Mode = "bartender",
        PrinterName = "Test Printer",
        TemplatePath = _templatePath,
        SnDataSourceName = "PackageSn",
        Copies = 1,
        PrintTimeoutSeconds = 30
    };

    private sealed class RecordingAdapter : IBarTenderSdkAdapter
    {
        public PrinterConfiguration? Configuration { get; private set; }
        public string? PackageSn { get; private set; }

        public IntegrationResult Test(PrinterConfiguration configuration)
        {
            Configuration = configuration;
            return new IntegrationResult(true, "test ok");
        }

        public IntegrationResult Print(PrinterConfiguration configuration, string packageSn)
        {
            Configuration = configuration;
            PackageSn = packageSn;
            return new IntegrationResult(true, "print ok");
        }
    }
}
