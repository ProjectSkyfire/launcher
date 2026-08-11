using System.Windows;
using SkyFireLauncher.Configuration;
using WinForms = System.Windows.Forms;

namespace SkyFireLauncher;

public partial class MainWindow : Window
{
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

        if (_config.DefaultVersion == ClientVersion.X64)
            Version64RadioButton.IsChecked = true;
        else
            Version32RadioButton.IsChecked = true;
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

        System.Windows.MessageBox.Show("Configuration saved.", "SkyFire Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
