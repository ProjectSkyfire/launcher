using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SkyFireLauncher.Realm;

// Launches the client, then immediately patches its hardcoded region logon
// hostnames directly in process memory. Nothing on disk (the exe, the hosts
// file) is ever touched - the patch only exists in the running process, for
// its lifetime.
//
// CREATE_SUSPENDED was tried first (ideal: patch before a single instruction
// runs) but a 32-bit target's WOW64 subsystem isn't far enough along yet at
// that point for CreateToolhelp32Snapshot to reliably enumerate its modules
// from a 64-bit caller (fails with ERROR_PARTIAL_COPY, retrying while still
// suspended does not help). Launching normally and patching immediately
// after is comfortably ahead of the client's own UI load time, which is the
// earliest it could attempt to connect anywhere.
public static class ClientProcessLauncher
{
    // The client's hardcoded region logon hostnames, and the format of what
    // replaces each one ({0} = the target address). The host:port variant is
    // a distinct occurrence from the plain hostname (not just a substring
    // match), so it needs its own explicit entry to be caught.
    private static readonly (string Pattern, string ReplacementFormat)[] PatchTargets =
    [
        ("us.logon.worldofwarcraft.com", "{0}"),
        ("kr.logon.worldofwarcraft.com", "{0}"),
        ("eu.logon.worldofwarcraft.com", "{0}"),
        ("tw.logon.worldofwarcraft.com", "{0}"),
        ("cn.logon.warcraftchina.com", "{0}"),
        ("us.logon.worldofwarcraft.com:3724", "{0}:3724")
    ];

    public static void LaunchAndRedirect(string exePath, string workingDirectory, string targetAddress, bool enableAuthnetLogin = false)
    {
        var startupInfo = new STARTUPINFO();
        startupInfo.cb = Marshal.SizeOf<STARTUPINFO>();

        if (!CreateProcess(exePath, null, IntPtr.Zero, IntPtr.Zero, false,
                ProcessCreationFlags.NONE, IntPtr.Zero, workingDirectory,
                ref startupInfo, out var processInfo))
        {
            throw new Win32Exception("Failed to start the client process");
        }

        try
        {
            PatchHostnames(processInfo.dwProcessId, exePath, targetAddress, enableAuthnetLogin);
        }
        catch
        {
            // Never leave an unpatched client connecting to the real hardcoded
            // address - if the patch didn't land, the process shouldn't run.
            TerminateProcess(processInfo.hProcess, 1);
            throw;
        }
        finally
        {
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
        }
    }

    private static void PatchHostnames(int processId, string exePath, string targetAddress, bool enableAuthnetLogin)
    {
        var exeFileName = Path.GetFileName(exePath);
        var (baseAddress, moduleSize) = GetMainModuleInfo(processId, exeFileName);

        var hProcess = OpenProcess(ProcessAccess.PROCESS_VM_READ | ProcessAccess.PROCESS_VM_WRITE | ProcessAccess.PROCESS_VM_OPERATION, false, processId);
        if (hProcess == IntPtr.Zero)
            throw new Win32Exception("Could not open the client process");

        try
        {
            var buffer = new byte[moduleSize];
            if (!ReadProcessMemory(hProcess, baseAddress, buffer, buffer.Length, out _))
                throw new Win32Exception("Could not read the client's memory");

            foreach (var (patternText, replacementFormat) in PatchTargets)
            {
                var pattern = Encoding.ASCII.GetBytes(patternText + '\0');
                var replacement = Encoding.ASCII.GetBytes(string.Format(replacementFormat, targetAddress) + '\0');
                var offset = 0;

                while ((offset = IndexOf(buffer, pattern, offset)) >= 0)
                {
                    WritePatch(hProcess, IntPtr.Add(baseAddress, offset), replacement);
                    offset += pattern.Length;
                }
            }

            // The actual connect hostname is built at runtime (region code +
            // a hardcoded suffix), so there's no static string for it to
            // find-and-replace above. Hook the DNS resolver import instead so
            // whatever hostname it builds resolves to the target regardless.
            //
            // Import-table lookup uses the file on disk, not the live buffer
            // above: once loaded, the loader overwrites FirstThunk (the IAT)
            // with resolved addresses, and not every import descriptor keeps
            // a separate OriginalFirstThunk - reading live memory for this
            // can end up treating resolved pointers as unresolved RVAs. RVAs
            // themselves are identical between file and loaded image (ASLR
            // only changes the base, not internal offsets), so the RVA found
            // on disk is still correct to apply against the live baseAddress.
            var fileBuffer = File.ReadAllBytes(exePath);
            var is64Bit = PeImportTable.IsPe64Bit(fileBuffer);
            DnsResolverHook.Install(hProcess, baseAddress, fileBuffer, is64Bit, targetAddress);

            // Authnet testing needs the client's login state machine left intact.
            // The legacy flow patches below are only for the classic path.
            var loginFlowPatches = is64Bit ? LoginFlowPatches.X64 : LoginFlowPatches.X86;
            if (enableAuthnetLogin)
                loginFlowPatches = [];

            foreach (var (_, pattern, replacement) in loginFlowPatches)
            {
                var matchOffset = IndexOfWildcard(buffer, pattern, 0);
                if (matchOffset < 0)
                    continue; // best-effort: skip patches that don't match this exact build

                WritePatch(hProcess, IntPtr.Add(baseAddress, matchOffset), replacement);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static void WritePatch(IntPtr hProcess, IntPtr address, byte[] replacement)
    {
        // String data typically lives in a read-only section - temporarily
        // unprotect it, write, then restore the original protection.
        if (!VirtualProtectEx(hProcess, address, (UIntPtr)replacement.Length, MemoryProtection.PAGE_READWRITE, out var oldProtect))
            throw new Win32Exception("Could not unprotect client memory");

        try
        {
            if (!WriteProcessMemory(hProcess, address, replacement, replacement.Length, out _))
                throw new Win32Exception("Could not write client memory");
        }
        finally
        {
            VirtualProtectEx(hProcess, address, (UIntPtr)replacement.Length, oldProtect, out _);
        }
    }

    private static (IntPtr baseAddress, int moduleSize) GetMainModuleInfo(int processId, string exeFileName)
    {
        // TH32CS_SNAPMODULE32 is required alongside TH32CS_SNAPMODULE to reliably
        // enumerate a 32-bit target process's modules from a 64-bit launcher.
        // The module list may not be queryable in the first instant after
        // CreateProcess returns - retry briefly.
        var snapshot = InvalidHandleValue;
        for (var attempt = 0; attempt < 20 && snapshot == InvalidHandleValue; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(50);

            snapshot = CreateToolhelp32Snapshot(SnapshotFlags.TH32CS_SNAPMODULE | SnapshotFlags.TH32CS_SNAPMODULE32, (uint)processId);
        }

        if (snapshot == InvalidHandleValue)
            throw new Win32Exception("Could not snapshot the client's modules");

        try
        {
            var entry = new MODULEENTRY32 { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>() };
            if (!Module32First(snapshot, ref entry))
                throw new Win32Exception("Could not enumerate the client's modules");

            do
            {
                if (string.Equals(entry.szModule, exeFileName, StringComparison.OrdinalIgnoreCase))
                    return (entry.modBaseAddr, (int)entry.modBaseSize);
            }
            while (Module32Next(snapshot, ref entry));

            throw new InvalidOperationException($"Could not find module '{exeFileName}' in the client process.");
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int startIndex)
    {
        for (var i = startIndex; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;

            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }

    // Same as IndexOf, but a 0x00 byte in the pattern matches anything.
    private static int IndexOfWildcard(byte[] haystack, byte[] pattern, int startIndex)
    {
        for (var i = startIndex; i <= haystack.Length - pattern.Length; i++)
        {
            var match = true;

            for (var j = 0; j < pattern.Length; j++)
            {
                if (pattern[j] != 0 && haystack[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }

    #region Win32 interop

    private static readonly IntPtr InvalidHandleValue = new(-1);

    [Flags]
    private enum ProcessCreationFlags : uint
    {
        NONE = 0x00000000
    }

    [Flags]
    private enum ProcessAccess : uint
    {
        PROCESS_VM_OPERATION = 0x0008,
        PROCESS_VM_READ = 0x0010,
        PROCESS_VM_WRITE = 0x0020
    }

    private enum MemoryProtection : uint
    {
        PAGE_READWRITE = 0x04
    }

    [Flags]
    private enum SnapshotFlags : uint
    {
        TH32CS_SNAPMODULE = 0x00000008,
        TH32CS_SNAPMODULE32 = 0x00000010
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MODULEENTRY32
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CreateProcess(
        string lpApplicationName,
        string? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        ProcessCreationFlags dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(ProcessAccess dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, MemoryProtection flNewProtect, out MemoryProtection lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Module32First(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Module32Next(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    #endregion
}
