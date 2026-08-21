using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SkyFireLauncher.Configuration;

namespace SkyFireLauncher.Realm;

// Launches the client, then immediately patches its hardcoded region logon
// hostnames directly in process memory. Nothing on disk (the exe, the hosts
// file) is ever touched - the patch only exists in the running process, for
// its lifetime.
public static class ClientProcessLauncher
{
    public static int LaunchAndRedirect(
        string exePath,
        string workingDirectory,
        string targetAddress,
        bool enableAuthnetLogin = false,
        LinuxLaunchOptions? linuxOptions = null)
    {
        if (OperatingSystem.IsWindows())
        {
            LaunchWindows(exePath, workingDirectory, targetAddress, enableAuthnetLogin);
            return 0;
        }

        if (OperatingSystem.IsLinux())
            return LaunchLinux(exePath, workingDirectory, targetAddress, enableAuthnetLogin, linuxOptions ?? new LinuxLaunchOptions());

        throw new PlatformNotSupportedException("SkyFire Launcher can start the client on Windows or Linux (via Wine or Proton).");
    }

    private static void LaunchWindows(string exePath, string workingDirectory, string targetAddress, bool enableAuthnetLogin)
    {
        var startupInfo = new WindowsRemoteProcess.Native.STARTUPINFO();
        startupInfo.cb = Marshal.SizeOf<WindowsRemoteProcess.Native.STARTUPINFO>();

        if (!WindowsRemoteProcess.Native.CreateProcess(exePath, null, IntPtr.Zero, IntPtr.Zero, false,
                WindowsRemoteProcess.Native.ProcessCreationFlags.NONE, IntPtr.Zero, workingDirectory,
                ref startupInfo, out var processInfo))
        {
            throw new Win32Exception("Failed to start the client process");
        }

        try
        {
            using var process = new WindowsRemoteProcess(processInfo.hProcess, processInfo.dwProcessId, ownsHandle: false);
            var exeFileName = Path.GetFileName(exePath);
            var (baseAddress, moduleSize) = WindowsRemoteProcess.GetMainModuleInfo(processInfo.dwProcessId, exeFileName);
            ClientImagePatcher.Apply(process, baseAddress, moduleSize, exePath, targetAddress, enableAuthnetLogin);
        }
        catch
        {
            // Never leave an unpatched client connecting to the real hardcoded
            // address - if the patch didn't land, the process shouldn't run.
            WindowsRemoteProcess.Native.TerminateProcess(processInfo.hProcess, 1);
            throw;
        }
        finally
        {
            WindowsRemoteProcess.Native.CloseHandle(processInfo.hThread);
            WindowsRemoteProcess.Native.CloseHandle(processInfo.hProcess);
        }
    }

    private static int LaunchLinux(
        string exePath,
        string workingDirectory,
        string targetAddress,
        bool enableAuthnetLogin,
        LinuxLaunchOptions linuxOptions)
    {
        var is64BitClient = PeImportTable.IsPe64Bit(File.ReadAllBytes(exePath));
        var startInfo = LinuxClientRuntime.BuildStartInfo(
            linuxOptions.Layer,
            exePath,
            workingDirectory,
            is64BitClient,
            linuxOptions.ProtonInstallPath,
            linuxOptions.ProtonPrefixPath);

        try
        {
            return StartAndPatch(startInfo, exePath, targetAddress, enableAuthnetLogin, LinuxClientRuntime.ReadyTimeout(linuxOptions.Layer));
        }
        catch (Exception ex) when (linuxOptions.Layer == LinuxCompatibilityLayer.Proton && IsAttachFailure(ex))
        {
            var proton = LinuxClientRuntime.ResolveProtonInstall(linuxOptions.ProtonInstallPath);
            if (proton is null)
                throw;

            var fallback = LinuxClientRuntime.BuildProtonWineFallbackStartInfo(
                proton.InstallPath,
                exePath,
                workingDirectory,
                linuxOptions.ProtonPrefixPath,
                is64BitClient);

            return StartAndPatch(fallback, exePath, targetAddress, enableAuthnetLogin, LinuxClientRuntime.ReadyTimeout(linuxOptions.Layer));
        }
    }

    private static int StartAndPatch(
        ProcessStartInfo startInfo,
        string exePath,
        string targetAddress,
        bool enableAuthnetLogin,
        TimeSpan timeout)
    {
        var hostProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {Path.GetFileName(startInfo.FileName)}.");

        if (OperatingSystem.IsLinux())
            LinuxClientRuntime.AttachLaunchLog(hostProcess, startInfo);

        int? clientPid = null;
        try
        {
            clientPid = LinuxRemoteProcess.WaitForMappedModule(exePath, hostProcess.Id, timeout);
            // Prefer to patch after d3d9 is mapped, but do not stall long if it never appears.
            LinuxRemoteProcess.WaitForMappedDll(clientPid.Value, "d3d9.dll", TimeSpan.FromSeconds(15));

            using (var process = new LinuxRemoteProcess(clientPid.Value))
            {
                var fileBuffer = File.ReadAllBytes(exePath);
                var (baseAddress, moduleSize) = LinuxRemoteProcess.FindPeModule(clientPid.Value, exePath, fileBuffer);
                ClientImagePatcher.Apply(process, baseAddress, moduleSize, exePath, targetAddress, enableAuthnetLogin);
            }

            // Confirm the client survives past patch + early init.
            if (!WaitForClientStillAlive(clientPid.Value, TimeSpan.FromSeconds(5)))
            {
                var logHint = startInfo.Environment.TryGetValue("SKYFIRE_LAUNCH_LOG", out var log) &&
                              !string.IsNullOrWhiteSpace(log)
                    ? $" See {log}."
                    : string.Empty;
                throw new InvalidOperationException(
                    "The client exited shortly after launch. " +
                    "Delete ~/.local/share/SkyFireLauncher/proton, pick GE-Proton (not *-slr), and try again." +
                    logHint);
            }

            return clientPid.Value;
        }
        catch
        {
            if (clientPid is int pid)
            {
                try { Process.GetProcessById(pid).Kill(); } catch { /* best-effort */ }
            }

            try
            {
                if (!hostProcess.HasExited)
                    hostProcess.Kill();
            }
            catch
            {
                // best-effort
            }

            throw;
        }
    }

    private static bool WaitForClientStillAlive(int pid, TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsPidAlive(pid))
                return false;
            Thread.Sleep(100);
        }

        return IsPidAlive(pid);
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAttachFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is IOException)
                return true;

            var message = current.Message;
            if (message.Contains("ptrace", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Timed out waiting", StringComparison.OrdinalIgnoreCase)
                || message.Contains("/proc/", StringComparison.OrdinalIgnoreCase)
                || message.Contains("input/output error", StringComparison.OrdinalIgnoreCase)
                || message.Contains("mprotect", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
