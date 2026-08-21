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
        StopPrefixWineServer(prefix, protonInstallPath);
        if (!TryEnsureProtonGraphicsStack(protonInstallPath, prefix))
        {
            throw new InvalidOperationException(
                BuildMissingDxvkMessage(protonInstallPath, umuAvailable: false));
        }

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
    /// Copies DXVK (and libvkd3d fallbacks) into the Proton prefix from the Proton
    /// tree or from a system DXVK package (Arch/CachyOS dxvk-mingw-git).
    /// </summary>
    /// <returns>True if at least 64-bit d3d9.dll was installed into the prefix.</returns>
    public static bool TryEnsureProtonGraphicsStack(string? protonInstallPath, string compatDataPath)
    {
        var pfx = Path.Combine(compatDataPath, "pfx");
        var system32 = Path.Combine(pfx, "drive_c", "windows", "system32");
        var syswow64 = Path.Combine(pfx, "drive_c", "windows", "syswow64");
        Directory.CreateDirectory(system32);
        Directory.CreateDirectory(syswow64);

        string[] dxvkNames = ["d3d9.dll", "d3d11.dll", "d3d10core.dll", "dxgi.dll"];
        var installedD3d9 = false;
        foreach (var name in dxvkNames)
        {
            if (TryInstallDxvkDll(protonInstallPath, name, system32, sixtyFourBit: true))
            {
                if (name.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase))
                    installedD3d9 = true;
            }

            TryInstallDxvkDll(protonInstallPath, name, syswow64, sixtyFourBit: false);
        }

        foreach (var name in new[]
                 {
                     "libvkd3d-1.dll",
                     "libvkd3d-shader-1.dll",
                     "libvkd3d-utils-1.dll",
                 })
        {
            TryInstallDxvkDll(protonInstallPath, name, system32, sixtyFourBit: true);
            TryInstallDxvkDll(protonInstallPath, name, syswow64, sixtyFourBit: false);
        }

        return installedD3d9;
    }

    private static string BuildMissingDxvkMessage(string? protonInstallPath, bool umuAvailable)
    {
        var path = string.IsNullOrWhiteSpace(protonInstallPath) ? "(none)" : protonInstallPath;
        return
            $"No DXVK d3d9.dll found for Proton at '{path}'. " +
            "On Arch/CachyOS install system DXVK (`sudo pacman -S dxvk-mingw-git`), " +
            "or install GE-Proton / Steam Proton 9+ and select it in Configuration" +
            (umuAvailable
                ? " (umu can also auto-download GE-Proton on the next launch)."
                : ".") +
            " Then: rm -rf ~/.local/share/SkyFireLauncher/proton";
    }

    public static TimeSpan ReadyTimeout(LinuxCompatibilityLayer layer) =>
        // First Proton/umu launch can wineboot + (with GE-Proton) download a full build.
        layer == LinuxCompatibilityLayer.Proton ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(8);

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
        StopPrefixWineServer(prefix, proton.InstallPath);

        var umu = FindOnPath("umu-run") ?? FindOnPath("umu");
        var hasDxvk = TryEnsureProtonGraphicsStack(proton.InstallPath, prefix);

        // proton-cachyos-native (and similar) may ship no host-side DXVK. Prefer
        // umu's GE-Proton codename so a complete Proton+DXVK tree is downloaded.
        var protonPath = proton.InstallPath;
        var useGeProtonDownload = false;
        if (!hasDxvk)
        {
            if (umu is null)
                throw new InvalidOperationException(BuildMissingDxvkMessage(proton.InstallPath, umuAvailable: false));

            protonPath = "GE-Proton";
            useGeProtonDownload = true;
        }

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };

        if (umu is not null)
        {
            startInfo.FileName = umu;
            startInfo.ArgumentList.Add(exePath);
            startInfo.Environment["PROTONPATH"] = protonPath;
            // Generic non-Steam ID so umu still installs DXVK/vkd3d into the prefix.
            startInfo.Environment["GAMEID"] = "0";
        }
        else
        {
            startInfo.FileName = proton.ProtonScriptPath;
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add(exePath);
        }

        ApplyProtonEnvironment(startInfo, useGeProtonDownload ? proton.InstallPath : protonPath, prefix);
        if (useGeProtonDownload)
            startInfo.Environment["PROTONPATH"] = "GE-Proton";

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
        // Match Proton's default sync so a leftover wineserver from a prior
        // launch does not reject children with WINEFSYNC mismatches.
        startInfo.Environment["WINEFSYNC"] = "1";
        startInfo.Environment["WINEESYNC"] = "1";
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

            var gate = new object();
            StreamWriter? writer = new StreamWriter(
                new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };

            void WriteLine(string? line)
            {
                if (line is null)
                    return;

                lock (gate)
                {
                    try
                    {
                        writer?.WriteLine(line);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Process exited and drained remaining output after dispose.
                    }
                    catch (IOException)
                    {
                        // Best-effort logging only.
                    }
                }
            }

            void CloseWriter()
            {
                lock (gate)
                {
                    try { writer?.Dispose(); } catch { /* ignore */ }
                    writer = null;
                }
            }

            hostProcess.OutputDataReceived += (_, e) => WriteLine(e.Data);
            hostProcess.ErrorDataReceived += (_, e) => WriteLine(e.Data);
            hostProcess.EnableRaisingEvents = true;
            hostProcess.Exited += (_, _) =>
            {
                // Let AsyncStreamReader finish flushing before closing the file.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(250);
                    CloseWriter();
                });
            };
            hostProcess.BeginOutputReadLine();
            hostProcess.BeginErrorReadLine();
        }
        catch
        {
            // Logging is best-effort; launching still proceeds.
        }
    }

    private static void StopPrefixWineServer(string compatDataPath, string? protonInstallPath = null)
    {
        // A crashed prior launch can leave wineserver running with different
        // WINEFSYNC/WINEESYNC flags; new children then abort immediately.
        var winePrefix = Path.Combine(compatDataPath, "pfx");
        if (!Directory.Exists(winePrefix))
            return;

        try
        {
            var wineserver = FindProtonWineServer(protonInstallPath) ?? FindOnPath("wineserver");
            if (wineserver is null)
                return;

            var startInfo = new ProcessStartInfo
            {
                FileName = wineserver,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-k");
            startInfo.Environment["WINEPREFIX"] = winePrefix;

            using var process = Process.Start(startInfo);
            process?.WaitForExit(3000);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static string? FindProtonWineServer(string? protonInstallPath)
    {
        if (string.IsNullOrWhiteSpace(protonInstallPath) || protonInstallPath is "GE-Proton")
            return null;

        foreach (var rel in new[] { "files/bin/wineserver", "dist/bin/wineserver" })
        {
            var candidate = Path.Combine(protonInstallPath, rel);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
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

    private static bool TryInstallDxvkDll(
        string? protonInstallPath,
        string fileName,
        string destinationDirectory,
        bool sixtyFourBit)
    {
        var source = FindDxvkDll(protonInstallPath, fileName, sixtyFourBit);
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

    private static string? FindDxvkDll(string? protonInstallPath, string fileName, bool sixtyFourBit)
    {
        if (!string.IsNullOrWhiteSpace(protonInstallPath))
        {
            var fromProton = FindDllUnderProton(protonInstallPath, fileName, sixtyFourBit);
            if (fromProton is not null)
                return fromProton;
        }

        return FindSystemDxvkDll(fileName, sixtyFourBit);
    }

    private static string? FindSystemDxvkDll(string fileName, bool sixtyFourBit)
    {
        // Arch/CachyOS dxvk-mingw-git, dxvk-bin, and similar layouts.
        var candidates = sixtyFourBit
            ? new[]
            {
                Path.Combine("/usr/lib/dxvk/win64", fileName),
                Path.Combine("/usr/lib/dxvk/x64", fileName),
                Path.Combine("/usr/share/dxvk/x64", fileName),
                Path.Combine("/usr/share/dxvk/win64", fileName),
                Path.Combine("/usr/lib64/dxvk", fileName),
            }
            : new[]
            {
                Path.Combine("/usr/lib/dxvk/win32", fileName),
                Path.Combine("/usr/lib/dxvk/x32", fileName),
                Path.Combine("/usr/share/dxvk/x32", fileName),
                Path.Combine("/usr/share/dxvk/win32", fileName),
                Path.Combine("/usr/lib32/dxvk", fileName),
            };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        foreach (var root in new[] { "/usr/lib/dxvk", "/usr/share/dxvk", "/usr/lib64/dxvk", "/usr/lib32/dxvk" })
        {
            if (!Directory.Exists(root))
                continue;

            try
            {
                foreach (var path in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
                {
                    var normalized = path.Replace('\\', '/');
                    if (sixtyFourBit)
                    {
                        if (normalized.Contains("/win32/", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("/x32/", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("/i386", StringComparison.OrdinalIgnoreCase))
                            continue;
                        return path;
                    }

                    if (normalized.Contains("/win64/", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Contains("/x64/", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Contains("/x86_64", StringComparison.OrdinalIgnoreCase))
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

    private static string? FindDllUnderProton(string protonInstallPath, string fileName, bool sixtyFourBit)
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
