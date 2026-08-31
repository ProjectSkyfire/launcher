namespace SkyFireLauncher.Configuration;

public class AppConfig
{
    public string ClientLocation { get; set; } = string.Empty;
    public ClientVersion DefaultVersion { get; set; } = ClientVersion.X64;
    public string LoginAddress { get; set; } = "127.0.0.1";
    public bool ClearCacheOnLogin { get; set; }

    // Experimental: routes an email-shaped login through the client's authnet
    // service toward realmListbn on port 1119.
    public bool EnableAuthnetLogin { get; set; }
    public string AuthnetIdentity { get; set; } = string.Empty;
}
