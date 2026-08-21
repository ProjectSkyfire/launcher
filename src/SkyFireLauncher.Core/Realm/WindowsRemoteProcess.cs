using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace SkyFireLauncher.Realm;

internal sealed class WindowsRemoteProcess : IRemoteProcess
{
    private IntPtr _handle;
    private readonly bool _ownsHandle;

    public int Id { get; }

    public WindowsRemoteProcess(IntPtr handle, int processId, bool ownsHandle)
    {
        if (handle == IntPtr.Zero)
            throw new Win32Exception("Could not open the client process");

        _handle = handle;
        _ownsHandle = ownsHandle;
        Id = processId;
    }

    public static WindowsRemoteProcess Open(int processId)
    {
        var handle = Native.OpenProcess(
            Native.ProcessAccess.PROCESS_VM_READ | Native.ProcessAccess.PROCESS_VM_WRITE | Native.ProcessAccess.PROCESS_VM_OPERATION,
            false,
            processId);
        return new WindowsRemoteProcess(handle, processId, ownsHandle: true);
    }

    public byte[] Read(nint address, int size)
    {
        var buffer = new byte[size];
        if (!Native.ReadProcessMemory(_handle, (IntPtr)address, buffer, buffer.Length, out _))
            throw new Win32Exception("Could not read the client's memory");
        return buffer;
    }

    public void Write(nint address, byte[] data)
    {
        if (!Native.VirtualProtectEx(_handle, (IntPtr)address, (UIntPtr)data.Length, Native.MemoryProtection.PAGE_READWRITE, out var oldProtect))
            throw new Win32Exception("Could not unprotect client memory");

        try
        {
            if (!Native.WriteProcessMemory(_handle, (IntPtr)address, data, data.Length, out _))
                throw new Win32Exception("Could not write client memory");
        }
        finally
        {
            Native.VirtualProtectEx(_handle, (IntPtr)address, (UIntPtr)data.Length, oldProtect, out _);
        }
    }

    public nint AllocateExecutable(int size, bool prefer32BitAddress)
    {
        _ = prefer32BitAddress;
        var allocBase = Native.VirtualAllocEx(
            _handle,
            IntPtr.Zero,
            (UIntPtr)size,
            Native.AllocationType.MEM_COMMIT | Native.AllocationType.MEM_RESERVE,
            Native.MemoryProtection.PAGE_EXECUTE_READWRITE);
        if (allocBase == IntPtr.Zero)
            throw new Win32Exception("Could not allocate memory in the client process");
        return (nint)allocBase;
    }

    public void Terminate() => Native.TerminateProcess(_handle, 1);

    public void Dispose()
    {
        if (_ownsHandle && _handle != IntPtr.Zero)
        {
            Native.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    public static (nint BaseAddress, int ModuleSize) GetMainModuleInfo(int processId, string exeFileName)
    {
        var snapshot = Native.InvalidHandleValue;
        for (var attempt = 0; attempt < 20 && snapshot == Native.InvalidHandleValue; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(50);

            snapshot = Native.CreateToolhelp32Snapshot(
                Native.SnapshotFlags.TH32CS_SNAPMODULE | Native.SnapshotFlags.TH32CS_SNAPMODULE32,
                (uint)processId);
        }

        if (snapshot == Native.InvalidHandleValue)
            throw new Win32Exception("Could not snapshot the client's modules");

        try
        {
            var entry = new Native.MODULEENTRY32 { dwSize = (uint)Marshal.SizeOf<Native.MODULEENTRY32>() };
            if (!Native.Module32First(snapshot, ref entry))
                throw new Win32Exception("Could not enumerate the client's modules");

            do
            {
                if (string.Equals(entry.szModule, exeFileName, StringComparison.OrdinalIgnoreCase))
                    return ((nint)entry.modBaseAddr, (int)entry.modBaseSize);
            }
            while (Native.Module32Next(snapshot, ref entry));

            throw new InvalidOperationException($"Could not find module '{exeFileName}' in the client process.");
        }
        finally
        {
            Native.CloseHandle(snapshot);
        }
    }

    internal static class Native
    {
        internal static readonly IntPtr InvalidHandleValue = new(-1);

        [Flags]
        internal enum ProcessCreationFlags : uint
        {
            NONE = 0x00000000
        }

        [Flags]
        internal enum ProcessAccess : uint
        {
            PROCESS_VM_OPERATION = 0x0008,
            PROCESS_VM_READ = 0x0010,
            PROCESS_VM_WRITE = 0x0020
        }

        internal enum MemoryProtection : uint
        {
            PAGE_READWRITE = 0x04,
            PAGE_EXECUTE_READWRITE = 0x40
        }

        [Flags]
        internal enum AllocationType : uint
        {
            MEM_COMMIT = 0x1000,
            MEM_RESERVE = 0x2000
        }

        [Flags]
        internal enum SnapshotFlags : uint
        {
            TH32CS_SNAPMODULE = 0x00000008,
            TH32CS_SNAPMODULE32 = 0x00000010
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct STARTUPINFO
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
        internal struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct MODULEENTRY32
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
        internal static extern bool CreateProcess(
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
        internal static extern IntPtr OpenProcess(ProcessAccess dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, MemoryProtection flNewProtect, out MemoryProtection lpflOldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, AllocationType flAllocationType, MemoryProtection flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern bool Module32First(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern bool Module32Next(IntPtr hSnapshot, ref MODULEENTRY32 lpme);
    }
}
