using SkyFireLauncher.Realm;
using System.Security.Cryptography;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Reject(Action action)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new Exception("Invalid input was accepted.");
}

var generator = new byte[128];
generator[0] = 4;
var hex = Convert.ToHexString(generator);
Check(AuthnetClientSupport.ParsePasswordProofPublicKey(" \n" + hex + "\r\n").SequenceEqual(generator), "Whitespace parsing failed.");
foreach (var invalid in new[] { "", "ABC", new string('Z', 256), new string('0', 256), "01" + new string('0', 254), new string('F', 256) })
    Reject(() => AuthnetClientSupport.ParsePasswordProofPublicKey(invalid));
Reject(() => AuthnetClientSupport.PatchPasswordProofPublicKey(new byte[128], true, hex));
Console.WriteLine("Public-key validation checks passed.");

// Optional real-module integration test. Do not redistribute retail modules in this repository.
if (args.Length >= 2)
{
    var original = File.ReadAllBytes(args[0]);
    var keyHex = File.ReadAllText(args[1]);
    var is64Bit = !args.Contains("--x86");
    var beforeHash = SHA256.HashData(original);
    var patched = AuthnetClientSupport.PatchPasswordProofPublicKey(original, is64Bit, keyHex);
    var key = AuthnetClientSupport.ParsePasswordProofPublicKey(keyHex);
    var offset = patched.AsSpan().IndexOf(key);
    Check(offset >= 0, "Public key was not installed.");
    Check(original.AsSpan(0, offset).SequenceEqual(patched.AsSpan(0, offset)) &&
        original.AsSpan(offset + 128).SequenceEqual(patched.AsSpan(offset + 128)), "Code outside the public key changed.");
    Check(SHA256.HashData(original).SequenceEqual(beforeHash), "Input module was modified.");
    original[0] ^= 1;
    Reject(() => AuthnetClientSupport.PatchPasswordProofPublicKey(original, is64Bit, keyHex));
    Console.WriteLine("Real-module checksum, key replacement and unchanged verification-code checks passed.");
    if (args.Contains("--prepare-cache"))
        Console.WriteLine("Prepared authentication module: " + AuthnetClientSupport.PrepareModuleCache(Path.GetDirectoryName(Path.GetFullPath(args[0]))!, is64Bit, keyHex));
}
