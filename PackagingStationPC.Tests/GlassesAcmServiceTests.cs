using System.Text;
using PackagingStationPC.Models;
using PackagingStationPC.Services;

namespace PackagingStationPC.Tests;

public sealed class GlassesAcmServiceTests
{
    [Fact]
    public async Task Query_ParsesVersionAndSerialUsingCSharpProtocolImplementation()
    {
        var runner = new QueueProcessRunner(
            AcmOutput(0xA4, Encoding.UTF8.GetBytes("{\"app\":\"V1.2.3\"}")),
            AcmOutput(0xB6, [0x01, 0x06, .. Encoding.UTF8.GetBytes("GLS123")]));
        var adb = new AdbClient(new AdbConfiguration { Path = "adb" }, runner);

        var result = await new GlassesAcmService(adb).QueryAsync("adb-001", useSudo: false);

        Assert.Equal(QueryState.Success, result.State);
        Assert.Equal("V1.2.3", result.Values["app"]);
        Assert.Equal("GLS123", result.Values["sn"]);
        Assert.Contains("/dev/cam0_acm1", result.Source);
        Assert.All(runner.Commands, command => Assert.DoesNotContain("python", command, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(runner.Commands, command => command.Contains("U1kDpA", StringComparison.Ordinal));
        Assert.Contains(runner.Commands, command => command.Contains("U1kDtQ", StringComparison.Ordinal));
    }

    private static string AcmOutput(byte type, byte[] payload)
    {
        var frame = new byte[12 + payload.Length];
        frame[0] = 0x53;
        frame[1] = 0x59;
        frame[2] = 0x03;
        frame[3] = type;
        BitConverter.TryWriteBytes(frame.AsSpan(7, 2), (ushort)payload.Length);
        payload.CopyTo(frame, 10);
        BitConverter.TryWriteBytes(frame.AsSpan(10 + payload.Length, 2), Crc16(frame.AsSpan(0, 10 + payload.Length)));
        return $"shell noise\n__ACM_PORT__=/dev/cam0_acm1\n__ACM_DATA__={Convert.ToBase64String(frame)}\n";
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
        }

        return crc;
    }

    private sealed class QueueProcessRunner(params string[] outputs) : IProcessRunner
    {
        private readonly Queue<string> _outputs = new(outputs);
        public List<string> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var values = arguments.ToArray();
            Commands.Add(values[^1]);
            return Task.FromResult(new ProcessResult(0, _outputs.Dequeue(), string.Empty, false));
        }
    }
}
