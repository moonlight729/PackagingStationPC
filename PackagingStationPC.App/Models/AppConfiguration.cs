namespace PackagingStationPC.Models;

public sealed class AppConfiguration
{
    public string StationCode { get; set; } = "PACKING-01";
    public string DatabaseFileName { get; set; } = "packaging-station.db";
    public AdbConfiguration Adb { get; set; } = new();
    public SnConfiguration Sn { get; set; } = new();
    public FactorySystemConfiguration FactorySystem { get; set; } = new();
    public PrinterConfiguration Printer { get; set; } = new();
    public WorkflowConfiguration Workflow { get; set; } = new();
}

public sealed class AdbConfiguration
{
    public string Path { get; set; } = "adb";
    public string DeviceSerial { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 10;
    public int PeripheralTimeoutSeconds { get; set; } = 45;
    public bool RequireCompleteVersionSet { get; set; } = true;
}

public sealed class SnConfiguration
{
    public string ProductCode { get; set; } = "G1";
    public string ProductName { get; set; } = "Origin Kit Gen1";
    public string[] FactoryCodes { get; set; } = ["F"];
    public string DefaultFactoryCode { get; set; } = "F";
    public int InitialSequence { get; set; } = 1;
}

public sealed class FactorySystemConfiguration
{
    public string Mode { get; set; } = "mock";
    public string BaseUrl { get; set; } = "http://127.0.0.1:8080/swiftmom/api";
    public string ClientLoginPath { get; set; } = "clientLogin";
    public string CheckRoutePath { get; set; } = "checkRoute";
    public string CreateRoutePath { get; set; } = "createRoute";
    public string UserNo { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ProdNo { get; set; } = string.Empty;
    public string StationNo { get; set; } = string.Empty;
    public string Retest { get; set; } = "0";
    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class PrinterConfiguration
{
    public string Mode { get; set; } = "mock";
    public string PrinterName { get; set; } = string.Empty;
    public string TemplatePath { get; set; } = string.Empty;
    public string SnDataSourceName { get; set; } = "PackageSn";
    public string BarTenderSdkAssemblyPath { get; set; } = string.Empty;
    public int Copies { get; set; } = 1;
    public int PrintTimeoutSeconds { get; set; } = 30;
    public double LabelWidthMm { get; set; } = 60;
    public double LabelHeightMm { get; set; } = 35;
}

public sealed class WorkflowConfiguration
{
    public bool RequireUploadBeforeNext { get; set; } = true;
    public bool RequirePrintBeforeNext { get; set; } = true;
}
