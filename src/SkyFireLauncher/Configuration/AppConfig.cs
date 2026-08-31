namespace SkyFireLauncher.Configuration;

public class AppConfig
{
    public string ClientLocation { get; set; } = string.Empty;
    public ClientVersion DefaultVersion { get; set; } = ClientVersion.X64;
    public string LoginAddress { get; set; } = "127.0.0.1";
    public bool ClearCacheOnLogin { get; set; }

    // Experimental: Soft/authnet login. DNS-redirects .logon.battle.net, keeps
    // BattlenetLogin (Email JZ), and forces a fixed Auth prop205 so Soft2
    // 629D0 matches authserver. Does not apply classic Grunt LoginFlowPatches.
    public bool EnableAuthnetLogin { get; set; } = true;
}
