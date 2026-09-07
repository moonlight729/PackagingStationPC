using System.Reflection;
using System.Text;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

public sealed class AdbClient
{
    private readonly AdbConfiguration _configuration;
    private readonly IProcessRunner _processRunner;

    public AdbClient(AdbConfiguration configuration, IProcessRunner processRunner)
    {
        _configuration = configuration;
        _processRunner = processRunner;
    }

    public async Task<IReadOnlyList<AdbDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await _processRunner.RunAsync(
            _configuration.Path,
            ["devices", "-l"],
            TimeSpan.FromSeconds(_configuration.CommandTimeoutSeconds),
            cancellationToken);
        EnsureSuccess(result, "ADB 设备枚举");

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Select(ParseDeviceLine)
            .Where(device => device is not null)
            .Cast<AdbDeviceInfo>()
            .ToArray();
    }

    public async Task<AdbDeviceInfo> ResolveDeviceAsync(
        string? selectedSerial,
        CancellationToken cancellationToken = default)
    {
        var devices = await GetDevicesAsync(cancellationToken);
        var preferred = string.IsNullOrWhiteSpace(selectedSerial) ? _configuration.DeviceSerial : selectedSerial;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var selected = devices.FirstOrDefault(device =>
                string.Equals(device.Serial, preferred, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                throw new InvalidOperationException($"未找到指定 ADB 设备 {preferred}。");
            }

            if (!selected.IsOnline)
            {
                throw new InvalidOperationException($"ADB 设备 {preferred} 当前状态为 {selected.State}。");
            }

            return selected;
        }

        var online = devices.Where(device => device.IsOnline).ToArray();
        return online.Length switch
        {
            0 => throw new InvalidOperationException("未检测到在线 ADB 设备。"),
            1 => online[0],
            _ => throw new InvalidOperationException("检测到多台 ADB 设备，请先选择本工位设备。")
        };
    }

    public Task<ProcessResult> ShellAsync(
        string serial,
        string command,
        CancellationToken cancellationToken = default) =>
        ShellAsync(serial, command, TimeSpan.FromSeconds(_configuration.CommandTimeoutSeconds), cancellationToken);

    public Task<ProcessResult> ShellAsync(
        string serial,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        _processRunner.RunAsync(
            _configuration.Path,
            ["-s", serial, "shell", command],
            timeout,
            cancellationToken);

    public async Task<ProcessResult> RunEmbeddedPythonAsync(
        string serial,
        string resourceFileName,
        bool useSudo,
        CancellationToken cancellationToken = default)
    {
        var source = ReadEmbeddedResource(resourceFileName);
        var uniqueName = $"packaging-station-{Guid.NewGuid():N}.py";
        var localPath = Path.Combine(Path.GetTempPath(), uniqueName);
        var remotePath = $"/tmp/{uniqueName}";
        var interpreter = useSudo ? "sudo -n python3" : "python3";
        try
        {
            await File.WriteAllTextAsync(localPath, source, new UTF8Encoding(false), cancellationToken);
            var push = await _processRunner.RunAsync(
                _configuration.Path,
                ["-s", serial, "push", localPath, remotePath],
                TimeSpan.FromSeconds(_configuration.CommandTimeoutSeconds),
                cancellationToken);
            EnsureSuccess(push, $"上传临时查询程序 {resourceFileName}");

            return await ShellAsync(
                serial,
                $"{interpreter} {ShellQuote(remotePath)}",
                TimeSpan.FromSeconds(_configuration.PeripheralTimeoutSeconds),
                cancellationToken);
        }
        finally
        {
            TryDeleteLocalFile(localPath);
            try
            {
                await ShellAsync(
                    serial,
                    $"rm -f {ShellQuote(remotePath)}",
                    TimeSpan.FromSeconds(_configuration.CommandTimeoutSeconds),
                    CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    private static AdbDeviceInfo? ParseDeviceLine(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
        {
            return null;
        }

        var description = string.Join(' ', tokens.Skip(2));
        return new AdbDeviceInfo(tokens[0], tokens[1], description);
    }

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.Success)
        {
            return;
        }

        var reason = result.TimedOut
            ? "执行超时"
            : string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        throw new InvalidOperationException($"{operation}失败: {reason}");
    }

    private static string ReadEmbeddedResource(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"缺少内嵌设备查询资源 {fileName}。");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"无法打开内嵌设备查询资源 {fileName}。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\\"'\\\"'")}'";

    private static void TryDeleteLocalFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
