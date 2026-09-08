using System.Text.RegularExpressions;

namespace PackagingStationPC.Services;

public static partial class DeviceVersionParser
{
    public static string? ParseBandHand(string output)
    {
        var match = BandHandRegex().Match(StripAnsi(output));
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["hand"].Value.ToLowerInvariant() switch
        {
            "l" or "left" => "left",
            "r" or "right" => "right",
            _ => null
        };
    }

    public static string? ParseBandVersion(string output)
    {
        var match = BandVersionRegex().Match(StripAnsi(output));
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    public static string? ParseBandSerial(string output)
    {
        var match = BandSerialRegex().Match(StripAnsi(output));
        return match.Success ? match.Groups["value"].Value.Trim().ToUpperInvariant() : null;
    }

    private static string StripAnsi(string value) => AnsiEscapeRegex().Replace(value, string.Empty);

    [GeneratedRegex("\\bhand\\s*=\\s*(?<hand>left|right|l|r)\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BandHandRegex();

    [GeneratedRegex("\\bversion\\s*=\\s*(?<value>[^\\r\\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BandVersionRegex();

    [GeneratedRegex("\\bsn\\s*=\\s*(?<value>[A-Za-z0-9.\\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BandSerialRegex();

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscapeRegex();
}
