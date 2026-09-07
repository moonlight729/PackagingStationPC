using PackagingStationPC.Services;

namespace PackagingStationPC.Tests;

public sealed class Code128EncoderTests
{
    [Fact]
    public void GetCodeValues_UsesCodeSetBAndChecksum()
    {
        var values = Code128Encoder.GetCodeValues("G1F2632000001");

        Assert.Equal(104, values[0]);
        Assert.Equal(106, values[^1]);
        var expectedChecksum = (104 + "G1F2632000001"
            .Select((character, index) => (character - 32) * (index + 1))
            .Sum()) % 103;
        Assert.Equal(expectedChecksum, values[^2]);
    }

    [Fact]
    public void GetModuleWidths_ContainsCompleteStopPattern()
    {
        var widths = Code128Encoder.GetModuleWidths("ABC");

        Assert.Equal(6 * 5 + 7, widths.Count);
        Assert.Equal(new[] { 2, 3, 3, 1, 1, 1, 2 }, widths.TakeLast(7));
    }
}
