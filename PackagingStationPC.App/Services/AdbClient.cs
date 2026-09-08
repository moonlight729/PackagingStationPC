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

    private static AdbDeviceInfo? ParseDeviceLine(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
        {
            return null;
        }

        return new AdbDeviceInfo(tokens[0], tokens[1], string.Join(' ', tokens.Skip(2)));
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
}
