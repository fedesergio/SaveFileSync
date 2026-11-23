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
        AutoSetup.IsChecked = config.AutoSetup;
        DefaultKeep.Value = config.DefaultKeepVersions;
        HistoryCleanup.IsChecked = config.HistoryCleanup;
        ShowDatabaseStatus();
        About.Text =
            $"SaveFileSync {AppHost.AppVersion}\n" +
            "Save locations: Ludusavi manifest (https://github.com/mtkennerly/ludusavi-manifest), compiled from PCGamingWiki " +
            "(https://www.pcgamingwiki.com). Personal, non-commercial use.\n" +
            "Built with .NET, WPF-UI, SQLite, YamlDotNet and Serilog.";
    }

    private void ShowDatabaseStatus()
    {
        var status = _host.Manifest.GetStatus();
        DatabaseStatus.Text = _host.Manifest.HasIndex
            ? $"{status.GameCount:N0} games with Windows save data. Updated {status.UpdatedUtc?.ToLocalTime():g}, last checked {status.CheckedUtc?.ToLocalTime():g}."
            : "Not downloaded yet. It is downloaded the first time you add a game from the database.";
    }

    private async void OnCleanNow(object sender, RoutedEventArgs e)
    {
        CleanNow.IsEnabled = false;
        CleanResult.Text = "Cleaning up…";
        try
        {
            var report = await _host.Service!.RunMaintenanceAsync();
            CleanResult.Text =
                $"Archived {report.Archived} version(s). " +
                (report.Cleanup.SkippedReason is { } why
                    ? $"File cleanup skipped: {why}"
                    : $"{report.Cleanup.Deleted} unused file(s) deleted, {report.Cleanup.Waiting} waiting out the 14-day grace period.") +
                (report.Repaired > 0 ? $" {report.Repaired} missing file(s) re-uploaded." : "");
        }
        catch (Exception ex)
        {
            CleanResult.Text = ex.Message;
        }
        finally
        {
            CleanNow.IsEnabled = true;
        }
    }

    private async void OnUpdateDatabase(object sender, RoutedEventArgs e)
    {
        UpdateDatabase.IsEnabled = false;
        DatabaseProgress.Visibility = Visibility.Visible;
        try
        {
            var result = await _host.Manifest.UpdateAsync(force: true, DateTime.UtcNow);
            DatabaseProgress.Visibility = Visibility.Collapsed;
            ShowDatabaseStatus();
            if (result.Kind == SaveFileSync.Manifest.ManifestUpdateKind.Failed)
            {
                MessageBox.Show(this, $"Could not update the game database: {result.Error}", "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            DatabaseProgress.Visibility = Visibility.Collapsed;
            UpdateDatabase.IsEnabled = true;
        }
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
            c.AutoSetup = AutoSetup.IsChecked == true;
            c.DefaultKeepVersions = (int)(DefaultKeep.Value ?? c.DefaultKeepVersions);
            c.HistoryCleanup = HistoryCleanup.IsChecked == true;
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
