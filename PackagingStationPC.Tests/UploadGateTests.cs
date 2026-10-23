using PackagingStationPC.Models;
using PackagingStationPC.ViewModels;

namespace PackagingStationPC.Tests;

/// <summary>扫码后的上报门禁：哪些情况必须卡住，哪些可以放行。</summary>
public sealed class UploadGateTests
{
    [Fact]
    public void ValidateForUpload_BlocksWhenTheLanMacActionFailed()
    {
        // 写 LAN MAC 没做成：SN 和版本都读到了（值按原样保留），门禁仍然不能放行。
        var snapshot = Snapshot(
            Box(QueryState.Failed, "写入盒子 LAN MAC 随机标记失败: permission denied"),
            Band());

        var error = MainViewModel.ValidateForUpload(snapshot);

        Assert.NotNull(error);
        Assert.Contains("写入盒子 LAN MAC 随机标记失败", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateForUpload_PassesWhenTheLanMacActionSucceeded()
    {
        var snapshot = Snapshot(Box(QueryState.Success, string.Empty), Band());

        Assert.Null(MainViewModel.ValidateForUpload(snapshot));
    }

    [Fact]
    public void ValidateForUpload_BlocksWhenTheBoxReadFailedWithoutValues()
    {
        var error = MainViewModel.ValidateForUpload(Snapshot(
            new VersionComponentResult
            {
                Component = "box",
                DisplayName = "盒子",
                Required = true,
                State = QueryState.Failed,
                Message = "读取盒子设备 SN失败: 无输出"
            },
            Band()));

        Assert.NotNull(error);
        Assert.Contains("读取盒子设备 SN失败", error, StringComparison.Ordinal);
    }

    private static VersionComponentResult Box(QueryState state, string message) =>
        new()
        {
            Component = "box",
            DisplayName = "盒子",
            Required = true,
            State = state,
            Message = message,
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sn"] = "D1S0001V30W263200002",
                ["uboot"] = "v0.0.8-260909",
                ["kernel"] = "v0.1.3-260909",
                ["rootfs"] = "0.1.5",
                ["gen1App"] = "0.5.0-20260920"
            }
        };

    private static VersionComponentResult Band() =>
        new()
        {
            Component = "band",
            DisplayName = "手环",
            Required = true,
            State = QueryState.Success,
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["leftSn"] = "V61S1.0-LEFT",
                ["rightSn"] = "V61S1.0-RIGHT"
            }
        };

    private static DeviceVersionSnapshot Snapshot(VersionComponentResult box, VersionComponentResult band) =>
        new()
        {
            AdbSerial = "adb-001",
            DeviceSerial = "D1S0001V30W263200002",
            Components = [box, Glasses(), band]
        };

    private static VersionComponentResult Glasses() =>
        new()
        {
            Component = "glasses",
            DisplayName = "眼镜",
            Required = true,
            State = QueryState.Success,
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sn"] = "G1S0001",
                ["uboot"] = "v0.0.1",
                ["kernel"] = "v0.0.1",
                ["rootfs"] = "0.0.1",
                ["app"] = "1.0.0"
            }
        };
}
