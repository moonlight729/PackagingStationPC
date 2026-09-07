using System.Text.Json;
using PackagingStationPC.Models;

namespace PackagingStationPC.Services;

public static class ConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppConfiguration Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new AppConfiguration();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions) ?? new AppConfiguration();
    }

    public static string GetDataDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OriginFlow",
            "PackagingStationPC");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
