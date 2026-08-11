using System.IO;

namespace SkyFireLauncher.Realm;

public static class RealmlistConfigWriter
{
    public static void SetRealmlist(string clientLocation, string realmlist)
    {
        var wtfDirectory = Path.Combine(clientLocation, "WTF");
        Directory.CreateDirectory(wtfDirectory);

        var configPath = Path.Combine(wtfDirectory, "Config.wtf");
        var line = $"SET realmlist \"{realmlist}\"";

        var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : [];
        var index = lines.FindIndex(l => l.TrimStart().StartsWith("SET realmlist ", StringComparison.OrdinalIgnoreCase));

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
