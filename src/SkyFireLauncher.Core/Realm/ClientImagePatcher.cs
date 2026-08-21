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
        var buffer = process.Read(baseAddress, moduleSize);

        foreach (var (patternText, replacementFormat) in PatchTargets)
        {
            var pattern = Encoding.ASCII.GetBytes(patternText + '\0');
            var replacement = Encoding.ASCII.GetBytes(string.Format(replacementFormat, targetAddress) + '\0');
            var offset = 0;

            while ((offset = IndexOf(buffer, pattern, offset)) >= 0)
            {
                process.Write(nint.Add(baseAddress, offset), replacement);
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
        DnsResolverHook.Install(process, baseAddress, fileBuffer, is64Bit, targetAddress);

        // This build defaults to routing login through the modern
        // Battle.net/Agent protocol, which SkyFire's authserver doesn't
        // speak. These patches force it into the classic realmList-based
        // connect flow instead, which does match SkyFire's protocol.
        //
        // "Email" is the one patch responsible for that: it forces the
        // client's login-service selector to always build GruntLogin,
        // even for an email-shaped login that would otherwise route to
        // BattlenetLogin. Skipping it when authnet login is enabled lets
        // plain usernames keep working through GRUNT (the "User" patch
        // still lets those past the client's own email-only UI gate)
        // while an email address takes the real BattlenetLogin path
        // toward realmListbn instead.
        var loginFlowPatches = is64Bit ? LoginFlowPatches.X64 : LoginFlowPatches.X86;
        if (enableAuthnetLogin)
            loginFlowPatches = loginFlowPatches.Where(p => p.Name != "Email").ToArray();

        foreach (var (_, pattern, replacement) in loginFlowPatches)
        {
            var matchOffset = IndexOfWildcard(buffer, pattern, 0);
            if (matchOffset < 0)
                continue; // best-effort: skip patches that don't match this exact build

            process.Write(nint.Add(baseAddress, matchOffset), replacement);
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
}
