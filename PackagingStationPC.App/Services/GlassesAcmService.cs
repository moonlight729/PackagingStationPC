using System.Text;
using System.Text.Json;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

public sealed class GlassesAcmService(AdbClient adbClient)
{
    private const byte ProtocolVersion = 0x03;
    private const byte UpdateVersion = 0xA4;
    private const byte SnRead = 0xB5;
    private const byte SnData = 0xB6;
    private const byte SnStatus = 0xB7;

    public async Task<VersionComponentResult> QueryAsync(
        string adbSerial,
        bool useSudo,
        CancellationToken cancellationToken = default)
    {
        var versionPayload = await QueryWithRetryAsync(adbSerial, useSudo, UpdateVersion, [UpdateVersion], cancellationToken);
        var snFrame = await QueryWithRetryAsync(adbSerial, useSudo, SnRead, [SnData, SnStatus], cancellationToken);
        var values = ParseVersion(versionPayload.Payload);
        values["sn"] = ParseSerialNumber(snFrame);
        return new VersionComponentResult
        {
            Component = "glasses",
            DisplayName = "眼镜",
            Required = true,
            State = QueryState.Success,
            Source = $"ACM YCTC v3: {versionPayload.Port}",
            Values = values
        };
    }

    private async Task<AcmResponse> QueryWithRetryAsync(
        string adbSerial,
        bool useSudo,
        byte requestType,
        byte[] responseTypes,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                return await QueryAsync(adbSerial, useSudo, requestType, responseTypes, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"第 {attempt} 次: {ex.Message}");
                if (attempt < 8)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }
        }

        throw new InvalidOperationException(
            $"眼镜 ACM 0x{requestType:X2} 重试 8 次失败。{string.Join("; ", errors)}");
    }

    private async Task<AcmResponse> QueryAsync(
        string adbSerial,
        bool useSudo,
        byte requestType,
        byte[] responseTypes,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                return await QueryOnceAsync(
                    adbSerial,
                    useSudo,
                    requestType,
                    responseTypes,
                    (ushort)attempt,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"第 {attempt} 次: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"眼镜 ACM 0x{requestType:X2} 重试 8 次失败。{string.Join("; ", errors)}");
    }

    private async Task<AcmResponse> QueryOnceAsync(
        string adbSerial,
        bool useSudo,
        byte requestType,
        byte[] responseTypes,
        ushort sequence,
        CancellationToken cancellationToken)
    {
        var frame = BuildFrame(requestType, sequence);
        var encoded = Convert.ToBase64String(frame);
        var privilege = useSudo ? "sudo -n " : string.Empty;
        var script = $"port=/dev/cam0_acm1; [ -e \"$port\" ] || port=/dev/ttyACM1; " +
                     "[ -e \"$port\" ] || { echo ACM_PORT_NOT_FOUND >&2; exit 2; }; " +
                     "echo __ACM_PORT__=$port; printf '__ACM_DATA__='; " +
                     $"printf '%s' '{encoded}' | base64 -d | {privilege}busybox microcom -X -t 2000 -s 921600 \"$port\" | " +
                     "base64 | tr -d '\\r\\n'; echo";
        var result = await adbClient.ShellAsync(
            adbSerial,
            script,
            TimeSpan.FromSeconds(6),
            cancellationToken);
        if (!result.Success)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new InvalidOperationException(
                $"眼镜 ACM 0x{requestType:X2} 查询失败(exit={result.ExitCode}, timeout={result.TimedOut}): {detail.Trim()}");
        }

        var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var port = lines.LastOrDefault(line => line.StartsWith("__ACM_PORT__=", StringComparison.Ordinal))?
            .Substring("__ACM_PORT__=".Length).Trim() ?? "unknown";
        foreach (var line in lines.Reverse())
        {
            if (!line.StartsWith("__ACM_DATA__=", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var bytes = Convert.FromBase64String(line.Substring("__ACM_DATA__=".Length).Trim());
                var parsed = FindFrame(bytes, responseTypes);
                if (parsed is not null)
                {
                    return parsed with { Port = port };
                }
            }
            catch (FormatException)
            {
            }
        }

        throw new InvalidOperationException($"眼镜 ACM 0x{requestType:X2} 未返回有效响应帧。");
    }

    private static byte[] BuildFrame(byte type, ushort sequence)
    {
        var frame = new byte[12];
        frame[0] = 0x53;
        frame[1] = 0x59;
        frame[2] = ProtocolVersion;
        frame[3] = type;
        frame[4] = 0;
        BitConverter.TryWriteBytes(frame.AsSpan(5, 2), sequence);
        var crc = Crc16(frame.AsSpan(0, 10));
        BitConverter.TryWriteBytes(frame.AsSpan(10, 2), crc);
        return frame;
    }

    private static AcmResponse? FindFrame(byte[] buffer, byte[] expectedTypes)
    {
        for (var offset = 0; offset <= buffer.Length - 12; offset++)
        {
            if (buffer[offset] != 0x53 || buffer[offset + 1] != 0x59 || buffer[offset + 2] != ProtocolVersion)
            {
                continue;
            }

            var payloadLength = BitConverter.ToUInt16(buffer, offset + 7);
            var frameLength = 12 + payloadLength;
            if (offset + frameLength > buffer.Length || !expectedTypes.Contains(buffer[offset + 3]))
            {
                continue;
            }

            var expectedCrc = BitConverter.ToUInt16(buffer, offset + 10 + payloadLength);
            if (Crc16(buffer.AsSpan(offset, 10 + payloadLength)) != expectedCrc)
            {
                continue;
            }

            return new AcmResponse(
                buffer[offset + 3],
                buffer.AsSpan(offset + 10, payloadLength).ToArray(),
                string.Empty);
        }

        return null;
    }

    private static Dictionary<string, string> ParseVersion(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string ParseSerialNumber(AcmResponse response)
    {
        if (response.Type == SnStatus)
        {
            var status = response.Payload.Length == 0 ? -1 : response.Payload[^1];
            throw new InvalidOperationException($"眼镜 SN 读取失败，SN_STATUS={status}。");
        }

        if (response.Payload.Length < 2 || response.Payload[1] > response.Payload.Length - 2)
        {
            throw new FormatException("眼镜 SN_DATA payload 格式无效。");
        }

        var serial = Encoding.UTF8.GetString(response.Payload, 2, response.Payload[1]).Trim();
        return string.IsNullOrWhiteSpace(serial)
            ? throw new InvalidOperationException("眼镜 SN 为空。")
            : serial;
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

    private sealed record AcmResponse(byte Type, byte[] Payload, string Port);
}
