using System.Diagnostics;
using SkyFireLauncher.Configuration;

namespace SkyFireLauncher.Realm;

public sealed class ProtonInstall
{
    public required string DisplayName { get; init; }
    public required string InstallPath { get; init; }

    public string ProtonScriptPath => Path.Combine(InstallPath, "proton");

    public override string ToString() => DisplayName;
}

public static class LinuxClientRuntime
{
    public static string DefaultProtonPrefixPath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "SkyFireLauncher", "proton");
        }
    }

    public static IReadOnlyList<ProtonInstall> DiscoverProtonInstalls()
    {
        var found = new Dictionary<string, ProtonInstall>(StringComparer.Ordinal);

        foreach (var dir in EnumerateCandidateProtonDirectories())
        {
            if (!IsProtonInstall(dir))
                continue;

            var full = Path.GetFullPath(dir);
            found.TryAdd(full, new ProtonInstall
            {
                DisplayName = Path.GetFileName(full),
                InstallPath = full
            });
        }

        return found.Values
            .OrderBy(Rank)
            .ThenByDescending(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ProtonInstall? ResolveProtonInstall(string? preferredPath)
    {
        var installs = DiscoverProtonInstalls();
        if (installs.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var match = installs.FirstOrDefault(p =>
                p.InstallPath.Equals(preferredPath.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;

            if (IsProtonInstall(preferredPath.Trim()))
            {
                return new ProtonInstall
                {
                    DisplayName = Path.GetFileName(Path.GetFullPath(preferredPath.Trim())),
                    InstallPath = Path.GetFullPath(preferredPath.Trim())
                };
            }
        }

        return installs[0];
    }

    public static ProcessStartInfo BuildStartInfo(
        LinuxCompatibilityLayer layer,
        string exePath,
        string workingDirectory,
        bool is64BitClient,
        string? protonInstallPath,
        string? protonPrefixPath)
    {
        if (layer == LinuxCompatibilityLayer.Wine)
            return BuildWineStartInfo(exePath, workingDirectory, is64BitClient);

        var proton = ResolveProtonInstall(protonInstallPath)
            ?? throw new InvalidOperationException(
                "No Proton install was found. Install Steam Proton or Proton-GE, or switch Compatibility to Wine.");

        return BuildProtonStartInfo(proton, exePath, workingDirectory, protonPrefixPath);
    }

    public static ProcessStartInfo BuildProtonWineFallbackStartInfo(
        string protonInstallPath,
        string exePath,
        string workingDirectory,
        string? protonPrefixPath,
        bool is64BitClient)
    {
        var wine = FindProtonWineBinary(protonInstallPath, is64BitClient)
            ?? throw new InvalidOperationException(
                "Proton was found, but its wine binary is missing. Try a different Proton version or Wine.");

        var prefix = ResolvePrefix(protonPrefixPath);
        Directory.CreateDirectory(prefix);

        var startInfo = new ProcessStartInfo
        {
            FileName = wine,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(exePath);
        startInfo.Environment["WINEPREFIX"] = Path.Combine(prefix, "pfx");
        ApplyProtonEnvironment(startInfo, protonInstallPath, prefix);
        return startInfo;
    }

    public static TimeSpan ReadyTimeout(LinuxCompatibilityLayer layer) =>
        layer == LinuxCompatibilityLayer.Proton ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(8);

    private static ProcessStartInfo BuildWineStartInfo(string exePath, string workingDirectory, bool is64BitClient)
    {
        var wine = LinuxRemoteProcess.ResolveWineBinary(is64BitClient);
        var startInfo = new ProcessStartInfo
        {
            FileName = wine,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(exePath);
        return startInfo;
    }

    private static ProcessStartInfo BuildProtonStartInfo(
        ProtonInstall proton,
        string exePath,
        string workingDirectory,
        string? protonPrefixPath)
    {
        var prefix = ResolvePrefix(protonPrefixPath);
        Directory.CreateDirectory(prefix);

        var umu = FindOnPath("umu-run") ?? FindOnPath("umu");
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };

        if (umu is not null)
        {
            startInfo.FileName = umu;
            startInfo.ArgumentList.Add(exePath);
            startInfo.Environment["PROTONPATH"] = proton.InstallPath;
            // Generic non-Steam ID so umu still installs DXVK/vkd3d into the prefix.
            startInfo.Environment["GAMEID"] = "0";
        }
        else
        {
            startInfo.FileName = proton.ProtonScriptPath;
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add(exePath);
        }

        ApplyProtonEnvironment(startInfo, proton.InstallPath, prefix);
        return startInfo;
    }

    private static void ApplyProtonEnvironment(ProcessStartInfo startInfo, string protonInstallPath, string prefix)
    {
        startInfo.Environment["STEAM_COMPAT_DATA_PATH"] = prefix;
        startInfo.Environment["PROTONPATH"] = protonInstallPath;
        // Wow 5.4.8 is D3D9; force DXVK instead of wined3d/vkd3d.
        startInfo.Environment["PROTON_USE_WINED3D"] = "0";

        var steamRoot = FindSteamRoot();
        if (steamRoot is not null)
            startInfo.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = steamRoot;
    }

    private static string ResolvePrefix(string? protonPrefixPath) =>
        string.IsNullOrWhiteSpace(protonPrefixPath) ? DefaultProtonPrefixPath : protonPrefixPath.Trim();

    private static string? FindProtonWineBinary(string installPath, bool is64BitClient)
    {
        string[] relative = is64BitClient
            ? ["files/bin/wine64", "files/bin/wine", "dist/bin/wine64", "dist/bin/wine"]
            : ["files/bin/wine", "dist/bin/wine", "files/bin/wine64", "dist/bin/wine64"];

        foreach (var rel in relative)
        {
            var candidate = Path.Combine(installPath, rel);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static bool IsProtonInstall(string directory)
    {
        try
        {
            return Directory.Exists(directory) && File.Exists(Path.Combine(directory, "proton"));
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateCandidateProtonDirectories()
    {
        foreach (var root in EnumerateSteamRoots())
        {
            yield return Path.Combine(root, "steamapps", "common");
            foreach (var child in SafeGetDirectories(Path.Combine(root, "steamapps", "common")))
                yield return child;

            foreach (var toolsDir in new[]
            {
                Path.Combine(root, "compatibilitytools.d"),
                Path.Combine(root, "steamapps", "compatibilitytools.d"),
            })
            {
                foreach (var child in SafeGetDirectories(toolsDir))
                    yield return child;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var toolsDir in new[]
        {
            Path.Combine(home, ".steam", "root", "compatibilitytools.d"),
            Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d"),
        })
        {
            foreach (var child in SafeGetDirectories(toolsDir))
                yield return child;
        }
    }

    private static IEnumerable<string> EnumerateSteamRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in new[]
        {
            Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH"),
            Environment.GetEnvironmentVariable("STEAM_DIR"),
            Environment.GetEnvironmentVariable("STEAM_ROOT"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".steam", "debian-installation"),
            Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
            "/usr/share/steam",
        })
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (!Directory.Exists(full) || !seen.Add(full))
                continue;

            yield return full;
        }
    }

    private static string? FindSteamRoot() => EnumerateSteamRoots().FirstOrDefault();

    private static IEnumerable<string> SafeGetDirectories(string path)
    {
        if (!Directory.Exists(path))
            return [];

        try
        {
            return Directory.GetDirectories(path);
        }
        catch
        {
            return [];
        }
    }

    private static int Rank(ProtonInstall install)
    {
        var name = install.DisplayName;
        if (name.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Proton-GE", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.Contains("Experimental", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (name.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 3;
    }

    internal static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
