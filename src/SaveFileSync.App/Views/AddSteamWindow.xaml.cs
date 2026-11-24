using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SaveFileSync.Core.Model;
using SaveFileSync.Steam;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

public sealed partial class SteamItem : ObservableObject
{
    public required SteamCandidate Candidate { get; init; }

    public required bool CanSelect { get; init; }

    public required string Detail { get; init; }

    [ObservableProperty]
    private bool _isChecked;

    public string Name => Candidate.Name;
}

/// <summary>Finds Steam games with saves on this PC and adds them as read-only backups (PLAN.md §11, phase 5).</summary>
public partial class AddSteamWindow : FluentWindow
{
    private readonly AppHost _host;
    private List<SteamItem> _items = [];

    public AddSteamWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        AddButton.IsEnabled = false;
        Loaded += async (_, _) => await ScanAsync();
    }

    private async Task ScanAsync()
    {
        var steam = _host.CurrentSteam(refresh: true);
        if (steam is null)
        {
            Progress.Visibility = Visibility.Collapsed;
            Notice.IsOpen = true;
            Notice.Title = "Steam was not found on this PC";
            Notice.Message = "Steam's folder is read from the registry (Software\\Valve\\Steam). Install Steam or sign in once, then try again.";
            return;
        }

        var index = _host.Manifest.Load();
        var folders = _host.Resolver.CreateContext().Folders;
        var found = await Task.Run(() => SteamBackupFinder.Find(steam, index?.Games ?? [], folders));

        var existing = _host.Service!.CurrentStatus
            .Select(s => _host.Service.GetDefinition(s.UnitId))
            .OfType<UnitDefinition>()
            .Where(d => d.Source?.SteamId is not null)
            .Select(d => d.Source!.SteamId!.Value)
            .ToHashSet();

        _items = found.Select(c =>
        {
            var already = existing.Contains(c.AppId);
            var detail = already
                ? "Already in your library"
                : $"{(c.Installed ? "Installed" : "Not installed")} · {c.Where} · {c.FileCount:N0} file(s)"
                  + (c.ConfirmedByCloud ? " · tracked by Steam Cloud" : " · not confirmed by Steam Cloud");
            return new SteamItem { Candidate = c, CanSelect = !already, Detail = detail, IsChecked = !already && c.ConfirmedByCloud };
        }).ToList();
        foreach (var item in _items)
        {
            item.PropertyChanged += (_, _) => UpdateSummary();
        }

        Games.ItemsSource = _items;
        Progress.Visibility = Visibility.Collapsed;
        if (index is null)
        {
            Notice.IsOpen = true;
            Notice.Title = "Game database not downloaded";
            Notice.Message = "Only games with files in Steam's userdata folder are listed. Update the game database in Settings (or add a game from it once) to also find games that keep saves in their own folders.";
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selected = _items.Count(i => i.IsChecked && i.CanSelect);
        Summary.Text = _items.Count == 0
            ? "No Steam games with saves were found."
            : $"{_items.Count} game(s) with saves found, {selected} selected.";
        AddButton.IsEnabled = selected > 0;
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        AddButton.IsEnabled = false;
        var added = 0;
        try
        {
            foreach (var item in _items.Where(i => i.IsChecked && i.CanSelect).ToList())
            {
                var draft = item.Candidate.Draft;
                await _host.Service!.AddUnitAsync(
                    draft.Name, draft.Locations, item.Candidate.ProcessNames, draft.Bindings,
                    MergeGranularity.Unit, draft.Source, UnitMode.BackupOnly);
                added++;
            }

            _host.Metadata.Trigger();
            MessageBox.Show(this, $"{added} game(s) added. Their saves are being backed up.", "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            AddButton.IsEnabled = true;
            MessageBox.Show(this, ex.Message, "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
