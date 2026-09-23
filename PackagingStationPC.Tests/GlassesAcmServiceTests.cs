using System.Text;
using PackagingStationPC.Models;
using PackagingStationPC.Services;
using Xunit;

namespace PackagingStationPC.Tests;

public sealed class GlassesAcmServiceTests
{
    [Fact]
    public async Task Query_ParsesVersionAndSerialFromOnDeviceAcmHelper()
    {
        // 版本帧与 SN 帧合并为一次 adb shell，助手只打印一次结果。
        var runner = new QueueProcessRunner(
            Success(AcmOutput(
                (0, 0xA4, Encoding.UTF8.GetBytes("{\"app\":\"V1.2.3\"}")),
                (1, 0xB6, [0x01, 0x06, .. Encoding.UTF8.GetBytes("GLS123")]))));
        var adb = new AdbClient(new AdbConfiguration { Path = "adb" }, runner);

        var result = await new GlassesAcmService(adb).QueryAsync("adb-001");

        Assert.Equal(QueryState.Success, result.State);
        Assert.Equal("V1.2.3", result.Values["app"]);
        Assert.Equal("GLS123", result.Values["sn"]);
        Assert.Contains("/dev/cam0_acm1", result.Source);
        Assert.Single(runner.Commands);
        Assert.All(runner.Commands, command => Assert.DoesNotContain("busybox microcom -X", command, StringComparison.Ordinal));
        Assert.All(runner.Commands, command => Assert.Contains("python3", command, StringComparison.Ordinal));
        Assert.All(runner.Commands, command => Assert.Contains("rm -f /var/lock/LCK..", command, StringComparison.Ordinal));
        Assert.Contains(runner.Commands, command => command.Contains("535903A4", StringComparison.Ordinal));
        Assert.Contains(runner.Commands, command => command.Contains("535903B5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Query_ExplainsMissingPortInsteadOfRawExitCode()
    {
        var runner = new QueueProcessRunner(Failure(2, "ACM_PORT_NOT_FOUND"));
        var adb = new AdbClient(new AdbConfiguration { Path = "adb" }, runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new GlassesAcmService(adb).QueryAsync("adb-001"));

        Assert.Contains("未接入盒子", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ACM 0xA4", exception.Message, StringComparison.Ordinal);
    }

    private static string AcmOutput(params (int Index, byte Type, byte[] Payload)[] responses)
    {
        var builder = new StringBuilder("resize: Terminal is not VT100-compatible\n__ACM_PORT__=/dev/cam0_acm1\n");
        foreach (var (index, type, payload) in responses)
        {
            builder.Append($"__ACM_RESP__={index}:{type:X2}:{Convert.ToHexString(payload)}\n");
        }

        return builder.Append("__ACM_RC__=0\n").ToString();
    }

    private static ProcessResult Success(string stdout) => new(0, stdout, string.Empty, false);

    private static ProcessResult Failure(int exitCode, string stderr) => new(exitCode, string.Empty, stderr, false);

    private sealed class QueueProcessRunner(params ProcessResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessResult> _results = new(results);
        public List<string> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var values = arguments.ToArray();
            Commands.Add(values[^1]);
            return Task.FromResult(_results.Count > 0
                ? _results.Dequeue()
                : new ProcessResult(0, string.Empty, string.Empty, false));
        }
    }
}
