using PackagingStationPC.Models;
using PackagingStationPC.Services;

namespace PackagingStationPC.Tests;

public sealed class SnGeneratorTests
{
    private readonly SnGenerator _generator = new();

    [Fact]
    public void Generate_FormatsSpecifiedRule()
    {
        var sn = _generator.Generate(new SnParts("G1", "F", 26, 32, 1));

        Assert.Equal("G1F2632000001", sn);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(54, 1)]
    [InlineData(32, 0)]
    [InlineData(32, 1000000)]
    public void Generate_RejectsInvalidRanges(int week, int sequence)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            _generator.Generate(new SnParts("G1", "F", 26, week, sequence)));
    }
}
