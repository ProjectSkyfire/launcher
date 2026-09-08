using System.IO;
using System.Reflection;
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

    internal static string PrepareModuleCache(string clientDirectory, bool is64Bit)
    {
        var baseHash = is64Bit ? X64BaseModuleHash : X86BaseModuleHash;
        var modulePath = FindBaseModule(clientDirectory, baseHash);
        var module = File.ReadAllBytes(modulePath);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(module));

        if (!string.Equals(actualHash, baseHash, StringComparison.Ordinal))
            throw new InvalidDataException($"The base authentication module '{modulePath}' has an unexpected checksum.");

        var passwordPattern = is64Bit
            ? new byte[] { 0x74, 0x84, 0x48, 0x8B, 0x03 }
            : [0x74, 0x89, 0x8B, 0x16, 0x8B, 0x42, 0x04];
        var matchOffset = FindUnique(module, passwordPattern);
        module[matchOffset] = 0x75;

        var patchedHash = Convert.ToHexStringLower(SHA256.HashData(module));
        InstallCachedModule(patchedHash, module);
        InstallEmbeddedModule(is64Bit ? X64RiskModuleHash : X86RiskModuleHash);
        return patchedHash;
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
                "Blizzard Entertainment", "Battle.net", "Cache", baseHash[..2], baseHash.Substring(2, 2), moduleName)
        };

        return candidates.FirstOrDefault(File.Exists)
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
