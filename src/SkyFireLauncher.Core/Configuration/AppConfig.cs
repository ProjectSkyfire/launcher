namespace SkyFireLauncher.Configuration;

public class AppConfig
{
    public string ClientLocation { get; set; } = string.Empty;
    public ClientVersion DefaultVersion { get; set; } = ClientVersion.X64;
    public string LoginAddress { get; set; } = "127.0.0.1";
    public bool ClearCacheOnLogin { get; set; }

    // Experimental: Soft/authnet login. DNS-redirects .logon.battle.net to the
    // configured login IP and keeps BattlenetLogin. Does not apply classic
    // Grunt LoginFlowPatches.
    public bool EnableAuthnetLogin { get; set; }

    // Linux-only. Proton is the default because MoP-era clients typically
    // run better through Proton's Wine/DXVK than distro Wine.
    public LinuxCompatibilityLayer LinuxRuntime { get; set; } = LinuxCompatibilityLayer.Proton;
    public string ProtonInstallPath { get; set; } = string.Empty;
    public string ProtonPrefixPath { get; set; } = string.Empty;
}
