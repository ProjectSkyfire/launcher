using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SkyFireLauncher.Configuration;
using SkyFireLauncher.Realm;

namespace SkyFireLauncher;

public partial class MainWindow : Window
{
    // The client's own BattlenetLogin CVar (realmListbn) is written with an
    // explicit port rather than relying on any client-side default port.
    private const ushort AuthnetGamePort = 1119;

    private readonly ConfigManager _configManager = new();
    private AppConfig _config = new();
    private Control? _noticeReturnPane;

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
        ProtonPrefixTextBox.Text = _config.ProtonPrefixPath;

        SetVersionRadios(Version32RadioButton, Version64RadioButton, _config.DefaultVersion);
        SetVersionRadios(LaunchVersion32RadioButton, LaunchVersion64RadioButton, _config.DefaultVersion);

        var useProton = _config.LinuxRuntime != LinuxCompatibilityLayer.Wine;
        RuntimeProtonRadioButton.IsChecked = useProton;
        RuntimeWineRadioButton.IsChecked = !useProton;
        RefreshProtonInstalls(_config.ProtonInstallPath);
        UpdateRuntimePanels();
    }

    private static void SetVersionRadios(RadioButton x86Radio, RadioButton x64Radio, ClientVersion version)
    {
        if (version == ClientVersion.X64)
            x64Radio.IsChecked = true;
        else
            x86Radio.IsChecked = true;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
            return;

        BeginMoveDrag(e);
    }

    private void ResizeNorth(object? sender, PointerPressedEventArgs e) => BeginResizeDrag(WindowEdge.North, e);
    private void ResizeSouth(object? sender, PointerPressedEventArgs e) => BeginResizeDrag(WindowEdge.South, e);
    private void ResizeWest(object? sender, PointerPressedEventArgs e) => BeginResizeDrag(WindowEdge.West, e);
    private void ResizeEast(object? sender, PointerPressedEventArgs e) => BeginResizeDrag(WindowEdge.East, e);

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void SettingsOpenButton_Click(object? sender, RoutedEventArgs e) => ShowSettings(true);

    private void SettingsCloseButton_Click(object? sender, RoutedEventArgs e) => ShowSettings(false);

    private async void ShowSettings(bool show)
    {
        if (show)
        {
            RefreshProtonInstalls(SelectedProtonInstallPath() ?? _config.ProtonInstallPath);
            SettingsPane.IsVisible = true;
            SettingsPane.Opacity = 0;
            if (SettingsPane.RenderTransform is TranslateTransform settingsSlide)
                settingsSlide.Y = 18;
            LaunchPane.IsEnabled = false;
            SettingsPane.Opacity = 1;
            if (SettingsPane.RenderTransform is TranslateTransform settingsRest)
                settingsRest.Y = 0;
        }
        else
        {
            SettingsPane.Opacity = 0;
            await Task.Delay(140);
            SettingsPane.IsVisible = false;
            LaunchPane.IsEnabled = true;
        }
    }

    private async void MigrateOpenButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowSettings(false);
        await Task.Delay(140);
        ShowMigrate(true);
    }

    private void MigrateCloseButton_Click(object? sender, RoutedEventArgs e) => ShowMigrate(false);

    private async void ShowMigrate(bool show)
    {
        if (show)
        {
            MigrateStatusTextBlock.Text = string.Empty;
            MigratePane.IsVisible = true;
            MigratePane.Opacity = 0;
            if (MigratePane.RenderTransform is TranslateTransform migrateSlide)
                migrateSlide.Y = 18;
            LaunchPane.IsEnabled = false;
            MigratePane.Opacity = 1;
            if (MigratePane.RenderTransform is TranslateTransform migrateRest)
                migrateRest.Y = 0;
        }
        else
        {
            MigratePane.Opacity = 0;
            await Task.Delay(140);
            MigratePane.IsVisible = false;
            LaunchPane.IsEnabled = true;
        }
    }

    private async void MigrateSubmitButton_Click(object? sender, RoutedEventArgs e)
    {
        var username = MigrateUsernameTextBox.Text?.Trim() ?? string.Empty;
        var oldPassword = MigrateOldPasswordBox.Text ?? string.Empty;
        var email = MigrateEmailTextBox.Text?.Trim() ?? string.Empty;
        var newPassword = MigrateNewPasswordBox.Text ?? string.Empty;
        var confirmPassword = MigrateConfirmPasswordBox.Text ?? string.Empty;

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
                MigrateOldPasswordBox.Text = string.Empty;
                MigrateNewPasswordBox.Text = string.Empty;
                MigrateConfirmPasswordBox.Text = string.Empty;
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

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Client Location",
            AllowMultiple = false
        });

        if (folders.Count > 0)
            ClientLocationTextBox.Text = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        _config.ClientLocation = ClientLocationTextBox.Text ?? string.Empty;
        _config.DefaultVersion = Version64RadioButton.IsChecked == true ? ClientVersion.X64 : ClientVersion.X86;
        _config.LoginAddress = LoginAddressTextBox.Text ?? string.Empty;
        _config.ClearCacheOnLogin = ClearCacheOnLoginCheckBox.IsChecked == true;
        _config.EnableAuthnetLogin = EnableAuthnetLoginCheckBox.IsChecked == true;
        _config.LinuxRuntime = RuntimeWineRadioButton.IsChecked == true
            ? LinuxCompatibilityLayer.Wine
            : LinuxCompatibilityLayer.Proton;
        _config.ProtonInstallPath = SelectedProtonInstallPath() ?? string.Empty;
        _config.ProtonPrefixPath = ProtonPrefixTextBox.Text?.Trim() ?? string.Empty;

        try
        {
            _configManager.Save(_config);
        }
        catch (Exception ex)
        {
            ShowNotice("SkyFire Launcher", $"Failed to save configuration: {ex.Message}", SettingsPane);
            return;
        }

        SetVersionRadios(LaunchVersion32RadioButton, LaunchVersion64RadioButton, _config.DefaultVersion);
        ShowNotice("SkyFire Launcher", "Configuration saved.", SettingsPane);
    }

    private void LaunchButton_Click(object? sender, RoutedEventArgs e)
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

            if (_config.EnableAuthnetLogin)
            {
                var loginHost = _config.LoginAddress.Split(':')[0];
                RealmlistConfigWriter.SetRealmlistBn(_config.ClientLocation, $"{loginHost}:{AuthnetGamePort}");
            }

            ClientProcessLauncher.LaunchAndRedirect(
                exePath,
                _config.ClientLocation,
                _config.LoginAddress,
                _config.EnableAuthnetLogin,
                new LinuxLaunchOptions
                {
                    Layer = _config.LinuxRuntime,
                    ProtonInstallPath = _config.ProtonInstallPath,
                    ProtonPrefixPath = _config.ProtonPrefixPath
                });

            var runtime = _config.LinuxRuntime == LinuxCompatibilityLayer.Proton ? "Proton" : "Wine";
            LaunchStatusTextBlock.Text = $"Launched {exeName} via {runtime}, redirected to {_config.LoginAddress}.";
        }
        catch (Exception ex)
        {
            LaunchStatusTextBlock.Text = $"Failed to launch: {ex.Message}";
        }
    }

    private async void BrowsePrefixButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Proton Prefix",
            AllowMultiple = false
        });

        if (folders.Count > 0)
            ProtonPrefixTextBox.Text = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
    }

    private void RuntimeRadio_Click(object? sender, RoutedEventArgs e) => UpdateRuntimePanels();

    private void UpdateRuntimePanels()
    {
        var proton = RuntimeProtonRadioButton.IsChecked == true;
        ProtonOptionsPanel.IsVisible = proton;
        if (proton)
            RefreshProtonInstalls(SelectedProtonInstallPath() ?? _config.ProtonInstallPath);
    }

    private void RefreshProtonInstalls(string? preferredPath)
    {
        var installs = LinuxClientRuntime.DiscoverProtonInstalls();
        ProtonVersionCombo.ItemsSource = installs;

        ProtonInstall? selected = null;
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            selected = installs.FirstOrDefault(p =>
                p.InstallPath.Equals(preferredPath, StringComparison.OrdinalIgnoreCase));
        }

        selected ??= LinuxClientRuntime.ResolveProtonInstall(preferredPath);
        ProtonVersionCombo.SelectedItem = selected;

        ProtonStatusTextBlock.Text = installs.Count == 0
            ? "No Proton install found. Install Steam Proton or Proton-GE, or use Wine."
            : $"{installs.Count} Proton install{(installs.Count == 1 ? "" : "s")} found. GE-Proton is preferred when present.";
    }

    private string? SelectedProtonInstallPath() =>
        ProtonVersionCombo.SelectedItem is ProtonInstall install ? install.InstallPath : null;

    private void ShowNotice(string title, string message, Control? returnPane)
    {
        _noticeReturnPane = returnPane;
        NoticeTitleTextBlock.Text = title;
        NoticeMessageTextBlock.Text = message;
        NoticePane.IsVisible = true;
        NoticePane.Opacity = 1;
        if (returnPane is not null)
            returnPane.IsEnabled = false;
    }

    private void NoticeOkButton_Click(object? sender, RoutedEventArgs e)
    {
        NoticePane.IsVisible = false;
        NoticePane.Opacity = 0;
        if (_noticeReturnPane is not null)
            _noticeReturnPane.IsEnabled = true;
        _noticeReturnPane = null;
    }

    private static void ClearClientCache(string clientLocation)
    {
        var cachePath = Path.Combine(clientLocation, "Cache");
        if (Directory.Exists(cachePath))
            Directory.Delete(cachePath, recursive: true);
    }
}
