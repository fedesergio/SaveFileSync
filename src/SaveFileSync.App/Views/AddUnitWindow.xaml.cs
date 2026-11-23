using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SaveFileSync.Core.Model;
using SaveFileSync.Manifest;
using SaveFileSync.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

public sealed record GameItem(GameRecord Game, bool Detected)
{
    public string Name => Game.Name;
}

/// <summary>
/// Adds a game: found in the Ludusavi game database, an emulator preset, or chosen by hand
/// (PLAN.md §8, item 3). All three fill the same "check what will be synced" form.
/// </summary>
public partial class AddUnitWindow : FluentWindow
{
    private static readonly TimeSpan MaxManifestAge = TimeSpan.FromDays(7);

    private readonly AppHost _host;
    private readonly ObservableCollection<LocationRow> _locations = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private ManifestIndex? _index;
    private HashSet<string> _detectedNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GameRecord> _detected = [];
    private UnitSource? _source;
    private EmulatorPreset? _preset;
    private string? _presetDir;
    private bool _filling;

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
        PresetBox.ItemsSource = EmulatorPresets.All.Select(p => p.DisplayName).ToList();
        PresetBox.SelectedIndex = 0;
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RunSearch();
        };
        Loaded += async (_, _) => await LoadDatabaseAsync();
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _searchTimer.Stop();
            _index = null; // the index is only needed while this window is open
        };
        Validate();
    }

    /// <summary>Dev-only (<c>--selftest-ui</c>): exercises the search, the form and the emulator paths.</summary>
    internal async Task<IReadOnlyList<string>> SelfTestAsync()
    {
        var problems = new List<string>();
        var deadline = DateTime.UtcNow.AddSeconds(240);
        while (_index is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
        }

        if (_index is null)
        {
            problems.Add("the game database could not be loaded");
            return problems;
        }

        SearchBox.Text = "celeste";
        _searchTimer.Stop();
        RunSearch();
        if (Results.Items.Count == 0)
        {
            problems.Add("searching for 'celeste' found nothing");
        }
        else
        {
            Results.SelectedIndex = 0;
            await Task.Delay(1500);
            if (Results.SelectedItem is GameItem item && GameName.Text != item.Name)
            {
                problems.Add("selecting a result did not fill the form");
            }
        }

        if (_detected.Count > 0)
        {
            await FillFromGameAsync(_detected[0]);
            if (_locations.Count == 0)
            {
                problems.Add($"detected game '{_detected[0].Name}' produced no save folder");
            }
        }

        foreach (var i in Enumerable.Range(0, EmulatorPresets.All.Count))
        {
            PresetBox.SelectedIndex = i;
            EmulatorFolder.Text = "";
            OnDetectEmulator(this, new RoutedEventArgs());
            if (PresetStatus.Text.Length == 0)
            {
                problems.Add("emulator detection gave no status text");
            }
        }

        Modes.SelectedIndex = 2;
        return problems;
    }

    // ------------------------------------------------------------ game database

    private async Task LoadDatabaseAsync()
    {
        var store = _host.Manifest;
        try
        {
            if (store.HasIndex)
            {
                await ShowDatabaseAsync();
            }

            if (store.IsStale(MaxManifestAge, DateTime.UtcNow))
            {
                SetDatabaseStatus(store.HasIndex ? "Checking for a newer game database…" : "Downloading the game database (about 17 MB, once)…", busy: true);
                var result = await store.UpdateAsync(force: false, DateTime.UtcNow, _cts.Token);
                if (result.Kind == ManifestUpdateKind.Failed)
                {
                    SetDatabaseStatus(
                        store.HasIndex
                            ? "Could not check for updates; using the saved game database."
                            : $"Could not download the game database ({result.Error}). You can still add a game manually.",
                        busy: false);
                }

                if (result.Kind == ManifestUpdateKind.Updated)
                {
                    await ShowDatabaseAsync();
                }
            }

            if (_index is not null)
            {
                SetDatabaseStatus(DatabaseSummary(), busy: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ShowDatabaseAsync()
    {
        var store = _host.Manifest;
        var ctx = _host.Resolver.CreateContext();
        var loaded = await Task.Run(() => store.Load(), _cts.Token);
        if (loaded is null)
        {
            return;
        }

        _index = loaded;
        SetDatabaseStatus("Looking for games on this PC…", busy: true);
        _detected = await Task.Run(() => GameDetector.DetectInstalled(loaded, ctx, _cts.Token), _cts.Token);
        _detectedNames = _detected.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SetDatabaseStatus(DatabaseSummary(), busy: false);
        RunSearch();
    }

    private string DatabaseSummary()
    {
        var status = _host.Manifest.GetStatus();
        var when = status.UpdatedUtc is { } t ? $", updated {t.ToLocalTime():d}" : "";
        return $"{_index?.Games.Count ?? status.GameCount:N0} games with Windows save data{when}. {_detected.Count} found on this PC" +
               (_host.Config.Snapshot().LibraryRoots.Count == 0 ? " (add your game library folders in Settings to find more)." : ".");
    }

    private void SetDatabaseStatus(string text, bool busy)
    {
        DatabaseStatus.Text = text;
        DatabaseProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, RoutedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void RunSearch()
    {
        if (_index is null)
        {
            Results.ItemsSource = null;
            return;
        }

        var query = SearchBox.Text.Trim();
        var games = query.Length == 0 ? _detected.Take(200).ToList() : _index.Search(query, 60);
        Results.ItemsSource = games.Select(g => new GameItem(g, _detectedNames.Contains(g.Name))).ToList();
    }

    private async void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is GameItem item && !_filling)
        {
            await FillFromGameAsync(item.Game);
        }
    }

    private async Task FillFromGameAsync(GameRecord game)
    {
        _filling = true;
        try
        {
            var ctx = _host.Resolver.CreateContext(game.InstallDirs);
            var roots = _host.Config.Snapshot().LibraryRoots;
            var candidates = await Task.Run(() => LocationFinder.Find(game, ctx));
            var processes = await Task.Run(() => GameDetector.SuggestProcessNames(game, roots));

            _locations.Clear();
            _preset = null;
            _source = new UnitSource
            {
                Kind = UnitSource.ManifestKind,
                ManifestName = game.Name,
                SteamId = game.SteamId,
                InstallDirs = game.InstallDirs,
                RegistryKeys = game.RegistryKeys.Count,
            };

            GameName.Text = game.Name;
            ProcessNames.Text = string.Join("; ", processes.Select(p => p + ".exe"));
            Granularity.SelectedIndex = 0;

            foreach (var c in candidates)
            {
                _locations.Add(new LocationRow
                {
                    Path = c.AbsoluteRoot,
                    Id = LocationRow.MakeId(c.Label, _locations.Select(l => l.Id)),
                    Label = c.Label,
                    Includes = string.Join("; ", c.Includes),
                    Templates = [c.RootTemplate],
                    TemplatePath = c.AbsoluteRoot,
                    IsSelected = c.Save,
                    Info = $"{c.FileCount} file(s), {Cli.FormatSize(c.Bytes)} · " + (c.Save ? "save data" : "settings only (not checked)"),
                });
            }

            SetRegistryNote(game.RegistryKeys.Count);
            PreviewText.Text = candidates.Count == 0
                ? "No save folder of this game was found on this PC. Add its folder with “Add folder…” (other PCs will then need it chosen by hand), or install the game and look again."
                : "";
            UpdateWarnings();
        }
        finally
        {
            _filling = false;
            Validate();
        }
    }

    private void SetRegistryNote(int keys)
    {
        RegistryBar.IsOpen = keys > 0;
        RegistryBar.Message = keys > 0
            ? $"This game also keeps data in the Windows registry ({keys} key(s)). SaveFileSync cannot sync registry data yet: only the files below are synced."
            : "";
    }

    // ------------------------------------------------------------ emulators

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // TabControl bubbles SelectionChanged from the ListBox/ComboBox inside it.
        if (!ReferenceEquals(e.OriginalSource, Modes))
        {
            return;
        }

        if (Modes.SelectedIndex == 2)
        {
            _source = null;
            _preset = null;
            SetRegistryNote(0);
        }
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        var preset = EmulatorPresets.All.ElementAtOrDefault(PresetBox.SelectedIndex);
        PresetSummary.Text = preset?.Summary ?? "";
        PresetStatus.Text = "";
    }

    private void OnBrowseEmulator(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder of the emulator (where its exe is)" };
        if (dialog.ShowDialog(this) == true)
        {
            EmulatorFolder.Text = dialog.FolderName;
            OnDetectEmulator(sender, e);
        }
    }

    private void OnDetectEmulator(object sender, RoutedEventArgs e)
    {
        var preset = EmulatorPresets.All.ElementAtOrDefault(PresetBox.SelectedIndex);
        if (preset is null)
        {
            return;
        }

        var typed = EmulatorFolder.Text.Trim();
        var dir = typed.Length > 0 ? typed : null;
        if (dir is not null && !Directory.Exists(dir))
        {
            PresetStatus.Text = "That folder does not exist.";
            return;
        }

        var bound = _host.Resolver.BindPreset(preset, dir);
        if (bound is null)
        {
            PresetStatus.Text = $"Could not find the {preset.DisplayName} data on this PC. Choose the folder where {preset.ExeName} is" +
                                (preset.Id == "rpcs3" ? " (its dev_hdd0 must already contain a user folder: start the emulator once)." : ".");
            return;
        }

        _filling = true;
        try
        {
            _locations.Clear();
            _preset = preset;
            _presetDir = dir ?? preset.FindEmulatorDirs(_host.Config.Snapshot().EmulatorDirs, _host.Config.Snapshot().LibraryRoots, new WindowsProcessPaths()).FirstOrDefault();
            _source = preset.Source;
            GameName.Text = preset.Id == "rpcs3" ? "RPCS3 saves" : "Ryujinx saves";
            ProcessNames.Text = string.Join("; ", preset.ProcessNames.Select(p => p + ".exe"));
            Granularity.SelectedIndex = GranularityOptions.IndexOf(preset.Granularity);
            SetRegistryNote(0);

            foreach (var location in preset.Locations)
            {
                var path = bound[location.Id];
                var exists = Directory.Exists(path);
                var row = new LocationRow
                {
                    Path = path,
                    Id = location.Id,
                    Label = location.Label,
                    Includes = string.Join("; ", location.Includes),
                    Excludes = string.Join("; ", location.Excludes),
                };
                var (files, bytes) = exists ? row.Preview() : (0, 0L);
                row.Info = exists
                    ? $"{files} file(s), {Cli.FormatSize(bytes)}"
                    : "This folder does not exist yet (no save has been made): it will be created when the first save arrives.";
                _locations.Add(row);
            }

            PresetStatus.Text = $"Found: {string.Join("; ", bound.Values)}";
            PreviewText.Text = "";
            UpdateWarnings();
        }
        finally
        {
            _filling = false;
            Validate();
        }
    }

    // ------------------------------------------------------------ form

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_filling)
        {
            UpdateWarnings();
        }

        Validate();
    }

    private void Validate()
    {
        if (AddButton is not null)
        {
            AddButton.IsEnabled = GameName.Text.Trim().Length > 0 && _locations.Any(l => l.IsSelected);
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
        var selected = _locations.Where(l => l.IsSelected).ToList();
        var totals = selected.Select(l => l.Preview()).ToList();
        PreviewText.Text = selected.Count == 0
            ? ""
            : $"{totals.Sum(t => t.Files)} file(s), {Cli.FormatSize(totals.Sum(t => t.Bytes))} would be synced.";
    }

    private void UpdateWarnings()
    {
        var repo = _host.Config.Snapshot().RepositoryPath ?? "";
        var messages = new List<string>();
        foreach (var location in _locations.Where(l => l.IsSelected))
        {
            if (OneDrive.IsInsideOneDrive(location.Path))
            {
                messages.Add($"{location.Path} is inside OneDrive, which already syncs it: syncing it again can create OneDrive conflict copies of game files.");
            }

            if (repo.Length > 0 && (location.Path.StartsWith(repo, StringComparison.OrdinalIgnoreCase) || repo.StartsWith(location.Path, StringComparison.OrdinalIgnoreCase)))
            {
                messages.Add($"{location.Path} overlaps the SaveFileSync repository folder.");
            }

            if (ControlledFolderAccess.WarningFor(location.Path) is { } cfa)
            {
                messages.Add(cfa);
            }
        }

        Warning.IsOpen = messages.Count > 0;
        Warning.Title = "Check these folders";
        Warning.Message = string.Join(Environment.NewLine, messages.Distinct());
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
        var selected = _locations.Where(l => l.IsSelected).ToList();
        var missing = selected.FirstOrDefault(l => !Directory.Exists(l.Path));
        if (missing is not null && _preset is null)
        {
            MessageBox.Show(this, $"The folder {missing.Path} does not exist.", "Add game", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AddButton.IsEnabled = false;
        try
        {
            foreach (var row in selected.Where(l => !Directory.Exists(l.Path)))
            {
                Directory.CreateDirectory(row.Path);
            }

            await _host.Service!.AddUnitAsync(
                GameName.Text.Trim(),
                selected.Select(l => l.ToDefinition()).ToList(),
                LocationRow.Split(ProcessNames.Text),
                selected.ToDictionary(l => l.Id, l => l.Path),
                GranularityOptions.At(Granularity.SelectedIndex),
                _source ?? new UnitSource());

            if (_preset is not null && _presetDir is not null)
            {
                _host.Config.Update(c => c.EmulatorDirs[_preset.Id] = _presetDir);
            }

            Close();
        }
        catch (Exception ex)
        {
            AddButton.IsEnabled = true;
            MessageBox.Show(this, ex.Message, "Add game", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
