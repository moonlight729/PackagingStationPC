namespace PackagingStationPC.Services;

public sealed class ScannerService
{
    public string Normalize(string rawInput) => rawInput
        .Trim()
        .Replace("\r", string.Empty)
        .Replace("\n", string.Empty);
}
