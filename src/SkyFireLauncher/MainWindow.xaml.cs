using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using SkyFireLauncher.Configuration;
using SkyFireLauncher.Realm;
using WinForms = System.Windows.Forms;

namespace SkyFireLauncher;

public partial class MainWindow : Window
{
    private readonly ConfigManager _configManager = new();
    private AppConfig _config = new();

    public MainWindow()
    {
        InitializeComponent();
        LoadAboutInfo();
        LoadConfig();
    }

    private void LoadAboutInfo()
    {
        var assembly = Assembly.GetExecutingAssembly();
        AboutVersionTextBlock.Text = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "1.4 Non Authnet";
        AboutCopyrightTextBlock.Text = assembly
            .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? "Copyright © 2026 Project SkyFire";
    }

    private void LoadConfig()
    {
        _config = _configManager.Load();
        ClientLocationTextBox.Text = _config.ClientLocation;
        LoginAddressTextBox.Text = _config.LoginAddress;
        ClearCacheOnLoginCheckBox.IsChecked = _config.ClearCacheOnLogin;

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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            return;

        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsOpenButton_Click(object sender, RoutedEventArgs e) => ShowSettings(true);

    private void SettingsCloseButton_Click(object sender, RoutedEventArgs e) => ShowSettings(false);

    private void AboutOpenButton_Click(object sender, RoutedEventArgs e) => ShowAbout(true);

    private void AboutCloseButton_Click(object sender, RoutedEventArgs e) => ShowAbout(false);

    private void ShowSettings(bool show) =>
        ShowOverlay(SettingsPane, SettingsPaneTransform, AboutPane, show);

    private void ShowAbout(bool show) =>
        ShowOverlay(AboutPane, AboutPaneTransform, SettingsPane, show);

    private void ShowOverlay(Grid pane, TranslateTransform transform, Grid otherPane, bool show)
    {
        if (show)
        {
            otherPane.BeginAnimation(OpacityProperty, null);
            otherPane.Visibility = Visibility.Collapsed;
            pane.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() };
            var slide = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            pane.BeginAnimation(OpacityProperty, fade);
            transform.BeginAnimation(TranslateTransform.YProperty, slide);
            LaunchPane.IsEnabled = false;
        }
        else
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) =>
            {
                pane.Visibility = Visibility.Collapsed;
                if (otherPane.Visibility != Visibility.Visible)
                    LaunchPane.IsEnabled = true;
            };
            pane.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (AboutPane.Visibility == Visibility.Visible)
            ShowAbout(false);
        else if (SettingsPane.Visibility == Visibility.Visible)
            ShowSettings(false);
        else
            return;

        e.Handled = true;
    }

    private void ExternalLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not open the link: {ex.Message}", "SkyFire Launcher",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        e.Handled = true;
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
        _config.LoginAddress = LoginAddressTextBox.Text;
        _config.ClearCacheOnLogin = ClearCacheOnLoginCheckBox.IsChecked == true;

        try
        {
            _configManager.Save(_config);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to save configuration: {ex.Message}", "SkyFire Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Keep the launch pane's selector in sync with the newly saved default.
        SetVersionRadios(LaunchVersion32RadioButton, LaunchVersion64RadioButton, _config.DefaultVersion);

        System.Windows.MessageBox.Show("Configuration saved.", "SkyFire Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        var version = LaunchVersion64RadioButton.IsChecked == true ? ClientVersion.X64 : ClientVersion.X86;
        var exeName = version == ClientVersion.X64 ? "Wow-64.exe" : "Wow.exe";

        if (string.IsNullOrWhiteSpace(_config.ClientLocation) || !Directory.Exists(_config.ClientLocation))
        {
            LaunchStatusTextBlock.Text = "Client location is not set. Open Configuration (⚙) first.";
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
            if (_config.ClearCacheOnLogin)
                ClearClientCache(_config.ClientLocation);

            RealmlistConfigWriter.SetRealmlist(_config.ClientLocation, _config.LoginAddress);
            ClientProcessLauncher.LaunchAndRedirect(exePath, _config.ClientLocation, _config.LoginAddress);
            LaunchStatusTextBlock.Text = $"Launched {exeName}, redirected to {_config.LoginAddress}.";
        }
        catch (Exception ex)
        {
            LaunchStatusTextBlock.Text = $"Failed to launch: {ex.Message}";
        }
    }

    private static void ClearClientCache(string clientLocation)
    {
        var cachePath = Path.Combine(clientLocation, "Cache");
        if (Directory.Exists(cachePath))
            Directory.Delete(cachePath, recursive: true);
    }
}
