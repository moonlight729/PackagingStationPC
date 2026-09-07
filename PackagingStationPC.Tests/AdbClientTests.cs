using PackagingStationPC.Models;
using PackagingStationPC.Services;

namespace PackagingStationPC.Tests;

public sealed class AdbClientTests
{
    [Fact]
    public async Task RunEmbeddedPython_PushesExecutesAndCleansTemporaryScript()
    {
        var runner = new RecordingProcessRunner();
        var client = new AdbClient(
            new AdbConfiguration { Path = "test-adb", CommandTimeoutSeconds = 10, PeripheralTimeoutSeconds = 20 },
            runner);

        var result = await client.RunEmbeddedPythonAsync(
            "device-1",
            "glasses_version_query.py",
            useSudo: false);

        Assert.True(result.Success);
        Assert.Equal(3, runner.Calls.Count);
        var push = runner.Calls[0];
        Assert.Equal(["-s", "device-1", "push"], push.Arguments.Take(3));
        Assert.StartsWith("/tmp/packaging-station-", push.Arguments[4]);
        Assert.Contains("MSG_UPDATE_VERSION = 0xA4", runner.PushedSource);
        Assert.False(File.Exists(push.Arguments[3]));

        var execute = runner.Calls[1];
        Assert.Equal(["-s", "device-1", "shell"], execute.Arguments.Take(3));
        Assert.Equal($"python3 '{push.Arguments[4]}'", execute.Arguments[3]);

        var cleanup = runner.Calls[2];
        Assert.Equal($"rm -f '{push.Arguments[4]}'", cleanup.Arguments[3]);
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<Invocation> Calls { get; } = [];
        public string PushedSource { get; private set; } = string.Empty;

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var values = arguments.ToArray();
            Calls.Add(new Invocation(fileName, values, timeout));
            if (values.Length >= 5 && values[2] == "push")
            {
                PushedSource = File.ReadAllText(values[3]);
            }

            return Task.FromResult(new ProcessResult(0, "ok", string.Empty, false));
        }
    }

    private sealed record Invocation(string FileName, string[] Arguments, TimeSpan Timeout);
}
