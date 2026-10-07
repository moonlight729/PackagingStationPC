using PackagingStationPC.Models;
using PackagingStationPC.Services;

namespace PackagingStationPC.Tests;

public sealed class VersionCheckerTests
{
    private static readonly Dictionary<string, string> BoxValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sn"] = "D1S0001V30W263200002",
        ["uboot"] = "v0.0.8-260909",
        ["kernel"] = "v0.1.3-260909",
        ["rootfs"] = "0.1.5",
        ["gen1App"] = "0.5.0-20260920-36.3dc2a236"
    };

    [Fact]
    public void Check_PassesWhenConfigurationIsDisabled()
    {
        var configuration = new VersionCheckConfiguration
        {
            Enabled = false,
            Checks = [new VersionCheckItem { Field = "gen1App", MinVersion = "9.9.9" }]
        };

        Assert.True(VersionChecker.Check(Box(QueryState.Success), configuration).Passed);
    }

    [Fact]
    public void Check_PassesWhenEveryFieldMeetsMinimumVersion()
    {
        var configuration = new VersionCheckConfiguration
        {
            Enabled = true,
            Checks =
            [
                new VersionCheckItem { Field = "gen1App", MinVersion = "0.5.0" },
                new VersionCheckItem { Field = "kernel", MinVersion = "0.1.3" },
                new VersionCheckItem { Field = "rootfs", MinVersion = "0.1.5" },
                new VersionCheckItem { Field = "uboot", MinVersion = "0.0.8" }
            ]
        };

        var result = VersionChecker.Check(Box(QueryState.Success), configuration);

        Assert.True(result.Passed);
        Assert.False(result.Blocking);
    }

    [Fact]
    public void Check_FailsWhenVersionIsBelowMinimum()
    {
        var configuration = new VersionCheckConfiguration
        {
            Enabled = true,
            Checks = [new VersionCheckItem { Field = "gen1App", MinVersion = "0.6.0" }]
        };

        var result = VersionChecker.Check(Box(QueryState.Success), configuration);

        Assert.False(result.Passed);
        Assert.True(result.Blocking);
        Assert.Equal(["gen1App"], result.Fields);
        Assert.Contains("低于要求的最低版本 0.6.0", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_SkipsItemsThatAreTurnedOff()
    {
        var configuration = new VersionCheckConfiguration
        {
            Enabled = true,
            Checks =
            [
                new VersionCheckItem { Field = "gen1App", MinVersion = "9.9.9", Enabled = false },
                new VersionCheckItem { Field = "kernel", MinVersion = "0.1.3" }
            ]
        };

        Assert.True(VersionChecker.Check(Box(QueryState.Success), configuration).Passed);
    }

    [Fact]
    public void Check_ReportsMissingFieldInsteadOfComparing()
    {
        var values = new Dictionary<string, string>(BoxValues, StringComparer.OrdinalIgnoreCase);
        values.Remove("gen1App");
        var configuration = new VersionCheckConfiguration
        {
            Enabled = true,
            Checks = [new VersionCheckItem { Field = "gen1App", MinVersion = "0.5.0" }]
        };

        var result = VersionChecker.Check(Box(QueryState.Success, values), configuration);

        Assert.False(result.Passed);
        Assert.Contains("未读取到，无法校验", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_AllowsMissingFieldWhenConfigured()
    {
        var values = new Dictionary<string, string>(BoxValues, StringComparer.OrdinalIgnoreCase);
        values.Remove("gen1App");
        var configuration = new VersionCheckConfiguration
        {
            Enabled = true,
            Checks = [new VersionCheckItem { Field = "gen1App", MinVersion = "0.5.0", AllowMissing = true }]
        };

        Assert.True(VersionChecker.Check(Box(QueryState.Success, values), configuration).Passed);
    }

    [Fact]
    public void Check_NonBlockingFailureDoesNotBlock()
    {
        var configuration = new VersionCheckConfiguration
        {
            Enabled = true,
            Checks = [new VersionCheckItem { Field = "gen1App", MinVersion = "9.9.9", BlockOnFail = false }]
        };

        var result = VersionChecker.Check(Box(QueryState.Success), configuration);

        Assert.False(result.Passed);
        Assert.False(result.Blocking);
    }

    [Fact]
    public void Check_DoesNotRunWhenTheQueryAlreadyFailed()
    {
        var configuration = new VersionCheckConfiguration
        {
            Enabled = true,
            Checks = [new VersionCheckItem { Field = "gen1App", MinVersion = "9.9.9" }]
        };

        Assert.True(VersionChecker.Check(Box(QueryState.Failed), configuration).Passed);
    }

    [Fact]
    public void Check_WorksForAnyComponent()
    {
        var band = new VersionComponentResult
        {
            Component = "band",
            DisplayName = "手环",
            Required = true,
            State = QueryState.Success,
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["leftSn"] = "V61S1.0-LEFT",
                ["rightSn"] = "V61S1.0-RIGHT",
                ["leftVersion"] = "0.9.0",
                ["rightVersion"] = "1.0.0"
            }
        };
        var configuration = new VersionCheckConfiguration
        {
            Enabled = true,
            Checks =
            [
                new VersionCheckItem { Field = "leftVersion", MinVersion = "1.0.0" },
                new VersionCheckItem { Field = "rightVersion", MinVersion = "1.0.0" }
            ]
        };

        var result = VersionChecker.Check(band, configuration);

        Assert.False(result.Passed);
        Assert.Equal(["leftVersion"], result.Fields);
    }

    [Fact]
    public void Check_ComparesExpectedValueExactlyRatherThanByVersion()
    {
        var configuration = new VersionCheckConfiguration
        {
            Enabled = true,
            Checks = [new VersionCheckItem { Field = "wlan0BtSwitch", Expected = "0" }]
        };

        Assert.True(VersionChecker.Check(Switch("0"), configuration).Passed);
        // 开关位是精确相等，不是"不低于"：1 比 0 大也不算通过。
        var failed = VersionChecker.Check(Switch("1"), configuration);
        Assert.False(failed.Passed);
        Assert.True(failed.Blocking);
        Assert.Contains("wlan0BtSwitch 当前为 1，要求为 0", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_TrimsBothSidesBeforeComparingExpectedValue()
    {
        var configuration = new VersionCheckConfiguration
        {
            Enabled = true,
            Checks = [new VersionCheckItem { Field = "wlan0BtSwitch", Expected = "0" }]
        };

        // 文件末尾换行、前后空格都不该影响判定。
        Assert.True(VersionChecker.Check(Switch("0\r\n"), configuration).Passed);
        Assert.True(VersionChecker.Check(Switch(" 0 "), configuration).Passed);
    }

    [Fact]
    public void Check_FailsWhenExpectedValueIsMissingAndNotAllowed()
    {
        var configuration = new VersionCheckConfiguration
        {
            Enabled = true,
            Checks = [new VersionCheckItem { Field = "wlan0BtSwitch", Expected = "0", AllowMissing = false }]
        };

        var result = VersionChecker.Check(Switch(string.Empty), configuration);

        Assert.False(result.Passed);
        Assert.Contains("要求为 0", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_PassesWhenTheSectionIsOnByDefaultButHasNoCheck()
    {
        // 默认开启 + 空规则 = 不校验。旧 appsettings.json（没有这三段）不会因为默认开启而被误判。
        var configuration = new VersionCheckConfiguration();

        Assert.True(configuration.Enabled);
        Assert.True(VersionChecker.Check(Measured("box", "盒子", BoxValues), configuration).Passed);
    }

    [Fact]
    public void Check_SkipsItemWithoutAnyRequirement()
    {
        var configuration = new VersionCheckConfiguration
        {
            Enabled = true,
            Checks = [new VersionCheckItem { Field = "wlan0BtSwitch" }]
        };

        Assert.True(VersionChecker.Check(Switch("1"), configuration).Passed);
    }

    /// <summary>
    /// 2026-10-07 16:33 那次完整采集的实测版本，配合 appsettings.json 里的默认 minVersion 必须全部通过。
    /// 换了新固件后如果这里红，说明默认配置该跟着调。
    /// </summary>
    [Fact]
    public void Check_PassesWithTheMeasuredVersionsOfTheLastRun()
    {
        var box = Measured("box", "盒子", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["uboot"] = "v0.1.1-260920.053723-289545544",
            ["kernel"] = "v0.1.6-260920.053745-289545544",
            ["rootfs"] = "0.1.6",
            ["gen1App"] = "0.5.2-20260929-65.e51ad76d"
        });
        var glasses = Measured("glasses", "眼镜", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["uboot"] = "V0.0.2-260901.101129-ee08c1496",
            ["kernel"] = "V0.0.7-260901.101137-ee08c1496",
            ["rootfs"] = "V0.0.6-260901.101156-ee08c1496",
            ["app"] = "V0.1.0-260901.101237-ee08c1496"
        });
        var band = Measured("band", "手环", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["leftVersion"] = "3.0.3.6",
            ["rightVersion"] = "3.0.3.6"
        });

        Assert.True(VersionChecker.Check(box, Configured(
            ("uboot", "0.1.1"), ("kernel", "0.1.6"), ("rootfs", "0.1.6"), ("gen1App", "0.5.2"))).Passed);
        Assert.True(VersionChecker.Check(glasses, Configured(
            ("uboot", "0.0.2"), ("kernel", "0.0.7"), ("rootfs", "0.0.6"), ("app", "0.1.0"))).Passed);
        Assert.True(VersionChecker.Check(band, Configured(
            ("leftVersion", "3.0.3.6"), ("rightVersion", "3.0.3.6"))).Passed);
    }

    [Theory]
    // 前导 v/V 与第一段之后的非数字后缀都不参与比较，只按 x.y.z 比。
    [InlineData("v0.0.8-260909", "0.0.8", 0)]
    [InlineData("0.5.0-20260920-36.3dc2a236", "0.5.0", 0)]
    [InlineData("V1.2.3", "1.2.3", 0)]
    // 位数不同时缺失位按 0 处理。
    [InlineData("0.1", "0.1.0", 0)]
    [InlineData("0.1.1", "0.1", 1)]
    [InlineData("0.9", "0.10", -1)]
    [InlineData("1.0.0", "0.9.9", 1)]
    public void CompareVersion_IgnoresPrefixAndSuffix(string actual, string expected, int expectedComparison)
    {
        Assert.Equal(expectedComparison, VersionChecker.CompareVersion(actual, expected));
    }

    [Fact]
    public void CompareVersion_ReturnsNullWhenNotComparable()
    {
        Assert.Null(VersionChecker.CompareVersion("unknown", "0.5.0"));
        Assert.Null(VersionChecker.CompareVersion("0.5.0", ""));
    }

    private static VersionComponentResult Switch(string value) =>
        Measured("box", "盒子", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wlan0BtSwitch"] = value
        });

    private static VersionCheckConfiguration Configured(params (string Field, string MinVersion)[] checks) =>
        new()
        {
            Enabled = true,
            Checks = checks.Select(check => new VersionCheckItem
            {
                Field = check.Field,
                MinVersion = check.MinVersion
            }).ToList()
        };

    private static VersionComponentResult Measured(
        string component,
        string displayName,
        Dictionary<string, string> values) =>
        new()
        {
            Component = component,
            DisplayName = displayName,
            Required = true,
            State = QueryState.Success,
            Values = values
        };

    private static VersionComponentResult Box(
        QueryState state,
        Dictionary<string, string>? values = null) =>
        new()
        {
            Component = "box",
            DisplayName = "盒子",
            Required = true,
            State = state,
            Values = new Dictionary<string, string>(values ?? BoxValues, StringComparer.OrdinalIgnoreCase)
        };
}
