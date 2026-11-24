using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SaveFileSync.Core.Config;
using SaveFileSync.Core.History;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Paths;
using SaveFileSync.Core.Sync;

namespace SaveFileSync.Core.Service;

public sealed class SyncServiceOptions
{
    public bool UseWatchers { get; init; } = true;

    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    public string? AppVersion { get; init; }
}

/// <summary>
/// Background scheduler (PLAN.md §6.5–6.6). A single worker thread owns all sync work;
/// UI commands are queued to it. <see cref="Tick"/> is also callable directly by tests.
/// </summary>
public sealed class SyncService : IDisposable
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1),
    ];

    private static readonly TimeSpan[] ResumeChecks =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60),
    ];

    private readonly ILocationResolver? _resolver;
    private readonly HistoryService _history;
    private DateTime _nextMaintenance = DateTime.MinValue;
    private readonly SyncEngine _engine;
    private readonly ConfigStore _config;
    private readonly DeviceIdentity _device;
    private readonly IProcessMonitor _processes;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private readonly SyncServiceOptions _options;
    private readonly FolderWatchers _watchers = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Dictionary<string, UnitRuntime> _units = new(StringComparer.Ordinal);
    private readonly List<DateTime> _resumeChecks = [];
    private readonly Queue<ActivityEntry> _activity = new();

    private Dictionary<string, string> _deviceNames = new(StringComparer.Ordinal);
    private IReadOnlySet<string> _running = new HashSet<string>();
    private IReadOnlyList<UnitStatus> _status = [];
    private DateTime _lastRemotePoll = DateTime.MinValue;
    private DateTime _lastFullScan = DateTime.MinValue;
    private DateTime _lastProcessPoll = DateTime.MinValue;
    private DateTime _lastDeviceWrite = DateTime.MinValue;
    private string? _fatalError;
    private bool _repositoryWasAvailable;
    private Thread? _worker;
    private CancellationTokenSource? _cts;

    public SyncService(
        SyncEngine engine,
        ConfigStore config,
        DeviceIdentity device,
        IProcessMonitor processes,
        IClock clock,
        ILogger? log = null,
        SyncServiceOptions? options = null,
        ILocationResolver? resolver = null)
    {
        _resolver = resolver;
        _engine = engine;
        _history = new HistoryService(engine.Remote, engine, device, clock, log);
        _config = config;
        _device = device;
        _processes = processes;
        _clock = clock;
        _log = log ?? NullLogger.Instance;
        _options = options ?? new SyncServiceOptions();
    }

    /// <summary>Raised on the worker thread whenever the status of any unit changes.</summary>
    public event Action<IReadOnlyList<UnitStatus>>? StatusChanged;

    /// <summary>Raised on the worker thread for things the user should see (tray balloon).</summary>
    public event Action<SyncNotification>? Notification;

    /// <summary>Raised (on any thread) when <see cref="RecentActivity"/> gets a new entry.</summary>
    public event Action? ActivityAdded;

    public IReadOnlyList<UnitStatus> CurrentStatus => Volatile.Read(ref _status);

    public string? FatalError => _fatalError;

    public IReadOnlyDictionary<string, string> DeviceNames => _deviceNames;

    private AppConfig Config => _config.Snapshot();

    public void Start()
    {
        if (_worker is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _worker = new Thread(() => Run(token)) { IsBackground = true, Name = "SaveFileSync worker" };
        _worker.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _wake.Set();
        _worker?.Join(TimeSpan.FromSeconds(30));
        _worker = null;
    }

    public void Dispose()
    {
        Stop();
        _watchers.Dispose();
        _wake.Dispose();
        _cts?.Dispose();
    }

    // ---------------------------------------------------------------- commands

    public void RequestSyncAll() => Post(() =>
    {
        foreach (var rt in _units.Values)
        {
            rt.ForceNow = true;
        }
    });

    public void RequestSync(string unitId) => Post(() =>
    {
        if (_units.TryGetValue(unitId, out var rt))
        {
            rt.ForceNow = true;
        }
    });

    /// <summary>Call when Windows resumes from sleep: re-checks the repository (PLAN.md §6.6).</summary>
    public void OnResume() => Post(() =>
    {
        var now = _clock.UtcNow;
        _log.LogInformation("Resume from sleep: re-checking the repository");
        _resumeChecks.Clear();
        _resumeChecks.AddRange(ResumeChecks.Select(d => now + d));
        _lastProcessPoll = DateTime.MinValue;
        foreach (var rt in _units.Values)
        {
            rt.RemoteDirty = true;
            rt.NotifiedStaleVersion = null;
        }
    });

    public void PauseAll(DateTime untilUtc) => Post(() => _config.Update(c => c.PausedUntilUtc = untilUtc));

    public void ResumeAll() => Post(() =>
    {
        _config.Update(c => c.PausedUntilUtc = null);
        foreach (var rt in _units.Values)
        {
            rt.ForceNow = true;
        }
    });

    public void SetUnitPaused(string unitId, bool paused) => Post(() => _config.Update(c =>
    {
        c.PausedUnits.Remove(unitId);
        if (paused)
        {
            c.PausedUnits.Add(unitId);
        }
    }));

    /// <summary>Creates a new unit in the repository and binds it on this PC.</summary>
    public Task<string> AddUnitAsync(
        string name,
        IReadOnlyList<LocationDefinition> locations,
        IReadOnlyList<string> processNames,
        IReadOnlyDictionary<string, string> bindings,
        MergeGranularity granularity = MergeGranularity.Unit,
        UnitSource? source = null) =>
        Invoke(() =>
        {
            var unitId = NewUnitId(name);
            var definition = new UnitDefinition
            {
                UnitId = unitId,
                Name = name.Trim(),
                Locations = locations,
                ProcessNames = processNames.Select(NormalizeProcessName).Where(p => p.Length > 0).Distinct().ToList(),
                Granularity = granularity,
                Source = source,
                KeepVersions = Math.Clamp(Config.DefaultKeepVersions, 1, 1000),
                Revision = 1,
                RevisionDevice = _device.ShortId,
                UpdatedUtc = _clock.UtcNow,
            };
            _engine.Remote.WriteUnitDefinition(definition, _device.ShortId);
            _config.Update(c => c.Bindings[unitId] = new Dictionary<string, string>(bindings, StringComparer.Ordinal));
            RefreshUnits();
            _units[unitId].ForceNow = true;
            _log.LogInformation("Unit {Unit} ({Name}) added", unitId, name);
            AddActivity(name, ActivityKind.Info, "Added to sync");
            return unitId;
        });

    /// <summary>Writes a new revision of a unit definition (name, process names, filters).</summary>
    public Task UpdateUnitAsync(UnitDefinition updated) => Invoke(() =>
    {
        var current = _engine.Remote.ReadUnitDefinition(updated.UnitId) ?? updated;
        var next = current.Revised(
            _device.ShortId,
            _clock.UtcNow,
            name: updated.Name,
            locations: updated.Locations,
            processNames: updated.ProcessNames.Select(NormalizeProcessName).Where(p => p.Length > 0).Distinct().ToList(),
            granularity: updated.Granularity);
        _engine.Remote.WriteUnitDefinition(next, _device.ShortId);
        RefreshUnits();
        return true;
    });

    public Task BindAsync(string unitId, IReadOnlyDictionary<string, string> bindings) => Invoke(() =>
    {
        _config.Update(c => c.Bindings[unitId] = new Dictionary<string, string>(bindings, StringComparer.Ordinal));
        RefreshUnits();
        if (_units.TryGetValue(unitId, out var rt))
        {
            rt.ForceNow = true;
        }

        return true;
    });

    /// <summary>Stops syncing a unit on this PC; optionally marks it removed on every PC.</summary>
    public Task RemoveUnitAsync(string unitId, bool fromAllPcs) => Invoke(() =>
    {
        if (fromAllPcs && _engine.Remote.ReadUnitDefinition(unitId) is { } def)
        {
            _engine.Remote.WriteUnitDefinition(def.Revised(_device.ShortId, _clock.UtcNow, removed: true), _device.ShortId);
        }

        _config.Update(c =>
        {
            c.Bindings.Remove(unitId);
            c.PausedUnits.Remove(unitId);
        });
        _watchers.UnwatchUnit(unitId);
        RefreshUnits();
        return true;
    });

    /// <param name="groupChoices">Per conflicting group, the version to keep (phase 1b); others use <paramref name="chosenVersionId"/>.</param>
    public Task<SyncResult> ResolveConflictAsync(string unitId, string chosenVersionId, IReadOnlyDictionary<string, string>? groupChoices = null) =>
        RunUnitCommand(unitId, (def, bindings) =>
        {
            var result = _engine.Resolve(def, bindings, chosenVersionId, groupChoices);
            AddActivity(def.Name, ActivityKind.Resolved, "Conflict resolved");
            return result;
        });

    /// <summary>Most recent sync operations and problems, newest first (PLAN.md §8, item 9).</summary>
    public IReadOnlyList<ActivityEntry> RecentActivity
    {
        get
        {
            lock (_activity)
            {
                return _activity.Reverse().ToList();
            }
        }
    }

    /// <summary>Re-publishes this PC's device info (e.g. after renaming it in Settings).</summary>
    public void RefreshDeviceInfo() => Post(() =>
    {
        _lastDeviceWrite = DateTime.MinValue;
        _lastRemotePoll = DateTime.MinValue;
    });

    /// <summary>Repository files ignored as OneDrive conflict copies or foreign files (diagnostics).</summary>
    public Task<IReadOnlyList<string>> FindIgnoredRepositoryFilesAsync() => Invoke(() =>
        _engine.Remote is IRepositoryDiagnostics diagnostics ? diagnostics.FindIgnoredFiles() : (IReadOnlyList<string>)[]);

    private void AddActivity(string unitName, ActivityKind kind, string message)
    {
        lock (_activity)
        {
            _activity.Enqueue(new ActivityEntry(_clock.UtcNow, unitName, kind, message));
            while (_activity.Count > 300)
            {
                _activity.Dequeue();
            }
        }

        ActivityAdded?.Invoke();
    }

    public Task<SyncResult> RestoreFromRepositoryAsync(string unitId) =>
        RunUnitCommand(unitId, _engine.RestoreBase);

    public Task<SyncResult> PropagateDeletionAsync(string unitId) =>
        RunUnitCommand(unitId, _engine.PropagateDeletion);

    // ---------------------------------------------------------------- history (phase 3)

    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string unitId) => Invoke(() => _history.GetHistory(unitId));

    public Task PinAsync(string unitId, string versionId, bool pinned) => Invoke(() =>
    {
        _history.SetPinned(unitId, versionId, pinned);
        return true;
    });

    /// <summary>Restores an older version; every PC receives it as a normal change (PLAN.md §11, phase 3).</summary>
    public Task<SyncResult> RestoreVersionAsync(string unitId, string versionId) =>
        RunUnitCommand(unitId, (def, bindings) => _engine.RestoreVersion(def, bindings, versionId));

    /// <summary>Changes how many versions stay restorable for a game; shared by all PCs.</summary>
    public Task SetKeepVersionsAsync(string unitId, int keep) => Invoke(() =>
    {
        var current = _engine.Remote.ReadUnitDefinition(unitId) ?? throw new InvalidOperationException("Unknown game.");
        _engine.Remote.WriteUnitDefinition(
            current.Revised(_device.ShortId, _clock.UtcNow, keepVersions: Math.Clamp(keep, 1, 1000)),
            _device.ShortId);
        RefreshUnits();
        return true;
    });

    /// <summary>
    /// Stores the cover/official-name match of a game (shared by all PCs). Does nothing when it
    /// would not change anything, so that no needless definition revision is written.
    /// </summary>
    public Task<bool> SetMetadataAsync(string unitId, UnitMetadata metadata) => Invoke(() =>
    {
        var current = _engine.Remote.ReadUnitDefinition(unitId);
        if (current is null || current.Removed || Same(current.Metadata, metadata))
        {
            return false;
        }

        _engine.Remote.WriteUnitDefinition(current.Revised(_device.ShortId, _clock.UtcNow, metadata: metadata), _device.ShortId);
        RefreshUnits();
        return true;
    });

    private static bool Same(UnitMetadata? a, UnitMetadata b) =>
        a is not null && Json.Serialize(a).AsSpan().SequenceEqual(Json.Serialize(b));

    public Task<IReadOnlyList<DeviceRow>> GetDevicesAsync() => Invoke(() => _history.GetDevices());

    public Task RetireDeviceAsync(string deviceId) => Invoke(() =>
    {
        _history.RetireDevice(deviceId);
        AddActivity("Devices", ActivityKind.Info, $"Retired {DeviceName(deviceId)}");
        return true;
    });

    /// <summary>
    /// Archives versions beyond each game's limit, deletes contents nobody needs any more (after the
    /// grace period) and repairs blobs that went missing. Also runs once a day by itself.
    /// </summary>
    public Task<MaintenanceReport> RunMaintenanceAsync() => Invoke(() => RunMaintenance(_clock.UtcNow));

    internal MaintenanceReport RunMaintenance(DateTime now)
    {
        _nextMaintenance = now + TimeSpan.FromHours(24);
        var archived = 0;
        var repaired = 0;
        foreach (var rt in _units.Values.ToList())
        {
            try
            {
                if (rt.IsBound && !rt.Busy)
                {
                    repaired += _engine.RepairMissingBlobs(rt.Definition, rt.Bindings);
                }

                var count = _history.ApplyRetention(rt.Definition);
                if (count > 0)
                {
                    archived += count;
                    AddActivity(rt.Definition.Name, ActivityKind.Info, $"Archived {count} old version(s); the latest {rt.Definition.EffectiveKeepVersions} stay restorable");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Unit {Unit}: history cleanup failed", rt.UnitId);
            }
        }

        GcReport gc;
        try
        {
            gc = BlobGarbageCollector.Run(_engine.Remote, _device.DeviceId, _device.ShortId, now, log: _log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Blob cleanup failed");
            gc = GcReport.Skipped(ex.Message);
        }

        if (gc.Deleted > 0)
        {
            AddActivity("Repository", ActivityKind.Info, $"Cleanup freed {gc.Deleted} unused file(s), {FormatBytes(gc.DeletedBytes)}");
        }

        return new MaintenanceReport(archived, repaired, gc);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    /// <summary>The candidate versions of a unit's recorded conflict.</summary>
    public Task<IReadOnlyList<VersionManifest>> GetConflictCandidatesAsync(string unitId) => Invoke(() =>
    {
        var conflict = _engine.GetConflict(unitId);
        if (conflict is null)
        {
            return (IReadOnlyList<VersionManifest>)[];
        }

        var (graph, _) = _engine.LoadGraph(unitId);
        return conflict.CandidateVersionIds.Select(graph.Get).OfType<VersionManifest>().ToList();
    });

    /// <summary>True if <paramref name="versionId"/> arrived while the game was open on this PC.</summary>
    public bool ArrivedWhileGameOpen(string unitId, string versionId) =>
        _units.TryGetValue(unitId, out var rt) && rt.SeenWhileRunning.Contains(versionId);

    public UnitDefinition? GetDefinition(string unitId) =>
        _units.TryGetValue(unitId, out var rt) ? rt.Definition : null;

    public string DeviceName(string deviceId) =>
        deviceId == _device.DeviceId ? "This PC" : _deviceNames.GetValueOrDefault(deviceId, deviceId[..Math.Min(8, deviceId.Length)]);

    // ---------------------------------------------------------------- scheduler

    private void Run(CancellationToken token)
    {
        try
        {
            Initialize();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Initialization failed");
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scheduler tick failed");
            }

            _wake.WaitOne(_options.TickInterval);
        }
    }

    /// <summary>Registers this device in the repository and checks for a cloned identity.</summary>
    internal void Initialize()
    {
        var remote = _engine.Remote;
        if (!remote.IsAvailable())
        {
            return;
        }

        var existing = remote.ReadDevices().FirstOrDefault(d => d.DeviceId == _device.DeviceId);
        if (existing?.Fingerprint is not null && _device.Fingerprint is not null && existing.Fingerprint != _device.Fingerprint)
        {
            _fatalError = "Another PC uses the same SaveFileSync identity (was the app data copied?). " +
                          "Delete device.json in the app data folder on one of them and restart.";
            _log.LogError("Cloned device identity detected; sync disabled");
            return;
        }

        WriteDeviceInfo();
        if (_options.UseWatchers)
        {
            _watchers.WatchRepository(remote.Root);
        }

        _repositoryWasAvailable = true;
        RefreshUnits();
    }

    /// <summary>One scheduler iteration: drains commands, polls, and syncs the units that are due.</summary>
    internal void Tick()
    {
        while (_commands.TryDequeue(out var command))
        {
            command();
        }

        var now = _clock.UtcNow;
        var config = Config;

        if (_fatalError is not null)
        {
            Publish(_units.Values.Select(rt => rt.ToStatus(UnitState.Error, _fatalError, this)).ToList());
            return;
        }

        if (!_engine.Remote.IsAvailable())
        {
            _repositoryWasAvailable = false;
            Publish(_units.Values.Select(rt => rt.ToStatus(UnitState.RepositoryUnavailable, "Repository folder not reachable", this)).ToList());
            return;
        }

        if (!_repositoryWasAvailable)
        {
            Initialize();
        }

        // Remote changes: watcher signal, periodic poll, or scheduled checks after resume.
        var resumeDue = _resumeChecks.RemoveAll(t => t <= now) > 0;
        if (_watchers.DrainRemoteSignal() || resumeDue || now - _lastRemotePoll >= TimeSpan.FromSeconds(config.RemotePollSeconds))
        {
            _lastRemotePoll = now;
            RefreshUnits();
            foreach (var rt in _units.Values)
            {
                rt.RemoteDirty = true;
            }

            if (now - _lastDeviceWrite > TimeSpan.FromHours(12))
            {
                WriteDeviceInfo();
            }
        }

        if (now - _lastFullScan >= TimeSpan.FromMinutes(config.FullRescanMinutes))
        {
            _lastFullScan = now;
            foreach (var rt in _units.Values)
            {
                rt.NeedsScan = true;
            }
        }

        foreach (var unitId in _watchers.DrainLocalSignals())
        {
            if (_units.TryGetValue(unitId, out var rt) && now >= rt.IgnoreLocalEventsUntil)
            {
                rt.Dirty = true;
                rt.LastLocalChange = now;
            }
        }

        if (now - _lastProcessPoll >= TimeSpan.FromSeconds(config.ProcessPollSeconds))
        {
            _lastProcessPoll = now;
            _running = _processes.GetRunningProcessNames();
        }

        var globalPause = config.PausedUntilUtc is { } until && until > now;
        foreach (var rt in _units.Values.OrderBy(u => u.Definition.Name, StringComparer.OrdinalIgnoreCase))
        {
            Evaluate(rt, now, config, globalPause);
        }

        Publish(_units.Values.Select(rt => rt.LastStatus(this)).ToList());

        // Once a day, first 10 minutes after start so a fresh start never cleans a half-synced repository.
        if (_nextMaintenance == DateTime.MinValue)
        {
            _nextMaintenance = now + TimeSpan.FromMinutes(10);
        }
        else if (now >= _nextMaintenance && config.HistoryCleanup && !globalPause)
        {
            RunMaintenance(now);
        }
    }

    /// <summary>Marks a unit's local files as changed (used when watchers are disabled, e.g. in tests).</summary>
    internal void NotifyLocalChange(string unitId)
    {
        if (_units.TryGetValue(unitId, out var rt))
        {
            rt.Dirty = true;
            rt.LastLocalChange = _clock.UtcNow;
        }
    }

    private void Evaluate(UnitRuntime rt, DateTime now, AppConfig config, bool globalPause)
    {
        if (!rt.IsBound && config.AutoSetup && _resolver is not null && now >= rt.NextAutoSetup)
        {
            TryAutoSetup(rt, now);
        }

        if (!rt.IsBound)
        {
            rt.Set(UnitState.NotSetUp, "Available — set up on this PC");
            return;
        }

        var running = rt.Definition.ProcessNames.Any(p => _running.Contains(p));
        if (running && !rt.Busy)
        {
            rt.Busy = true;
            rt.BusySince = now;
        }
        else if (!running && rt.Busy)
        {
            rt.Busy = false;
            rt.ProcessExitedAt = now;
            rt.NeedsScan = true;
        }

        if (globalPause || config.PausedUnits.Contains(rt.UnitId))
        {
            rt.Set(UnitState.Paused, globalPause ? "Sync paused" : "Paused for this game");
            return;
        }

        if (rt.NextAttempt > now && !rt.ForceNow)
        {
            return;
        }

        var quiet = TimeSpan.FromSeconds(config.QuietPeriodSeconds);
        bool allowPush, allowApply, due;

        if (rt.Busy)
        {
            allowApply = false;
            allowPush = rt.Dirty && rt.LastLocalChange is { } changed && now - changed >= TimeSpan.FromMinutes(config.InPlayCapMinutes);
            due = allowPush || rt.RemoteDirty || rt.ForceNow;
            if (!due)
            {
                if (rt.State != UnitState.Stale)
                {
                    rt.Set(UnitState.GameRunning, "Game running — sync after it closes");
                }

                return;
            }
        }
        else
        {
            if (rt.ProcessExitedAt is { } exited && now - exited < TimeSpan.FromSeconds(config.ProcessExitDelaySeconds))
            {
                rt.Set(UnitState.Waiting, "Game closed — syncing shortly");
                return;
            }

            var exitTriggered = rt.ProcessExitedAt is not null;
            var quietOk = exitTriggered || rt.ForceNow || rt.LastLocalChange is null || now - rt.LastLocalChange >= quiet;
            if (rt.Dirty && !quietOk)
            {
                rt.Set(UnitState.Waiting, "Waiting for the game to stop writing", rt.LastLocalChange + quiet);
                return;
            }

            allowPush = true;
            allowApply = true;
            due = rt.Dirty || rt.RemoteDirty || rt.NeedsScan || rt.ForceNow || rt.State == UnitState.Pending;
        }

        if (!due)
        {
            return;
        }

        if (rt.State == UnitState.Conflict && !rt.RemoteDirty && !rt.ForceNow)
        {
            return;
        }

        rt.ForceNow = false;
        rt.Set(UnitState.Syncing, "Syncing…");
        Publish(_units.Values.Select(u => u.LastStatus(this)).ToList());

        SyncResult result;
        try
        {
            result = _engine.Sync(rt.Definition, rt.Bindings, new SyncOptions(allowPush, allowApply));
        }
        catch (Exception ex)
        {
            HandleFailure(rt, now, ex);
            return;
        }

        rt.RemoteDirty = false;
        rt.NeedsScan = false;
        HandleResult(rt, now, result);
    }

    /// <summary>
    /// Binds a game that another PC added when its save folder is found here (PLAN.md §7).
    /// The first sync still goes through the normal rules: local data that differs from the
    /// repository becomes a conflict to choose from, never a silent overwrite.
    /// </summary>
    private void TryAutoSetup(UnitRuntime rt, DateTime now)
    {
        rt.NextAutoSetup = now + TimeSpan.FromMinutes(2);
        IReadOnlyDictionary<string, string>? found;
        try
        {
            found = _resolver!.TryResolve(rt.Definition);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Unit {Unit}: automatic setup could not look for the save folder", rt.UnitId);
            return;
        }

        if (found is null)
        {
            return;
        }

        var bindings = new Dictionary<string, string>(found, StringComparer.Ordinal);
        _config.Update(c => c.Bindings[rt.UnitId] = new Dictionary<string, string>(bindings, StringComparer.Ordinal));
        rt.Bindings = bindings;
        rt.IsBound = true;
        rt.ForceNow = true;
        if (_options.UseWatchers)
        {
            _watchers.WatchUnit(rt.UnitId, bindings.Values);
        }

        _log.LogInformation("Unit {Unit}: set up automatically on this PC", rt.UnitId);
        AddActivity(rt.Definition.Name, ActivityKind.Info, "Set up automatically on this PC");
    }

    private void HandleResult(UnitRuntime rt, DateTime now, SyncResult result)
    {
        rt.Failures = 0;
        rt.NextAttempt = DateTime.MinValue;
        rt.LastChangeDevice = BaseDeviceName(rt.UnitId);
        rt.LastSyncUtc = _engine.GetLastSyncUtc(rt.UnitId);
        RecordActivity(rt, result);

        switch (result.Kind)
        {
            case SyncResultKind.UpToDate:
            case SyncResultKind.Pushed:
            case SyncResultKind.Pulled:
                rt.Dirty = false;
                rt.ProcessExitedAt = null;
                rt.ConflictNotified = false;
                rt.AttentionNotified = false;
                if (result.Kind == SyncResultKind.Pulled)
                {
                    rt.IgnoreLocalEventsUntil = now + TimeSpan.FromSeconds(3);
                }

                if (rt.Busy)
                {
                    rt.Set(UnitState.GameRunning, "Game running — sync after it closes");
                }
                else
                {
                    rt.Set(UnitState.Synced, result.Kind switch
                    {
                        SyncResultKind.Pushed => "Uploaded",
                        SyncResultKind.Pulled => $"Updated from {rt.LastChangeDevice}",
                        _ => "Up to date",
                    });
                }

                break;

            case SyncResultKind.RemoteNewer:
                var from = result.RemoteVersion is null ? "another PC" : DeviceName(result.RemoteVersion.DeviceId);
                if (rt.Busy)
                {
                    if (result.RemoteVersion is not null)
                    {
                        rt.SeenWhileRunning.Add(result.RemoteVersion.VersionId);
                    }

                    rt.Set(UnitState.Stale, $"Game running · newer save on {from}");
                    var versionId = result.RemoteVersion?.VersionId ?? "?";
                    if (rt.NotifiedStaleVersion != versionId)
                    {
                        rt.NotifiedStaleVersion = versionId;
                        var when = result.RemoteVersion is null ? "" : $" ({result.RemoteVersion.CreatedUtc.ToLocalTime():g})";
                        Raise(new SyncNotification(
                            $"Newer save for {rt.Definition.Name}",
                            $"A newer save is available from {from}{when}. Close the game to update this PC before playing.",
                            NotificationLevel.Warning,
                            rt.UnitId));
                    }
                }
                else
                {
                    rt.Set(UnitState.Waiting, $"Newer save on {from}");
                }

                break;

            case SyncResultKind.PushDeferred:
                rt.Set(UnitState.GameRunning, "Game running — sync after it closes");
                break;

            case SyncResultKind.Conflict:
                rt.Dirty = false;
                rt.ProcessExitedAt = null;
                rt.Conflict = _engine.GetConflict(rt.UnitId);
                rt.Set(UnitState.Conflict, "Changed on more than one PC — choose which version to keep");
                if (!rt.ConflictNotified)
                {
                    rt.ConflictNotified = true;
                    Raise(new SyncNotification(
                        $"Conflict: {rt.Definition.Name}",
                        "This game's saves changed on more than one PC. Open SaveFileSync to choose which version to keep.",
                        NotificationLevel.Warning,
                        rt.UnitId));
                }

                break;

            case SyncResultKind.Unstable:
                rt.Dirty = true;
                rt.LastLocalChange = now;
                rt.Set(UnitState.Waiting, "Files still being written", now + TimeSpan.FromSeconds(Config.QuietPeriodSeconds));
                break;

            case SyncResultKind.Incomplete:
                rt.RemoteDirty = true;
                rt.NextAttempt = now + TimeSpan.FromSeconds(30);
                rt.Set(UnitState.Waiting, "Waiting for OneDrive to finish downloading");
                break;

            case SyncResultKind.LocalEmptied:
                rt.Set(UnitState.Attention, "Local saves are missing — restore them or confirm the deletion");
                if (!rt.AttentionNotified)
                {
                    rt.AttentionNotified = true;
                    Raise(new SyncNotification(
                        $"Saves missing: {rt.Definition.Name}",
                        "The save folder on this PC is empty. Nothing was deleted elsewhere. Open SaveFileSync to restore or confirm.",
                        NotificationLevel.Warning,
                        rt.UnitId));
                }

                break;

            case SyncResultKind.LocationMissing:
                rt.NextAttempt = now + TimeSpan.FromMinutes(1);
                rt.Set(UnitState.LocationMissing, "Save folder not found on this PC");
                break;

            case SyncResultKind.RepositoryUnavailable:
                rt.NextAttempt = now + TimeSpan.FromMinutes(1);
                rt.Set(UnitState.RepositoryUnavailable, "Repository folder not reachable");
                break;
        }

        if (result.Kind is not SyncResultKind.Conflict)
        {
            rt.Conflict = _engine.GetConflict(rt.UnitId);
        }
    }

    private void RecordActivity(UnitRuntime rt, SyncResult result)
    {
        var name = rt.Definition.Name;
        switch (result.Kind)
        {
            case SyncResultKind.Pushed:
                AddActivity(name, ActivityKind.Uploaded, "Uploaded a new version");
                break;
            case SyncResultKind.Pulled:
                var v = result.RemoteVersion;
                if (v is { Kind: VersionKind.Restore } && v.DeviceId == _device.DeviceId)
                {
                    AddActivity(name, ActivityKind.Resolved, "Restored an earlier version on all PCs");
                }
                else if (v is { Kind: VersionKind.Merge } && v.DeviceId == _device.DeviceId)
                {
                    AddActivity(name, ActivityKind.Merged, "Merged changes made on several PCs");
                }
                else
                {
                    AddActivity(name, ActivityKind.Downloaded, v is null ? "Updated" : $"Updated from {DeviceName(v.DeviceId)}");
                }

                break;
            case SyncResultKind.Conflict when !rt.ConflictNotified:
                AddActivity(name, ActivityKind.Conflict, "Changed on more than one PC: waiting for your choice");
                break;
            case SyncResultKind.RemoteNewer when rt.Busy && result.RemoteVersion is { } r && rt.NotifiedStaleVersion != r.VersionId:
                AddActivity(name, ActivityKind.Warning, $"Newer save from {DeviceName(r.DeviceId)} arrived while the game is open");
                break;
            case SyncResultKind.LocalEmptied when !rt.AttentionNotified:
                AddActivity(name, ActivityKind.Warning, "Save folder is empty on this PC: nothing was propagated");
                break;
        }
    }

    private void HandleFailure(UnitRuntime rt, DateTime now, Exception ex)
    {
        AddActivity(rt.Definition.Name, ActivityKind.Error, ex.Message);
        rt.Failures++;
        rt.NextAttempt = now + Backoff[Math.Min(rt.Failures - 1, Backoff.Length - 1)];
        var message = ex switch
        {
            UnauthorizedAccessException => "Access denied. If the folder is in Documents, Windows Security " +
                                           "'Controlled folder access' may be blocking SaveFileSync.",
            IOException io when io.HResult == unchecked((int)0x80070070) => "Disk full.",
            _ => ex.Message,
        };
        rt.Set(UnitState.Error, message);
        _log.LogError(ex, "Unit {Unit}: sync failed (attempt {N})", rt.UnitId, rt.Failures);
        if (rt.Failures == 3)
        {
            Raise(new SyncNotification($"Sync problem: {rt.Definition.Name}", message, NotificationLevel.Error, rt.UnitId));
        }
    }

    private Task<SyncResult> RunUnitCommand(string unitId, Func<UnitDefinition, IReadOnlyDictionary<string, string>, SyncResult> action) =>
        Invoke(() =>
        {
            if (!_units.TryGetValue(unitId, out var rt) || !rt.IsBound)
            {
                return SyncResult.Of(SyncResultKind.LocationMissing, "This game is not set up on this PC.");
            }

            if (rt.Busy)
            {
                return SyncResult.Of(SyncResultKind.PushDeferred, "Close the game first.");
            }

            var now = _clock.UtcNow;
            try
            {
                var result = action(rt.Definition, rt.Bindings);
                HandleResult(rt, now, result);
                if (result.Kind is SyncResultKind.Pulled or SyncResultKind.Pushed)
                {
                    rt.IgnoreLocalEventsUntil = now + TimeSpan.FromSeconds(3);
                }

                return result;
            }
            catch (Exception ex)
            {
                HandleFailure(rt, now, ex);
                throw;
            }
            finally
            {
                Publish(_units.Values.Select(u => u.LastStatus(this)).ToList());
            }
        });

    private void RefreshUnits()
    {
        var remote = _engine.Remote;
        var config = Config;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        _deviceNames = remote.ReadDevices().GroupBy(d => d.DeviceId).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        foreach (var unitId in remote.ListUnitIds())
        {
            var definition = remote.ReadUnitDefinition(unitId);
            if (definition is null || definition.Removed)
            {
                continue;
            }

            seen.Add(unitId);
            var bindings = config.Bindings.GetValueOrDefault(unitId) ?? new Dictionary<string, string>();
            if (!_units.TryGetValue(unitId, out var rt))
            {
                rt = new UnitRuntime(definition) { Conflict = _engine.GetConflict(unitId) };
                rt.LastSyncUtc = _engine.GetLastSyncUtc(unitId);
                rt.LastChangeDevice = BaseDeviceName(unitId);
                _units[unitId] = rt;
            }

            var bindingsChanged = !rt.Bindings.OrderBy(k => k.Key).SequenceEqual(bindings.OrderBy(k => k.Key));
            rt.Definition = definition;
            rt.Bindings = new Dictionary<string, string>(bindings, StringComparer.Ordinal);
            rt.IsBound = definition.Locations.Count > 0 && definition.Locations.All(l => bindings.ContainsKey(l.Id));
            if (bindingsChanged && _options.UseWatchers)
            {
                if (rt.IsBound)
                {
                    _watchers.WatchUnit(unitId, rt.Bindings.Values);
                }
                else
                {
                    _watchers.UnwatchUnit(unitId);
                }
            }
        }

        foreach (var gone in _units.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _units.Remove(gone);
            _watchers.UnwatchUnit(gone);
        }
    }

    private string? BaseDeviceName(string unitId)
    {
        var baseId = _engine.GetBaseVersionId(unitId);
        if (baseId is null)
        {
            return null;
        }

        var shortId = baseId.Split('-')[0];
        if (shortId == _device.ShortId)
        {
            return "This PC";
        }

        var deviceId = _deviceNames.Keys.FirstOrDefault(k => k.StartsWith(shortId, StringComparison.Ordinal));
        return deviceId is null ? shortId : _deviceNames[deviceId];
    }

    private void WriteDeviceInfo()
    {
        _lastDeviceWrite = _clock.UtcNow;
        _engine.Remote.WriteDevice(
            new DeviceInfo
            {
                DeviceId = _device.DeviceId,
                ShortId = _device.ShortId,
                Name = Config.DeviceName ?? Environment.MachineName,
                AppVersion = _options.AppVersion,
                Fingerprint = _device.Fingerprint,
                LastSeenUtc = _clock.UtcNow,
            },
            _device.ShortId);
    }

    private void Publish(IReadOnlyList<UnitStatus> status)
    {
        var previous = Volatile.Read(ref _status);
        if (previous.Count == status.Count && previous.SequenceEqual(status))
        {
            return;
        }

        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(status);
    }

    private void Raise(SyncNotification notification)
    {
        _log.LogInformation("Notification: {Title} — {Message}", notification.Title, notification.Message);
        Notification?.Invoke(notification);
    }

    private void Post(Action action)
    {
        _commands.Enqueue(action);
        _wake.Set();
    }

    private Task<T> Invoke<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private static string NewUnitId(string name)
    {
        var slug = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }

            if (slug.Length >= 24)
            {
                break;
            }
        }

        var prefix = slug.ToString().Trim('-');
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var suffix = new string(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
        return string.IsNullOrEmpty(prefix) ? $"unit-{suffix}" : $"{prefix}-{suffix}";
    }

    public static string NormalizeProcessName(string name)
    {
        var n = Path.GetFileName(name.Trim());
        return (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n).ToLowerInvariant();
    }

    private sealed class UnitRuntime(UnitDefinition definition)
    {
        private UnitStatus? _last;

        public UnitDefinition Definition { get; set; } = definition;

        public string UnitId => Definition.UnitId;

        public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.Ordinal);

        public bool IsBound { get; set; }

        public UnitState State { get; private set; } = UnitState.Pending;

        public string Detail { get; private set; } = "Not synced yet";

        public DateTime? QuietUntil { get; private set; }

        public DateTime? LastSyncUtc { get; set; }

        public string? LastChangeDevice { get; set; }

        public ConflictInfo? Conflict { get; set; }

        public bool Dirty { get; set; }

        public bool RemoteDirty { get; set; }

        public bool NeedsScan { get; set; }

        public bool ForceNow { get; set; }

        public DateTime? LastLocalChange { get; set; }

        public bool Busy { get; set; }

        public DateTime? BusySince { get; set; }

        public DateTime? ProcessExitedAt { get; set; }

        public DateTime IgnoreLocalEventsUntil { get; set; } = DateTime.MinValue;

        public DateTime NextAttempt { get; set; } = DateTime.MinValue;

        public int Failures { get; set; }

        public string? NotifiedStaleVersion { get; set; }

        public bool ConflictNotified { get; set; }

        public bool AttentionNotified { get; set; }

        public HashSet<string> SeenWhileRunning { get; } = new(StringComparer.Ordinal);

        public void Set(UnitState state, string detail, DateTime? quietUntil = null)
        {
            State = state;
            Detail = detail;
            QuietUntil = quietUntil;
        }

        public DateTime NextAutoSetup { get; set; } = DateTime.MinValue;

        public UnitStatus ToStatus(UnitState state, string detail, SyncService owner) =>
            new(UnitId, Definition.Name, state, detail, LastSyncUtc, LastChangeDevice, null, Busy, IsBound, Conflict, HasRegistryData);

        private bool HasRegistryData => Definition.Source?.RegistryKeys > 0;

        public UnitStatus LastStatus(SyncService owner)
        {
            var status = new UnitStatus(UnitId, Definition.Name, State, Detail, LastSyncUtc, LastChangeDevice, QuietUntil, Busy, IsBound, Conflict, HasRegistryData);
            if (_last is not null && _last == status)
            {
                return _last;
            }

            _last = status;
            return status;
        }
    }
}
