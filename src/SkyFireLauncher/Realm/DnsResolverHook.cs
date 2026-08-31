using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace SkyFireLauncher.Realm;

// The client builds its logon hostname at runtime (region code + a hardcoded
// suffix, e.g. "US" + ".logon.battle.net"), so there's no static string to
// find-and-replace for it. Instead, this hooks the client's import of
// ws2_32.dll!gethostbyname so that ANY hostname it asks to resolve comes
// back as the target address - sidestepping the string entirely.
//
// Mechanism: allocate a small RWX block in the target process containing a
// handful of bytes of shellcode plus a fake `hostent` structure describing
// the target address, then overwrite the client's Import Address Table slot
// for gethostbyname to point at that shellcode instead of the real function.
// The client builds logon hostnames at runtime (region + ".logon.battle.net"),
// so there is no static string to patch. Soft/Creep uses ws2_32!getaddrinfo
// for that connect; classic code uses gethostbyname. gethostbyname still
// returns the login IPv4 for every name. getaddrinfo fakes "logon.battle"
// and "batt" (battle.net / battlenet.com.cn); names containing "patch"
// (enUS.patch.battle.net) and gethostname() stay on the real ws2_32 import.
// freeaddrinfo is
// a no-op because the fake getaddrinfo returns a static block (Creep copies
// then frees). Real getaddrinfo results then leak — acceptable for one session.
public static class DnsResolverHook
{
    public static void Install(IntPtr hProcess, IntPtr baseAddress, byte[] fileBuffer, byte[] liveBuffer, bool is64Bit, string targetAddress)
    {
        var host = targetAddress.Split(':')[0];
        var ip = IPAddress.Parse(host);
        var ipBytes = ip.GetAddressBytes();
        if (ipBytes.Length != 4)
            throw new NotSupportedException("Only IPv4 targets are supported.");

        // IAT RVAs must come from the file; the live IAT is already resolved.
        InstallGethostbyname(hProcess, baseAddress, fileBuffer, is64Bit, ipBytes, targetAddress);

        // Wow-64 Soft hop: B7FBA0 → C1B1A0 FQDN → Tumor A75CC0, which
        // uses gethostbyname (not getaddrinfo / C888E0). getaddrinfo is
        // still hooked for curl / Agent GET /agent ("batt*").
        if (is64Bit)
        {
            InstallGetAddrInfo(hProcess, baseAddress, fileBuffer, ipBytes);
            // Stock Sunken port is 0 without type-0xC. connect(:0) never
            // hits authnet; rewrite only port-0 sockaddrs to loginIPv4:1119.
            // Do not steal port 1119 — patch.battle.net:1119 is real HTTP.
            // Hop trampoline first — connect port rewrite still needed for :0.
            var hopBlock = PatchSoftHopAfterA75EarlyOut(hProcess, baseAddress, liveBuffer, ipBytes);
            InstallConnectPortHook(hProcess, baseAddress, fileBuffer, ipBytes, hopBlock);
        }
    }

    // A75CC0 jz-not-connected → set flag, original epilogue.
    // B7FBA0 FQDN C1B1A0 → if flag: B80270 + Manager+16 (no Auth 9609/9610).
    // Soft2-good plateau (00:54): Soft2 89→Success, mute hop / Connecting.
    // Do NOT patch Soft Success / BE6B80 / call B53E40 from launcher — Soft2
    // 178 B or Soft-finish assert (B5411E) every time Soft-finish is armed.
    private static IntPtr PatchSoftHopAfterA75EarlyOut(IntPtr hProcess, IntPtr baseAddress, byte[] live, byte[] ipBytes)
    {
        byte[] prefix = [0x48, 0x8B, 0x54, 0x24, 0x48, 0x48, 0x8B, 0xCB, 0xE8];
        byte[] fqdnTail = [0x90, 0xE9, 0xCB, 0x01, 0x00, 0x00];
        byte[] emptyTail = [0x90, 0x48, 0x8B, 0x8D, 0x88, 0x00, 0x00, 0x00];

        var sites = new List<int>();
        for (var start = 0; ;)
        {
            var at = IndexOf(live, prefix, start);
            if (at < 0)
                break;
            var tailAt = at + prefix.Length + 4;
            if (TailEq(live, tailAt, fqdnTail) || TailEq(live, tailAt, emptyTail))
                sites.Add(at + prefix.Length - 1);
            start = at + 1;
        }

        if (sites.Count != 2)
        {
            throw new InvalidOperationException(
                $"Could not find B7FBA0 C1B1A0 sites (found {sites.Count}, expected 2).");
        }

        foreach (var jzRva in new[] { 0xA75CE4, 0xA75CF2 })
        {
            if (jzRva + 6 > live.Length || live[jzRva] != 0x0F || live[jzRva + 1] != 0x84)
                throw new InvalidOperationException($"A75CC0 early-out jz missing at RVA 0x{jzRva:X}.");
        }

        const int rvaC1B1A0 = 0xC1B1A0;
        const int rvaB80270 = 0xB80270;
        const int rvaA75EC9 = 0xA75EC9;
        const int a75Stub = 0x00;
        const int c1Stub = 0x10;
        const int flagOff = 0x98;
        const int pendingAuthOff = 0xA0;
        const int pendingFlagOff = 0xA8;
        const int pendingSunkenOff = 0xB0;
        const int packed6 = 0xB8;
        var block = new byte[0xC0];

        // A75CC0 early-out: flag=1; jmp original "Can't resolve" epilogue.
        block[a75Stub + 0] = 0xC6;
        block[a75Stub + 1] = 0x05;
        WriteInt32(block, a75Stub + 2, flagOff - (a75Stub + 7));
        block[a75Stub + 6] = 0x01;
        block[a75Stub + 7] = 0xE9;

        // C1B1A0 wrap. push rbx/r12/r13; sub rsp,30h (16-byte aligned for calls).
        // Do not set Auth+9609/9610 here (armed Soft-finish on mute hop → 00:36 AV).
        var s = c1Stub;
        const int clearFlag = 0x70;
        const int epilogue = 0x77;
        block[s + 0x00] = 0x53;
        block[s + 0x01] = 0x41;
        block[s + 0x02] = 0x54;
        block[s + 0x03] = 0x41;
        block[s + 0x04] = 0x55;
        block[s + 0x05] = 0x48;
        block[s + 0x06] = 0x83;
        block[s + 0x07] = 0xEC;
        block[s + 0x08] = 0x30;
        block[s + 0x09] = 0x48;
        block[s + 0x0A] = 0x89;
        block[s + 0x0B] = 0xCB;
        block[s + 0x0C] = 0xE8; // call C1B1A0
        block[s + 0x11] = 0x80;
        block[s + 0x12] = 0x3D;
        WriteInt32(block, s + 0x13, flagOff - (s + 0x18));
        block[s + 0x17] = 0x01;
        block[s + 0x18] = 0x75;
        block[s + 0x19] = (byte)(epilogue - 0x1A);
        block[s + 0x1A] = 0x48;
        block[s + 0x1B] = 0x8B;
        block[s + 0x1C] = 0x43;
        block[s + 0x1D] = 0x08;
        block[s + 0x1E] = 0x48;
        block[s + 0x1F] = 0x85;
        block[s + 0x20] = 0xC0;
        block[s + 0x21] = 0x74;
        block[s + 0x22] = (byte)(epilogue - 0x23);
        block[s + 0x23] = 0x49;
        block[s + 0x24] = 0x89;
        block[s + 0x25] = 0xC5;
        block[s + 0x26] = 0x4C;
        block[s + 0x27] = 0x8B;
        block[s + 0x28] = 0xA0;
        WriteInt32(block, s + 0x29, 0xDA0);
        block[s + 0x2D] = 0x4D;
        block[s + 0x2E] = 0x85;
        block[s + 0x2F] = 0xE4;
        block[s + 0x30] = 0x74;
        block[s + 0x31] = (byte)(epilogue - 0x32);
        // 16-byte NOP slot where Auth+9609/9610 used to be set (keeps later offsets).
        for (var i = 0; i < 16; i++)
            block[s + 0x32 + i] = 0x90;
        block[s + 0x42] = 0x4C;
        block[s + 0x43] = 0x89;
        block[s + 0x44] = 0x2D;
        WriteInt32(block, s + 0x45, pendingAuthOff - (s + 0x49));
        block[s + 0x49] = 0xC6;
        block[s + 0x4A] = 0x05;
        WriteInt32(block, s + 0x4B, pendingFlagOff - (s + 0x50));
        block[s + 0x4F] = 0x01;
        block[s + 0x50] = 0x4C;
        block[s + 0x51] = 0x89;
        block[s + 0x52] = 0xE1;
        block[s + 0x53] = 0x48;
        block[s + 0x54] = 0x8D;
        block[s + 0x55] = 0x15;
        WriteInt32(block, s + 0x56, packed6 - (s + 0x5A));
        block[s + 0x5A] = 0xE8; // call B80270
        block[s + 0x5F] = 0x48;
        block[s + 0x60] = 0x85;
        block[s + 0x61] = 0xC0;
        block[s + 0x62] = 0x74;
        block[s + 0x63] = (byte)(clearFlag - 0x64);
        block[s + 0x64] = 0x49;
        block[s + 0x65] = 0x89;
        block[s + 0x66] = 0x44;
        block[s + 0x67] = 0x24;
        block[s + 0x68] = 0x10;
        block[s + 0x69] = 0x48;
        block[s + 0x6A] = 0x89;
        block[s + 0x6B] = 0x05;
        WriteInt32(block, s + 0x6C, pendingSunkenOff - (s + 0x70));
        block[s + clearFlag + 0x00] = 0xC6;
        block[s + clearFlag + 0x01] = 0x05;
        WriteInt32(block, s + clearFlag + 0x02, flagOff - (s + clearFlag + 0x07));
        block[s + clearFlag + 0x06] = 0x00;
        block[s + epilogue + 0x00] = 0x48;
        block[s + epilogue + 0x01] = 0x83;
        block[s + epilogue + 0x02] = 0xC4;
        block[s + epilogue + 0x03] = 0x30;
        block[s + epilogue + 0x04] = 0x41;
        block[s + epilogue + 0x05] = 0x5D;
        block[s + epilogue + 0x06] = 0x41;
        block[s + epilogue + 0x07] = 0x5C;
        block[s + epilogue + 0x08] = 0x5B;
        block[s + epilogue + 0x09] = 0xC3;
        ipBytes.CopyTo(block, packed6);
        block[packed6 + 4] = 0x04;
        block[packed6 + 5] = 0x5F; // htons(1119)

        var alloc = AllocateNear(hProcess, baseAddress, SizeOfImage(live), block.Length);
        var abs = alloc.ToInt64();
        var image = baseAddress.ToInt64();
        WriteInt32(block, a75Stub + 8, Rel32(image + rvaA75EC9, abs + a75Stub + 12));
        WriteInt32(block, s + 0x0D, Rel32(image + rvaC1B1A0, abs + s + 0x11));
        WriteInt32(block, s + 0x5B, Rel32(image + rvaB80270, abs + s + 0x5F));
        WriteAll(hProcess, alloc, block);

        foreach (var jzRva in new[] { 0xA75CE4, 0xA75CF2 })
        {
            var site = IntPtr.Add(baseAddress, jzRva);
            var rel = Rel32(abs + a75Stub, site.ToInt64() + 6);
            var jz = new byte[6];
            jz[0] = 0x0F;
            jz[1] = 0x84;
            BitConverter.GetBytes(rel).CopyTo(jz, 2);
            WriteCode(hProcess, site, jz);
        }

        foreach (var e8Rva in sites)
        {
            var site = IntPtr.Add(baseAddress, e8Rva);
            var rel = Rel32(abs + c1Stub, site.ToInt64() + 5);
            var call = new byte[5];
            call[0] = 0xE8;
            BitConverter.GetBytes(rel).CopyTo(call, 1);
            WriteCode(hProcess, site, call);
        }

        FlushInstructionCache(hProcess, IntPtr.Zero, UIntPtr.Zero);
        return alloc;
    }

    private static int Rel32(long dest, long nextIp)
    {
        var rel = dest - nextIp;
        if (rel < int.MinValue || rel > int.MaxValue)
            throw new InvalidOperationException("Soft hop trampoline is too far for a near call.");
        return (int)rel;
    }

    private static bool TailEq(byte[] image, int offset, byte[] tail)
    {
        if (offset < 0 || offset + tail.Length > image.Length)
            return false;
        for (var i = 0; i < tail.Length; i++)
        {
            if (image[offset + i] != tail[i])
                return false;
        }
        return true;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        var last = haystack.Length - needle.Length;
        for (var i = start; i <= last; i++)
        {
            var n = 0;
            while (n < needle.Length && haystack[i + n] == needle[n])
                n++;
            if (n == needle.Length)
                return i;
        }
        return -1;
    }

    private static void InstallGethostbyname(IntPtr hProcess, IntPtr baseAddress, byte[] moduleBuffer, bool is64Bit, byte[] ipBytes, string targetAddress)
    {
        // gethostbyname is ordinal 52 in ws2_32.dll - stable/documented since
        // Windows 95, and how this client actually imports it (by ordinal,
        // not by name, like most of the classic Winsock 1.1 API surface).
        var iatSlotRva = PeImportTable.FindIatSlotRva(moduleBuffer, "ws2_32.dll", "gethostbyname", knownOrdinal: 52);
        if (iatSlotRva == 0)
            throw new InvalidOperationException("Could not find gethostbyname import in the client.");

        var block = BuildFakeHostentBlock(is64Bit, ipBytes, targetAddress, out var shellcodeOffset);
        var allocBase = AllocateRwX(hProcess, block.Length);
        PatchBlockPointers(block, allocBase, is64Bit, shellcodeOffset);
        WriteAll(hProcess, allocBase, block);
        RedirectIat(hProcess, baseAddress, iatSlotRva, nint.Add(allocBase, shellcodeOffset), is64Bit);
    }

    private static void InstallGetAddrInfo(IntPtr hProcess, IntPtr baseAddress, byte[] moduleBuffer, byte[] ipBytes)
    {
        var getAddrRva = PeImportTable.FindIatSlotRva(moduleBuffer, "ws2_32.dll", "getaddrinfo");
        if (getAddrRva == 0)
            throw new InvalidOperationException("Could not find getaddrinfo import in the client.");

        var iatSlot = IntPtr.Add(baseAddress, getAddrRva);
        var origBytes = new byte[8];
        if (!ReadProcessMemory(hProcess, iatSlot, origBytes, origBytes.Length, out _))
            throw new Win32Exception("Could not read the client's getaddrinfo import");
        var originalGetAddrInfo = BitConverter.ToInt64(origBytes, 0);
        if (originalGetAddrInfo == 0)
            throw new InvalidOperationException("getaddrinfo IAT slot is empty.");

        var block = BuildGetAddrInfoBlock(ipBytes);
        var allocBase = AllocateRwX(hProcess, block.Length);
        PatchGetAddrInfoPointers(block, allocBase, originalGetAddrInfo);
        WriteAll(hProcess, allocBase, block);
        RedirectIat(hProcess, baseAddress, getAddrRva, allocBase, is64Bit: true);

        var freeRva = PeImportTable.FindIatSlotRva(moduleBuffer, "ws2_32.dll", "freeaddrinfo");
        if (freeRva != 0)
            RedirectIat(hProcess, baseAddress, freeRva, nint.Add(allocBase, FreeAddrInfoOffset), is64Bit: true);
    }

    // ws2_32!connect. Soft's second hop uses this after getaddrinfo (and the
    // numeric-IP path skips getaddrinfo entirely). sockaddr port 0 → login:1119.
    private static void InstallConnectPortHook(IntPtr hProcess, IntPtr baseAddress, byte[] moduleBuffer, byte[] ipBytes, IntPtr hopBlock)
    {
        var iatRva = PeImportTable.FindIatSlotRva(moduleBuffer, "ws2_32.dll", "connect", knownOrdinal: 4);
        if (iatRva == 0)
            throw new InvalidOperationException("Could not find connect import in the client.");

        var iatSlot = IntPtr.Add(baseAddress, iatRva);
        var origBytes = new byte[8];
        if (!ReadProcessMemory(hProcess, iatSlot, origBytes, origBytes.Length, out _))
            throw new Win32Exception("Could not read the client's connect import");
        var originalConnect = BitConverter.ToInt64(origBytes, 0);
        if (originalConnect == 0)
            throw new InvalidOperationException("connect IAT slot is empty.");

        _ = hopBlock;
        var stub = BuildConnectPortStub(ipBytes, originalConnect);
        var allocBase = AllocateRwX(hProcess, stub.Length);
        WriteAll(hProcess, allocBase, stub);
        RedirectIat(hProcess, baseAddress, iatRva, allocBase, is64Bit: true);
    }

    // Port 0 → login:1119 only. Soft-finish from connect/B80270 crashes
    // (23:40/23:54) when WowConnection is not ready.
    private static byte[] BuildConnectPortStub(byte[] ipBytes, long originalConnect)
    {
        const int origOff = 37;
        const int stubSize = 58;

        byte[] stub =
        [
            0x48, 0x85, 0xD2,
            0x74, 0x20,
            0x49, 0x83, 0xF8, 0x10,
            0x72, 0x1A,
            0x66, 0x83, 0x3A, 0x02,
            0x75, 0x14,
            0x66, 0x83, 0x7A, 0x02, 0x00,
            0x75, 0x0D,
            0x66, 0xC7, 0x42, 0x02, 0x04, 0x5F,
            0xC7, 0x42, 0x04, 0, 0, 0, 0,
            0x48, 0x83, 0xEC, 0x28,
            0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0,
            0xFF, 0xD0,
            0x48, 0x83, 0xC4, 0x28,
            0xC3
        ];

        if (stub.Length != stubSize)
            throw new InvalidOperationException($"connect stub size {stub.Length}, expected {stubSize}.");

        ipBytes.CopyTo(stub, 33);
        BitConverter.GetBytes(originalConnect).CopyTo(stub, 43);

        stub[4] = (byte)(origOff - 5);
        stub[10] = (byte)(origOff - 11);
        stub[16] = (byte)(origOff - 17);
        stub[23] = (byte)(origOff - 24);

        return stub;
    }

    // [scan+tailjmp 87][fake 27][pad][addrinfo 48][sockaddr_in 16][freeaddrinfo]
    // Fake addrinfo/sockaddr are immutable after install. Creep caches the
    // pointer (C75610); Agent GET /agent also fakes "batt*" and used to
    // overwrite the same sockaddr port while Sunken still held it.
    private const int GetAddrInfoPrefixSize = 87;
    private const int GetAddrInfoFakeSize = 27;
    private const int GetAddrInfoStubSize = GetAddrInfoPrefixSize + GetAddrInfoFakeSize;
    private const int AddrInfoOffset = 0xD0;
    private const int SockAddrOffset = AddrInfoOffset + 48;
    private const int FreeAddrInfoOffset = SockAddrOffset + 16;
    private const int OriginalGetAddrInfoImmOffset = 4;
    // fake: test r9,r9; jz fail (5) + mov r11,imm64 (2+8). Imm starts at +7.
    // +5 was wrong: PatchGetAddrInfoPointers overwrote 49 BB, so a "batt*"
    // hit fell into the pointer bytes (NATIT AV write @ null+4, RIP mid-imm).
    private const int AddrInfoImmOffset = GetAddrInfoPrefixSize + 7;

    private static byte[] BuildGetAddrInfoBlock(byte[] ipBytes)
    {
        var block = new byte[FreeAddrInfoOffset + 3];

        // Tail-jmp the real getaddrinfo for null/"patch*" names. Fake
        // "logon.battle" and "batt" (battle.net / battlenet.com.cn) so the
        // B7FBA0 hop cannot fall through to retail :1119.
        byte[] prefix =
        [
            0xEB, 0x0C,
            0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0,
            0xFF, 0xE0,
            0x48, 0x85, 0xC9, 0x74, 0xEF,
            0x4C, 0x8B, 0xD1,
            0x41, 0x80, 0x3A, 0x00, 0x74, 0xE6,
            0x41, 0x81, 0x3A, 0x70, 0x61, 0x74, 0x63, 0x75, 0x07,
            0x41, 0x80, 0x7A, 0x04, 0x68, 0x74, 0xD6,
            0x41, 0x81, 0x3A, 0x6C, 0x6F, 0x67, 0x6F, 0x75, 0x14,
            0x41, 0x81, 0x7A, 0x04, 0x6E, 0x2E, 0x62, 0x61, 0x75, 0x0A,
            0x41, 0x81, 0x7A, 0x08, 0x74, 0x74, 0x6C, 0x65, 0x74, 0x0E,
            0x41, 0x81, 0x3A, 0x62, 0x61, 0x74, 0x74, 0x74, 0x05,
            0x49, 0xFF, 0xC2, 0xEB, 0xBF
        ];
        // Immutable result: *ppResult = &addrinfo; return 0. Port/IP/family
        // are filled once at install (htons(1119) + login IPv4).
        byte[] fake =
        [
            0x4D, 0x85, 0xC9, 0x74, 0x10,                   // test r9,r9; jz fail
            0x49, 0xBB, 0, 0, 0, 0, 0, 0, 0, 0,             // mov r11, addrinfo
            0x4D, 0x89, 0x19,                               // mov [r9], r11
            0x31, 0xC0, 0xC3,                               // xor eax,eax; ret
            0xB8, 0x04, 0x00, 0x00, 0x00, 0xC3              // fail: mov eax,4; ret
        ];
        if (prefix.Length != GetAddrInfoPrefixSize)
            throw new InvalidOperationException($"getaddrinfo prefix size {prefix.Length}, expected {GetAddrInfoPrefixSize}.");
        if (fake.Length != GetAddrInfoFakeSize)
            throw new InvalidOperationException($"getaddrinfo fake size {fake.Length}, expected {GetAddrInfoFakeSize}.");
        if (GetAddrInfoStubSize > AddrInfoOffset)
            throw new InvalidOperationException("getaddrinfo stub overlaps addrinfo.");

        Array.Copy(prefix, 0, block, 0, prefix.Length);
        Array.Copy(fake, 0, block, prefix.Length, fake.Length);

        // addrinfo: family=AF_INET, socktype=STREAM, protocol=TCP, addrlen=16
        WriteInt32(block, AddrInfoOffset + 4, 2);
        WriteInt32(block, AddrInfoOffset + 8, 1);
        WriteInt32(block, AddrInfoOffset + 12, 6);
        BitConverter.GetBytes((long)16).CopyTo(block, AddrInfoOffset + 16);

        // sockaddr_in
        WriteInt16(block, SockAddrOffset, 2); // AF_INET
        WriteInt16(block, SockAddrOffset + 2, unchecked((short)0x5F04)); // htons(1119)
        Array.Copy(ipBytes, 0, block, SockAddrOffset + 4, 4);

        block[FreeAddrInfoOffset] = 0x31; // xor eax, eax
        block[FreeAddrInfoOffset + 1] = 0xC0;
        block[FreeAddrInfoOffset + 2] = 0xC3;
        return block;
    }

    private static void PatchGetAddrInfoPointers(byte[] block, IntPtr allocBase, long originalGetAddrInfo)
    {
        var addrInfo = allocBase.ToInt64() + AddrInfoOffset;
        var sockAddr = allocBase.ToInt64() + SockAddrOffset;
        BitConverter.GetBytes(originalGetAddrInfo).CopyTo(block, OriginalGetAddrInfoImmOffset);
        BitConverter.GetBytes(addrInfo).CopyTo(block, AddrInfoImmOffset);
        BitConverter.GetBytes(sockAddr).CopyTo(block, AddrInfoOffset + 32); // ai_addr
    }

    private static IntPtr AllocateRwX(IntPtr hProcess, int size)
    {
        var alloc = VirtualAllocEx(
            hProcess, IntPtr.Zero, (UIntPtr)size,
            AllocationType.MEM_COMMIT | AllocationType.MEM_RESERVE,
            MemoryProtection.PAGE_EXECUTE_READWRITE);
        if (alloc == IntPtr.Zero)
            throw new Win32Exception("Could not allocate memory in the client process");
        return alloc;
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
        // Allocate past SizeOfImage — using file length lands inside the PE
        // mapping gap (23:40 execute fault just before trampoline).
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

        throw new Win32Exception("Could not allocate Soft hop trampoline near Wow-64.");
    }

    private static void WriteCode(IntPtr hProcess, IntPtr address, byte[] data)
    {
        if (!VirtualProtectEx(hProcess, address, (UIntPtr)data.Length, MemoryProtection.PAGE_EXECUTE_READWRITE, out var oldProtect))
            throw new Win32Exception("Could not unprotect client memory for Soft hop hook");

        try
        {
            if (!WriteProcessMemory(hProcess, address, data, data.Length, out _))
                throw new Win32Exception("Could not write Soft hop hook into client memory");
        }
        finally
        {
            VirtualProtectEx(hProcess, address, (UIntPtr)data.Length, oldProtect, out _);
        }
    }

    private static void WriteAll(IntPtr hProcess, IntPtr address, byte[] data)
    {
        if (!WriteProcessMemory(hProcess, address, data, data.Length, out _))
            throw new Win32Exception("Could not write the resolver hook into client memory");
    }

    private static void RedirectIat(IntPtr hProcess, IntPtr baseAddress, int iatSlotRva, nint shellcodeAddress, bool is64Bit)
    {
        var iatSlotAddress = IntPtr.Add(baseAddress, iatSlotRva);
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

    private static void WriteInt32(byte[] block, int offset, int value) => BitConverter.GetBytes(value).CopyTo(block, offset);

    // Layout: [shellcode][hostent][addrList: 2 pointers][ip bytes: 4][name string]
    // Everything after the shellcode is data the shellcode's returned hostent
    // pointer refers into - built once we know the allocation's real address.
    private static byte[] BuildFakeHostentBlock(bool is64Bit, byte[] ipBytes, string name, out int shellcodeOffset)
    {
        var ptrSize = is64Bit ? 8 : 4;
        var hostentSize = is64Bit ? 32 : 16; // see layout notes in PatchBlockPointers
        var shellcodeSize = is64Bit ? 16 : 8;
        var nameBytes = Encoding.ASCII.GetBytes(name.Split(':')[0] + '\0');

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
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, MemoryProtection flNewProtect, out MemoryProtection lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

    #endregion
}
