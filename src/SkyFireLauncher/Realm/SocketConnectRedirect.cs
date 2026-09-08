using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace SkyFireLauncher.Realm;

// Redirects IPv4 socket connections after hostname resolution. The client
// uses both legacy and modern resolver APIs, so enforcing the destination at
// connect time keeps every login path on the configured server.
public static class SocketConnectRedirect
{
    public static void Install(IntPtr hProcess, IntPtr baseAddress, byte[] moduleBuffer, bool is64Bit, string targetAddress)
    {
        var iatSlotRva = PeImportTable.FindIatSlotRva(moduleBuffer, "ws2_32.dll", "connect", knownOrdinal: 4);
        if (iatSlotRva == 0)
            throw new InvalidOperationException("Could not find connect import in the client.");

        var ip = IPAddress.Parse(targetAddress);
        var ipBytes = ip.GetAddressBytes();
        if (ipBytes.Length != 4)
            throw new NotSupportedException("Only IPv4 targets are supported.");

        var iatSlotAddress = IntPtr.Add(baseAddress, iatSlotRva);
        var pointerSize = is64Bit ? 8 : 4;
        var originalPointerBytes = new byte[pointerSize];
        if (!ReadProcessMemory(hProcess, iatSlotAddress, originalPointerBytes, pointerSize, out _))
            throw new Win32Exception("Could not read the client's connect import");

        var originalConnect = is64Bit
            ? BitConverter.ToInt64(originalPointerBytes)
            : BitConverter.ToUInt32(originalPointerBytes);
        var shellcode = BuildShellcode(is64Bit, originalConnect, ipBytes);

        var shellcodeAddress = VirtualAllocEx(hProcess, IntPtr.Zero, (UIntPtr)shellcode.Length,
            AllocationType.MEM_COMMIT | AllocationType.MEM_RESERVE, MemoryProtection.PAGE_EXECUTE_READWRITE);
        if (shellcodeAddress == IntPtr.Zero)
            throw new Win32Exception("Could not allocate the client connection redirect");

        if (!WriteProcessMemory(hProcess, shellcodeAddress, shellcode, shellcode.Length, out _))
            throw new Win32Exception("Could not write the client connection redirect");

        var redirectPointerBytes = is64Bit
            ? BitConverter.GetBytes(shellcodeAddress.ToInt64())
            : BitConverter.GetBytes((uint)shellcodeAddress.ToInt64());

        if (!VirtualProtectEx(hProcess, iatSlotAddress, (UIntPtr)redirectPointerBytes.Length,
                MemoryProtection.PAGE_READWRITE, out var oldProtect))
        {
            throw new Win32Exception("Could not unprotect the client's connect import");
        }

        try
        {
            if (!WriteProcessMemory(hProcess, iatSlotAddress, redirectPointerBytes, redirectPointerBytes.Length, out _))
                throw new Win32Exception("Could not redirect the client's socket connection");
        }
        finally
        {
            VirtualProtectEx(hProcess, iatSlotAddress, (UIntPtr)redirectPointerBytes.Length, oldProtect, out _);
        }
    }

    private static byte[] BuildShellcode(bool is64Bit, long originalConnect, byte[] ipBytes)
    {
        if (is64Bit)
        {
            // sockaddr is in RDX. Replace sin_addr only for AF_INET, then tail
            // call the original connect with every argument otherwise intact.
            var shellcode = new byte[]
            {
                0x66, 0x83, 0x3A, 0x02,                   // cmp word ptr [rdx], AF_INET
                0x75, 0x07,                               // jne call_original
                0xC7, 0x42, 0x04, 0, 0, 0, 0,            // mov dword ptr [rdx+4], target
                0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0,     // mov rax, original connect
                0xFF, 0xE0                                // jmp rax
            };
            Array.Copy(ipBytes, 0, shellcode, 9, 4);
            BitConverter.GetBytes(originalConnect).CopyTo(shellcode, 15);
            return shellcode;
        }

        // sockaddr is the second stack argument on x86.
        var x86Shellcode = new byte[]
        {
            0x8B, 0x44, 0x24, 0x08,                       // mov eax, [esp+8]
            0x66, 0x83, 0x38, 0x02,                       // cmp word ptr [eax], AF_INET
            0x75, 0x07,                                   // jne call_original
            0xC7, 0x40, 0x04, 0, 0, 0, 0,                // mov dword ptr [eax+4], target
            0xB8, 0, 0, 0, 0,                            // mov eax, original connect
            0xFF, 0xE0                                    // jmp eax
        };
        Array.Copy(ipBytes, 0, x86Shellcode, 13, 4);
        BitConverter.GetBytes((uint)originalConnect).CopyTo(x86Shellcode, 18);
        return x86Shellcode;
    }

    [Flags]
    private enum AllocationType : uint
    {
        MEM_COMMIT = 0x1000,
        MEM_RESERVE = 0x2000
    }

    private enum MemoryProtection : uint
    {
        PAGE_READWRITE = 0x04,
        PAGE_EXECUTE_READWRITE = 0x40
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize,
        AllocationType flAllocationType, MemoryProtection flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer,
        int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer,
        int dwSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize,
        MemoryProtection flNewProtect, out MemoryProtection lpflOldProtect);
}
