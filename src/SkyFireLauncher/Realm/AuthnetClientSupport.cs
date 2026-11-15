using System.IO;
using System.Reflection;
using System.Numerics;
using System.Security.Cryptography;

namespace SkyFireLauncher.Realm;

internal static class AuthnetClientSupport
{
    private const string X64BaseModuleHash = "0a3afee2cade3a0e8b458c4b4660104cac7fc50e2ca9bef0d708942e77f15c1d";
    private const string X86BaseModuleHash = "8f52906a2c85b416a595702251570f96d3522f39237603115f2f1ab24962043c";
    private const string X64RiskModuleHash = "8c43bda10be33a32abbc09fb2279126c7f5953336391276cff588565332fcd40";
    private const string X86RiskModuleHash = "5e298e530698af905e1247e51ef0b109b352ac310ce7802a1f63613db980ed17";

    internal static readonly byte[] ModulusPattern =
    [
        0x91, 0xD5, 0x9B, 0xB7, 0xD4, 0xE1, 0x83, 0xA5
    ];

    internal static readonly byte[] ReplacementModulus =
    [
        0x5F, 0xD6, 0x80, 0x0B, 0xA7, 0xFF, 0x01, 0x40, 0xC7, 0xBC, 0x8E, 0xF5, 0x6B, 0x27, 0xB0, 0xBF,
        0xF0, 0x1D, 0x1B, 0xFE, 0xDD, 0x0B, 0x1F, 0x3D, 0xB6, 0x6F, 0x1A, 0x48, 0x0D, 0xFB, 0x51, 0x08,
        0x65, 0x58, 0x4F, 0xDB, 0x5C, 0x6E, 0xCF, 0x64, 0xCB, 0xC1, 0x6B, 0x2E, 0xB8, 0x0F, 0x5D, 0x08,
        0x5D, 0x89, 0x06, 0xA9, 0x77, 0x8B, 0x9E, 0xAA, 0x04, 0xB0, 0x83, 0x10, 0xE2, 0x15, 0x4D, 0x08,
        0x77, 0xD4, 0x7A, 0x0E, 0x5A, 0xB0, 0xBB, 0x00, 0x61, 0xD7, 0xA6, 0x75, 0xDF, 0x06, 0x64, 0x88,
        0xBB, 0xB9, 0xCA, 0xB0, 0x18, 0x8B, 0x54, 0x13, 0xE2, 0xCB, 0x33, 0xDF, 0x17, 0xD8, 0xDA, 0xA9,
        0xA5, 0x60, 0xA3, 0x1F, 0x4E, 0x27, 0x05, 0x98, 0x6F, 0xAA, 0xEE, 0x14, 0x3B, 0xF3, 0x97, 0xA8,
        0x12, 0x02, 0x94, 0x0D, 0x84, 0xDC, 0x0E, 0xF1, 0x76, 0x23, 0x95, 0x36, 0x13, 0xF9, 0xA9, 0xC5,
        0x48, 0xDB, 0xDA, 0x86, 0xBE, 0x29, 0x22, 0x54, 0x44, 0x9D, 0x9F, 0x80, 0x7B, 0x07, 0x80, 0x30,
        0xEA, 0xD2, 0x83, 0xCC, 0xCE, 0x37, 0xD1, 0xD1, 0xCF, 0x85, 0xBE, 0x91, 0x25, 0xCE, 0xC0, 0xCC,
        0x55, 0xC8, 0xC0, 0xFB, 0x38, 0xC5, 0x49, 0x03, 0x6A, 0x02, 0xA9, 0x9F, 0x9F, 0x86, 0xFB, 0xC7,
        0xCB, 0xC6, 0xA5, 0x82, 0xA2, 0x30, 0xC2, 0xAC, 0xE6, 0x98, 0xDA, 0x83, 0x64, 0x43, 0x7F, 0x0D,
        0x13, 0x18, 0xEB, 0x90, 0x53, 0x5B, 0x37, 0x6B, 0xE6, 0x0D, 0x80, 0x1E, 0xEF, 0xED, 0xC7, 0xB8,
        0x68, 0x9B, 0x4C, 0x09, 0x7B, 0x60, 0xB2, 0x57, 0xD8, 0x59, 0x8D, 0x7F, 0xEA, 0xCD, 0xEB, 0xC4,
        0x60, 0x9F, 0x45, 0x7A, 0xA9, 0x26, 0x8A, 0x2F, 0x85, 0x0C, 0xF2, 0x19, 0xC6, 0x53, 0x92, 0xF7,
        0xF0, 0xB8, 0x32, 0xCB, 0x5B, 0x66, 0xCE, 0x51, 0x54, 0xB4, 0xC3, 0xD3, 0xD4, 0xDC, 0xB3, 0xEE
    ];

    internal static readonly (string Name, byte[] Pattern, byte[] Replacement, bool Wildcard)[] X64RuntimePatches =
    [
        ("server address", [0x8B, 0x02, 0x89, 0x41, 0x0C, 0x48, 0x8B, 0xC1, 0xC3],
            [0xB8, 0xD5, 0xF8, 0x7F, 0x82, 0x89, 0x41, 0x0C, 0x48, 0x8B, 0xC1, 0xC3], false),
        ("module signature", [0xE8, 0x00, 0x00, 0x00, 0x00, 0x84, 0xC0, 0x0F, 0x85, 0x88, 0x00, 0x00, 0x00, 0x45, 0x33, 0xC0],
            [0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0xE9], true)
    ];

    internal static readonly (string Name, byte[] Pattern, byte[] Replacement, bool Wildcard)[] X86RuntimePatches =
    [
        ("server address", [0x8B, 0x75, 0x08, 0x8D, 0x78, 0x0C],
            [0xC7, 0x40, 0x0C, 0xD5, 0xF8, 0x7F, 0x82], false),
        ("module signature", [0xE8, 0x00, 0x00, 0x00, 0x00, 0x84, 0xC0, 0x75, 0x5F, 0x33, 0xC0],
            [0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0xEB], true)
    ];

    // Build 18414 password-proof public key (128 little-endian bytes), shared by x86/x64.
    private static readonly byte[] OriginalPasswordProofPublicKey = Convert.FromHexString("71683a6cff38f8558038ff6a2d4c30f952e9cbb02f7dde3a23ffcdc18bd9c44e58a6d568dc30f9b156eb921b94d3106f2c9f55170293c556b6c0fc302bfa8da04bcdf0d78d96b8e96bf8ad8952fcf3a6488efe5816a2aaa3d9cecbff39c56a261a009a3d8d06546eae8af26757a45fcf153ab9ce3decca3b547d9eab7b256842");
    private static readonly BigInteger PasswordProofModulus = new(Convert.FromHexString("a348f5dcfb382d0b88a3c932e93158156e9817733bf0a4028e0ec4320b9be13adb6b4f044c0ef111d3ff2792edc408fb7b424d49e572ecb56d9d2a9655854445ed756813ac7c18b7b6ccb8537947739b19955f8f18ba4fcac5dddfa1f1cd0f5d6ba2baeb6d5133a69ac2be3450daa1702bb2da7ea5ffa367431080248663348d"), isUnsigned: true, isBigEndian: false);

    internal static byte[] ParsePasswordProofPublicKey(string publicKeyHex)
    {
        var normalized = string.Concat((publicKeyHex ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));
        if (normalized.Length != 256 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Authnet requires the server's 128-byte public key (256 hexadecimal characters). Set it in Configuration.");
        var key = Convert.FromHexString(normalized);
        var value = new BigInteger(key, isUnsigned: true, isBigEndian: false);
        if (value <= BigInteger.One || value >= PasswordProofModulus - BigInteger.One ||
            BigInteger.ModPow(value, (PasswordProofModulus - BigInteger.One) / 2, PasswordProofModulus) != BigInteger.One)
            throw new InvalidDataException("The Authnet public key is not a valid password-proof group element.");
        return key;
    }

    internal static byte[] PatchPasswordProofPublicKey(byte[] original, bool is64Bit, string publicKeyHex)
    {
        var key = ParsePasswordProofPublicKey(publicKeyHex);
        var expectedHash = is64Bit ? X64BaseModuleHash : X86BaseModuleHash;
        if (Convert.ToHexStringLower(SHA256.HashData(original)) != expectedHash)
            throw new InvalidDataException("The base authentication module has an unexpected checksum.");
        var patched = (byte[])original.Clone();
        key.CopyTo(patched, FindUnique(patched, OriginalPasswordProofPublicKey));
        // Keep the original success/failure branch: the client must verify the server proof.
        return patched;
    }

    internal static string PrepareModuleCache(string clientDirectory, bool is64Bit, string publicKeyHex)
    {
        var baseHash = is64Bit ? X64BaseModuleHash : X86BaseModuleHash;
        var modulePath = FindBaseModule(clientDirectory, baseHash);
        var module = File.ReadAllBytes(modulePath);
        var original = module;
        module = PatchPasswordProofPublicKey(original, is64Bit, publicKeyHex);

        var patchedHash = Convert.ToHexStringLower(SHA256.HashData(module));
        InstallCachedModule(patchedHash, module);
        RetirePreviousPasswordModules(original, is64Bit, patchedHash);
        InstallEmbeddedModule(is64Bit ? X64RiskModuleHash : X86RiskModuleHash);
        return patchedHash;
    }

    private static void RetirePreviousPasswordModules(byte[] original, bool is64Bit, string activeHash)
    {
        // The client discovers cached modules by type. Keep one verifier per architecture,
        // otherwise a module trusting a previous server key may be selected at login.
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Blizzard Entertainment", "Battle.net", "Cache");
        var backup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SkyFireLauncher", "AuthModuleBackups");
        var keyOffset = FindUnique(original, OriginalPasswordProofPublicKey);
        var branchOffset = FindUnique(original, is64Bit
            ? new byte[] { 0x74, 0x84, 0x48, 0x8B, 0x03 }
            : [0x74, 0x89, 0x8B, 0x16, 0x8B, 0x42, 0x04]);
        foreach (var path in Directory.EnumerateFiles(cache, "*.auth", SearchOption.AllDirectories))
        {
            if (Path.GetFileNameWithoutExtension(path).Equals(activeHash, StringComparison.OrdinalIgnoreCase))
                continue;
            if (new FileInfo(path).Length != original.Length)
                continue;
            var candidate = File.ReadAllBytes(path);
            if (candidate[branchOffset] != 0x74 && candidate[branchOffset] != 0x75)
                continue;
            var normalized = (byte[])candidate.Clone();
            OriginalPasswordProofPublicKey.CopyTo(normalized, keyOffset);
            normalized[branchOffset] = original[branchOffset];
            if (!normalized.SequenceEqual(original))
                continue; // Never move unrelated authentication modules.
            Directory.CreateDirectory(backup);
            var destination = Path.Combine(backup, Convert.ToHexStringLower(SHA256.HashData(candidate)) + ".auth");
            if (!File.Exists(destination))
                File.Copy(path, destination);
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private static void InstallEmbeddedModule(string expectedHash)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var suffix = expectedHash + ".auth";
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Required authentication resource '{suffix}' is missing.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException($"Required authentication resource '{suffix}' could not be opened.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var module = buffer.ToArray();
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(module));
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Authentication resource '{suffix}' has an unexpected checksum.");

        InstallCachedModule(expectedHash, module);
    }

    private static void InstallCachedModule(string hash, byte[] module)
    {
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Blizzard Entertainment", "Battle.net", "Cache", hash[..2], hash.Substring(2, 2));
        var cachedModulePath = Path.Combine(cacheDirectory, hash + ".auth");

        Directory.CreateDirectory(cacheDirectory);
        if (!File.Exists(cachedModulePath) || !SHA256.HashData(File.ReadAllBytes(cachedModulePath)).SequenceEqual(SHA256.HashData(module)))
        {
            if (File.Exists(cachedModulePath))
                File.SetAttributes(cachedModulePath, FileAttributes.Normal);

            var temporaryPath = cachedModulePath + ".tmp";
            File.WriteAllBytes(temporaryPath, module);
            File.Move(temporaryPath, cachedModulePath, true);
        }

        File.SetAttributes(cachedModulePath, FileAttributes.ReadOnly);
    }

    private static string FindBaseModule(string clientDirectory, string baseHash)
    {
        var moduleName = baseHash + ".auth";
        var candidates = new[]
        {
            Path.Combine(clientDirectory, moduleName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Blizzard Entertainment", "Battle.net", "Cache", baseHash[..2], baseHash.Substring(2, 2), moduleName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SkyFireLauncher", "AuthModuleBackups", moduleName)
        };

        return candidates.FirstOrDefault(path => File.Exists(path) &&
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == baseHash)
            ?? throw new FileNotFoundException($"Required authentication module '{moduleName}' was not found.");
    }

    private static int FindUnique(byte[] data, byte[] pattern)
    {
        var match = Find(data, pattern, 0);
        if (match < 0)
            throw new InvalidDataException("The authentication module does not match build 18414.");

        if (Find(data, pattern, match + 1) >= 0)
            throw new InvalidDataException("The authentication module contains an ambiguous password verifier pattern.");

        return match;
    }

    private static int Find(byte[] data, byte[] pattern, int start)
    {
        for (var i = start; i <= data.Length - pattern.Length; ++i)
        {
            var matches = true;
            for (var j = 0; j < pattern.Length; ++j)
            {
                if (data[i + j] == pattern[j])
                    continue;

                matches = false;
                break;
            }

            if (matches)
                return i;
        }

        return -1;
    }
}
