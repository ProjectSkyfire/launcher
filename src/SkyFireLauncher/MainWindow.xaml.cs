using System.Diagnostics;
using System.IO;
using System.Windows;
using SkyFireLauncher.Configuration;
using SkyFireLauncher.Realm;
using WinForms = System.Windows.Forms;

namespace SkyFireLauncher;

public partial class MainWindow : Window
{
    // TODO: replace with a configurable server address once we build that out.
    private const string TargetRealmAddress = "127.0.0.1";

    private readonly ConfigManager _configManager = new();
    private AppConfig _config = new();

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
    }

    private void LoadConfig()
    {
        _config = _configManager.Load();
        ClientLocationTextBox.Text = _config.ClientLocation;

        SetVersionRadios(Version32RadioButton, Version64RadioButton, _config.DefaultVersion);
        SetVersionRadios(LaunchVersion32RadioButton, LaunchVersion64RadioButton, _config.DefaultVersion);
    }

    private static void SetVersionRadios(System.Windows.Controls.RadioButton x86Radio, System.Windows.Controls.RadioButton x64Radio, ClientVersion version)
    {
        if (version == ClientVersion.X64)
            x64Radio.IsChecked = true;
        else
            x86Radio.IsChecked = true;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select Client Location",
            SelectedPath = ClientLocationTextBox.Text
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            ClientLocationTextBox.Text = dialog.SelectedPath;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _config.ClientLocation = ClientLocationTextBox.Text;
        _config.DefaultVersion = Version64RadioButton.IsChecked == true ? ClientVersion.X64 : ClientVersion.X86;
        _configManager.Save(_config);

        // Keep the launch tab's selector in sync with the newly saved default.
        SetVersionRadios(LaunchVersion32RadioButton, LaunchVersion64RadioButton, _config.DefaultVersion);

        System.Windows.MessageBox.Show("Configuration saved.", "SkyFire Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        var version = LaunchVersion64RadioButton.IsChecked == true ? ClientVersion.X64 : ClientVersion.X86;
        var exeName = version == ClientVersion.X64 ? "Wow-64.exe" : "Wow.exe";

        if (string.IsNullOrWhiteSpace(_config.ClientLocation) || !Directory.Exists(_config.ClientLocation))
        {
            LaunchStatusTextBlock.Text = "Client location is not set. Configure it on the Configuration tab first.";
            return;
        }

        var exePath = Path.Combine(_config.ClientLocation, exeName);
        if (!File.Exists(exePath))
        {
            LaunchStatusTextBlock.Text = $"Could not find {exeName} in the configured client location.";
            return;
        }

        try
        {
            ClientProcessLauncher.LaunchAndRedirect(exePath, _config.ClientLocation, TargetRealmAddress);
            LaunchStatusTextBlock.Text = $"Launched {exeName}, redirected to {TargetRealmAddress}.";
        }
        catch (Exception ex)
        {
            LaunchStatusTextBlock.Text = $"Failed to launch: {ex.Message}";
        }
    }
}
