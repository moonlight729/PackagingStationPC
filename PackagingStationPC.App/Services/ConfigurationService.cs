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

    public static void Save(AppConfiguration configuration, string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var json = JsonSerializer.Serialize(configuration, new JsonSerializerOptions(JsonOptions)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        File.WriteAllText(path, json);
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

    public static string GetLogDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PackagingStationPC.slnx")))
            {
                var projectLogs = Path.Combine(directory.FullName, "logs");
                Directory.CreateDirectory(projectLogs);
                return projectLogs;
            }

            directory = directory.Parent;
        }

        var applicationLogs = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(applicationLogs);
        return applicationLogs;
    }
}
