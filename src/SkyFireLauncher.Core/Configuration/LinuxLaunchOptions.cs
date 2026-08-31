namespace SkyFireLauncher.Configuration;

public enum LinuxCompatibilityLayer
{
    Wine,
    Proton
}

public sealed class LinuxLaunchOptions
{
    public LinuxCompatibilityLayer Layer { get; init; } = LinuxCompatibilityLayer.Proton;
    public string? ProtonInstallPath { get; init; }
    public string? ProtonPrefixPath { get; init; }
}
