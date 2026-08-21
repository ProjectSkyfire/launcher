using System.IO;
using System.Text.Json;

namespace SkyFireLauncher.Configuration;

public class ConfigManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _configPath;

    public ConfigManager()
    {
        // Program Files (where the installer puts the exe) isn't writable by
        // standard users - keep the config in the per-user app data folder instead.
        // On Linux this is $XDG_CONFIG_HOME or ~/.config.
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkyFireLauncher");
        Directory.CreateDirectory(appDataDir);
        _configPath = Path.Combine(appDataDir, "config.json");
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
