using System.Globalization;
using System.Text;
using System.Text.Json;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

/// <summary>
/// 读取眼镜版本与 SN。
///
/// 眼镜的 YCTC UART v3 控制口 /dev/cam0_acm1 与盒子上的 camera10_node 共用，
/// 直接用 busybox microcom 会有两个问题：
/// 1) microcom 会占用 /var/lock/LCK..cam0_acm1 锁文件，adb 侧超时中断后远端进程不会退出，
///    锁文件残留会让后续所有 microcom 都返回 “can't create ... File exists”，导致再也读不到版本；
/// 2) microcom 不会拉 DTR/RTS，而眼镜板要求 DTR/RTS 有效。
///
/// 因此这里改为把一段小型 python 助手推到盒子上执行（与现场 ota-query-versions.sh 的做法一致）：
/// 自己 open 串口 + termios 921600 8N1 + TIOCMSET 拉 DTR/RTS + tcflush 后发帧读帧并校验 CRC。
/// </summary>
public sealed class GlassesAcmService(AdbClient adbClient)
{
    private const byte ProtocolVersion = 0x03;
    private const byte UpdateVersion = 0xA4;
    private const byte SnRead = 0xB5;
    private const byte SnData = 0xB6;
    private const byte SnStatus = 0xB7;

    private const int Attempts = 2;
    private const string PortMarker = "__ACM_PORT__=";
    private const string ResponseMarker = "__ACM_RESP__=";
    private const string MissingMarker = "__ACM_MISS__=";
    private const string HelperPath = "/tmp/.packaging-station-acm.py";

    // 板上 python 助手一轮最多 ROUNDS×TIMEOUT，两帧合计约 8s，留足余量即可。
    private static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);
    private static readonly string HelperBase64 =
        Convert.ToBase64String(Encoding.UTF8.GetBytes(HelperSource));

    /// <summary>
    /// 一次 adb shell 内完成版本帧与 SN 帧的查询：python 只启动一次、脚本只推送一次、
    /// 串口锁和 microcom 只清理一次，把原先两次完整的 adb 往返压成一次。
    /// </summary>
    public async Task<VersionComponentResult> QueryAsync(
        string adbSerial,
        CancellationToken cancellationToken = default)
    {
        var requests = new[]
        {
            (Request: UpdateVersion, Expected: new[] { UpdateVersion }),
            (Request: SnRead, Expected: new[] { SnData, SnStatus })
        };
        var responses = await QueryFramesAsync(adbSerial, requests, cancellationToken);
        var version = responses.FirstOrDefault(item => item.Index == 0)
            ?? throw new InvalidOperationException($"眼镜 ACM 0x{UpdateVersion:X2} 未返回有效响应帧。");
        var sn = responses.FirstOrDefault(item => item.Index == 1)
            ?? throw new InvalidOperationException($"眼镜 ACM 0x{SnRead:X2} 未返回有效响应帧。");

        var values = ParseVersion(version.Payload);
        values["sn"] = ParseSerialNumber(sn);
        return new VersionComponentResult
        {
            Component = "glasses",
            DisplayName = "眼镜",
            Required = true,
            State = QueryState.Success,
            Source = $"ACM YCTC v3: {version.Port}",
            Values = values
        };
    }

    private async Task<IReadOnlyList<AcmResponse>> QueryFramesAsync(
        string adbSerial,
        (byte Request, byte[] Expected)[] requests,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                return await QueryOnceAsync(adbSerial, requests, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"第 {attempt} 次: {Truncate(ex.Message, 200)}");
                if (attempt < Attempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }
        }

        var commands = string.Join('/', requests.Select(item => $"0x{item.Request:X2}"));
        throw new InvalidOperationException(
            $"眼镜 ACM {commands} 重试 {Attempts} 次失败。{string.Join("; ", errors)}");
    }

    private async Task<IReadOnlyList<AcmResponse>> QueryOnceAsync(
        string adbSerial,
        (byte Request, byte[] Expected)[] requests,
        CancellationToken cancellationToken)
    {
        // 每帧一组参数：期望响应类型列表 + 请求帧 hex。
        var arguments = new List<string>();
        foreach (var request in requests)
        {
            arguments.Add(string.Join(
                ',',
                request.Expected.Select(type => type.ToString("X2", CultureInfo.InvariantCulture))));
            arguments.Add(Convert.ToHexString(BuildFrame(request.Request, 1)));
        }

        // 权限在脚本内自判（root 直接跑，非 root 走 sudo -n），省掉一次额外的 adb shell 探测。
        var invocation = $"python3 {HelperPath} $port {string.Join(' ', arguments.Select(item => $"'{item}'"))}";
        // 设备侧脚本：定位串口 -> 清掉上次遗留的 microcom 锁与进程 -> 落地 python 助手 -> 执行 -> 清理。
        // pkill 的模式写成 [b]usybox microcom，避免匹配到本脚本自身的命令行。
        var script =
            "port=; for p in /dev/cam0_acm1 /dev/ttyACM1; do [ -e $p ] && { port=$p; break; }; done; " +
            "[ -n $port ] || { echo ACM_PORT_NOT_FOUND >&2; exit 2; }; " +
            "command -v python3 >/dev/null 2>&1 || { echo ACM_PYTHON_MISSING >&2; exit 3; }; " +
            "rm -f /var/lock/LCK..$(basename $port); " +
            "pkill -f '[b]usybox microcom' >/dev/null 2>&1; " +
            $"echo {PortMarker}$port; " +
            $"printf '%s' '{HelperBase64}' | base64 -d > {HelperPath}; " +
            $"if [ \"$(id -u)\" = \"0\" ]; then {invocation}; else sudo -n {invocation}; fi; rc=$?; " +
            $"rm -f {HelperPath}; echo __ACM_RC__=$rc; exit $rc";
        var result = await adbClient.ShellAsync(adbSerial, script, ShellTimeout, cancellationToken);
        var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var port = "unknown";
        var responses = new List<AcmResponse>();
        foreach (var line in lines)
        {
            var portIndex = line.IndexOf(PortMarker, StringComparison.Ordinal);
            if (portIndex >= 0)
            {
                port = line[(portIndex + PortMarker.Length)..].Trim()
                    .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "unknown";
            }

            var responseIndex = line.IndexOf(ResponseMarker, StringComparison.Ordinal);
            if (responseIndex >= 0 &&
                TryParseResponse(line[(responseIndex + ResponseMarker.Length)..], out var parsed))
            {
                responses.Add(parsed);
            }
        }

        if (!result.Success)
        {
            var commands = string.Join('/', requests.Select(item => $"0x{item.Request:X2}"));
            throw new InvalidOperationException(
                $"眼镜 ACM {commands} 查询失败(exit={result.ExitCode}, timeout={result.TimedOut}): {Describe(result, requests)}");
        }

        for (var index = 0; index < requests.Length; index++)
        {
            if (responses.All(item => item.Index != index))
            {
                throw new InvalidOperationException(
                    $"眼镜 ACM 0x{requests[index].Request:X2} 未返回有效响应帧（{Describe(result, requests)}）。");
            }
        }

        return responses.Select(item => item with { Port = port }).ToArray();
    }

    private static string Describe(ProcessResult result, (byte Request, byte[] Expected)[] requests)
    {
        var probe = result.StandardError + "\n" + result.StandardOutput;
        if (probe.Contains("ACM_PORT_NOT_FOUND", StringComparison.Ordinal))
        {
            return "盒子上不存在 /dev/cam0_acm1 与 /dev/ttyACM1，眼镜可能未接入盒子";
        }

        if (probe.Contains("ACM_PYTHON_MISSING", StringComparison.Ordinal))
        {
            return "盒子上未安装 python3，无法查询眼镜版本";
        }

        for (var index = 0; index < requests.Length; index++)
        {
            if (probe.Contains($"{MissingMarker}{index}", StringComparison.Ordinal))
            {
                return $"眼镜未响应 ACM 0x{requests[index].Request:X2} 请求帧，串口可能被占用或眼镜无应答";
            }
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        var tail = string.Join(
            ' ',
            detail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .TakeLast(3));
        return tail.Length == 0 ? "无输出" : Truncate(tail, 200);
    }

    /// <summary>
    /// 解析 <c>&lt;序号&gt;:&lt;类型&gt;:&lt;payload hex&gt;</c>。
    /// </summary>
    private static bool TryParseResponse(string text, out AcmResponse response)
    {
        response = null!;
        var separator = text.IndexOf(':');
        if (separator <= 0 ||
            !int.TryParse(text[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return false;
        }

        var rest = text[(separator + 1)..];
        var typeSeparator = rest.IndexOf(':');
        if (typeSeparator <= 0 ||
            !byte.TryParse(rest[..typeSeparator], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var type))
        {
            return false;
        }

        var hexLength = 0;
        while (hexLength < rest.Length - typeSeparator - 1 && IsHexDigit(rest[typeSeparator + 1 + hexLength]))
        {
            hexLength++;
        }

        hexLength -= hexLength % 2;
        if (hexLength == 0)
        {
            return false;
        }

        try
        {
            response = new AcmResponse(
                index,
                type,
                Convert.FromHexString(rest.Substring(typeSeparator + 1, hexLength)),
                string.Empty);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsHexDigit(char value) =>
        (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F');

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

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

    private sealed record AcmResponse(int Index, byte Type, byte[] Payload, string Port);

    /// <summary>
    /// 在盒子上执行的 python 助手：直接打开 ACM 串口（termios 921600 8N1 + DTR/RTS），
    /// 依次写多个请求帧，每帧最多做 ROUNDS 轮读取（每轮 TIMEOUT 秒），命中期望类型且 CRC 正确的帧时打印
    /// <c>__ACM_RESP__=&lt;序号&gt;:&lt;类型&gt;:&lt;payload hex&gt;</c>；某帧始终无应答时打印
    /// <c>__ACM_MISS__=&lt;序号&gt;</c> 并以非 0 退出。
    /// 参数：argv[1]=串口，之后按 (期望响应类型逗号分隔 hex, 请求帧 hex) 成对传入。
    /// </summary>
    private const string HelperSource = """
        import fcntl, os, select, struct, sys, termios, time

        TIOCMSET = 0x5402
        TIOCM_DTR = 0x002
        TIOCM_RTS = 0x004
        MAGIC = b'\x53\x59'
        VER = 0x03
        TIMEOUT = 0.8
        ROUNDS = 5
        MARKER = '__ACM_RESP__='
        MISS = '__ACM_MISS__='


        def crc16(data):
            crc = 0xFFFF
            for value in data:
                crc ^= value
                for _ in range(8):
                    crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
            return crc


        def pick(buf, wanted):
            index = buf.find(MAGIC)
            while index >= 0 and len(buf) - index >= 12:
                frame = buf[index:]
                if frame[2] == VER and frame[3] in wanted and frame[9] == 0:
                    length = int.from_bytes(frame[7:9], 'little')
                    if len(frame) >= 12 + length and crc16(frame[:10 + length]) == int.from_bytes(frame[10 + length:12 + length], 'little'):
                        return frame[3], frame[10:10 + length]
                index = buf.find(MAGIC, index + 1)
            return None


        def ask(fd, wanted, request):
            for attempt in range(1, ROUNDS + 1):
                frame = bytearray(request)
                frame[5] = attempt & 0xFF
                frame[6] = (attempt >> 8) & 0xFF
                crc = crc16(bytes(frame[:10]))
                frame[10] = crc & 0xFF
                frame[11] = (crc >> 8) & 0xFF
                termios.tcflush(fd, termios.TCIFLUSH)
                os.write(fd, bytes(frame))
                deadline = time.time() + TIMEOUT
                buf = b''
                while time.time() < deadline:
                    ready, _, _ = select.select([fd], [], [], 0.05)
                    if ready:
                        try:
                            buf += os.read(fd, 4096)
                        except BlockingIOError:
                            continue
                    hit = pick(buf, wanted)
                    if hit is not None:
                        return hit
            return None


        def main(port, queries):
            fd = os.open(port, os.O_RDWR | os.O_NOCTTY | os.O_NONBLOCK)
            try:
                try:
                    fcntl.flock(fd, fcntl.LOCK_SH | fcntl.LOCK_NB)
                except OSError:
                    pass
                attrs = termios.tcgetattr(fd)
                attrs[0] = attrs[1] = attrs[3] = 0
                attrs[2] = termios.CS8 | termios.CLOCAL | termios.CREAD
                attrs[4] = attrs[5] = termios.B921600
                attrs[6][termios.VMIN] = attrs[6][termios.VTIME] = 0
                termios.tcsetattr(fd, termios.TCSANOW, attrs)
                fcntl.ioctl(fd, TIOCMSET, struct.pack('I', TIOCM_DTR | TIOCM_RTS))
                failed = 0
                for index, (wanted, request) in enumerate(queries):
                    hit = ask(fd, wanted, request)
                    if hit is None:
                        failed += 1
                        print(MISS + str(index))
                    else:
                        print(MARKER + '%d:%02X:' % (index, hit[0]) + hit[1].hex())
                return 1 if failed else 0
            finally:
                os.close(fd)


        args = sys.argv[1:]
        pairs = [(set(int(x, 16) for x in args[i].split(',')), bytes.fromhex(args[i + 1]))
                 for i in range(1, len(args), 2)]
        sys.exit(main(args[0], pairs))
        """;
}
