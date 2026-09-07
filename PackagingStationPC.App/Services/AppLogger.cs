using System.Globalization;

namespace PackagingStationPC.Services;

public sealed class AppLogger
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppLogger(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    public async Task WriteAsync(string level, string message)
    {
        await _gate.WaitAsync();
        try
        {
            var line = $"{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)} [{level}] {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(_path, line);
        }
        finally
        {
            _gate.Release();
        }
    }
}
