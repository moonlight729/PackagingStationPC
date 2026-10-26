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
        "[INFO] VENDOR_LAN_MAC_ID write ok\n" +
        "__BOX_MAC_RC__=0\n" +
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
    public async Task Collect_RandomizesLanMacBeforeReadingVersion()
    {
        var run = await RunAsync(BoxOutputWithSwitch("1"));

        var box = run.Snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Success, box.State);
        var shell = run.Runner.Commands.First(command => command.Contains("vendor_storage", StringComparison.Ordinal));
        var writeIndex = shell.IndexOf("vendor_storage -w VENDOR_LAN_MAC_ID -i \"00\"", StringComparison.Ordinal);
        var versionIndex = shell.IndexOf("version 2>&1", StringComparison.Ordinal);
        Assert.True(writeIndex >= 0, "盒子脚本里没有写 VENDOR_LAN_MAC_ID 的动作。");
        Assert.True(writeIndex < versionIndex, "LAN MAC 随机化必须在读版本号之前执行。");
        // 只写不读：MAC 不进采集结果，也就不可能进上报。
        Assert.DoesNotContain(box.Values.Keys, key => key.Contains("mac", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Collect_FailsBoxWhenTheLanMacWriteFails()
    {
        // 写 LAN MAC 没做成就判盒子不合格，卡住后面的预留 SN 与上传；
        // 读到的 SN 和版本仍然保留，界面上看得到读到了什么。
        var run = await RunAsync(BoxOutputWithSwitch("1").Replace("__BOX_MAC_RC__=0", "__BOX_MAC_RC__=1"));

        var box = run.Snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Failed, box.State);
        Assert.Contains("写入盒子 LAN MAC 随机标记失败", box.Message, StringComparison.Ordinal);
        Assert.Equal("D1S0001V30W263200002", box.Values["sn"]);
        Assert.Equal("v0.1.3-260909", box.Values["kernel"]);
        // 版本校验不再叠加到已经失败的组件上，避免把"动作没做成"误报成"版本不合格"。
        Assert.DoesNotContain(VersionChecker.FailurePrefix, box.Message, StringComparison.Ordinal);

        var action = Assert.Single(box.Actions);
        Assert.False(action.Success);
        Assert.Equal(DeviceVersionService.LanMacActionKey, action.Key);
    }

    [Fact]
    public async Task Collect_RecordsTheLanMacActionWhenItSucceeds()
    {
        var snapshot = await CollectAsync(BoxOutputWithSwitch("1"));

        var box = snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Success, box.State);
        var action = Assert.Single(box.Actions);
        Assert.True(action.Success);
        Assert.Equal("LAN MAC 随机化", action.DisplayName);
        Assert.Contains("vendor_storage -w VENDOR_LAN_MAC_ID", action.Command, StringComparison.Ordinal);
        // 动作只留痕，不进采集字段（也就不会进上报）。
        Assert.DoesNotContain(box.Values.Keys, key => key.Contains("mac", StringComparison.OrdinalIgnoreCase));
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
        // 不合格也保留读到的原值：清空会让界面显示"未获取"，和真的没读到无法区分。
        Assert.Equal("1", box.Values[DeviceVersionService.SwitchField]);
        Assert.Equal("D1S0001V30W263200002", snapshot.DeviceSerial);
    }

    [Fact]
    public async Task Collect_KeepsTheMeasuredValueWhenTheCheckFails()
    {
        // 不合格也保留读到的原值：以前会清空，界面显示"未获取"，和真的没读到无法区分。
        var configuration = new AppConfiguration
        {
            BoxVersion = new VersionCheckConfiguration
            {
                Enabled = true,
                Checks =
                [
                    new VersionCheckItem { Field = "kernel", Version = "9.9.9" },
                    new VersionCheckItem { Field = DeviceVersionService.SwitchField, Expected = "0" }
                ]
            }
        };

        var snapshot = await CollectAsync(BoxOutputWithSwitch("1"), configuration);

        var box = snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Failed, box.State);
        Assert.Equal("v0.1.3-260909", box.Values["kernel"]);
        Assert.Equal("1", box.Values[DeviceVersionService.SwitchField]);
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
    public async Task Collect_FailsBoxWhenVersionDiffersFromTheConfiguredOne()
    {
        var configuration = new AppConfiguration
        {
            BoxVersion = new VersionCheckConfiguration
            {
                Enabled = true,
                Checks = [new VersionCheckItem { Field = "gen1App", Version = "0.6.0" }]
            }
        };

        var snapshot = await CollectAsync(BoxOutput, configuration);

        var box = snapshot.Components.First(item => item.Component == "box");
        Assert.Equal(QueryState.Failed, box.State);
        Assert.Contains("与要求的版本 0.6.0 不一致（低于）", box.Message, StringComparison.Ordinal);
        // 值原样保留，阻断交给门禁看组件状态，不再靠清空值。
        Assert.Equal("0.5.0-20260920-36.3dc2a236", box.Values["gen1App"]);
        // 设备动作是采集时已经做过的事：版本校验替换组件对象时不能把它丢掉，
        // 否则界面上的"设备动作"卡片会显示成"本轮没有设备动作记录"。
        Assert.Single(box.Actions);
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
                Checks = [new VersionCheckItem { Field = "gen1App", Version = "0.5.0" }]
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
                    new VersionCheckItem { Field = "leftVersion", Version = "1.0.0" },
                    new VersionCheckItem { Field = "rightVersion", Version = "2.0.0" }
                ]
            }
        };

        var snapshot = await CollectAsync(BoxOutput, configuration);

        var band = snapshot.Components.First(item => item.Component == "band");
        Assert.Equal(QueryState.Failed, band.State);
        Assert.Contains("与要求的版本 2.0.0 不一致（低于）", band.Message, StringComparison.Ordinal);
        Assert.Equal("1.0.0", band.Values["rightVersion"]);
        // 版本保留，左右 SN 也保留，配对判定与 DeviceSerial 不受影响。
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
                Checks = [new VersionCheckItem { Field = "app", Version = "9.9.9" }]
            },
            BoxVersion = new VersionCheckConfiguration
            {
                Enabled = true,
                Checks = [new VersionCheckItem { Field = "kernel", Version = "9.9.9" }]
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
                    new VersionCheckItem { Field = "leftVersion", Version = "3.0.3.6", AllowMissing = false },
                    new VersionCheckItem { Field = "rightVersion", Version = "3.0.3.6", AllowMissing = false }
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
                Checks = [new VersionCheckItem { Field = "leftVersion", Version = "3.0.3.6", AllowMissing = true }]
            }
        };

        var snapshot = await CollectAsync(BoxOutput, configuration, includeBandVersions: false);

        Assert.Equal(QueryState.Success, snapshot.Components.First(item => item.Component == "band").State);
    }

    [Fact]
    public async Task QueryDeviceTime_ReturnsTheBoxClock()
    {
        // 盒子 shell 会先吐终端噪声（ESC[c、"resize:Terminal is not VT100-compatible."），
        // 取到的必须是它后面的时间正文，不能是这些噪声行。
        var service = CreateTimeService(new ProcessResult(
            0,
            "\u001b[c\r\nresize:Terminal is not VT100-compatible.\r\nThu Oct 23 10:11:12 CST 2026\r\n",
            string.Empty,
            false));

        var action = await service.QueryDeviceTimeAsync("adb-001");

        Assert.True(action.Success);
        Assert.Equal(DeviceVersionService.DeviceTimeActionKey, action.Key);
        Assert.Equal("date", action.Command);
        Assert.Equal("Thu Oct 23 10:11:12 CST 2026", action.Message);
    }

    [Fact]
    public async Task QueryDeviceTime_ReportsFailureWithoutThrowing()
    {
        // 只做显示：命令失败也只是回一条失败记录，不能把正在跑的流水线带崩。
        var service = CreateTimeService(new ProcessResult(1, string.Empty, "date: not found", false));

        var action = await service.QueryDeviceTimeAsync("adb-001");

        Assert.False(action.Success);
        Assert.Contains("date: not found", action.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Thu Oct 23 10:11:12 CST 2026", "2026-10-23 10:11:12")]
    [InlineData("Thu Oct  3 09:05:00 GMT+08:00 2026", "2026-10-03 09:05:00")]
    // 24 小时制：下午 1 点 07 分不能被写成 01:07。
    [InlineData("Fri Oct 23 13:07:09 CST 2026", "2026-10-23 13:07:09")]
    [InlineData("2026-10-23 19:52:31", "2026-10-23 19:52:31")]
    // 认不出的一律原样返回，不做猜测，界面上看得出"这就是盒子给的原文"。
    [InlineData("not a clock", "not a clock")]
    [InlineData("", "")]
    // 盒子的 date 是 12 小时制带 AM/PM，下面这组上下午全都要换成 24 小时制。
    [InlineData("Mon Oct 26 09:45:25 AM CST 2026", "2026-10-26 09:45:25")]
    [InlineData("Mon Oct 26 11:59:59 AM CST 2026", "2026-10-26 11:59:59")]
    [InlineData("Mon Oct 26 12:00:00 PM CST 2026", "2026-10-26 12:00:00")]
    [InlineData("Mon Oct 26 12:30:00 PM CST 2026", "2026-10-26 12:30:00")]
    [InlineData("Mon Oct 26 01:00:00 PM CST 2026", "2026-10-26 13:00:00")]
    // 下午 5 点必须显示成 17 点，不能被当成 05 点。
    [InlineData("Mon Oct 26 05:00:00 PM CST 2026", "2026-10-26 17:00:00")]
    [InlineData("Mon Oct 26 11:59:59 PM CST 2026", "2026-10-26 23:59:59")]
    // 半夜 12 点：AM 的 12 点是 00 点。
    [InlineData("Mon Oct 26 12:00:00 AM CST 2026", "2026-10-26 00:00:00")]
    [InlineData("Mon Oct 26 12:30:00 AM CST 2026", "2026-10-26 00:30:00")]
    // 小写 am/pm 同样要认。
    [InlineData("Mon Oct 26 05:00:00 pm CST 2026", "2026-10-26 17:00:00")]
    [InlineData("Mon Oct 26 05:00:00 pm +0800 2026", "2026-10-26 17:00:00")]
    public void FormatDeviceTime_TurnsTheBoxClockIntoAReadableDate(string raw, string expected) =>
        Assert.Equal(expected, DeviceVersionService.FormatDeviceTime(raw));

    [Fact]
    public async Task QueryDeviceTime_IgnoresShellNoiseWhenThereIsNoClockLine()
    {
        // 只有终端噪声、没有时间正文时不能把噪声当成时间显示出来。
        var service = CreateTimeService(new ProcessResult(
            0,
            "\u001b[c\r\nresize:Terminal is not VT100-compatible.\r\n",
            string.Empty,
            false));

        var action = await service.QueryDeviceTimeAsync("adb-001");

        Assert.False(action.Success);
        Assert.DoesNotContain("resize", action.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryDeviceTime_ReportsFailureWhenNoDeviceIsOnline()
    {
        var runner = new RoutingProcessRunner()
            .On("devices", () => new ProcessResult(0, "List of devices attached", string.Empty, false));
        var adb = new AdbClient(new AdbConfiguration { Path = "adb" }, runner);
        var service = new DeviceVersionService(adb, new StubBandService(), new GlassesAcmService(adb));

        var action = await service.QueryDeviceTimeAsync("adb-001");

        Assert.False(action.Success);
        Assert.Contains("ADB", action.Message, StringComparison.Ordinal);
    }

    /// <summary>造一个只关心 <c>date</c> 这条命令的服务。</summary>
    private static DeviceVersionService CreateTimeService(ProcessResult dateResult)
    {
        var runner = new RoutingProcessRunner()
            .On("devices", () => new ProcessResult(0, "List of devices attached\nadb-001 device", string.Empty, false))
            .On("date", () => dateResult);
        var adb = new AdbClient(new AdbConfiguration { Path = "adb" }, runner);
        return new DeviceVersionService(adb, new StubBandService(), new GlassesAcmService(adb));
    }

    /// <summary>在原盒子输出后面拼上 wlan0_bt_switch 那一段（脚本里它是最后一条命令）。</summary>
    private static string BoxOutputWithSwitch(string switchOutput, int exitCode = 0) =>
        $"{BoxOutput}\n{switchOutput}\n__BOX_SW_RC__={exitCode}";

    private static async Task<DeviceVersionSnapshot> CollectAsync(
        string boxOutput,
        AppConfiguration? configuration = null,
        bool includeBandVersions = true) =>
        (await RunAsync(boxOutput, configuration, includeBandVersions)).Snapshot;

    /// <summary>跑一轮采集并留下 adb 命令与进度文本，供断言脚本内容和提示信息用。</summary>
    private static async Task<CollectRun> RunAsync(
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
        var progress = new List<string>();
        var service = new DeviceVersionService(
            adb,
            new StubBandService { IncludeVersions = includeBandVersions },
            new GlassesAcmService(adb),
            configuration);
        service.ProgressChanged += progress.Add;

        var snapshot = await service.CollectAsync("adb-001");
        return new CollectRun(snapshot, runner, progress);
    }

    private sealed record CollectRun(
        DeviceVersionSnapshot Snapshot,
        RoutingProcessRunner Runner,
        List<string> Progress);

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
