using System.Text.RegularExpressions;

namespace PackagingStationPC.Models;

/// <summary>
/// 应用构建版本：<c>BuildVersionInfo.Describe</c> 由 MSBuild 在编译时执行
/// <c>git describe --tags --always --dirty --long</c> 生成，因此版本以 "tag + commit" 形式呈现，
/// 便于追溯现场运行的程序来自哪个 tag 和哪个提交。
/// </summary>
public static class BuildVersion
{
    /// <summary>编译期生成的原始 git describe 输出。</summary>
    public static string Describe => BuildVersionInfo.Describe;

    /// <summary>解析后的版本信息。</summary>
    public static GitVersionDescription Info => GitVersionDescription.Parse(Describe);

    /// <summary>界面展示用版本，例如 <c>V1.0.0+1 (393da69-dirty)</c>。</summary>
    public static string Display => Info.Display;

    /// <summary>悬浮提示用详细说明。</summary>
    public static string Detail => $"git describe: {Describe}{Environment.NewLine}{Info.Detail}";
}

/// <summary>
/// git describe 解析结果。
/// </summary>
/// <param name="Tag">最近的 tag，无 tag 时为 null。</param>
/// <param name="CommitsSinceTag">距该 tag 的提交数。</param>
/// <param name="Commit">短提交号，无法获取时为 null。</param>
/// <param name="IsDirty">编译时工作区是否有未提交修改。</param>
public readonly partial record struct GitVersionDescription(
    string? Tag,
    int CommitsSinceTag,
    string? Commit,
    bool IsDirty)
{
    public string Display
    {
        get
        {
            if (Tag is null)
            {
                return Commit is null ? "未知版本" : WithDirty(Commit);
            }

            var distance = CommitsSinceTag > 0 ? $"+{CommitsSinceTag}" : string.Empty;
            var commit = Commit is null ? string.Empty : $" ({WithDirty(Commit)})";
            var dirty = Commit is null && IsDirty ? "-dirty" : string.Empty;
            return $"{Tag}{dirty}{distance}{commit}";
        }
    }

    public string Detail =>
        string.Join(
            Environment.NewLine,
            $"Tag: {Tag ?? "-"}",
            $"Commit: {Commit ?? "-"}",
            $"距 Tag 提交数: {CommitsSinceTag}",
            $"工作区: {(IsDirty ? "有未提交修改" : "干净")}");

    /// <summary>
    /// 解析 git describe（<c>--tags --always --dirty --long</c>）输出。
    /// 典型形式：<c>V1.0.0-1-g393da69-dirty</c>；无 tag 时只有短提交号；取不到时为空或 unknown。
    /// </summary>
    public static GitVersionDescription Parse(string? describe)
    {
        var text = describe?.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return new GitVersionDescription(null, 0, null, false);
        }

        var dirty = false;
        if (text.EndsWith("-dirty", StringComparison.OrdinalIgnoreCase))
        {
            dirty = true;
            text = text[..^"-dirty".Length];
        }

        var match = DescribePattern().Match(text);
        if (match.Success)
        {
            return new GitVersionDescription(
                match.Groups["tag"].Value,
                int.Parse(match.Groups["distance"].Value),
                match.Groups["commit"].Value,
                dirty);
        }

        return IsCommitHash(text)
            ? new GitVersionDescription(null, 0, text, dirty)
            : new GitVersionDescription(text, 0, null, dirty);
    }

    private string WithDirty(string commit) => IsDirty ? $"{commit}-dirty" : commit;

    private static bool IsCommitHash(string text) =>
        text.Length >= 7 && text.All(Uri.IsHexDigit);

    [GeneratedRegex(@"^(?<tag>.+)-(?<distance>\d+)-g(?<commit>[0-9a-fA-F]+)$")]
    private static partial Regex DescribePattern();
}
