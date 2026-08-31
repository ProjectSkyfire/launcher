using System.Text;

namespace SkyFireLauncher.Realm;

internal static class ClientImagePatcher
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

    public static void Apply(IRemoteProcess process, nint baseAddress, int moduleSize, string exePath, string targetAddress, bool enableAuthnetLogin)
    {
        // Search a reconstructed virtual image from disk. Reading the whole live
        // SizeOfImage under ptrace (tens of MB) routinely kills Wine/Wow on Linux.
        var fileBuffer = File.ReadAllBytes(exePath);
        var searchBuffer = PeImportTable.BuildVirtualImage(fileBuffer);
        _ = moduleSize;

        foreach (var (patternText, replacementFormat) in PatchTargets)
        {
            var pattern = Encoding.ASCII.GetBytes(patternText + '\0');
            var replacement = Encoding.ASCII.GetBytes(string.Format(replacementFormat, targetAddress) + '\0');
            if (replacement.Length > pattern.Length)
                throw new InvalidOperationException($"Replacement for '{patternText}' is longer than the original string.");

            // Pad with NULs so we do not leave trailing original hostname bytes.
            if (replacement.Length < pattern.Length)
            {
                var padded = new byte[pattern.Length];
                Array.Copy(replacement, padded, replacement.Length);
                replacement = padded;
            }

            var offset = 0;
            while ((offset = IndexOf(searchBuffer, pattern, offset)) >= 0)
            {
                process.Write(nint.Add(baseAddress, offset), replacement);
                offset += pattern.Length;
            }
        }

        var is64Bit = PeImportTable.IsPe64Bit(fileBuffer);
        var loginHost = targetAddress.Split(':')[0];

        // DNS hook needs remote mmap via ptrace syscall injection, which crashes
        // Proton/Wine often. Keep it on Windows; on Linux rely on string patches +
        // Config.wtf realmlist (authnet can revisit a safer hook later).
        if (!OperatingSystem.IsLinux())
            DnsResolverHook.Install(process, baseAddress, fileBuffer, is64Bit, loginHost);

        if (LoginFlowPatches.TryFindEmailOpcode(searchBuffer, is64Bit, out var emailOffset))
            process.Write(nint.Add(baseAddress, emailOffset), LoginFlowPatches.EmailOpcode(enableAuthnetLogin));
        else if (enableAuthnetLogin)
            throw new InvalidOperationException("Could not find the client's Email login-flow branch to restore BattlenetLogin.");

        // Soft/authnet: hostname + DNS + Email JZ only. Classic Send/User/RaF/Rcv
        // patches force GruntLogin and block Soft Join / .logon.battle.net.
        if (enableAuthnetLogin)
            return;

        var loginFlowPatches = is64Bit ? LoginFlowPatches.X64 : LoginFlowPatches.X86;

        foreach (var (_, pattern, replacement) in loginFlowPatches)
        {
            var matchOffset = IndexOfWildcard(searchBuffer, pattern, 0);
            if (matchOffset < 0)
                continue;

            process.Write(nint.Add(baseAddress, matchOffset), replacement);
        }
    }

    /// <summary>
    /// Applies hostname + login-flow patches to a PE file on disk (used on Linux
    /// so Proton can run with a full Steam runtime / GUI without host ptrace).
    /// </summary>
    public static void ApplyToFile(string exePath, string targetAddress, bool enableAuthnetLogin)
    {
        var fileBuffer = File.ReadAllBytes(exePath);
        var searchBuffer = PeImportTable.BuildVirtualImage(fileBuffer);
        var dirty = false;

        foreach (var (patternText, replacementFormat) in PatchTargets)
        {
            var pattern = Encoding.ASCII.GetBytes(patternText + '\0');
            var replacement = Encoding.ASCII.GetBytes(string.Format(replacementFormat, targetAddress) + '\0');
            if (replacement.Length > pattern.Length)
                throw new InvalidOperationException($"Replacement for '{patternText}' is longer than the original string.");

            if (replacement.Length < pattern.Length)
            {
                var padded = new byte[pattern.Length];
                Array.Copy(replacement, padded, replacement.Length);
                replacement = padded;
            }

            var rva = 0;
            while ((rva = IndexOf(searchBuffer, pattern, rva)) >= 0)
            {
                if (!PeImportTable.TryRvaToFileOffset(fileBuffer, rva, out var fileOffset))
                {
                    rva += pattern.Length;
                    continue;
                }

                Array.Copy(replacement, 0, fileBuffer, fileOffset, replacement.Length);
                dirty = true;
                rva += pattern.Length;
            }
        }

        var is64Bit = PeImportTable.IsPe64Bit(fileBuffer);
        if (LoginFlowPatches.TryFindEmailOpcode(searchBuffer, is64Bit, out var emailRva) &&
            PeImportTable.TryRvaToFileOffset(fileBuffer, emailRva, out var emailFileOffset))
        {
            var emailOpcode = LoginFlowPatches.EmailOpcode(enableAuthnetLogin);
            Array.Copy(emailOpcode, 0, fileBuffer, emailFileOffset, emailOpcode.Length);
            dirty = true;
        }
        else if (enableAuthnetLogin)
            throw new InvalidOperationException("Could not find the client's Email login-flow branch to restore BattlenetLogin.");

        if (enableAuthnetLogin)
        {
            if (dirty)
                File.WriteAllBytes(exePath, fileBuffer);
            return;
        }

        var loginFlowPatches = is64Bit ? LoginFlowPatches.X64 : LoginFlowPatches.X86;

        foreach (var (_, pattern, replacement) in loginFlowPatches)
        {
            var rva = IndexOfWildcard(searchBuffer, pattern, 0);
            if (rva < 0)
                continue;
            if (!PeImportTable.TryRvaToFileOffset(fileBuffer, rva, out var fileOffset))
                continue;

            Array.Copy(replacement, 0, fileBuffer, fileOffset, replacement.Length);
            dirty = true;
        }

        if (dirty)
            File.WriteAllBytes(exePath, fileBuffer);
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
}
