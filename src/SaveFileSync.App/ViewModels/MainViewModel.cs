using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SaveFileSync.Core.Service;
using SaveFileSync.Windows;

namespace SaveFileSync.App.ViewModels;

public sealed partial class UnitItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private UnitState _state;

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private string _lastSyncText = "";

    /// <summary>Extra line under the status, e.g. that registry data is not synced.</summary>
    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private ImageSource? _cover;

    private string? _coverPath;
    private DateTime? _quietUntil;
    private string _baseDetail = "";

    public UnitItemViewModel(string unitId)
    {
        UnitId = unitId;
    }

    public string UnitId { get; }

    public bool HasCover => Cover is not null;

    /// <summary>Loads the cover from the cache folder (decoded small, off the UI thread). Null clears it.</summary>
    public async Task SetCoverAsync(string? path)
    {
        if (path == _coverPath)
        {
            return;
        }

        _coverPath = path;
        if (path is null)
        {
            Cover = null;
            return;
        }

        var image = await Task.Run(() => CoverImage.Load(path, CoverImage.TileWidth));
        if (_coverPath == path)
        {
            Cover = image;
        }
    }

    public void Update(UnitStatus status, string? coverPath)
    {
        _ = SetCoverAsync(coverPath);
        Name = status.Name;
        State = status.State;
        _baseDetail = status.Detail;
        _quietUntil = status.QuietUntilUtc;
        var last = status.LastSyncUtc is { } t ? $"Last sync {t.ToLocalTime():g}" : "Never synced on this PC";
        LastSyncText = status.LastChangeDevice is { } device ? $"{last} · latest from {device}" : last;
        Note = status.HasRegistryData ? "Also uses the Windows registry (registry data is not synced)" : "";
        Tick();
    }

    /// <summary>Refreshes the countdown of the quiet period.</summary>
    public void Tick()
    {
        if (State == UnitState.Waiting && _quietUntil is { } until && until > DateTime.UtcNow)
        {
            var left = until - DateTime.UtcNow;
            Detail = $"{_baseDetail} ({left:m\\:ss})";
        }
        else
        {
            Detail = _baseDetail;
        }
    }
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly SyncService _service;
    private readonly DispatcherTimer _timer;
    private bool _loading;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _hasUnits;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private bool _isGridView;

    /// <summary>Attribution line, shown while at least one cover is displayed.</summary>
    [ObservableProperty]
    private string _coversCredit = "";

    public MainViewModel(AppHost host)
    {
        _host = host;
        _service = host.Service!;
        var config = host.Config.Snapshot();
        DeviceName = config.DeviceName ?? Environment.MachineName;
        RepositoryPath = config.RepositoryPath ?? "";
        Version = AppHost.AppVersion;

        _loading = true;
        StartWithWindows = Autostart.IsEnabled(host.Profile);
        IsGridView = config.GridView;
        _loading = false;

        _service.StatusChanged += OnStatusChanged;
        host.Config.Changed += OnConfigChanged;
        host.Metadata.UnitChanged += OnMetadataChanged;
        Apply(_service.CurrentStatus);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            foreach (var unit in Units)
            {
                unit.Tick();
            }
        };
        _timer.Start();
    }

    public ObservableCollection<UnitItemViewModel> Units { get; } = [];

    public string DeviceName { get; }

    public string RepositoryPath { get; }

    public string Version { get; }

    [RelayCommand]
    private void SyncAll() => _service.RequestSyncAll();

    [RelayCommand]
    private void TogglePause()
    {
        if (App.Current.IsPaused())
        {
            _service.ResumeAll();
        }
        else
        {
            _service.PauseAll(DateTime.MaxValue);
        }
    }

    [RelayCommand]
    private void ToggleView() => IsGridView = !IsGridView;

    partial void OnIsGridViewChanged(bool value)
    {
        if (!_loading)
        {
            _host.Config.Update(c => c.GridView = value);
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        if (value)
        {
            Autostart.Enable(Environment.ProcessPath!, _host.Profile);
        }
        else
        {
            Autostart.Disable(_host.Profile);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _service.StatusChanged -= OnStatusChanged;
        _host.Config.Changed -= OnConfigChanged;
        _host.Metadata.UnitChanged -= OnMetadataChanged;
    }

    private void OnMetadataChanged(string unitId) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Apply(_service.CurrentStatus));

    private void OnStatusChanged(IReadOnlyList<UnitStatus> status) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Apply(status));

    private void OnConfigChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Apply(_service.CurrentStatus));

    private void Apply(IReadOnlyList<UnitStatus> status)
    {
        var ordered = status
            .OrderBy(s => s.IsBound ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = Units.Count - 1; i >= 0; i--)
        {
            if (ordered.All(s => s.UnitId != Units[i].UnitId))
            {
                Units.RemoveAt(i);
            }
        }

        var anyCover = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            var existing = Units.FirstOrDefault(u => u.UnitId == ordered[i].UnitId);
            if (existing is null)
            {
                existing = new UnitItemViewModel(ordered[i].UnitId);
                Units.Insert(Math.Min(i, Units.Count), existing);
            }
            else if (Units.IndexOf(existing) != i)
            {
                Units.Move(Units.IndexOf(existing), Math.Min(i, Units.Count - 1));
            }

            var definition = _service.GetDefinition(ordered[i].UnitId);
            var coverPath = definition is null ? null : _host.Metadata.CoverPath(definition);
            anyCover |= coverPath is not null;
            existing.Update(ordered[i], coverPath);
        }

        CoversCredit = anyCover ? " · covers: SteamGridDB" : "";

        HasUnits = Units.Count > 0;
        IsPaused = App.Current.IsPaused();
        Problem = _service.FatalError
            ?? (status.Any(s => s.State == UnitState.RepositoryUnavailable)
                ? $"The repository folder is not reachable: {RepositoryPath}. Is OneDrive running?"
                : null);

        var attention = status.Count(s => s.State is UnitState.Conflict or UnitState.Attention or UnitState.Error or UnitState.Stale or UnitState.LocationMissing);
        var notSetUp = status.Count(s => !s.IsBound);
        Summary = IsPaused ? "Sync is paused."
            : attention > 0 ? $"{attention} game(s) need your attention."
            : Units.Count == 0 ? "No games yet."
            : notSetUp > 0 ? $"All set-up games are in sync. {notSetUp} game(s) can be set up on this PC."
            : "All games are in sync.";
    }
}
