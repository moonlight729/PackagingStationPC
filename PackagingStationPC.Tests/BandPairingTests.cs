using PackagingStationPC.Models;
using PackagingStationPC.Services;
using PackagingStationPC.ViewModels;
using Xunit;

namespace PackagingStationPC.Tests;

public sealed class BandPairingTests
{
    [Fact]
    public void ValidateHandPairing_AcceptsOneLeftAndOneRight()
    {
        Assert.Null(BandSerialVersionService.ValidateHandPairing(1, 1, "V61S1.0-LEFT", "V61S1.0-RIGHT"));
    }

    [Theory]
    [InlineData(0, 1, "V61S1.0-LEFT", "V61S1.0-RIGHT", "未读取到左手手环 SN")]
    [InlineData(1, 0, "V61S1.0-LEFT", "V61S1.0-RIGHT", "未读取到右手手环 SN")]
    [InlineData(1, 1, "", "V61S1.0-RIGHT", "未读取到左手手环 SN")]
    [InlineData(1, 1, "V61S1.0-LEFT", "  ", "未读取到右手手环 SN")]
    public void ValidateHandPairing_RejectsMissingHand(
        int leftCount,
        int rightCount,
        string? leftSn,
        string? rightSn,
        string expected)
    {
        Assert.Equal(expected, BandSerialVersionService.ValidateHandPairing(leftCount, rightCount, leftSn, rightSn));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    public void ValidateHandPairing_RejectsDuplicatedHand(int leftCount, int rightCount)
    {
        var error = BandSerialVersionService.ValidateHandPairing(leftCount, rightCount, "V61S1.0-LEFT", "V61S1.0-RIGHT");

        Assert.NotNull(error);
        Assert.Contains("左右手环未正确配对", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateHandPairing_RejectsIdenticalSerialNumbers()
    {
        var error = BandSerialVersionService.ValidateHandPairing(1, 1, "V61S1.0-SAME", " V61S1.0-SAME ");

        Assert.NotNull(error);
        Assert.Contains("SN 相同", error, StringComparison.Ordinal);
    }

    [Fact]
    public void IsBandPairValid_RejectsFailedOrSameSerialBand()
    {
        Assert.True(MainViewModel.IsBandPairValid(Band("V61S1.0-LEFT", "V61S1.0-RIGHT", QueryState.Success)));
        Assert.False(MainViewModel.IsBandPairValid(Band("V61S1.0-LEFT", "V61S1.0-RIGHT", QueryState.Failed)));
        Assert.False(MainViewModel.IsBandPairValid(Band("V61S1.0-LEFT", "V61S1.0-LEFT", QueryState.Success)));
        Assert.False(MainViewModel.IsBandPairValid(Band("V61S1.0-LEFT", string.Empty, QueryState.Success)));
        Assert.False(MainViewModel.IsBandPairValid(Band(string.Empty, "V61S1.0-RIGHT", QueryState.Success)));
    }

    [Fact]
    public void RetryCandidates_CoversColdStartAndSkipsUnopenablePorts()
    {
        var probes = new[]
        {
            // 刚上电没响应：需要补测
            new BandSerialVersionService.BandProbe("COM5", null, null, "hand 命令重试 3 次仍无有效响应"),
            // 识别出手环但没读到 SN：需要补测
            new BandSerialVersionService.BandProbe("COM6", "left", null, "sn 命令重试 2 次仍无 SN"),
            // 读到 SN：不需要补测
            new BandSerialVersionService.BandProbe("COM7", "right", "3.0.3.6", null, "V61S1.0-RIGHT"),
            // 端口被占用：补测也没用，排除
            new BandSerialVersionService.BandProbe("COM8", null, null, "打开串口失败：Access denied", Retryable: false)
        };

        Assert.Equal(["COM5", "COM6"], BandSerialVersionService.RetryCandidates(probes));
    }

    [Fact]
    public void Merge_PrefersProbeWithSerialNumber()
    {
        var first = new[]
        {
            new BandSerialVersionService.BandProbe("COM5", null, null, "hand 命令重试 3 次仍无有效响应"),
            new BandSerialVersionService.BandProbe("COM7", "right", "3.0.3.6", null, "V61S1.0-RIGHT")
        };
        var second = new[]
        {
            new BandSerialVersionService.BandProbe("COM5", "left", "3.0.3.6", null, "V61S1.0-LEFT"),
            new BandSerialVersionService.BandProbe("COM7", null, null, "hand 命令重试 3 次仍无有效响应")
        };

        var merged = BandSerialVersionService.Merge(first, second);

        Assert.Equal(2, merged.Count);
        Assert.Equal("V61S1.0-LEFT", merged[0].Serial);
        // 第二轮更差的结果不能覆盖第一轮已经读到的 SN
        Assert.Equal("V61S1.0-RIGHT", merged[1].Serial);
    }

    [Fact]
    public void NeedsMoreProbes_OnlyWhenPairIsIncomplete()
    {
        // 一左一右且都读到 SN：不再做任何多余探测，成功路径耗时不受影响
        Assert.False(BandSerialVersionService.NeedsMoreProbes(new[]
        {
            new BandSerialVersionService.BandProbe("COM4", "left", "3.0.3.6", null, "V61S1.0-LEFT"),
            new BandSerialVersionService.BandProbe("COM5", "right", "3.0.3.6", null, "V61S1.0-RIGHT")
        }));

        // 只有一只（另一只被 USB 过滤排除）：必须继续找，否则永远缺一侧
        Assert.True(BandSerialVersionService.NeedsMoreProbes(new[]
        {
            new BandSerialVersionService.BandProbe("COM5", "right", "3.0.3.6", null, "V61S1.0-RIGHT")
        }));

        // 识别出手环但没读到 SN：继续找
        Assert.True(BandSerialVersionService.NeedsMoreProbes(new[]
        {
            new BandSerialVersionService.BandProbe("COM4", "left", null, "sn 命令重试 2 次仍无 SN"),
            new BandSerialVersionService.BandProbe("COM5", "right", "3.0.3.6", null, "V61S1.0-RIGHT")
        }));
    }

    private static VersionComponentResult Band(string leftSn, string rightSn, QueryState state) => new()
    {
        Component = "band",
        DisplayName = "手环",
        Required = true,
        State = state,
        Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["leftSn"] = leftSn,
            ["leftVersion"] = "3.0.3.6",
            ["rightSn"] = rightSn,
            ["rightVersion"] = "3.0.3.6"
        }
    };
}
