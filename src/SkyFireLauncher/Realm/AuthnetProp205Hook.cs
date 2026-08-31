using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SkyFireLauncher.Realm;

// Soft2 629D0 requires challenge.second to be derived from Auth prop
// 2055043961 (prop205) *before* Password.dll checks the Soft2 op=3 proof.
// Wow-64 CryptGenRandoms that buffer in Soft Success (B541A0 / B54330)
// *after* authserver has already sent second, so a stock client always
// #103. IDA used to overwrite the 128-byte buffer at the Set call; this
// hook does the same without a debugger:
//
// Replace the two `call BE6050` fills that sit immediately before
// `mov edx, 2055043961` with a trampoline that memcpy's a fixed 128-byte
// blob. Authserver bakes the same bytes into challenge.second
// (PasswordSrp::kAuthnetFixedProp205). Wow.exe on disk is not modified.
internal static class AuthnetProp205Hook
{
    // SHA256("SkyFire.Authnet.FixedProp205.v1") × 4. Must match
    // SkyFire::Authnet::PasswordSrp::kAuthnetFixedProp205.
    public static readonly byte[] FixedBytes = Convert.FromHexString(
        "294A1FECB9CB9C7DF5A194BD6C3EA9EB74BD87D84BF5D7116EE9061BDC438B36" +
        "294A1FECB9CB9C7DF5A194BD6C3EA9EB74BD87D84BF5D7116EE9061BDC438B36" +
        "294A1FECB9CB9C7DF5A194BD6C3EA9EB74BD87D84BF5D7116EE9061BDC438B36" +
        "294A1FECB9CB9C7DF5A194BD6C3EA9EB74BD87D84BF5D7116EE9061BDC438B36");

    // `mov edx, 2055043961` — only two hits on 5.4.8 Wow-64 (B5423A, B545D8).
    private static readonly byte[] PropIdMovEdx = [0xBA, 0x79, 0x7B, 0x7D, 0x7A];

    // `call BE6050` is 19 bytes before that mov (lea r8 / mov r9d / mov r11).
    private const int CallBeforePropId = 19;

    public static void Install(IntPtr hProcess, IntPtr baseAddress, byte[] image, bool is64Bit)
    {
        if (!is64Bit)
            throw new InvalidOperationException("Soft/authnet login requires Wow-64.exe.");

        if (FixedBytes.Length != 128)
            throw new InvalidOperationException("Soft prop205 constant must be 128 bytes.");

        var callRvas = FindFillCallRvas(image);
        if (callRvas.Count != 2)
        {
            throw new InvalidOperationException(
                $"Could not find Soft Success prop205 fill sites (found {callRvas.Count}, expected 2).");
        }

        var stub = BuildStub();
        var stubAddress = AllocateNear(hProcess, baseAddress, SizeOfImage(image), stub.Length);
        Write(hProcess, stubAddress, stub);

        foreach (var rva in callRvas)
        {
            var site = IntPtr.Add(baseAddress, rva);
            var rel = stubAddress.ToInt64() - (site.ToInt64() + 5);
            if (rel < int.MinValue || rel > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "Soft prop205 trampoline is too far from Wow-64 code for a near jump.");
            }

            // Must be CALL (E8), not JMP (E9): the stub RETs to the insn after
            // the original `call BE6050`. JMP+RET pops garbage and crashes
            // (CET/shadow stack: ACCESS_VIOLATION reading -1).
            var call = new byte[5];
            call[0] = 0xE8;
            BitConverter.GetBytes((int)rel).CopyTo(call, 1);
            Write(hProcess, site, call);
        }

        FlushInstructionCache(hProcess, IntPtr.Zero, UIntPtr.Zero);
        PatchDefaultSunkenPort(hProcess, baseAddress, image);
        PatchAuthCtorSunkenPort(hProcess, baseAddress, image);
        // Soft-finish without Soft0: B537E0 Get(A2057DF4) always fails on
        // stock Wow. Force return 1 so B53E40 can reach B53940 after hop TCP.
        PatchB537E0ForceSuccess(hProcess, baseAddress);
        // Do not force B7FBA0's numeric-host path. C1B1A0("127.0.0.1")
        // takes Tumor's inet_pton shortcut and never reaches
        // WowConnection::StartConnect (playtest 18:35: no hop, 444 close).
        // FQDN C1B1A0 reaches Tumor A75CC0 (gethostbyname). DnsResolverHook
        // Soft Success sets softOk then B7FBA0→C1→B80270; Soft-finish
        // runs from BE6B80 once Sunken+45 is set (not from the C1 wrap).
        // Do not jump A75E8F (21:23).
    }

    // Same as ida_prop205_force.py B537E0 hook: mov al, 1; ret.
    private static void PatchB537E0ForceSuccess(IntPtr hProcess, IntPtr baseAddress)
    {
        Write(hProcess, IntPtr.Add(baseAddress, 0xB537E0), [0xB0, 0x01, 0xC3]);
    }

    // Soft2 Success B541A0→B7FBA0 uses Auth+8448 as the Sunken connect port.
    // Stock default at 1416327B4 is 0 (type-0xC host:port never arrives), so
    // the second TCP is :0 and never hits authnet. htons(1119) = 04 5F.
    private static void PatchDefaultSunkenPort(IntPtr hProcess, IntPtr baseAddress, byte[] image)
    {
        byte[] needle =
        [
            0x88, 0x86, 0x00, 0x21, 0x00, 0x00, 0x0F, 0xB6, 0x05
        ];
        var store = IndexOf(image, needle);
        if (store < 0 || store < 7 || image[store - 7] != 0x0F || image[store - 6] != 0xB6 || image[store - 5] != 0x05)
            throw new InvalidOperationException("Could not find Soft Sunken default-port stores (Auth+8448).");

        var rel = BitConverter.ToInt32(image, store - 4);
        var dataRva = store + rel;
        if (dataRva < 0 || dataRva + 2 > image.Length)
            throw new InvalidOperationException("Soft Sunken default-port RIP displacement is out of range.");

        Write(hProcess, IntPtr.Add(baseAddress, dataRva), [0x04, 0x5F]);
    }

    // B6FFA0 copies the two default port bytes into Auth+8448. Replace that
    // loop with htons(1119). Keep rdx=0: the following memset uses it as a
    // count and a leftover rdx crashes (16:17:04). Do not write +0x2000 or
    // flag 8177 here — a later ctor memset wipes +0x2000, and forcing the
    // B7FBA0 full-host branch with an empty string produced no hop TCP.
    private static void PatchAuthCtorSunkenPort(IntPtr hProcess, IntPtr baseAddress, byte[] image)
    {
        byte[] needle =
        [
            0x48, 0x8D, 0x8F, 0x00, 0x21, 0x00, 0x00, 0x4C, 0x8D, 0x05
        ];
        var at = IndexOf(image, needle);
        if (at < 0)
            throw new InvalidOperationException("Could not find Soft Auth constructor Sunken port copy.");

        const int copyLen = 45;
        if (at + copyLen > image.Length || image[at + copyLen - 2] != 0x75 || image[at + copyLen - 1] != 0xEC)
            throw new InvalidOperationException("Soft Auth constructor Sunken port copy is not the expected 45-byte loop.");

        var patch = new byte[copyLen];
        Array.Fill(patch, (byte)0x90);
        byte[] store =
        [
            0x66, 0xC7, 0x87, 0x00, 0x21, 0x00, 0x00, 0x04, 0x5F, // mov word [rdi+2100h], htons(1119)
            0x31, 0xD2                                            // xor edx, edx
        ];
        Array.Copy(store, patch, store.Length);
        Write(hProcess, IntPtr.Add(baseAddress, at), patch);
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
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

    private static List<int> FindFillCallRvas(byte[] image)
    {
        var hits = new List<int>();
        var last = image.Length - PropIdMovEdx.Length;
        for (var i = 0; i <= last; i++)
        {
            var matched = true;
            for (var j = 0; j < PropIdMovEdx.Length; j++)
            {
                if (image[i + j] != PropIdMovEdx[j])
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
                continue;

            var callAt = i - CallBeforePropId;
            if (callAt < 0)
                continue;

            // Stock and hooked client both use E8 (call rel32) at this site.
            if (image[callAt] != 0xE8)
                continue;

            hits.Add(callAt);
        }

        return hits;
    }

    // rcx = destination buffer (same ABI as BE6050). Copies FixedBytes and returns.
    private static byte[] BuildStub()
    {
        const int codeLen = 0x17;
        var stub = new byte[codeLen + FixedBytes.Length];
        stub[0x00] = 0xFC; // cld
        stub[0x01] = 0x56; // push rsi
        stub[0x02] = 0x57; // push rdi
        stub[0x03] = 0x48; // lea rsi, [rip+disp]
        stub[0x04] = 0x8D;
        stub[0x05] = 0x35;
        // RIP after lea is code+0x0A; payload starts at codeLen.
        BitConverter.GetBytes(codeLen - 0x0A).CopyTo(stub, 0x06);
        stub[0x0A] = 0x48; // mov rdi, rcx
        stub[0x0B] = 0x89;
        stub[0x0C] = 0xCF;
        stub[0x0D] = 0xB9; // mov ecx, 128
        stub[0x0E] = 0x80;
        stub[0x0F] = 0x00;
        stub[0x10] = 0x00;
        stub[0x11] = 0x00;
        stub[0x12] = 0xF3; // rep movsb
        stub[0x13] = 0xA4;
        stub[0x14] = 0x5F; // pop rdi
        stub[0x15] = 0x5E; // pop rsi
        stub[0x16] = 0xC3; // ret
        Buffer.BlockCopy(FixedBytes, 0, stub, codeLen, FixedBytes.Length);
        return stub;
    }

    private static int SizeOfImage(byte[] pe)
    {
        if (pe.Length < 0x40)
            throw new InvalidOperationException("Client image is too small to parse.");
        var e = BitConverter.ToInt32(pe, 0x3C);
        if (e < 0 || e + 24 + 56 + 4 > pe.Length)
            throw new InvalidOperationException("Client PE header is truncated.");
        return BitConverter.ToInt32(pe, e + 24 + 56);
    }

    private static IntPtr AllocateNear(IntPtr hProcess, IntPtr moduleBase, int moduleSize, int size)
    {
        const int granule = 0x10000;
        var length = (UIntPtr)((size + 0xFFF) & ~0xFFF);
        var after = (moduleBase.ToInt64() + moduleSize + granule - 1) & ~(long)(granule - 1);
        long[] hints =
        [
            after,
            after + granule,
            after + granule * 2,
            after + granule * 8,
            moduleBase.ToInt64() - 0x100000,
            moduleBase.ToInt64() - 0x200000,
            moduleBase.ToInt64() - 0x400000,
        ];

        foreach (var hint in hints)
        {
            if (hint <= 0)
                continue;

            var alloc = VirtualAllocEx(
                hProcess,
                new IntPtr(hint),
                length,
                AllocationType.MEM_COMMIT | AllocationType.MEM_RESERVE,
                MemoryProtection.PAGE_EXECUTE_READWRITE);
            if (alloc != IntPtr.Zero)
                return alloc;
        }

        throw new Win32Exception("Could not allocate Soft prop205 trampoline near Wow-64.");
    }

    private static void Write(IntPtr hProcess, IntPtr address, byte[] data)
    {
        if (!VirtualProtectEx(hProcess, address, (UIntPtr)data.Length, MemoryProtection.PAGE_EXECUTE_READWRITE, out var oldProtect))
            throw new Win32Exception("Could not unprotect client memory for Soft prop205 hook");

        try
        {
            if (!WriteProcessMemory(hProcess, address, data, data.Length, out _))
                throw new Win32Exception("Could not write Soft prop205 hook into client memory");
        }
        finally
        {
            VirtualProtectEx(hProcess, address, (UIntPtr)data.Length, oldProtect, out _);
        }
    }

    [Flags]
    private enum AllocationType : uint
    {
        MEM_COMMIT = 0x1000,
        MEM_RESERVE = 0x2000
    }

    private enum MemoryProtection : uint
    {
        PAGE_EXECUTE_READWRITE = 0x40
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, AllocationType flAllocationType, MemoryProtection flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, MemoryProtection flNewProtect, out MemoryProtection lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);
}
