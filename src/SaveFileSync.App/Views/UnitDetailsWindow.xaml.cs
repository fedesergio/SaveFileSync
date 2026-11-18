using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Service;
using SaveFileSync.Core.Sync;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

/// <summary>Unit details, binding ("Set up on this PC") and actions (PLAN.md §8, items 4–5).</summary>
public partial class UnitDetailsWindow : FluentWindow
{
    private readonly AppHost _host;
    private readonly string _unitId;
    private readonly ObservableCollection<LocationRow> _locations = [];
    private UnitDefinition _definition;

    public UnitDetailsWindow(AppHost host, string unitId)
    {
        _host = host;
        _unitId = unitId;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        _definition = host.Service!.GetDefinition(unitId) ?? throw new InvalidOperationException("Unknown game.");
        var bindings = host.Config.Snapshot().Bindings.GetValueOrDefault(unitId) ?? new Dictionary<string, string>();
        foreach (var location in _definition.Locations)
        {
            _locations.Add(new LocationRow
            {
                Id = location.Id,
                Label = location.Label ?? location.Id,
                Path = bindings.GetValueOrDefault(location.Id) ?? "",
                Includes = string.Join("; ", location.Includes),
                Excludes = string.Join("; ", location.Excludes),
            });
        }

        Locations.ItemsSource = _locations;
        GameName.Text = _definition.Name;
        ProcessNames.Text = string.Join("; ", _definition.ProcessNames.Select(p => p + ".exe"));

        host.Service.StatusChanged += OnStatusChanged;
        Closed += (_, _) => host.Service.StatusChanged -= OnStatusChanged;
        Refresh(App.FindStatus(unitId));
    }

    private void OnStatusChanged(IReadOnlyList<UnitStatus> status) =>
        Dispatcher.BeginInvoke(() => Refresh(status.FirstOrDefault(s => s.UnitId == _unitId)));

    private void Refresh(UnitStatus? status)
    {
        Header.Text = _definition.Name;
        TitleBar.Title = _definition.Name;
        if (status is null)
        {
            return;
        }

        StatusDetail.Text = status.Detail;
        ChipText.Text = StateToTextConverter.Text(status.State);
        Chip.Background = (Brush)new StateToBrushConverter().Convert(status.State, typeof(Brush), null!, System.Globalization.CultureInfo.CurrentCulture);

        var unitPaused = _host.Config.Snapshot().PausedUnits.Contains(_unitId);
        PauseButton.Content = unitPaused ? "Resume this game" : "Pause this game";

        BindingHint.Text = status.IsBound
            ? "Where this game's saves are on this PC. Other PCs can use different folders."
            : "This game comes from another PC. Choose where its saves are (or should be) on this PC, then Save changes.";
        SaveButton.Content = status.IsBound ? "Save changes" : "Set up on this PC";

        ResolveButton.Visibility = status.State == UnitState.Conflict ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.Visibility = status.State == UnitState.Attention ? Visibility.Visible : Visibility.Collapsed;
        DeleteEverywhereButton.Visibility = RestoreButton.Visibility;

        (ActionBar.IsOpen, ActionBar.Severity, ActionBar.Title, ActionBar.Message) = status.State switch
        {
            UnitState.Conflict => (true, InfoBarSeverity.Warning, "Conflict",
                "The saves changed on more than one PC since the last sync. Choose which version to keep: the other one stays in the history."),
            UnitState.Attention => (true, InfoBarSeverity.Warning, "Saves missing on this PC",
                "The save folder is empty but the repository has saves. Nothing was deleted on other PCs. Restore them here, or confirm that they should be deleted everywhere."),
            UnitState.Stale => (true, InfoBarSeverity.Warning, "Newer save available",
                "Another PC saved this game while it was open here. Close the game to receive the newer save before playing."),
            UnitState.Error or UnitState.LocationMissing or UnitState.RepositoryUnavailable => (true, InfoBarSeverity.Error, "Problem", status.Detail),
            _ => (false, InfoBarSeverity.Informational, "", ""),
        };
    }

    private void OnBrowseLocation(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LocationRow row })
        {
            var dialog = new OpenFolderDialog { Title = $"Folder for '{row.Label}' on this PC" };
            if (Directory.Exists(row.Path))
            {
                dialog.InitialDirectory = row.Path;
            }

            if (dialog.ShowDialog(this) == true)
            {
                row.Path = dialog.FolderName;
            }
        }
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LocationRow row } && Directory.Exists(row.Path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{row.Path}\"") { UseShellExecute = true });
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var missing = _locations.FirstOrDefault(l => string.IsNullOrWhiteSpace(l.Path) || !Directory.Exists(l.Path));
        if (missing is not null)
        {
            var create = MessageBox.Show(this,
                $"The folder for '{missing.Label}' does not exist:\n{missing.Path}\n\nCreate it (saves from the other PCs will be downloaded into it)?",
                "SaveFileSync", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (create != MessageBoxResult.Yes || string.IsNullOrWhiteSpace(missing.Path) || !Path.IsPathFullyQualified(missing.Path))
            {
                return;
            }

            Directory.CreateDirectory(missing.Path);
        }

        try
        {
            var service = _host.Service!;
            var updated = new UnitDefinition
            {
                UnitId = _definition.UnitId,
                Name = GameName.Text.Trim().Length > 0 ? GameName.Text.Trim() : _definition.Name,
                Locations = _locations.Select(l => l.ToDefinition()).ToList(),
                ProcessNames = LocationRow.Split(ProcessNames.Text),
                Granularity = _definition.Granularity,
                Mode = _definition.Mode,
            };

            if (DefinitionChanged(updated))
            {
                await service.UpdateUnitAsync(updated);
            }

            await service.BindAsync(_unitId, _locations.ToDictionary(l => l.Id, l => l.Path));
            _definition = service.GetDefinition(_unitId) ?? _definition;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool DefinitionChanged(UnitDefinition updated) =>
        updated.Name != _definition.Name
        || !updated.ProcessNames.Select(SyncService.NormalizeProcessName).SequenceEqual(_definition.ProcessNames)
        || !updated.Locations.Select(l => (string.Join(";", l.Includes), string.Join(";", l.Excludes)))
            .SequenceEqual(_definition.Locations.Select(l => (string.Join(";", l.Includes), string.Join(";", l.Excludes))));

    private void OnSyncNow(object sender, RoutedEventArgs e) => _host.Service!.RequestSync(_unitId);

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        var paused = _host.Config.Snapshot().PausedUnits.Contains(_unitId);
        _host.Service!.SetUnitPaused(_unitId, !paused);
        PauseButton.Content = paused ? "Pause this game" : "Resume this game";
    }

    private void OnResolve(object sender, RoutedEventArgs e)
    {
        new ConflictWindow(_host, _unitId) { Owner = this }.ShowDialog();
    }

    private async void OnRestore(object sender, RoutedEventArgs e) =>
        await RunAndReport(() => _host.Service!.RestoreFromRepositoryAsync(_unitId), "Saves restored from the repository.");

    private async void OnDeleteEverywhere(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Delete this game's saves on every PC? The current version stays in the repository history and can be exported.",
            "Delete on all PCs", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
        {
            await RunAndReport(() => _host.Service!.PropagateDeletionAsync(_unitId), "The deletion will reach the other PCs.");
        }
    }

    private async void OnRemoveHere(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Stop syncing this game on this PC? Its files stay where they are.", "SaveFileSync",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            await _host.Service!.RemoveUnitAsync(_unitId, fromAllPcs: false);
            Close();
        }
    }

    private async void OnRemoveEverywhere(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Stop syncing this game on all PCs? No save files are deleted.", "SaveFileSync",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            await _host.Service!.RemoveUnitAsync(_unitId, fromAllPcs: true);
            Close();
        }
    }

    private async Task RunAndReport(Func<Task<SyncResult>> action, string success)
    {
        try
        {
            var result = await action();
            var message = result.Kind switch
            {
                SyncResultKind.Pulled or SyncResultKind.Pushed or SyncResultKind.UpToDate => success,
                _ => result.Message ?? result.Kind.ToString(),
            };
            MessageBox.Show(this, message, "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
