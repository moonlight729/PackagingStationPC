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
    public VersionCheckConfiguration BoxVersion { get; set; } = new();
    public VersionCheckConfiguration GlassesVersion { get; set; } = new();
    public VersionCheckConfiguration BandVersion { get; set; } = new();
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

/// <summary>
/// 单个组件的版本配置校验。这是一个独立于采集的动作：等全部版本号都读完之后，
/// 再把该组件读到的版本逐项与这里配置的要求版本比对，必须完全一致。
/// </summary>
public sealed class VersionCheckConfiguration
{
    /// <summary>本组件的总开关，默认开启（盒子、眼镜、手环三段都要求默认校验），需要临时停用时显式置 false。</summary>
    public bool Enabled { get; set; } = true;

    public List<VersionCheckItem> Checks { get; set; } = [];
}

/// <summary>单个版本字段的校验项。</summary>
public sealed class VersionCheckItem
{
    /// <summary>
    /// 采集结果里的字段名。盒子可取 <c>uboot</c>、<c>kernel</c>、<c>rootfs</c>、<c>gen1App</c>、<c>wlan0BtSwitch</c>；
    /// 眼镜可取 <c>uboot</c>、<c>kernel</c>、<c>rootfs</c>、<c>app</c>；
    /// 手环可取 <c>leftVersion</c>、<c>rightVersion</c>。
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// 要求的版本号，**必须完全一致**：写 <c>0.5.2</c> 就只认 0.5.2，比它高或比它低都判失败。
    /// 比较时忽略前导 v/V 与第一段之后的构建信息，<c>v0.1.6-260920.053745-289545544</c> 按 <c>0.1.6</c> 比，
    /// 所以同一版本重新编译出的新构建号仍然算一致。
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// 旧配置里 <c>minVersion</c> 的兼容别名，语义已与 <see cref="Version"/> 相同（必须一致），不再是最低版本。
    /// 两个都填时以 <see cref="Version"/> 为准。
    /// </summary>
    public string MinVersion { get; set; } = string.Empty;

    /// <summary>
    /// 要求的精确值（trim 后完全相等），用于版本之外的开关位、配置项，例如 <c>wlan0BtSwitch</c> 要求为 <c>0</c>。
    /// 填了它就按字面相等比较，<see cref="Version"/> 不再参与；两个都不填则该项跳过。
    /// </summary>
    public string Expected { get; set; } = string.Empty;

    /// <summary>本项是否参与校验，便于临时停用单条规则而不是整段删掉。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>失败时是否阻断。true（默认）把该组件置为 Failed 并禁止预留 SN 与上报；false 只置 Warning 提示。</summary>
    public bool BlockOnFail { get; set; } = true;

    /// <summary>
    /// 该字段读不到时是否放行，默认 false（读不到即判失败）。
    /// 想让某个字段"读得到就比、读不到就算了"时才设为 true。
    /// </summary>
    public bool AllowMissing { get; set; }
}
