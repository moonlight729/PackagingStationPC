using System.Globalization;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

/// <summary>
/// 版本配置校验：等全部版本号读完之后才执行，不参与也不改变采集过程。
/// 把组件读到的版本与配置项里的期望（最低）版本逐项比对，产出失败明细。
/// 盒子、眼镜、手环共用同一套规则，各用各的配置段。
/// </summary>
public static class VersionChecker
{
    /// <summary>校验失败时写在组件 Message 上的固定前缀，供上报门禁识别这是校验失败而不是读取失败。</summary>
    public const string FailurePrefix = "版本校验未通过：";

    public static VersionCheckResult Check(
        VersionComponentResult component,
        VersionCheckConfiguration configuration)
    {
        // 采集本身已经失败时不再叠加校验结论，避免把"读不到"误报成"版本不合格"。
        if (!configuration.Enabled || configuration.Checks.Count == 0 || component.State is not QueryState.Success)
        {
            return VersionCheckResult.Pass();
        }

        var failures = new List<VersionCheckFailure>();
        foreach (var check in configuration.Checks)
        {
            if (!check.Enabled || string.IsNullOrWhiteSpace(check.Field))
            {
                continue;
            }

            // 期望值和最低版本都没配就没有判断依据，跳过这一项而不是误判失败。
            if (string.IsNullOrWhiteSpace(check.Expected) && string.IsNullOrWhiteSpace(check.MinVersion))
            {
                continue;
            }

            var field = check.Field.Trim();
            var expected = check.Expected.Trim();
            var actual = component.Values.GetValueOrDefault(field);
            if (string.IsNullOrWhiteSpace(actual))
            {
                if (check.AllowMissing)
                {
                    continue;
                }

                failures.Add(new VersionCheckFailure(
                    field,
                    check.BlockOnFail,
                    $"{field} 未读取到，无法校验（{DescribeRequirement(expected, check.MinVersion)}）"));
                continue;
            }

            // 配了期望值就按精确相等比较（开关位、配置项这类非版本字段），否则按最低版本比对。
            if (expected.Length > 0)
            {
                if (!string.Equals(actual.Trim(), expected, StringComparison.Ordinal))
                {
                    failures.Add(new VersionCheckFailure(
                        field,
                        check.BlockOnFail,
                        $"{field} 当前为 {actual.Trim()}，{DescribeRequirement(expected, check.MinVersion)}"));
                }

                continue;
            }

            var comparison = CompareVersion(actual, check.MinVersion);
            if (comparison is null)
            {
                failures.Add(new VersionCheckFailure(
                    field,
                    check.BlockOnFail,
                    $"{field} 版本 {actual} 无法与配置的最低版本 {check.MinVersion} 比较"));
                continue;
            }

            if (comparison < 0)
            {
                failures.Add(new VersionCheckFailure(
                    field,
                    check.BlockOnFail,
                    $"{field} 版本 {actual} 低于要求的最低版本 {check.MinVersion}"));
            }
        }

        return failures.Count == 0 ? VersionCheckResult.Pass() : new VersionCheckResult(failures);
    }

    private static string DescribeRequirement(string expected, string minVersion) =>
        expected.Length > 0 ? $"要求为 {expected}" : $"要求 >= {minVersion}";

    /// <summary>
    /// 比较版本号：只按 <c>x.y.z</c> 这样的数字点分串比较，忽略前导 <c>v</c>/<c>V</c>
    /// 和第一段之后的非数字后缀，因此 <c>v0.0.8-260909</c>、<c>0.5.0-20260920-36.3dc2a236</c>
    /// 分别按 <c>0.0.8</c>、<c>0.5.0</c> 比较。位数不同时缺失位按 0 处理（<c>0.1</c> 等于 <c>0.1.0</c>）。
    /// 返回 null 表示无法比较。
    /// </summary>
    public static int? CompareVersion(string actual, string expected)
    {
        if (!TryParseSegments(actual, out var left) || !TryParseSegments(expected, out var right))
        {
            return null;
        }

        var length = Math.Max(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var actualPart = index < left.Length ? left[index] : 0;
            var expectedPart = index < right.Length ? right[index] : 0;
            if (actualPart != expectedPart)
            {
                return actualPart < expectedPart ? -1 : 1;
            }
        }

        return 0;
    }

    private static bool TryParseSegments(string value, out int[] segments)
    {
        segments = [];
        var text = value.Trim();
        if (text.Length > 0 && text[0] is 'v' or 'V')
        {
            text = text[1..];
        }

        var end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'))
        {
            end++;
        }

        if (end == 0)
        {
            return false;
        }

        var parts = text[..end].Split('.');
        var parsed = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed[index]))
            {
                return false;
            }
        }

        segments = parsed;
        return true;
    }
}

/// <summary>单条校验项的失败明细。</summary>
public sealed record VersionCheckFailure(string Field, bool Blocking, string Message);

/// <summary>单个组件的版本校验结果。</summary>
public sealed record VersionCheckResult(IReadOnlyList<VersionCheckFailure> Failures)
{
    public bool Passed => Failures.Count == 0;

    /// <summary>是否存在需要阻断的失败项（blockOnFail=false 的项只提示）。</summary>
    public bool Blocking => Failures.Any(failure => failure.Blocking);

    public string Message => string.Join("；", Failures.Select(failure => failure.Message));

    /// <summary>需要清空的字段，让既有的"必需字段非空"门禁一并拦住不合格的版本。</summary>
    public IReadOnlyList<string> Fields => Failures.Select(failure => failure.Field).ToArray();

    public static VersionCheckResult Pass() => new([]);
}
