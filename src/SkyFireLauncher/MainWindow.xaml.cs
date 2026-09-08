using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using SkyFireLauncher.Configuration;
using SkyFireLauncher.Realm;
using WinForms = System.Windows.Forms;

namespace SkyFireLauncher;

public partial class MainWindow : Window
{
    // The client's own authnet CVar is written with an explicit port rather
    // than relying on any client-side default port.
    private const ushort AuthnetGamePort = 1119;

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
        LoginAddressTextBox.Text = _config.LoginAddress;
        ClearCacheOnLoginCheckBox.IsChecked = _config.ClearCacheOnLogin;
        EnableAuthnetLoginCheckBox.IsChecked = _config.EnableAuthnetLogin;

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

    private void ShowSettings(bool show)
    {
        if (show)
        {
            SettingsPane.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() };
            var slide = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            SettingsPane.BeginAnimation(OpacityProperty, fade);
            SettingsPaneTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
            LaunchPane.IsEnabled = false;
        }
        else
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) =>
            {
                SettingsPane.Visibility = Visibility.Collapsed;
                LaunchPane.IsEnabled = true;
            };
            SettingsPane.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void MigrateOpenButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings(false);
        ShowMigrate(true);
    }

    private void MigrateCloseButton_Click(object sender, RoutedEventArgs e) => ShowMigrate(false);

    private void ShowMigrate(bool show)
    {
        if (show)
        {
            MigrateStatusTextBlock.Text = string.Empty;
            MigratePane.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() };
            var slide = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            MigratePane.BeginAnimation(OpacityProperty, fade);
            MigratePaneTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
            LaunchPane.IsEnabled = false;
        }
        else
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) =>
            {
                MigratePane.Visibility = Visibility.Collapsed;
                LaunchPane.IsEnabled = true;
            };
            MigratePane.BeginAnimation(OpacityProperty, fade);
        }
    }

    private async void MigrateSubmitButton_Click(object sender, RoutedEventArgs e)
    {
        var username = MigrateUsernameTextBox.Text.Trim();
        var oldPassword = MigrateOldPasswordBox.Password;
        var email = MigrateEmailTextBox.Text.Trim();
        var newPassword = MigrateNewPasswordBox.Password;
        var confirmPassword = MigrateConfirmPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(oldPassword) ||
            string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(newPassword))
        {
            MigrateStatusTextBlock.Text = "Fill in every field.";
            return;
        }

        if (newPassword != confirmPassword)
        {
            MigrateStatusTextBlock.Text = "New password and confirmation do not match.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.LoginAddress))
        {
            MigrateStatusTextBlock.Text = "Login address is not set. Configure it above first.";
            return;
        }

        MigrateSubmitButton.IsEnabled = false;
        MigrateStatusTextBlock.Text = "Migrating…";

        try
        {
            var result = await AccountMigrationClient.MigrateAsync(_config.LoginAddress, username, oldPassword, email, newPassword);
            MigrateStatusTextBlock.Text = result.ToDisplayMessage();

            if (result == AuthMigrateResult.Ok)
            {
                MigrateOldPasswordBox.Password = string.Empty;
                MigrateNewPasswordBox.Password = string.Empty;
                MigrateConfirmPasswordBox.Password = string.Empty;
            }
        }
        catch (Exception ex)
        {
            MigrateStatusTextBlock.Text = $"Migration failed: {ex.Message}";
        }
        finally
        {
            MigrateSubmitButton.IsEnabled = true;
        }
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
        _config.EnableAuthnetLogin = EnableAuthnetLoginCheckBox.IsChecked == true;

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
            LaunchButton.IsEnabled = false;

            if (_config.ClearCacheOnLogin)
                ClearClientCache(_config.ClientLocation);

            RealmlistConfigWriter.SetRealmlist(_config.ClientLocation, _config.LoginAddress);

            if (_config.EnableAuthnetLogin)
            {
                var loginHost = _config.LoginAddress.Split(':')[0];
                RealmlistConfigWriter.SetRealmlistBn(_config.ClientLocation, $"{loginHost}:{AuthnetGamePort}");
            }
            else
            {
                RealmlistConfigWriter.ClearRealmlistBn(_config.ClientLocation);
            }

            var useAuthnetClientRoute = _config.EnableAuthnetLogin;
            ClientProcessLauncher.LaunchAndRedirect(exePath, _config.ClientLocation, _config.LoginAddress, useAuthnetClientRoute);

            LaunchStatusTextBlock.Text = $"Launched {exeName}, redirected to {_config.LoginAddress}.";
        }
        catch (Exception ex)
        {
            LaunchStatusTextBlock.Text = $"Failed to launch: {ex.Message}";
        }
        finally
        {
            LaunchButton.IsEnabled = true;
        }
    }

    private static void ClearClientCache(string clientLocation)
    {
        var cachePath = Path.Combine(clientLocation, "Cache");
        if (Directory.Exists(cachePath))
            Directory.Delete(cachePath, recursive: true);
    }
}
