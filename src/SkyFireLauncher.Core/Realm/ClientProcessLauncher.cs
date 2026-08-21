using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SkyFireLauncher.Configuration;

namespace SkyFireLauncher.Realm;

// Windows: launches the client and patches logon hostnames in live process memory
// (original on-disk exe untouched).
//
// Linux: Proton's Steam runtime / ptrace isolation makes live memory patches kill
// the client or deny /proc maps. Instead we copy the exe into the Proton prefix,
// apply the same hostname + login-flow patches to that copy only, and launch it
// with a normal `proton run` (Steam runtime on) so the game can open a window.
// The user's game-directory client stays unmodified.
public static class ClientProcessLauncher
{
    /// <summary>Status text while Linux launch/patch is in progress (UI poll).</summary>
    public static string LaunchWaitStatus { get; internal set; } = string.Empty;

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
        LaunchWaitStatus = "preparing patched client copy";
        var patchedExe = LinuxClientRuntime.PreparePatchedClientCopy(exePath, linuxOptions.ProtonPrefixPath);
        ClientImagePatcher.ApplyToFile(patchedExe, targetAddress, enableAuthnetLogin);

        var is64BitClient = PeImportTable.IsPe64Bit(File.ReadAllBytes(patchedExe));
        var startInfo = LinuxClientRuntime.BuildStartInfo(
            linuxOptions.Layer,
            patchedExe,
            workingDirectory,
            is64BitClient,
            linuxOptions.ProtonInstallPath,
            linuxOptions.ProtonPrefixPath);

        return StartPatchedLinuxClient(startInfo, patchedExe, LinuxClientRuntime.ReadyTimeout(linuxOptions.Layer));
    }

    private static int StartPatchedLinuxClient(ProcessStartInfo startInfo, string exePath, TimeSpan timeout)
    {
        LaunchWaitStatus = "starting Proton";
        var hostProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {Path.GetFileName(startInfo.FileName)}.");

        LinuxClientRuntime.AttachLaunchLog(hostProcess, startInfo);

        int? clientPid = null;
        try
        {
            // No ptrace — just wait until Wow shows up and stays alive.
            clientPid = LinuxRemoteProcess.WaitForReadyClient(exePath, hostProcess.Id, timeout);
            LaunchWaitStatus = $"running pid {clientPid.Value}";

            if (!WaitForClientStillAlive(clientPid.Value, TimeSpan.FromSeconds(5)))
            {
                var logHint = startInfo.Environment.TryGetValue("SKYFIRE_LAUNCH_LOG", out var log) &&
                              !string.IsNullOrWhiteSpace(log)
                    ? $" See {log}."
                    : string.Empty;
                throw new InvalidOperationException(
                    "The client exited shortly after launch." + logHint);
            }

            LaunchWaitStatus = string.Empty;
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
}
