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
        EnsureProtonGraphicsStack(protonInstallPath, prefix);

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

    /// <summary>
    /// Copies DXVK (and libvkd3d fallbacks) into the Proton prefix. Newer Proton
    /// builds keep DLLs under wine/dxvk/x86_64-windows/; if prefix setup skips
    /// that step, Wine's d3d9.dll loads instead and dies on missing libvkd3d.
    /// </summary>
    public static void EnsureProtonGraphicsStack(string protonInstallPath, string compatDataPath)
    {
        var pfx = Path.Combine(compatDataPath, "pfx");
        var system32 = Path.Combine(pfx, "drive_c", "windows", "system32");
        var syswow64 = Path.Combine(pfx, "drive_c", "windows", "syswow64");
        Directory.CreateDirectory(system32);
        Directory.CreateDirectory(syswow64);

        string[] dxvkNames = ["d3d9.dll", "d3d11.dll", "d3d10core.dll", "dxgi.dll"];
        var copiedDxvk = 0;
        foreach (var name in dxvkNames)
        {
            if (TryInstallProtonDll(protonInstallPath, name, system32, sixtyFourBit: true))
                copiedDxvk++;
            TryInstallProtonDll(protonInstallPath, name, syswow64, sixtyFourBit: false);
        }

        foreach (var name in new[]
                 {
                     "libvkd3d-1.dll",
                     "libvkd3d-shader-1.dll",
                     "libvkd3d-utils-1.dll",
                 })
        {
            TryInstallProtonDll(protonInstallPath, name, system32, sixtyFourBit: true);
            TryInstallProtonDll(protonInstallPath, name, syswow64, sixtyFourBit: false);
        }

        if (copiedDxvk == 0)
        {
            throw new InvalidOperationException(
                $"Proton at '{protonInstallPath}' has no DXVK d3d9.dll. Pick GE-Proton or Steam Proton 9+, " +
                "or delete the prefix after installing one: rm -rf ~/.local/share/SkyFireLauncher/proton");
        }
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
        EnsureProtonGraphicsStack(proton.InstallPath, prefix);

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
        var winePrefix = Path.Combine(prefix, "pfx");
        startInfo.Environment["STEAM_COMPAT_DATA_PATH"] = prefix;
        startInfo.Environment["WINEPREFIX"] = winePrefix;
        startInfo.Environment["PROTONPATH"] = protonInstallPath;
        // Wow 5.4.8 is D3D9; force DXVK instead of wined3d/vkd3d.
        startInfo.Environment["PROTON_USE_WINED3D"] = "0";
        MergeWineDllOverrides(startInfo, "d3d9,d3d11,d3d10core,dxgi=n");

        var steamRoot = FindSteamRoot() ?? EnsureSteamClientStub(prefix);
        startInfo.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = steamRoot;

        var logPath = Path.Combine(prefix, "skyfire-launch.log");
        startInfo.Environment["WINEDEBUG"] = startInfo.Environment.TryGetValue("WINEDEBUG", out var existing) &&
                                             !string.IsNullOrWhiteSpace(existing)
            ? existing
            : "+err,+module";
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        // Consumers must call AttachLaunchLog after Process.Start.
        startInfo.Environment["SKYFIRE_LAUNCH_LOG"] = logPath;
    }

    public static void AttachLaunchLog(Process hostProcess, ProcessStartInfo startInfo)
    {
        if (!startInfo.RedirectStandardError && !startInfo.RedirectStandardOutput)
            return;

        if (!startInfo.Environment.TryGetValue("SKYFIRE_LAUNCH_LOG", out var logPath) ||
            string.IsNullOrWhiteSpace(logPath))
            return;

        try
        {
            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var writer = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };
            hostProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    lock (writer) writer.WriteLine(e.Data);
            };
            hostProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    lock (writer) writer.WriteLine(e.Data);
            };
            hostProcess.BeginOutputReadLine();
            hostProcess.BeginErrorReadLine();
            hostProcess.Exited += (_, _) =>
            {
                try { lock (writer) writer.Dispose(); } catch { /* ignore */ }
            };
            hostProcess.EnableRaisingEvents = true;
        }
        catch
        {
            // Logging is best-effort; launching still proceeds.
        }
    }

    private static void MergeWineDllOverrides(ProcessStartInfo startInfo, string addition)
    {
        if (startInfo.Environment.TryGetValue("WINEDLLOVERRIDES", out var existing) &&
            !string.IsNullOrWhiteSpace(existing))
        {
            startInfo.Environment["WINEDLLOVERRIDES"] = existing.TrimEnd(';') + ";" + addition;
        }
        else
        {
            startInfo.Environment["WINEDLLOVERRIDES"] = addition;
        }
    }

    private static string EnsureSteamClientStub(string compatDataPath)
    {
        // Proton's setup_prefix requires STEAM_COMPAT_CLIENT_INSTALL_PATH and
        // reads legacycompat optionally. A stub keeps prefix setup from aborting.
        var stub = Path.Combine(compatDataPath, "steam-client-stub");
        Directory.CreateDirectory(Path.Combine(stub, "legacycompat"));
        return stub;
    }

    private static bool TryInstallProtonDll(
        string protonInstallPath,
        string fileName,
        string destinationDirectory,
        bool sixtyFourBit)
    {
        var source = FindProtonDll(protonInstallPath, fileName, sixtyFourBit);
        if (source is null)
            return false;

        var destination = Path.Combine(destinationDirectory, fileName);
        try
        {
            File.Copy(source, destination, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindProtonDll(string protonInstallPath, string fileName, bool sixtyFourBit)
    {
        var relative = sixtyFourBit
            ? new[]
            {
                Path.Combine("files", "lib64", "wine", "dxvk", "x86_64-windows", fileName),
                Path.Combine("files", "lib", "wine", "dxvk", "x86_64-windows", fileName),
                Path.Combine("files", "lib64", "wine", "dxvk", fileName),
                Path.Combine("files", "lib", "wine", "dxvk", fileName),
                Path.Combine("files", "lib64", "vkd3d", fileName),
                Path.Combine("files", "lib", "vkd3d", fileName),
                Path.Combine("files", "lib64", "wine", "x86_64-windows", fileName),
                Path.Combine("files", "lib", "wine", "x86_64-windows", fileName),
                Path.Combine("files", "share", "default_pfx", "drive_c", "windows", "system32", fileName),
                Path.Combine("dist", "lib64", "wine", "dxvk", fileName),
                Path.Combine("dist", "lib", "wine", "dxvk", "x86_64-windows", fileName),
            }
            : new[]
            {
                Path.Combine("files", "lib", "wine", "dxvk", "i386-windows", fileName),
                Path.Combine("files", "lib64", "wine", "dxvk", "i386-windows", fileName),
                Path.Combine("files", "lib", "wine", "dxvk", fileName),
                Path.Combine("files", "lib", "vkd3d", fileName),
                Path.Combine("files", "lib64", "vkd3d", fileName),
                Path.Combine("files", "lib", "wine", "i386-windows", fileName),
                Path.Combine("files", "share", "default_pfx", "drive_c", "windows", "syswow64", fileName),
                Path.Combine("dist", "lib", "wine", "dxvk", fileName),
                Path.Combine("dist", "lib", "wine", "dxvk", "i386-windows", fileName),
            };

        foreach (var rel in relative)
        {
            var candidate = Path.Combine(protonInstallPath, rel);
            if (File.Exists(candidate))
                return candidate;
        }

        // Last resort for odd Proton layouts (search wine/dxvk and vkd3d trees).
        foreach (var rootName in new[] { "files", "dist" })
        {
            var root = Path.Combine(protonInstallPath, rootName);
            if (!Directory.Exists(root))
                continue;

            try
            {
                foreach (var path in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
                {
                    var normalized = path.Replace('\\', '/');
                    var inDxvk = normalized.Contains("/dxvk/", StringComparison.OrdinalIgnoreCase);
                    var inVkd3d = normalized.Contains("/vkd3d", StringComparison.OrdinalIgnoreCase);
                    if (!inDxvk && !inVkd3d)
                        continue;

                    if (sixtyFourBit)
                    {
                        if (normalized.Contains("/i386-windows/", StringComparison.OrdinalIgnoreCase))
                            continue;
                        return path;
                    }

                    if (normalized.Contains("/x86_64-windows/", StringComparison.OrdinalIgnoreCase))
                        continue;
                    return path;
                }
            }
            catch
            {
                // Ignore unreadable trees.
            }
        }

        return null;
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
            "/usr/share/steam/compatibilitytools.d",
            "/usr/share/proton",
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
        // wine-cachyos / experimental Wine builds often ship nested DXVK paths and
        // create bare prefixes when used outside Steam; prefer Valve/GE first.
        if (name.Contains("cachyos", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("wine-", StringComparison.OrdinalIgnoreCase))
            return 8;
        if (name.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Proton-GE", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.Contains("Experimental", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (name.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase))
            return 3;
        return 5;
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
