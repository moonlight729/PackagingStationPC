using PackagingStationPC.Models;
using PackagingStationPC.Services;

namespace PackagingStationPC.Tests;

public sealed class DeviceVersionServiceTests
{
    private const string BoxOutput =
        "resize: Terminal is not VT100-compatible\n" +
        "[Debug] vendor_storage_read id = 1\n" +
        "[INFO] VENDOR_SN_ID:D1S0001V30W263200002\n" +
        "__BOX_SN_RC__=0\n" +
        "uboot version:  v0.0.8-260909\n" +
        "kernel version: v0.1.3-260909\n" +
        "rootfs version: 0.1.5\n" +
        "__BOX_VER_RC__=0\n" +
        "gen1-app\t0.5.0-20260920-36.3dc2a236\n" +
        "__BOX_APP_RC__=0";

    [Fact]
    public async Task Collect_ParsesBoxSnAndVersionsFromSingleShell()
    {
        var snapshot = await CollectAsync(BoxOutput);

        var box = snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Success, box.State);
        Assert.Equal("D1S0001V30W263200002", box.Values["sn"]);
        Assert.Equal("v0.0.8-260909", box.Values["uboot"]);
        Assert.Equal("v0.1.3-260909", box.Values["kernel"]);
        Assert.Equal("0.1.5", box.Values["rootfs"]);
        Assert.Equal("0.5.0-20260920-36.3dc2a236", box.Values["gen1App"]);
        Assert.Equal("D1S0001V30W263200002", snapshot.DeviceSerial);
    }

    [Fact]
    public async Task Collect_ReadsWlan0BtSwitch()
    {
        var snapshot = await CollectAsync(BoxOutputWithSwitch("1"));

        var box = snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Success, box.State);
        Assert.Equal("1", box.Values[DeviceVersionService.SwitchField]);
    }

    [Fact]
    public async Task Collect_KeepsSwitchEmptyWhenTheFileIsUnreadable()
    {
        // cat 失败不该把整轮采集（连 SN 一起）拖成失败，开关留空交给校验配置去判。
        var snapshot = await CollectAsync(BoxOutputWithSwitch("cat: No such file or directory", exitCode: 1));

        var box = snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Success, box.State);
        Assert.Equal("D1S0001V30W263200002", box.Values["sn"]);
        Assert.Equal(string.Empty, box.Values[DeviceVersionService.SwitchField]);
    }

    [Fact]
    public async Task Collect_FailsBoxWhenWlan0BtSwitchIsNotTheExpectedValue()
    {
        var configuration = new AppConfiguration
        {
            BoxVersion = new VersionCheckConfiguration
            {
                Enabled = true,
                Checks =
                [
                    new VersionCheckItem
                    {
                        Field = DeviceVersionService.SwitchField,
                        Expected = "0",
                        AllowMissing = false
                    }
                ]
            }
        };

        var snapshot = await CollectAsync(BoxOutputWithSwitch("1"), configuration);

        var box = snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Failed, box.State);
        Assert.Contains("wlan0BtSwitch 当前为 1，要求为 0", box.Message, StringComparison.Ordinal);
        // 开关位不合格时该字段清空，SN 不受影响。
        Assert.Equal(string.Empty, box.Values[DeviceVersionService.SwitchField]);
        Assert.Equal("D1S0001V30W263200002", snapshot.DeviceSerial);
    }

    [Fact]
    public async Task Collect_PassesBoxWhenWlan0BtSwitchMatches()
    {
        var configuration = new AppConfiguration
        {
            BoxVersion = new VersionCheckConfiguration
            {
                Enabled = true,
                Checks =
                [
                    new VersionCheckItem { Field = DeviceVersionService.SwitchField, Expected = "0" }
                ]
            }
        };

        var snapshot = await CollectAsync(BoxOutputWithSwitch("0"), configuration);

        Assert.Equal(QueryState.Success, snapshot.Components.First(item => item.Component == "box").State);
    }

    [Fact]
    public async Task Collect_ReportsBoxFailureWhenGen1AppIsMissing()
    {
        var snapshot = await CollectAsync(BoxOutput.Replace("__BOX_APP_RC__=0", "__BOX_APP_RC__=1"));

        var box = snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Failed, box.State);
        Assert.Contains("读取盒子 gen1-app 版本失败", box.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collect_ReportsBoxFailureWhenVersionTabIsLost()
    {
        var snapshot = await CollectAsync(BoxOutput.Replace("gen1-app\t", "gen1-app"));

        var box = snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Failed, box.State);
        Assert.Contains("dpkg-query 输出中没有 gen1-app 版本", box.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collect_ReportsBoxFailureWhenShellExitCodeIsNotZero()
    {
        var snapshot = await CollectAsync(BoxOutput.Replace("__BOX_SN_RC__=0", "__BOX_SN_RC__=1"));

        var box = snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Failed, box.State);
        Assert.Contains("读取盒子设备 SN失败", box.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collect_FailsBoxWhenVersionIsBelowConfiguredMinimum()
    {
        var configuration = new AppConfiguration
        {
            BoxVersion = new VersionCheckConfiguration
            {
                Enabled = true,
                Checks = [new VersionCheckItem { Field = "gen1App", MinVersion = "0.6.0" }]
            }
        };

        var snapshot = await CollectAsync(BoxOutput, configuration);

        var box = snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Failed, box.State);
        Assert.Contains("低于要求的最低版本 0.6.0", box.Message, StringComparison.Ordinal);
        // 失败项的值被清空，让既有的"必需字段非空"门禁一并拦住。
        Assert.Equal(string.Empty, box.Values["gen1App"]);
        // 校验只针对版本字段，产品 SN 与其余链路不受影响。
        Assert.Equal("D1S0001V30W263200002", snapshot.DeviceSerial);
        Assert.Equal(QueryState.Success, snapshot.Components.First(item => item.Component == "band").State);
    }

    [Fact]
    public async Task Collect_LeavesBoxAloneWhenVersionCheckPasses()
    {
        var configuration = new AppConfiguration
        {
            BoxVersion = new VersionCheckConfiguration
            {
                Enabled = true,
                Checks = [new VersionCheckItem { Field = "gen1App", MinVersion = "0.5.0" }]
            }
        };

        var snapshot = await CollectAsync(BoxOutput, configuration);

        var box = snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Success, box.State);
        Assert.Equal("0.5.0-20260920-36.3dc2a236", box.Values["gen1App"]);
    }

    [Fact]
    public async Task Collect_FailsBandWithoutTouchingBox()
    {
        var configuration = new AppConfiguration
        {
            BandVersion = new VersionCheckConfiguration
            {
                Enabled = true,
                Checks =
                [
                    new VersionCheckItem { Field = "leftVersion", MinVersion = "1.0.0" },
                    new VersionCheckItem { Field = "rightVersion", MinVersion = "2.0.0" }
                ]
            }
        };

        var snapshot = await CollectAsync(BoxOutput, configuration);

        var band = snapshot.Components.First(item => item.Component == "band");
        Assert.Equal(QueryState.Failed, band.State);
        Assert.Contains("低于要求的最低版本 2.0.0", band.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, band.Values["rightVersion"]);
        // 版本被清空，但左右 SN 保留，配对判定与 DeviceSerial 不受影响。
        Assert.Equal("V61S1.0-LEFT", band.Values["leftSn"]);
        Assert.Equal("V61S1.0-RIGHT", band.Values["rightSn"]);
        // 手环配置段不会影响盒子。
        Assert.Equal(QueryState.Success, snapshot.Components.First(item => item.Component == "box").State);
    }

    [Fact]
    public async Task Collect_EachComponentUsesItsOwnSwitch()
    {
        var configuration = new AppConfiguration
        {
            // 眼镜是关着的，盒子是开着的：只有盒子会被判失败。
            GlassesVersion = new VersionCheckConfiguration
            {
                Enabled = false,
                Checks = [new VersionCheckItem { Field = "app", MinVersion = "9.9.9" }]
            },
            BoxVersion = new VersionCheckConfiguration
            {
                Enabled = true,
                Checks = [new VersionCheckItem { Field = "kernel", MinVersion = "9.9.9" }]
            }
        };

        var snapshot = await CollectAsync(BoxOutput, configuration);

        Assert.Equal(QueryState.Failed, snapshot.Components.First(item => item.Component == "box").State);
        // 眼镜本来就没读到（采集失败），不受校验影响。
        Assert.Equal(QueryState.Failed, snapshot.Components.First(item => item.Component == "glasses").State);
        Assert.DoesNotContain(VersionChecker.FailurePrefix, snapshot.Components.First(item => item.Component == "glasses").Message, StringComparison.Ordinal);
        Assert.Equal(QueryState.Success, snapshot.Components.First(item => item.Component == "band").State);
    }

    [Fact]
    public async Task Collect_FailsBandWhenItsVersionIsMissing()
    {
        // allowMissing=false：手环版本读不到即判失败，左右 SN 仍然保留。
        var configuration = new AppConfiguration
        {
            BandVersion = new VersionCheckConfiguration
            {
                Enabled = true,
                Checks =
                [
                    new VersionCheckItem { Field = "leftVersion", MinVersion = "3.0.3.6", AllowMissing = false },
                    new VersionCheckItem { Field = "rightVersion", MinVersion = "3.0.3.6", AllowMissing = false }
                ]
            }
        };

        var snapshot = await CollectAsync(BoxOutput, configuration, includeBandVersions: false);

        var band = snapshot.Components.First(item => item.Component == "band");
        Assert.Equal(QueryState.Failed, band.State);
        Assert.StartsWith(VersionChecker.FailurePrefix, band.Message, StringComparison.Ordinal);
        Assert.Contains("未读取到，无法校验", band.Message, StringComparison.Ordinal);
        Assert.Equal("V61S1.0-LEFT", band.Values["leftSn"]);
        Assert.Equal("V61S1.0-RIGHT", band.Values["rightSn"]);
    }

    [Fact]
    public async Task Collect_AllowsMissingBandVersionWhenConfigured()
    {
        var configuration = new AppConfiguration
        {
            BandVersion = new VersionCheckConfiguration
            {
                Enabled = true,
                Checks = [new VersionCheckItem { Field = "leftVersion", MinVersion = "3.0.3.6", AllowMissing = true }]
            }
        };

        var snapshot = await CollectAsync(BoxOutput, configuration, includeBandVersions: false);

        Assert.Equal(QueryState.Success, snapshot.Components.First(item => item.Component == "band").State);
    }

    /// <summary>在原盒子输出后面拼上 wlan0_bt_switch 那一段（脚本里它是最后一条命令）。</summary>
    private static string BoxOutputWithSwitch(string switchOutput, int exitCode = 0) =>
        $"{BoxOutput}\n{switchOutput}\n__BOX_SW_RC__={exitCode}";

    private static async Task<DeviceVersionSnapshot> CollectAsync(
        string boxOutput,
        AppConfiguration? configuration = null,
        bool includeBandVersions = true)
    {
        var runner = new RoutingProcessRunner()
            .On("devices", () => new ProcessResult(0, "List of devices attached\nadb-001 device", string.Empty, false))
            .On("vendor_storage", () => new ProcessResult(0, boxOutput, string.Empty, false))
            .On("base64 -d", () => new ProcessResult(0, "__ACM_RDY__=0", string.Empty, false))
            .On("python3", () => new ProcessResult(0, "__ACM_PORT__=/dev/cam0_acm1", string.Empty, false));
        var adb = new AdbClient(new AdbConfiguration { Path = "adb" }, runner);
        var service = new DeviceVersionService(
            adb,
            new StubBandService { IncludeVersions = includeBandVersions },
            new GlassesAcmService(adb),
            configuration);

        return await service.CollectAsync("adb-001");
    }

    private sealed class StubBandService : IBandSerialVersionService
    {
        public bool IncludeVersions { get; init; } = true;

        public Task<VersionComponentResult> QueryAsync(CancellationToken cancellationToken = default)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["leftSn"] = "V61S1.0-LEFT",
                ["rightSn"] = "V61S1.0-RIGHT"
            };
            if (IncludeVersions)
            {
                values["leftVersion"] = "1.0.0";
                values["rightVersion"] = "1.0.0";
            }

            return Task.FromResult(new VersionComponentResult
            {
                Component = "band",
                DisplayName = "手环",
                Required = true,
                State = QueryState.Success,
                Values = values
            });
        }
    }

    private sealed class RoutingProcessRunner : IProcessRunner
    {
        private readonly List<(string Keyword, Func<ProcessResult> Factory)> _routes = [];

        public List<string> Commands { get; } = [];

        public RoutingProcessRunner On(string keyword, Func<ProcessResult> factory)
        {
            _routes.Add((keyword, factory));
            return this;
        }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var command = string.Join(' ', arguments);
            lock (Commands)
            {
                Commands.Add(command);
            }

            foreach (var (keyword, factory) in _routes)
            {
                if (command.Contains(keyword, StringComparison.Ordinal))
                {
                    return Task.FromResult(factory());
                }
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, false));
        }
    }
}
