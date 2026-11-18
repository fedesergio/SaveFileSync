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

/// <summary>Conflict dialog: one card per version, the user keeps one (PLAN.md §8, item 6).</summary>
public partial class ConflictWindow : FluentWindow
{
    private readonly AppHost _host;
    private readonly string _unitId;
    private List<CandidateItem> _items = [];

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
        var status = App.FindStatus(_unitId);
        Explanation.Text = status?.Conflict?.Reason switch
        {
            ConflictReason.RemoteDiverged => "Two other PCs changed these saves independently. Choose the version to use everywhere.",
            _ => "These saves changed on this PC and on another PC since the last sync. Choose the version to use everywhere.",
        };

        IReadOnlyList<VersionManifest> candidates = await service.GetConflictCandidatesAsync(_unitId);
        var thisDevice = _host.Device!.DeviceId;
        _items = candidates
            .OrderByDescending(c => c.DeviceId == thisDevice)
            .Select(c => new CandidateItem
            {
                VersionId = c.VersionId,
                Title = c.DeviceId == thisDevice ? "This PC" : service.DeviceName(c.DeviceId),
                When = c.CreatedUtc.ToLocalTime().ToString("f"),
                ClockNote = c.DeviceId == thisDevice ? "(time of this PC)" : $"(time as reported by {service.DeviceName(c.DeviceId)})",
                Files = c.Files.Count,
                Size = Cli.FormatSize(c.TotalSize),
                Hint = c.DeviceId != thisDevice && service.ArrivedWhileGameOpen(_unitId, c.VersionId)
                    ? "This version was created while the game was open on this PC."
                    : "",
            })
            .ToList();

        if (_items.Count == 0)
        {
            Explanation.Text = "This conflict has already been resolved.";
            KeepButton.IsEnabled = false;
        }

        Candidates.ItemsSource = _items;
    }

    private async void OnKeep(object sender, RoutedEventArgs e)
    {
        var chosen = _items.FirstOrDefault(i => i.IsSelected);
        if (chosen is null)
        {
            MessageBox.Show(this, "Select the version to keep.", "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        KeepButton.IsEnabled = false;
        try
        {
            var result = await _host.Service!.ResolveConflictAsync(_unitId, chosen.VersionId);
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
