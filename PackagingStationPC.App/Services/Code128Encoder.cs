namespace PackagingStationPC.Services;

public static class Code128Encoder
{
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112"
    ];

    public static IReadOnlyList<int> GetCodeValues(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(character => character is < (char)32 or > (char)126))
        {
            throw new ArgumentException("Code 128-B 仅支持 ASCII 32 到 126。", nameof(value));
        }

        const int startCodeB = 104;
        var values = new List<int> { startCodeB };
        var checksum = startCodeB;
        for (var index = 0; index < value.Length; index++)
        {
            var code = value[index] - 32;
            values.Add(code);
            checksum += code * (index + 1);
        }

        values.Add(checksum % 103);
        values.Add(106);
        return values;
    }

    public static IReadOnlyList<int> GetModuleWidths(string value)
    {
        var widths = new List<int>();
        foreach (var code in GetCodeValues(value))
        {
            widths.AddRange(Patterns[code].Select(character => character - '0'));
        }

        return widths;
    }
}
