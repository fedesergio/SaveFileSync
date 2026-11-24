using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SaveFileSync.Core.History;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Sync;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

/// <summary>A history line prepared for display.</summary>
public sealed class HistoryRow
{
    public required HistoryEntry Entry { get; init; }

    public string When => Entry.CreatedUtc.ToLocalTime().ToString("g");

    public string From => Entry.DeviceName;

    public string Kind => Entry.Kind switch
    {
        VersionKind.Merge => "Merged",
        VersionKind.Resolve => "Conflict resolved",
        VersionKind.Restore => "Restored",
        VersionKind.Initial => "First version",
        _ => "Saved",
    };

    public string Contents => $"{Entry.FileCount} file(s), {Cli.FormatSize(Entry.TotalSize)}";

    public string Status
    {
        get
        {
            var parts = new List<string>();
            if (Entry.IsLatest)
            {
                parts.Add("Latest");
            }

            if (Entry.IsCurrentHere)
            {
                parts.Add("On this PC");
            }

            if (Entry.PinnedBy.Count > 0)
            {
                parts.Add("Pinned by " + string.Join(", ", Entry.PinnedBy));
            }

            if (Entry.Archived)
            {
                parts.Add("Archived — files removed");
            }
            else if (!Entry.Restorable)
            {
                parts.Add("Still syncing…");
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>History of one game: restore, pin, export, and how many versions to keep (PLAN.md §8, item 4).</summary>
public partial class HistoryWindow : FluentWindow
{
    private readonly AppHost _host;
    private readonly string _unitId;

    public HistoryWindow(AppHost host, string unitId)
    {
        _host = host;
        _unitId = unitId;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        var definition = host.Service!.GetDefinition(unitId);
        Header.Text = definition is null ? "History" : $"{definition.Name} — history";
        KeepBox.Value = definition?.EffectiveKeepVersions ?? UnitDefinition.DefaultKeepVersions;
        Loaded += async (_, _) => await ReloadAsync();
        UpdateButtons();
    }

    /// <summary>Dev-only (<c>--selftest-ui</c>): loads the history and toggles a pin.</summary>
    internal async Task<IReadOnlyList<string>> SelfTestAsync()
    {
        var problems = new List<string>();
        await ReloadAsync();
        if (Versions.Items.Count == 0)
        {
            problems.Add("the history is empty");
            return problems;
        }

        Versions.SelectedIndex = 0;
        if (Selected is { Entry.Restorable: true })
        {
            await _host.Service!.PinAsync(_unitId, Selected.Entry.VersionId, true);
            await ReloadAsync();
            Versions.SelectedIndex = 0;
            if (Selected?.Entry.PinnedHere != true || PinButton.Content as string != "Unpin")
            {
                problems.Add("pinning did not show in the list");
            }

            await _host.Service.PinAsync(_unitId, Selected!.Entry.VersionId, false);
            await ReloadAsync();
        }
        else
        {
            problems.Add("the first version is not restorable");
        }

        return problems;
    }

    private HistoryRow? Selected => Versions.SelectedItem as HistoryRow;

    private async Task ReloadAsync()
    {
        try
        {
            var keepSelection = Selected?.Entry.VersionId;
            var entries = await _host.Service!.GetHistoryAsync(_unitId);
            var rows = entries.Select(e => new HistoryRow { Entry = e }).ToList();
            Versions.ItemsSource = rows;
            Versions.SelectedItem = rows.FirstOrDefault(r => r.Entry.VersionId == keepSelection);
            Message.Text = rows.Count == 0 ? "No versions yet." : $"{rows.Count} version(s), {rows.Count(r => r.Entry.Restorable)} restorable.";
        }
        catch (Exception ex)
        {
            Message.Text = ex.Message;
        }

        UpdateButtons();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        if (RestoreButton is null)
        {
            return;
        }

        var row = Selected;
        RestoreButton.IsEnabled = row is { Entry: { Restorable: true, IsLatest: false } };
        ExportButton.IsEnabled = row is { Entry.Restorable: true };
        PinButton.IsEnabled = row is { Entry: { Restorable: true } } || row is { Entry.PinnedHere: true };
        PinButton.Content = row is { Entry.PinnedHere: true } ? "Unpin" : "Pin";
        PinButton.Icon = new SymbolIcon(row is { Entry.PinnedHere: true } ? SymbolRegular.PinOff24 : SymbolRegular.Pin24);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        var steamBackup = _host.Service!.GetDefinition(_unitId)?.Mode == UnitMode.BackupOnly;
        var answer = MessageBox.Show(this,
            steamBackup
                ? $"Restore the version saved {row.When} ({row.From}) into Steam's save folder on this PC?\n\n" +
                  "Steam must be closed. Only this PC is changed; your other PCs keep their own backups. " +
                  "The version it replaces stays in the history.\n\n" +
                  "Heads-up: at its next start Steam may report a Cloud conflict for this game. " +
                  "Choose to keep the LOCAL copy, otherwise Steam replaces the restored saves with its cloud copy."
                : $"Restore the version saved {row.When} ({row.From})?\n\n" +
                  "The saves on this PC, and then on all your other PCs, will be replaced by it. " +
                  "The version they replace stays in the history, so you can undo this.",
            "Restore version", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        RestoreButton.IsEnabled = false;
        try
        {
            var result = await _host.Service!.RestoreVersionAsync(_unitId, row.Entry.VersionId);
            var text = result.Kind switch
            {
                SyncResultKind.Pulled => "Restored. Your other PCs will receive it shortly.",
                SyncResultKind.UpToDate => result.Message ?? "Nothing to do.",
                _ => result.Message ?? result.Kind.ToString(),
            };
            MessageBox.Show(this, text, "Restore version", MessageBoxButton.OK,
                result.Kind == SyncResultKind.Pulled ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Restore version", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        await ReloadAsync();
    }

    private async void OnPin(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        try
        {
            await _host.Service!.PinAsync(_unitId, row.Entry.VersionId, !row.Entry.PinnedHere);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Pin version", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await ReloadAsync();
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || _host.Remote is null)
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = $"Export the version saved {row.When} to…" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var target = dialog.FolderName;
            var remote = _host.Remote;
            var versionId = row.Entry.VersionId;
            var report = await Task.Run(() => new Core.Recovery.RecoveryService(remote).Export(_unitId, target, versionId));
            var problems = report.Problems.Count == 0 ? "" : $"\n\nProblems:\n{string.Join("\n", report.Problems.Take(10))}";
            MessageBox.Show(this, $"{report.FilesWritten} file(s) exported to:\n{string.Join("\n", report.ExportedFolders)}{problems}",
                "Export", MessageBoxButton.OK, report.Problems.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnSaveKeep(object sender, RoutedEventArgs e)
    {
        var keep = (int)(KeepBox.Value ?? UnitDefinition.DefaultKeepVersions);
        try
        {
            await _host.Service!.SetKeepVersionsAsync(_unitId, keep);
            Message.Text = $"Saved: the latest {keep} versions stay restorable. Older ones are archived at the next daily cleanup.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
