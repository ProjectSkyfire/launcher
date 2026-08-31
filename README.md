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
  in the launcher, by hooking the client's own DNS resolution (including
  runtime names such as `US.logon.battle.net` via `gethostbyname` and
  `getaddrinfo`).
- **Classic GRUNT:** applies a small set of in-memory login-flow patches so
  the client authenticates against SkyFire's realmlist-based authserver.
- **Soft / authnet:** enable the checkbox in Configuration. This keeps
  BattlenetLogin (Email JZ), skips those classic-force patches, DNS-redirects
  `.logon.battle.net` to the configured login IP (Soft finish on authserver
  port 1119), and overwrites Wow-64's per-login Auth prop205 with a fixed
  128-byte value that matches SkyFire authserver. Does not modify Wow.exe
  on disk (Windows). Requires the 64-bit client.
- Leaves the client executable on disk untouched on Windows. Every change is
  undone the moment the process exits.

Support for pre-patched clients, and eventually a Battle.net-protocol-compatible
server, is planned.

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
4. On the **Launch** tab, pick a version and click **Launch**.

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
