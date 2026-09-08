using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

public interface IBandSerialVersionService
{
    Task<VersionComponentResult> QueryAsync(CancellationToken cancellationToken = default);
}

public sealed partial class BandSerialVersionService : IBandSerialVersionService
{
    internal const int BaudRate = 2_000_000;
    private const int HandQueryAttempts = 3;
    private const int SnQueryAttempts = 2;
    private const int VersionQueryAttempts = 2;
    private static readonly TimeSpan HandTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan SnTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(2);
    private static readonly byte[] ConsoleResync = "\u001b[1;1R\r\n\r\n"u8.ToArray();

    public async Task<VersionComponentResult> QueryAsync(CancellationToken cancellationToken = default)
    {
        var availablePorts = SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PortNumber)
            .ThenBy(port => port, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (availablePorts.Length == 0)
        {
            return Failure("电脑未检测到可用串口。");
        }

        var identifiedBandPorts = ReadBandPortsFromRegistry()
            .Intersect(availablePorts, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var usedUsbFilter = identifiedBandPorts.Length > 0;
        var candidates = usedUsbFilter ? identifiedBandPorts : availablePorts;
        var probes = new List<BandProbe>();

        foreach (var portName in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                probes.Add(await ProbeAsync(portName, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                probes.Add(new BandProbe(portName, null, null, $"串口查询异常：{ex.Message}"));
            }
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var sourcePorts = new List<string>();
        foreach (var hand in new[] { "left", "right" })
        {
            var matches = probes
                .Where(probe => string.Equals(probe.Hand, hand, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length > 1)
            {
                AddUnique(errors, $"检测到 {matches.Length} 个{HandDisplayName(hand)}手手环：{string.Join(", ", matches.Select(item => item.PortName))}");
            }

            foreach (var failed in matches.Where(item => !string.IsNullOrWhiteSpace(item.Error)))
            {
                AddUnique(errors, $"{failed.PortName}: {failed.Error}");
            }

            var selected = matches.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Serial));
            if (selected is null)
            {
                AddUnique(errors, $"未读取到{HandDisplayName(hand)}手手环 SN");
                values[$"{hand}Sn"] = string.Empty;
                values[$"{hand}Version"] = string.Empty;
                continue;
            }

            values[$"{hand}Sn"] = selected.Serial!;
            values[$"{hand}Version"] = selected.Version ?? string.Empty;
            sourcePorts.Add(selected.PortName);
        }

        var unrecognized = probes.Where(probe => probe.Hand is null).ToArray();
        if (usedUsbFilter || values.Count == 0)
        {
            foreach (var probe in unrecognized)
            {
                AddUnique(errors, $"{probe.PortName}: {probe.Error}");
            }
        }

        var hasBothSerials = values.ContainsKey("leftSn") && values.ContainsKey("rightSn");
        var source = sourcePorts.Count > 0
            ? $"本机串口 {BaudRate} 8N1: {string.Join(", ", sourcePorts)}"
            : $"本机串口 {BaudRate} 8N1";
        return new VersionComponentResult
        {
            Component = "band",
            DisplayName = "手环",
            Required = true,
            State = errors.Count == 0
                ? QueryState.Success
                : hasBothSerials ? QueryState.Warning : QueryState.Failed,
            Source = source,
            Message = string.Join("; ", errors),
            Values = values
        };
    }

    private static async Task<BandProbe> ProbeAsync(string portName, CancellationToken cancellationToken)
    {
        using var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 100,
            WriteTimeout = 1000,
            Encoding = Encoding.UTF8,
            NewLine = "\r\n"
        };

        try
        {
            await Task.Run(port.Open, cancellationToken);
            // Opening the ESP32-S3 USB serial port toggles DTR/RTS and can reset it.
            await Task.Delay(500, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BandProbe(portName, null, null, $"打开串口失败：{ex.Message}");
        }

        string handResponse = string.Empty;
        string? hand = null;
        for (var attempt = 1; attempt <= HandQueryAttempts && hand is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            port.DiscardInBuffer();
            await ResyncConsoleAsync(port, cancellationToken);
            port.DiscardInBuffer();
            await WriteLinePacedAsync(port, "hand", cancellationToken);
            await Task.Delay(50, cancellationToken);
            handResponse = await ReadUntilAsync(port, "hand=", HandTimeout, TimeSpan.FromMilliseconds(250), cancellationToken);
            hand = DeviceVersionParser.ParseBandHand(handResponse);
            if (hand is null && attempt < HandQueryAttempts)
            {
                await Task.Delay(300, cancellationToken);
            }
        }

        if (hand is null)
        {
            return new BandProbe(
                portName,
                null,
                null,
                $"hand 命令重试 {HandQueryAttempts} 次仍无有效响应{ResponseSuffix(handResponse)}");
        }

        string snResponse = string.Empty;
        string? sn = null;
        for (var attempt = 1; attempt <= SnQueryAttempts && sn is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            port.DiscardInBuffer();
            await WriteLinePacedAsync(port, "sn", cancellationToken);
            await Task.Delay(50, cancellationToken);
            snResponse = await ReadUntilAsync(port, "sn=", SnTimeout, TimeSpan.FromMilliseconds(350), cancellationToken);
            sn = DeviceVersionParser.ParseBandSerial(snResponse);
            if (sn is null && attempt < SnQueryAttempts)
            {
                await Task.Delay(300, cancellationToken);
            }
        }

        string versionResponse = string.Empty;
        string? version = null;
        for (var attempt = 1; attempt <= VersionQueryAttempts && version is null; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            port.DiscardInBuffer();
            await WriteLinePacedAsync(port, "version", cancellationToken);
            await Task.Delay(50, cancellationToken);
            versionResponse = await ReadUntilAsync(port, "version=", VersionTimeout, TimeSpan.FromMilliseconds(350), cancellationToken);
            version = DeviceVersionParser.ParseBandVersion(versionResponse);
            if (version is null && attempt < VersionQueryAttempts)
            {
                await Task.Delay(300, cancellationToken);
            }
        }

        if (sn is null)
        {
            return new BandProbe(
                portName,
                hand,
                version,
                $"sn 命令重试 {SnQueryAttempts} 次仍无 SN{ResponseSuffix(snResponse)}");
        }

        return version is not null
            ? new BandProbe(portName, hand, version, null, sn)
            : new BandProbe(
                portName,
                hand,
                null,
                $"version 命令重试 {VersionQueryAttempts} 次仍无版本号{ResponseSuffix(versionResponse)}",
                sn);
    }

    private static async Task ResyncConsoleAsync(SerialPort port, CancellationToken cancellationToken)
    {
        port.Write(ConsoleResync, 0, ConsoleResync.Length);
        await DrainInputAsync(port, TimeSpan.FromMilliseconds(200), cancellationToken);
    }

    private static async Task WriteLinePacedAsync(
        SerialPort port,
        string command,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        var oneByte = new byte[1];
        foreach (var value in bytes)
        {
            oneByte[0] = value;
            port.Write(oneByte, 0, 1);
            await Task.Delay(1, cancellationToken);
        }
    }

    private static async Task DrainInputAsync(
        SerialPort port,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        var buffer = new byte[512];
        while (deadline.Elapsed < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = port.BytesToRead;
            if (available > 0)
            {
                port.Read(buffer, 0, Math.Min(available, buffer.Length));
            }
            else
            {
                await Task.Delay(20, cancellationToken);
            }
        }
    }

    private static async Task<string> ReadUntilAsync(
        SerialPort port,
        string marker,
        TimeSpan timeout,
        TimeSpan trailingDuration,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryStream();
        var elapsed = Stopwatch.StartNew();
        TimeSpan? markerSeenAt = null;
        var buffer = new byte[1024];
        while (elapsed.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = port.BytesToRead;
            if (available > 0)
            {
                var count = port.Read(buffer, 0, Math.Min(available, buffer.Length));
                response.Write(buffer, 0, count);
                if (markerSeenAt is null &&
                    Encoding.UTF8.GetString(response.GetBuffer(), 0, checked((int)response.Length))
                        .Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    markerSeenAt = elapsed.Elapsed;
                }
            }
            else
            {
                await Task.Delay(20, cancellationToken);
            }

            if (markerSeenAt is not null && elapsed.Elapsed - markerSeenAt.Value >= trailingDuration)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(response.ToArray());
    }

    private static IReadOnlyCollection<string> ReadBandPortsFromRegistry()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb is null)
            {
                return result;
            }

            foreach (var deviceName in usb.GetSubKeyNames()
                         .Where(name => name.StartsWith("VID_303A&PID_1001", StringComparison.OrdinalIgnoreCase)))
            {
                using var device = usb.OpenSubKey(deviceName);
                if (device is null)
                {
                    continue;
                }

                foreach (var instanceName in device.GetSubKeyNames())
                {
                    using var parameters = device.OpenSubKey($@"{instanceName}\Device Parameters");
                    if (parameters?.GetValue("PortName") is string portName &&
                        !string.IsNullOrWhiteSpace(portName))
                    {
                        result.Add(portName);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }

        return result;
    }

    private static void AddUnique(List<string> errors, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error) && !errors.Contains(error, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(error);
        }
    }

    private static string HandDisplayName(string hand) => hand == "left" ? "左" : "右";

    private static int PortNumber(string portName)
    {
        var match = ComPortNumberRegex().Match(portName);
        return match.Success && int.TryParse(match.Groups["number"].Value, out var number)
            ? number
            : int.MaxValue;
    }

    private static string ResponseSuffix(string response)
    {
        var summary = string.Join(
            " ",
            response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (summary.Length > 160)
        {
            summary = summary[^160..];
        }
        return summary.Length == 0 ? string.Empty : $"，收到：{summary}";
    }

    private static VersionComponentResult Failure(string message) => new()
    {
        Component = "band",
        DisplayName = "手环",
        Required = true,
        State = QueryState.Failed,
        Source = $"本机串口 {BaudRate} 8N1",
        Message = message
    };

    private sealed record BandProbe(
        string PortName,
        string? Hand,
        string? Version,
        string? Error,
        string? Serial = null);

    [GeneratedRegex("^COM(?<number>\\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComPortNumberRegex();
}
