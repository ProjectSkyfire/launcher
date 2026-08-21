namespace SkyFireLauncher.Configuration;

public class AppConfig
{
    public string ClientLocation { get; set; } = string.Empty;
    public ClientVersion DefaultVersion { get; set; } = ClientVersion.X64;
    public string LoginAddress { get; set; } = "127.0.0.1";
    public bool ClearCacheOnLogin { get; set; }

    // Experimental: routes an email-shaped login through the client's own
    // BattlenetLogin service (toward realmListbn on port 1119) instead of
    // forcing classic GruntLogin for everything. There is no authnet server
    // to answer that connection yet - see the authnet roadmap.
    public bool EnableAuthnetLogin { get; set; }

    // Linux-only. Proton is the default because MoP-era clients typically
    // run better through Proton's Wine/DXVK than distro Wine.
    public LinuxCompatibilityLayer LinuxRuntime { get; set; } = LinuxCompatibilityLayer.Proton;
    public string ProtonInstallPath { get; set; } = string.Empty;
    public string ProtonPrefixPath { get; set; } = string.Empty;
}
