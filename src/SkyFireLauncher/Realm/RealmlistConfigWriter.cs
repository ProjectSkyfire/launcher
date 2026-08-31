using System.IO;

namespace SkyFireLauncher.Realm;

public static class RealmlistConfigWriter
{
    public static void SetRealmlist(string clientLocation, string realmlist) =>
        SetCvar(clientLocation, "realmlist", realmlist);

    // realmListbn is the client's own CVar for authnet, separate from the
    // classic realmlist CVar. Its compiled-in default is empty, so it has to
    // come from Config.wtf the same way realmlist already does.
    public static void SetRealmlistBn(string clientLocation, string address) =>
        SetCvar(clientLocation, "realmListbn", address);

    private static void SetCvar(string clientLocation, string cvarName, string value)
    {
        var wtfDirectory = Path.Combine(clientLocation, "WTF");
        Directory.CreateDirectory(wtfDirectory);

        var configPath = Path.Combine(wtfDirectory, "Config.wtf");
        var line = $"SET {cvarName} \"{value}\"";

        var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : [];
        var index = lines.FindIndex(l => l.TrimStart().StartsWith($"SET {cvarName} ", StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            if (lines[index] == line)
                return;

            lines[index] = line;
        }
        else
        {
            lines.Insert(0, line);
        }

        File.WriteAllLines(configPath, lines);
    }
}
