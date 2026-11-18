using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using SaveFileSync.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

/// <summary>Adds a game with manual save folders (phase 1a: no manifest search yet).</summary>
public partial class AddUnitWindow : FluentWindow
{
    private readonly AppHost _host;
    private readonly ObservableCollection<LocationRow> _locations = [];

    public AddUnitWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        Locations.ItemsSource = _locations;
        Warning.IsOpen = false;
        Granularity.ItemsSource = GranularityOptions.Texts;
        Granularity.SelectedIndex = 0;
        GranularityHint.Text = GranularityOptions.Hint;
        Validate();
    }

    private void OnChanged(object sender, RoutedEventArgs e) => Validate();

    private void Validate()
    {
        if (AddButton is not null)
        {
            AddButton.IsEnabled = GameName.Text.Trim().Length > 0 && _locations.Count > 0;
        }
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a save folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folder = dialog.FolderName;
        if (_locations.Any(l => string.Equals(l.Path, folder, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _locations.Add(new LocationRow
        {
            Path = folder,
            Id = LocationRow.MakeId(folder, _locations.Select(l => l.Id)),
            Label = System.IO.Path.GetFileName(folder),
        });

        if (GameName.Text.Trim().Length == 0)
        {
            GameName.Text = System.IO.Path.GetFileName(folder);
        }

        UpdateWarnings();
        ShowPreview();
        Validate();
    }

    private void OnRemoveLocation(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LocationRow row })
        {
            _locations.Remove(row);
            UpdateWarnings();
            ShowPreview();
            Validate();
        }
    }

    private void OnPreview(object sender, RoutedEventArgs e) => ShowPreview();

    private void ShowPreview()
    {
        var totals = _locations.Select(l => l.Preview()).ToList();
        PreviewText.Text = _locations.Count == 0
            ? ""
            : $"{totals.Sum(t => t.Files)} file(s), {Cli.FormatSize(totals.Sum(t => t.Bytes))} would be synced.";
    }

    private void UpdateWarnings()
    {
        var repo = _host.Config.Snapshot().RepositoryPath ?? "";
        var messages = new List<string>();
        foreach (var location in _locations)
        {
            if (OneDrive.IsInsideOneDrive(location.Path))
            {
                messages.Add($"{location.Path} is inside OneDrive, which already syncs it: syncing it again can create OneDrive conflict copies of game files.");
            }

            if (repo.Length > 0 && (location.Path.StartsWith(repo, StringComparison.OrdinalIgnoreCase) || repo.StartsWith(location.Path, StringComparison.OrdinalIgnoreCase)))
            {
                messages.Add($"{location.Path} overlaps the SaveFileSync repository folder.");
            }
        }

        Warning.IsOpen = messages.Count > 0;
        Warning.Title = "Check these folders";
        Warning.Message = string.Join(Environment.NewLine, messages);
    }

    private void OnRunningAppsOpened(object? sender, EventArgs e)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.Id != Environment.ProcessId)
                    {
                        names.Add(p.ProcessName + ".exe");
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        RunningApps.ItemsSource = names.ToList();
    }

    private void OnAddRunningApp(object sender, RoutedEventArgs e)
    {
        if (RunningApps.SelectedItem is string name)
        {
            var current = LocationRow.Split(ProcessNames.Text).ToList();
            if (!current.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                current.Add(name);
            }

            ProcessNames.Text = string.Join("; ", current);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var missing = _locations.FirstOrDefault(l => !Directory.Exists(l.Path));
        if (missing is not null)
        {
            System.Windows.MessageBox.Show(this, $"The folder {missing.Path} does not exist.", "Add game", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AddButton.IsEnabled = false;
        try
        {
            await _host.Service!.AddUnitAsync(
                GameName.Text.Trim(),
                _locations.Select(l => l.ToDefinition()).ToList(),
                LocationRow.Split(ProcessNames.Text),
                _locations.ToDictionary(l => l.Id, l => l.Path),
                GranularityOptions.At(Granularity.SelectedIndex));
            Close();
        }
        catch (Exception ex)
        {
            AddButton.IsEnabled = true;
            System.Windows.MessageBox.Show(this, ex.Message, "Add game", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
