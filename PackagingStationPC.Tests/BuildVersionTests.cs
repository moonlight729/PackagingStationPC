using PackagingStationPC.Models;
using Xunit;

namespace PackagingStationPC.Tests;

public sealed class BuildVersionTests
{
    [Theory]
    [InlineData("V1.0.0-1-g393da69-dirty", "V1.0.0", 1, "393da69", true, "V1.0.0+1 (393da69-dirty)")]
    [InlineData("V1.0.0-3-g393da69", "V1.0.0", 3, "393da69", false, "V1.0.0+3 (393da69)")]
    [InlineData("V1.0.0-0-g393da69", "V1.0.0", 0, "393da69", false, "V1.0.0 (393da69)")]
    [InlineData("V1.0.0-0-g393da69-dirty", "V1.0.0", 0, "393da69", true, "V1.0.0 (393da69-dirty)")]
    [InlineData("393da69", null, 0, "393da69", false, "393da69")]
    [InlineData("V1.0.0-dirty", "V1.0.0", 0, null, true, "V1.0.0-dirty")]
    public void Parse_ReadsTagCommitAndDirtyState(
        string describe,
        string? expectedTag,
        int expectedCommits,
        string? expectedCommit,
        bool expectedDirty,
        string expectedDisplay)
    {
        var info = GitVersionDescription.Parse(describe);

        Assert.Equal(expectedTag, info.Tag);
        Assert.Equal(expectedCommits, info.CommitsSinceTag);
        Assert.Equal(expectedCommit, info.Commit);
        Assert.Equal(expectedDirty, info.IsDirty);
        Assert.Equal(expectedDisplay, info.Display);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    public void Parse_UnknownDescribeShowsFallback(string? describe)
    {
        var info = GitVersionDescription.Parse(describe);

        Assert.Null(info.Tag);
        Assert.Null(info.Commit);
        Assert.False(info.IsDirty);
        Assert.Equal("未知版本", info.Display);
    }

    [Fact]
    public void Detail_ShowsTagAndCommit()
    {
        var info = GitVersionDescription.Parse("V1.0.0-1-g393da69-dirty");

        Assert.Contains("Tag: V1.0.0", info.Detail, StringComparison.Ordinal);
        Assert.Contains("Commit: 393da69", info.Detail, StringComparison.Ordinal);
        Assert.Contains("距 Tag 提交数: 1", info.Detail, StringComparison.Ordinal);
        Assert.Contains("有未提交修改", info.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVersion_UsesCompiledGitDescribe()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildVersion.Describe));
        Assert.False(string.IsNullOrWhiteSpace(BuildVersion.Display));
        Assert.Contains("git describe:", BuildVersion.Detail, StringComparison.Ordinal);
        Assert.Equal(BuildVersion.Display, BuildVersion.Info.Display);
    }
}
