using System.IO;
using System.Security.Cryptography;

namespace SkyFireLauncher.Realm;

// Wow-64 ClientSideCache looks up Password.dll as
// <cacheRoot>\Cache\<hh0>\<hh1>\<sha256>.auth. The 40-byte Soft account key is
// FourCC "auth" + this SHA256. Missing file is Battle.net #114.
// The module bytes are compiled into the launcher on the operator build
// machine so a prebuilt exe copied to a new PC can still seed the cache.
public static class AuthModuleCache
{
    public const string Sha256Hex = "0a3afee2cade3a0e8b458c4b4660104cac7fc50e2ca9bef0d708942e77f15c1d";
    public const string FileName = Sha256Hex + ".auth";

    public readonly record struct DeployResult(string Path, bool Copied, string Detail);

    public static string DestinationPath => CacheFileUnder(ProgramDataBattleNetRoot);

    public static DeployResult EnsureDeployed(string? clientLocation = null)
    {
        byte[] bytes = LoadBundledBytes();
        var destinations = EnumerateDestinations(clientLocation);
        var written = new List<string>();
        var errors = new List<string>();

        foreach (var dest in destinations)
        {
            try
            {
                if (IsValidModule(dest))
                    continue;

                var destDir = Path.GetDirectoryName(dest)
                    ?? throw new InvalidOperationException("Could not resolve Battle.net Cache path.");
                Directory.CreateDirectory(destDir);
                File.WriteAllBytes(dest, bytes);
                if (!IsValidModule(dest))
                    throw new InvalidDataException("Wrote AuthModule but SHA256 check failed: " + dest);

                written.Add(dest);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidDataException)
            {
                errors.Add(dest + " (" + ex.Message + ")");
            }
        }

        var programData = DestinationPath;
        if (IsValidModule(programData))
        {
            var detail = written.Count > 0
                ? "Installed AuthModule cache."
                : "AuthModule cache already present.";
            return new DeployResult(programData, written.Count > 0, detail);
        }

        if (written.Count > 0)
        {
            return new DeployResult(written[0], true,
                "Installed AuthModule cache to " + written[0] +
                ". ProgramData write failed — run the launcher once as Administrator if login hits Battle.net #114.");
        }

        var tried = string.Join("; ", errors.Count > 0 ? errors : destinations);
        throw new IOException(
            "Could not install the Password.dll AuthModule cache. " +
            "Run SkyFire Launcher once as Administrator, or create this file: " +
            programData + " Tried: " + tried);
    }

    private static IReadOnlyList<string> EnumerateDestinations(string? clientLocation)
    {
        var list = new List<string> { DestinationPath };

        void add(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !list.Exists(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
                list.Add(path);
        }

        add(CacheFileUnder(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Blizzard Entertainment", "Battle.net")));
        add(CacheFileUnder(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Battle.net")));

        if (!string.IsNullOrWhiteSpace(clientLocation))
            add(CacheFileUnder(Path.Combine(clientLocation, "Battle.net")));

        return list;
    }

    private static string ProgramDataBattleNetRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Blizzard Entertainment", "Battle.net");

    private static string CacheFileUnder(string battleNetRoot) =>
        Path.Combine(battleNetRoot, "Cache", Sha256Hex[..2], Sha256Hex[2..4], FileName);

    private static byte[] LoadBundledBytes()
    {
        var sidecar = FindSidecarModule();
        if (sidecar is not null)
            return File.ReadAllBytes(sidecar);

        byte[] bytes = AuthModuleBlob.Load();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(Sha256Hex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Launcher embedded AuthModule SHA256 does not match " + Sha256Hex);

        return bytes;
    }

    private static string? FindSidecarModule()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "AuthModules", FileName),
            Path.Combine(AppContext.BaseDirectory, FileName),
        };

        foreach (var path in candidates)
        {
            if (IsValidModule(path))
                return path;
        }

        return null;
    }

    private static bool IsValidModule(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            return hash.Equals(Sha256Hex, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
