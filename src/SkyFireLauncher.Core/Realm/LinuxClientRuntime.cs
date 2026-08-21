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

        return BuildProtonStartInfo(proton, exePath, workingDirectory, protonPrefixPath, is64BitClient);
    }

    public static ProcessStartInfo BuildProtonWineFallbackStartInfo(
        string protonInstallPath,
        string exePath,
        string workingDirectory,
        string? protonPrefixPath,
        bool is64BitClient)
    {
        var prefix = ResolvePrefix(protonPrefixPath);
        Directory.CreateDirectory(prefix);
        StopPrefixWineServer(prefix, protonInstallPath);
        RepairProtonCompatData(prefix);
        if (!TryEnsureProtonGraphicsStack(protonInstallPath, prefix))
        {
            throw new InvalidOperationException(
                BuildMissingDxvkMessage(protonInstallPath, umuAvailable: false));
        }

        return BuildDirectProtonWineStartInfo(
            protonInstallPath, exePath, workingDirectory, prefix, is64BitClient);
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
        // Direct wine/Proton wineboot can take a bit; keep this bounded so the
        // UI status can fail clearly instead of hanging forever.
        layer == LinuxCompatibilityLayer.Proton ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(8);

    private static ProcessStartInfo BuildWineStartInfo(string exePath, string workingDirectory, bool is64BitClient)
    {
        var wine = LinuxRemoteProcess.ResolveWineBinary(is64BitClient);
        var startInfo = new ProcessStartInfo
        {
            FileName = wine,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        AddClientArguments(startInfo, exePath);
        return startInfo;
    }

    private static ProcessStartInfo BuildProtonStartInfo(
        ProtonInstall proton,
        string exePath,
        string workingDirectory,
        string? protonPrefixPath,
        bool is64BitClient)
    {
        var prefix = ResolvePrefix(protonPrefixPath);
        Directory.CreateDirectory(prefix);

        var selected = PreferAttachableProton(proton);
        StopPrefixWineServer(prefix, selected.InstallPath);
        RepairProtonCompatData(prefix);

        // *-slr / pressure-vessel blocks ptrace and /proc/pid/maps. Prefer GE
        // with PROTON_NO_STEAM_RUNTIME so the host can attach for authnet patches.
        if (IsSteamRuntimeProton(selected.DisplayName) ||
            IsSteamRuntimeProton(selected.InstallPath))
        {
            selected = FindInstalledGeProton()
                       ?? DiscoverProtonInstalls().FirstOrDefault(p =>
                           !IsSteamRuntimeProton(p.DisplayName) &&
                           !IsSteamRuntimeProton(p.InstallPath))
                       ?? throw new InvalidOperationException(
                           "This Proton build uses Steam Linux Runtime (*-slr), which blocks " +
                           "in-memory patches. Install GE-Proton (ProtonUp-Qt or umu) and select it.");
            StopPrefixWineServer(prefix, selected.InstallPath);
            RepairProtonCompatData(prefix);
        }

        TryEnsureProtonGraphicsStack(selected.InstallPath, prefix);
        return BuildProtonScriptStartInfo(selected, exePath, workingDirectory, prefix);
    }

    private static ProcessStartInfo BuildProtonScriptStartInfo(
        ProtonInstall proton,
        string exePath,
        string workingDirectory,
        string prefix)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = proton.ProtonScriptPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        AddClientArguments(startInfo, exePath);
        ApplyProtonEnvironment(startInfo, proton.InstallPath, prefix, workingDirectory);
        // Host namespace required for /proc/pid/maps + ptrace (authnet live patches).
        // GE 11's Steam runtime/pressure-vessel makes maps return EACCES from the host.
        startInfo.Environment["PROTON_NO_STEAM_RUNTIME"] = "1";
        startInfo.Environment["STEAM_RUNTIME"] = "0";
        startInfo.Environment["UMU_NO_RUNTIME"] = "1";
        startInfo.Environment["PROTONFIXES_DISABLE"] = "1";
        startInfo.Environment["PROTON_FSR4_UPGRADE"] = "0";
        startInfo.Environment["PROTON_FSR4_RDNA3_UPGRADE"] = "0";
        startInfo.Environment["PROTON_DLSS_UPGRADE"] = "0";
        // Help DXVK find the host Vulkan driver without the Steam runtime container.
        EnsureHostVulkanIcd(startInfo);
        ApplyProtonHostLibraryPath(startInfo, proton.InstallPath);
        return startInfo;
    }

    private static ProcessStartInfo BuildDirectProtonWineStartInfo(
        string protonInstallPath,
        string exePath,
        string workingDirectory,
        string prefix,
        bool is64BitClient)
    {
        var wine = FindProtonWineBinary(protonInstallPath, is64BitClient)
            ?? throw new InvalidOperationException(
                "Proton was found, but its wine binary is missing. Try GE-Proton or Wine.");

        var startInfo = new ProcessStartInfo
        {
            FileName = wine,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        AddClientArguments(startInfo, exePath);
        startInfo.Environment["WINEPREFIX"] = Path.Combine(prefix, "pfx");
        ApplyProtonEnvironment(startInfo, protonInstallPath, prefix, workingDirectory);
        ApplyProtonHostLibraryPath(startInfo, protonInstallPath);
        return startInfo;
    }

    /// <summary>
    /// Match a known-good manual Proton launch (e.g. launch-wow.sh): exe + -console.
    /// </summary>
    private static void AddClientArguments(ProcessStartInfo startInfo, string exePath)
    {
        startInfo.ArgumentList.Add(exePath);
        startInfo.ArgumentList.Add("-console");
    }

    private static void ApplyProtonHostLibraryPath(ProcessStartInfo startInfo, string protonInstallPath)
    {
        // Without the `proton` wrapper we must point the dynamic linker at Proton's
        // own libs or wine may load mismatched system libraries.
        var dirs = new List<string>();
        foreach (var rel in new[]
                 {
                     "files/lib64",
                     "files/lib",
                     "files/lib64/wine/x86_64-unix",
                     "files/lib/wine/i386-unix",
                     "dist/lib64",
                     "dist/lib",
                 })
        {
            var full = Path.Combine(protonInstallPath, rel);
            if (Directory.Exists(full))
                dirs.Add(full);
        }

        if (dirs.Count == 0)
            return;

        var joined = string.Join(':', dirs);
        if (startInfo.Environment.TryGetValue("LD_LIBRARY_PATH", out var existing) &&
            !string.IsNullOrWhiteSpace(existing))
            startInfo.Environment["LD_LIBRARY_PATH"] = joined + ":" + existing;
        else
            startInfo.Environment["LD_LIBRARY_PATH"] = joined;

        var dllPath = string.Join(':', dirs.Select(d => Path.Combine(d, "wine")).Where(Directory.Exists));
        if (!string.IsNullOrEmpty(dllPath))
            startInfo.Environment["WINEDLLPATH"] = dllPath;
    }

    private static ProtonInstall PreferAttachableProton(ProtonInstall selected)
    {
        // *-slr builds always enter pressure-vessel; prefer GE/native for ptrace.
        if (!IsSteamRuntimeProton(selected.DisplayName) &&
            !IsSteamRuntimeProton(selected.InstallPath))
            return selected;

        return FindInstalledGeProton()
               ?? DiscoverProtonInstalls().FirstOrDefault(p => !IsSteamRuntimeProton(p.DisplayName))
               ?? selected;
    }

    private static bool IsSteamRuntimeProton(string nameOrPath) =>
        nameOrPath.Contains("-slr", StringComparison.OrdinalIgnoreCase) ||
        nameOrPath.Contains("_slr", StringComparison.OrdinalIgnoreCase) ||
        nameOrPath.Contains("SteamLinuxRuntime", StringComparison.OrdinalIgnoreCase);

    private static ProtonInstall? FindInstalledGeProton()
    {
        return DiscoverProtonInstalls()
            .FirstOrDefault(p =>
                p.DisplayName.StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase) ||
                p.DisplayName.StartsWith("Proton-GE", StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyProtonEnvironment(
        ProcessStartInfo startInfo,
        string protonInstallPath,
        string prefix,
        string workingDirectory)
    {
        var winePrefix = Path.Combine(prefix, "pfx");
        startInfo.Environment["STEAM_COMPAT_DATA_PATH"] = prefix;
        startInfo.Environment["WINEPREFIX"] = winePrefix;
        startInfo.Environment["PROTONPATH"] = protonInstallPath;
        // Same bare-Proton env as a manual launch-wow.sh (outside Steam).
        startInfo.Environment["SteamAppId"] = "0";
        startInfo.Environment["SteamGameId"] = "0";
        // Wow 5.4.8 is D3D9; force DXVK instead of wined3d/vkd3d.
        startInfo.Environment["PROTON_USE_WINED3D"] = "0";
        // Match Proton's default sync so a leftover wineserver from a prior
        // launch does not reject children with WINEFSYNC mismatches.
        startInfo.Environment["WINEFSYNC"] = "1";
        startInfo.Environment["WINEESYNC"] = "1";
        MergeWineDllOverrides(startInfo, "d3d9,d3d11,d3d10core,dxgi=n");

        var steamRoot = FindSteamRoot() ?? EnsureSteamClientStub(prefix);
        startInfo.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = steamRoot;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.Environment["STEAM_COMPAT_INSTALL_PATH"] = Path.GetFullPath(workingDirectory);

        var logPath = Path.Combine(prefix, "skyfire-launch.log");
        if (!startInfo.Environment.TryGetValue("WINEDEBUG", out var existing) ||
            string.IsNullOrWhiteSpace(existing))
        {
            startInfo.Environment["WINEDEBUG"] =
                Environment.GetEnvironmentVariable("SKYFIRE_WINEDEBUG") ?? "+err";
        }

        // Always capture err logs; keep channels narrow so pipes do not stall.
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.Environment["SKYFIRE_LAUNCH_LOG"] = logPath;
        startInfo.CreateNoWindow = true;

        // Make sure the game can open a window on the same session as the launcher.
        CopyEnvIfPresent(startInfo, "DISPLAY");
        CopyEnvIfPresent(startInfo, "WAYLAND_DISPLAY");
        CopyEnvIfPresent(startInfo, "XAUTHORITY");
        CopyEnvIfPresent(startInfo, "XDG_RUNTIME_DIR");
        CopyEnvIfPresent(startInfo, "XDG_SESSION_TYPE");
        CopyEnvIfPresent(startInfo, "DBUS_SESSION_BUS_ADDRESS");
        CopyEnvIfPresent(startInfo, "VK_ICD_FILENAMES");
        CopyEnvIfPresent(startInfo, "VK_DRIVER_FILES");
        CopyEnvIfPresent(startInfo, "LIBVA_DRIVER_NAME");
        CopyEnvIfPresent(startInfo, "AMD_VULKAN_ICD");
        CopyEnvIfPresent(startInfo, "__GLX_VENDOR_LIBRARY_NAME");
        CopyEnvIfPresent(startInfo, "XDG_CURRENT_DESKTOP");

        TryAddSteamCompatMounts(startInfo, workingDirectory);
    }

    private static void CopyEnvIfPresent(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
            startInfo.Environment[name] = value;
    }

    /// <summary>
    /// When Steam runtime is disabled, DXVK needs a host Vulkan ICD or the client
    /// stays alive with no window.
    /// </summary>
    private static void EnsureHostVulkanIcd(ProcessStartInfo startInfo)
    {
        if (startInfo.Environment.TryGetValue("VK_ICD_FILENAMES", out var existing) &&
            !string.IsNullOrWhiteSpace(existing))
            return;

        string[] candidates =
        [
            "/usr/share/vulkan/icd.d/radeon_icd.x86_64.json",
            "/usr/share/vulkan/icd.d/amd_icd64.json",
            "/usr/share/vulkan/icd.d/nvidia_icd.json",
            "/usr/share/vulkan/icd.d/nvidia_icd.x86_64.json",
            "/usr/share/vulkan/icd.d/intel_icd.x86_64.json",
            "/usr/share/vulkan/icd.d/lvp_icd.x86_64.json",
            "/etc/vulkan/icd.d/nvidia_icd.json",
        ];

        var found = candidates.Where(File.Exists).ToArray();
        if (found.Length > 0)
            startInfo.Environment["VK_ICD_FILENAMES"] = string.Join(':', found);
    }

    private static void TryAddSteamCompatMounts(ProcessStartInfo startInfo, string? installPath)
    {
        try
        {
            var mounts = new HashSet<string>(StringComparer.Ordinal);
            if (startInfo.Environment.TryGetValue("STEAM_COMPAT_MOUNTS", out var existing) &&
                !string.IsNullOrWhiteSpace(existing))
            {
                foreach (var part in existing.Split(':', StringSplitOptions.RemoveEmptyEntries))
                    mounts.Add(part);
            }

            if (!string.IsNullOrWhiteSpace(installPath))
            {
                var full = Path.GetFullPath(installPath);
                mounts.Add(full);
                var parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar, '/'));
                if (!string.IsNullOrWhiteSpace(parent))
                    mounts.Add(parent);
            }

            // WorkingDirectory is the client folder for our launches.
            if (!string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
            {
                var client = Path.GetFullPath(startInfo.WorkingDirectory);
                mounts.Add(client);
                var parent = Path.GetDirectoryName(client.TrimEnd(Path.DirectorySeparatorChar, '/'));
                if (!string.IsNullOrWhiteSpace(parent))
                    mounts.Add(parent);
            }

            if (mounts.Count > 0)
                startInfo.Environment["STEAM_COMPAT_MOUNTS"] = string.Join(':', mounts);
        }
        catch
        {
            // Optional.
        }
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

    /// <summary>
    /// GE/Valve Proton crashes if a Wine-created prefix exists without Proton's
    /// tracked_files bookkeeping (FileNotFoundError in update_builtin_libs).
    /// Reset that broken compatdata so the next <c>proton run</c> can copy_pfx.
    /// </summary>
    public static void RepairProtonCompatData(string compatDataPath)
    {
        var trackedFiles = Path.Combine(compatDataPath, "tracked_files");
        var pfx = Path.Combine(compatDataPath, "pfx");
        var userReg = Path.Combine(pfx, "user.reg");

        var hasWinePrefix = File.Exists(userReg) || Directory.Exists(Path.Combine(pfx, "drive_c"));
        if (!hasWinePrefix || File.Exists(trackedFiles))
            return;

        try
        {
            if (Directory.Exists(compatDataPath))
                Directory.Delete(compatDataPath, recursive: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Proton prefix is incomplete (missing tracked_files) and could not be reset automatically. " +
                $"Delete it manually: rm -rf '{compatDataPath}' ({ex.Message})");
        }

        Directory.CreateDirectory(compatDataPath);
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
        // SLR builds run inside pressure-vessel and cannot be ptraced from the host.
        if (IsSteamRuntimeProton(name) || IsSteamRuntimeProton(install.InstallPath))
            return 20;
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
