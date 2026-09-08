using PackagingStationPC.Services;

namespace PackagingStationPC.Tests;

public sealed class DeviceVersionParserTests
{
    [Theory]
    [InlineData("I (302609) basic_info: hand=l", "left")]
    [InlineData(">>>>> hand\r\nI (302609) basic_info: hand=right\r\n>>>>>", "right")]
    [InlineData("\u001b[2K>>>>> hand\r\nI (1) basic_info: HAND = L", "left")]
    public void ParseBandHand_IgnoresConsolePrompt(string output, string expected)
    {
        Assert.Equal(expected, DeviceVersionParser.ParseBandHand(output));
    }

    [Fact]
    public void ParseBandVersion_ReturnsOnlyVersionAndIgnoresPrompt()
    {
        const string output = """
            >>>>> version
            I (305245) factory: [ok]version=3.0.3.3
            I (305245) factory: [ok]project=Prometheus
            I (305245) factory: [ok]idf_ver=v5.5.2
            I (305246) factory: [ok]partition=ota_1
            >>>>>
            """;

        Assert.Equal("3.0.3.3", DeviceVersionParser.ParseBandVersion(output));
    }

    [Fact]
    public void ParseBandHand_RejectsUnrelatedSerialOutput()
    {
        Assert.Null(DeviceVersionParser.ParseBandHand(">>>>> help\r\nunknown command"));
    }

    [Fact]
    public void ParseBandSerial_ReturnsFactorySerialNumber()
    {
        const string output = ">>>>> sn\r\nI (1) factory: [ok]sn=V61S1.0-C62100000042326\r\n>>>>>";

        Assert.Equal("V61S1.0-C62100000042326", DeviceVersionParser.ParseBandSerial(output));
    }
}
