using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace SkyFireLauncher.Realm;

// The client builds its logon hostname at runtime, so there's no static string
// to find-and-replace for it. Instead, this hooks the client's import of
// ws2_32.dll!gethostbyname so that ANY hostname it asks to resolve comes
// back as the target address - sidestepping the string entirely.
//
// Mechanism: allocate a small RWX block in the target process containing a
// handful of bytes of shellcode plus a fake `hostent` structure describing
// the target address, then overwrite the client's Import Address Table slot
// for gethostbyname to point at that shellcode instead of the real function.
public static class DnsResolverHook
{
    public static void Install(IntPtr hProcess, IntPtr baseAddress, byte[] moduleBuffer, bool is64Bit, string targetAddress)
    {
        // gethostbyname is ordinal 52 in ws2_32.dll - stable/documented since
        // Windows 95, and how this client actually imports it (by ordinal,
        // not by name, like most of the classic Winsock 1.1 API surface).
        var iatSlotRva = PeImportTable.FindIatSlotRva(moduleBuffer, "ws2_32.dll", "gethostbyname", knownOrdinal: 52);
        if (iatSlotRva == 0)
            throw new InvalidOperationException("Could not find gethostbyname import in the client.");

        var ip = IPAddress.Parse(targetAddress);
        var ipBytes = ip.GetAddressBytes();
        if (ipBytes.Length != 4)
            throw new NotSupportedException("Only IPv4 targets are supported.");

        var block = BuildFakeHostentBlock(is64Bit, ipBytes, targetAddress, out var shellcodeOffset);

        var allocSize = (UIntPtr)block.Length;
        var allocBase = VirtualAllocEx(hProcess, IntPtr.Zero, allocSize, AllocationType.MEM_COMMIT | AllocationType.MEM_RESERVE, MemoryProtection.PAGE_EXECUTE_READWRITE);
        if (allocBase == IntPtr.Zero)
            throw new Win32Exception("Could not allocate memory in the client process");

        // Now that we know the allocation's address, fix up the shellcode's
        // embedded pointer and the hostent's internal pointers, then write
        // the finished block in one shot.
        PatchBlockPointers(block, allocBase, is64Bit, shellcodeOffset);

        if (!WriteProcessMemory(hProcess, allocBase, block, block.Length, out _))
            throw new Win32Exception("Could not write the resolver hook into client memory");

        var iatSlotAddress = IntPtr.Add(baseAddress, iatSlotRva);
        var shellcodeAddress = IntPtr.Add(allocBase, shellcodeOffset);
        var pointerBytes = is64Bit
            ? BitConverter.GetBytes(shellcodeAddress.ToInt64())
            : BitConverter.GetBytes((uint)shellcodeAddress.ToInt64());

        if (!VirtualProtectEx(hProcess, iatSlotAddress, (UIntPtr)pointerBytes.Length, MemoryProtection.PAGE_READWRITE, out var oldProtect))
            throw new Win32Exception("Could not unprotect the client's import table");

        try
        {
            if (!WriteProcessMemory(hProcess, iatSlotAddress, pointerBytes, pointerBytes.Length, out _))
                throw new Win32Exception("Could not redirect the client's DNS resolver import");
        }
        finally
        {
            VirtualProtectEx(hProcess, iatSlotAddress, (UIntPtr)pointerBytes.Length, oldProtect, out _);
        }
    }

    // Layout: [shellcode][hostent][addrList: 2 pointers][ip bytes: 4][name string]
    // Everything after the shellcode is data the shellcode's returned hostent
    // pointer refers into - built once we know the allocation's real address.
    private static byte[] BuildFakeHostentBlock(bool is64Bit, byte[] ipBytes, string name, out int shellcodeOffset)
    {
        var ptrSize = is64Bit ? 8 : 4;
        var hostentSize = is64Bit ? 32 : 16; // see layout notes in PatchBlockPointers
        var shellcodeSize = is64Bit ? 16 : 8;
        var nameBytes = Encoding.ASCII.GetBytes(name + '\0');

        shellcodeOffset = 0;
        var hostentOffset = Align(shellcodeOffset + shellcodeSize, ptrSize);
        var addrListOffset = hostentOffset + hostentSize;
        var ipBytesOffset = addrListOffset + 2 * ptrSize;
        var nameOffset = ipBytesOffset + 4;
        var totalSize = nameOffset + nameBytes.Length;

        var block = new byte[totalSize];
        Array.Copy(ipBytes, 0, block, ipBytesOffset, 4);
        Array.Copy(nameBytes, 0, block, nameOffset, nameBytes.Length);

        // Stash the computed offsets in a side-channel the caller already has
        // (hostentOffset/addrListOffset/nameOffset are recomputed identically
        // in PatchBlockPointers using the same layout math).
        return block;
    }

    private static void PatchBlockPointers(byte[] block, IntPtr allocBase, bool is64Bit, int shellcodeOffset)
    {
        var ptrSize = is64Bit ? 8 : 4;
        var hostentSize = is64Bit ? 32 : 16;
        var shellcodeSize = is64Bit ? 16 : 8;

        var hostentOffset = Align(shellcodeOffset + shellcodeSize, ptrSize);
        var addrListOffset = hostentOffset + hostentSize;
        var ipBytesOffset = addrListOffset + 2 * ptrSize;
        var nameOffset = ipBytesOffset + 4;

        long Abs(int offset) => is64Bit ? allocBase.ToInt64() + offset : (uint)(allocBase.ToInt64() + offset);

        // struct hostent { char *h_name; char **h_aliases; short h_addrtype;
        //                  short h_length; char **h_addr_list; };
        // (x64 pads h_addr_list to 8-byte alignment after the two shorts)
        WritePointer(block, hostentOffset + 0 * ptrSize, Abs(nameOffset), is64Bit);          // h_name
        WritePointer(block, hostentOffset + 1 * ptrSize, 0, is64Bit);                        // h_aliases = NULL
        WriteInt16(block, hostentOffset + 2 * ptrSize, 2);                                   // h_addrtype = AF_INET
        WriteInt16(block, hostentOffset + 2 * ptrSize + 2, 4);                                // h_length = 4
        var addrListFieldOffset = is64Bit ? hostentOffset + 24 : hostentOffset + 12;
        WritePointer(block, addrListFieldOffset, Abs(addrListOffset), is64Bit);              // h_addr_list

        // addrList[0] -> ip bytes, addrList[1] = NULL terminator
        WritePointer(block, addrListOffset, Abs(ipBytesOffset), is64Bit);
        WritePointer(block, addrListOffset + ptrSize, 0, is64Bit);

        // Shellcode: ignore whatever hostname was asked for, always return a
        // pointer to our fake hostent.
        //   x64 (fastcall, arg in rcx, return in rax):
        //     48 B8 <imm64>   mov rax, imm64
        //     C3              ret
        //   x86 (stdcall, one 4-byte stack arg, callee cleans up):
        //     B8 <imm32>      mov eax, imm32
        //     C2 04 00        ret 4
        if (is64Bit)
        {
            block[shellcodeOffset + 0] = 0x48;
            block[shellcodeOffset + 1] = 0xB8;
            BitConverter.GetBytes(Abs(hostentOffset)).CopyTo(block, shellcodeOffset + 2);
            block[shellcodeOffset + 10] = 0xC3;
        }
        else
        {
            block[shellcodeOffset + 0] = 0xB8;
            BitConverter.GetBytes((int)Abs(hostentOffset)).CopyTo(block, shellcodeOffset + 1);
            block[shellcodeOffset + 5] = 0xC2;
            block[shellcodeOffset + 6] = 0x04;
            block[shellcodeOffset + 7] = 0x00;
        }
    }

    private static void WritePointer(byte[] block, int offset, long value, bool is64Bit)
    {
        if (is64Bit)
            BitConverter.GetBytes(value).CopyTo(block, offset);
        else
            BitConverter.GetBytes((int)value).CopyTo(block, offset);
    }

    private static void WriteInt16(byte[] block, int offset, short value) => BitConverter.GetBytes(value).CopyTo(block, offset);

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    #region Win32 interop

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
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, AllocationType flAllocationType, MemoryProtection flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, MemoryProtection flNewProtect, out MemoryProtection lpflOldProtect);

    #endregion
}
