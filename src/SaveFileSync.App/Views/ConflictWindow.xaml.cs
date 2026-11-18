using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Sync;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

public sealed partial class CandidateItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public required string VersionId { get; init; }

    public required string Title { get; init; }

    public required string When { get; init; }

    public required string ClockNote { get; init; }

    public int Files { get; init; }

    public required string Size { get; init; }

    public string Hint { get; init; } = "";

    public bool HasHint => Hint.Length > 0;
}

public sealed record GroupOption(string VersionId, string Label);

public sealed partial class GroupItem : ObservableObject
{
    [ObservableProperty]
    private string? _selectedVersionId;

    public required string Key { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyList<GroupOption> Options { get; init; }
}

/// <summary>
/// Conflict dialog (PLAN.md §8, item 6): one card per version; with folder/file granularity
/// each conflicting save can be taken from a different version (phase 1b).
/// </summary>
public partial class ConflictWindow : FluentWindow
{
    private readonly AppHost _host;
    private readonly string _unitId;
    private List<CandidateItem> _items = [];
    private List<GroupItem> _groups = [];

    public ConflictWindow(AppHost host, string unitId)
    {
        _host = host;
        _unitId = unitId;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var service = _host.Service!;
        var definition = service.GetDefinition(_unitId);
        Header.Text = definition?.Name ?? "Conflict";
        var conflict = App.FindStatus(_unitId)?.Conflict;
        Explanation.Text = conflict?.Reason switch
        {
            ConflictReason.RemoteDiverged => "Two other PCs changed these saves independently. Choose the version to use everywhere.",
            _ => "These saves changed on this PC and on another PC since the last sync. Choose the version to use everywhere.",
        };

        IReadOnlyList<VersionManifest> candidates = await service.GetConflictCandidatesAsync(_unitId);
        var thisDevice = _host.Device!.DeviceId;
        var ordered = candidates.OrderByDescending(c => c.DeviceId == thisDevice).ToList();
        _items = ordered.Select(c => new CandidateItem
        {
            VersionId = c.VersionId,
            Title = TitleOf(c),
            When = c.CreatedUtc.ToLocalTime().ToString("f"),
            ClockNote = c.DeviceId == thisDevice ? "(time of this PC)" : $"(time as reported by {service.DeviceName(c.DeviceId)})",
            Files = c.Files.Count,
            Size = Cli.FormatSize(c.TotalSize),
            Hint = c.DeviceId != thisDevice && service.ArrivedWhileGameOpen(_unitId, c.VersionId)
                ? "This version was created while the game was open on this PC."
                : "",
        }).ToList();

        foreach (var item in _items)
        {
            item.PropertyChanged += OnCandidateChanged;
        }

        if (_items.Count == 0)
        {
            Explanation.Text = "This conflict has already been resolved.";
            KeepButton.IsEnabled = false;
        }

        Candidates.ItemsSource = _items;

        if (conflict is { Granularity: not MergeGranularity.Unit, Groups.Count: > 0 })
        {
            _groups = conflict.Groups.Select(g => new GroupItem
            {
                Key = g,
                Name = g,
                Options = ordered.Select(c => new GroupOption(c.VersionId, DescribeGroup(c, g, conflict.Granularity))).ToList(),
            }).ToList();
            Groups.ItemsSource = _groups;
            GroupsPanel.Visibility = Visibility.Visible;
        }
    }

    private string TitleOf(VersionManifest version) =>
        version.DeviceId == _host.Device!.DeviceId ? "This PC" : _host.Service!.DeviceName(version.DeviceId);

    private string DescribeGroup(VersionManifest version, string group, MergeGranularity granularity)
    {
        var files = Merge.FilesInGroup(version.Content, group, granularity);
        if (files.Count == 0)
        {
            return $"{TitleOf(version)} — deleted";
        }

        var latest = files.Max(f => f.MtimeUtc).ToLocalTime();
        return $"{TitleOf(version)} — {Cli.FormatSize(files.Sum(f => f.Size))}, modified {latest:g}";
    }

    private void OnCandidateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CandidateItem.IsSelected) && sender is CandidateItem { IsSelected: true } selected)
        {
            foreach (var group in _groups)
            {
                group.SelectedVersionId = selected.VersionId;
            }
        }
    }

    private async void OnKeep(object sender, RoutedEventArgs e)
    {
        var chosen = _items.FirstOrDefault(i => i.IsSelected)?.VersionId
            ?? _groups.Select(g => g.SelectedVersionId).FirstOrDefault(id => id is not null);
        if (chosen is null || _groups.Any(g => g.SelectedVersionId is null && _items.All(i => !i.IsSelected)))
        {
            MessageBox.Show(this, "Select the version to keep.", "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var choices = _groups
            .Where(g => g.SelectedVersionId is not null)
            .ToDictionary(g => g.Key, g => g.SelectedVersionId!, StringComparer.OrdinalIgnoreCase);

        KeepButton.IsEnabled = false;
        try
        {
            var result = await _host.Service!.ResolveConflictAsync(_unitId, chosen, choices.Count > 0 ? choices : null);
            if (result.Kind == SyncResultKind.PushDeferred)
            {
                MessageBox.Show(this, "Close the game first, then choose again.", "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Warning);
                KeepButton.IsEnabled = true;
                return;
            }

            Close();
        }
        catch (Exception ex)
        {
            KeepButton.IsEnabled = true;
            MessageBox.Show(this, ex.Message, "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();
}
