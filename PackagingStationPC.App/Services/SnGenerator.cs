using System.Globalization;
using System.Text.RegularExpressions;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

public sealed partial class SnGenerator
{
    public const int MaximumSequence = 999999;

    public string Generate(SnParts parts)
    {
        Validate(parts);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{parts.ProductCode}{parts.FactoryCode}{parts.Year:00}{parts.Week:00}{parts.Sequence:000000}");
    }

    public void Validate(SnParts parts)
    {
        if (!string.Equals(parts.ProductCode, "G1", StringComparison.Ordinal))
        {
            throw new ArgumentException("产品代码必须为 G1。", nameof(parts));
        }

        if (!FactoryCodeRegex().IsMatch(parts.FactoryCode))
        {
            throw new ArgumentException("包装厂代码必须是一个大写字母或数字。", nameof(parts));
        }

        if (parts.Year is < 0 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(parts), "包装年份必须在 00 到 99 之间。");
        }

        if (parts.Week is < 1 or > 53)
        {
            throw new ArgumentOutOfRangeException(nameof(parts), "包装周次必须在 01 到 53 之间。");
        }

        if (parts.Sequence is < 1 or > MaximumSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(parts), "流水号必须在 000001 到 999999 之间。");
        }
    }

    public static (int Year, int Week) CurrentIsoYearWeek(DateTime date)
    {
        var isoYear = ISOWeek.GetYear(date);
        return (isoYear % 100, ISOWeek.GetWeekOfYear(date));
    }

    [GeneratedRegex("^[A-Z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex FactoryCodeRegex();
}
