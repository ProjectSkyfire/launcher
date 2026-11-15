<p align="center">
  <img src="Images/skyfire-launcher-logo.png" alt="SkyFire Launcher" width="360">
</p>

# SkyFire Launcher

SkyFire Launcher is a Windows desktop client (C#/WPF) for connecting to a
[Project SkyFire](https://github.com/ProjectSkyfire) World of Warcraft: Mists of
Pandaria (5.4.8) private server. Its goal is to let players connect with a
completely **unpatched, retail-original client** — no permanently modified game
files, no hosts-file edits, no manual configuration.

Instead of patching files on disk, the launcher starts the WoW client normally
and applies its changes directly to the running process in memory:

- Redirects the client's login/realm connection to the server address configured
  in the launcher, by hooking the client's own DNS resolution.
- Applies targeted in-memory endpoint and trust patches so the clean client can
  use the server's authnet protocol implementation.
- Prepares the authentication modules required by the client without modifying
  the client executable.
- Leaves account and password entry to the client; the launcher does not ask
  players to enter their credentials a second time.
- Leaves the client executable on disk untouched. Every change is undone the
  moment the process exits.

The authnet login path is experimental and is being developed alongside the
server implementation.

## Status

Early and actively evolving alongside the rest of the SkyFire project. Expect
rough edges.

## Getting Started

### Installer

Download and run `SkyFireLauncherSetup.msi` from the latest
[release](https://github.com/ProjectSkyfire/launcher/releases). It installs
SkyFire Launcher (self-contained, no separate .NET install required) with a
Start Menu shortcut and a standard uninstaller entry.

### From source

1. Build the solution:
   ```
   dotnet build SkyFireLauncher.slnx
   ```
2. Launch `SkyFireLauncher.exe` from the `Build` output folder.
3. On the **Configuration** tab, set your WoW 5.4.8 client folder, default
   client architecture (32 or 64-bit), and the login address of the SkyFire
   server you want to connect to.
4. For Authnet, enable Authnet login and paste your server administrator's
   **password-proof public key** into Configuration. Use 256 hex characters
   encoding the 128-byte little-endian key; whitespace is allowed. This is not
   the server's private key or its RSA module-signing key. A missing or invalid
   key prevents Authnet launch. Standard login does not require it.
5. On the **Launch** tab, pick a version and click **Launch**.

The launcher installs a password module trusting the configured key and retains
its server-proof verification. It backs up recognized older password modules
from the shared Battle.net cache to `%APPDATA%/SkyFireLauncher/AuthModuleBackups`
so they cannot override the selected key. Close existing game clients before
switching server keys. The pristine module in your client directory is preserved.
Key changes take effect on the next client launch.

#### Matching authserver configuration

For the verified 64-bit build 18414 login path, set the authserver's
`Authnet.StartupModuleKey` to `6175746800004555` followed by the 64-character
SHA-256 hash of the patched password module installed by the launcher. The
result is an 80-character hex value. Use the hash for your server's public key;
changing that key changes the module hash. Leaving this setting empty makes
the current authserver generate a random module identifier, which the client
cannot load. This identifier is public metadata, not the password-proof private
key. The 32-bit path has not yet been validated with this configuration.

### Building the installer

Requires the [WiX Toolset](https://wixtoolset.org/) CLI:
```
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
```
Then, from the `installer` folder:
```
./build.ps1
```
Produces `installer\bin\x64\Release\SkyFireLauncherSetup.msi`.

## License

See [LICENSE](LICENSE).

### Public-key checks

Run `dotnet run --project tests/AuthnetPublicKey -c Release` for key validation
checks. To also verify a real build 18414 module without launching the game, append
`-- <pristine-module.auth> <public-key.hex>` (and `--x86` for a 32-bit module).
The integration check verifies the original checksum and confirms that only the
128-byte public key changes; the verification code remains intact.
