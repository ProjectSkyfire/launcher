using System.IO;
using System.Text.Json;

namespace SkyFireLauncher.Configuration;

public class ConfigManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _configPath;

    public ConfigManager()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _configPath = Path.Combine(baseDir, "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(_configPath))
            return new AppConfig();

        try
        {
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch (Exception)
        {
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, SerializerOptions);
        File.WriteAllText(_configPath, json);
    }
}
