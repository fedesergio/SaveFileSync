using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using SaveFileSync.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

/// <summary>Settings (PLAN.md §8, item 7).</summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly AppHost _host;
    private readonly ObservableCollection<string> _roots;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        var config = host.Config.Snapshot();
        DeviceName.Text = config.DeviceName ?? Environment.MachineName;
        StartWithWindows.IsChecked = Autostart.IsEnabled(host.Profile);
        RepositoryPath.Text = config.RepositoryPath ?? "";
        QuietSeconds.Value = config.QuietPeriodSeconds;
        ExitDelaySeconds.Value = config.ProcessExitDelaySeconds;
        InPlayCapMinutes.Value = config.InPlayCapMinutes;
        _roots = new ObservableCollection<string>(config.LibraryRoots);
        Roots.ItemsSource = _roots;
        About.Text =
            $"SaveFileSync {AppHost.AppVersion}\n" +
            "Save locations data (from a later version): Ludusavi manifest, compiled from PCGamingWiki.\n" +
            "Built with .NET, WPF-UI, SQLite and Serilog.";
    }

    private void OnAddRoot(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder where games are installed" };
        if (dialog.ShowDialog(this) == true && !_roots.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            _roots.Add(dialog.FolderName);
        }
    }

    private void OnRemoveRoot(object sender, RoutedEventArgs e)
    {
        if (Roots.SelectedItem is string selected)
        {
            _roots.Remove(selected);
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_host.Paths.LogsDirectory}\"") { UseShellExecute = true });

    private async void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export diagnostics",
            FileName = $"SaveFileSync-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = "Zip archive|*.zip",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await Diagnostics.ExportAsync(_host, dialog.FileName);
            MessageBox.Show(this, $"Saved to {dialog.FileName}", "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = DeviceName.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "The PC name cannot be empty.", "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var nameChanged = false;
        _host.Config.Update(c =>
        {
            nameChanged = c.DeviceName != name;
            c.DeviceName = name;
            c.QuietPeriodSeconds = (int)(QuietSeconds.Value ?? c.QuietPeriodSeconds);
            c.ProcessExitDelaySeconds = (int)(ExitDelaySeconds.Value ?? c.ProcessExitDelaySeconds);
            c.InPlayCapMinutes = (int)(InPlayCapMinutes.Value ?? c.InPlayCapMinutes);
            c.LibraryRoots = _roots.ToList();
        });

        if (StartWithWindows.IsChecked == true)
        {
            Autostart.Enable(Environment.ProcessPath!, _host.Profile);
        }
        else
        {
            Autostart.Disable(_host.Profile);
        }

        if (nameChanged)
        {
            _host.Service?.RefreshDeviceInfo();
        }

        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
