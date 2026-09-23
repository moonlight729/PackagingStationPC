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
