using SaveFileSync.Core;
using SaveFileSync.Core.Config;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Paths;
using SaveFileSync.Core.Service;
using SaveFileSync.Core.State;
using SaveFileSync.Core.Sync;
using SaveFileSync.Repository;

namespace SaveFileSync.Simulation.Tests;

public sealed class FakeClock(DateTime start) : IClock
{
    public DateTime UtcNow { get; private set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FakeProcesses : IProcessMonitor
{
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> GetRunningProcessNames() => new HashSet<string>(Running, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Several simulated PCs on one machine (PLAN.md §10.3): one temporary "OneDrive"
/// repository folder, and per PC its own state, device id, save folder, clock and processes.
/// </summary>
public sealed class Harness : IDisposable
{
    private readonly List<TestPc> _pcs = [];

    public Harness()
    {
        Root = Path.Combine(Path.GetTempPath(), "sfs-sim-" + Guid.NewGuid().ToString("N")[..8]);
        RepoDir = Path.Combine(Root, "repo");
        new FolderRemoteStore(RepoDir).Initialize(DateTime.UtcNow);
    }

    public string Root { get; }

    public string RepoDir { get; }

    public static UnitDefinition Unit(string unitId = "game-abc123", MergeGranularity granularity = MergeGranularity.Unit, params string[] processNames) => new()
    {
        UnitId = unitId,
        Name = "Test Game",
        Locations = [new LocationDefinition { Id = "saves" }],
        ProcessNames = processNames,
        Granularity = granularity,
    };

    /// <param name="clockOffset">Simulates a PC whose clock is wrong by this amount.</param>
    /// <param name="network">When set, this PC sees other PCs' writes only once the test delivers them.</param>
    /// <param name="identity">Reuse another PC's identity (to simulate a cloned app data folder).</param>
    public TestPc CreatePc(string name, TimeSpan clockOffset = default, LagNetwork? network = null, DeviceIdentity? identity = null)
    {
        var pc = new TestPc(this, name, new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc) + clockOffset, network, identity);
        _pcs.Add(pc);
        return pc;
    }

    public void Dispose()
    {
        foreach (var pc in _pcs)
        {
            pc.Dispose();
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class TestPc : IDisposable
{
    private SyncService? _service;

    internal TestPc(Harness harness, string name, DateTime start, LagNetwork? network, DeviceIdentity? identity)
    {
        Name = name;
        var dir = Path.Combine(harness.Root, name);
        SavesDir = Path.Combine(dir, "saves");
        Directory.CreateDirectory(SavesDir);
        Clock = new FakeClock(start);
        Device = identity is null
            ? DeviceIdentity.Create("fp-" + name)
            : new DeviceIdentity { DeviceId = identity.DeviceId, ShortId = identity.ShortId, Fingerprint = "fp-" + name };
        IRemoteStore folder = new FolderRemoteStore(harness.RepoDir);
        Remote = network is null ? folder : new LaggyRemoteStore(folder, network, name);
        State = new SqliteLocalState(Path.Combine(dir, "state.db"));
        Config = new ConfigStore(Path.Combine(dir, "config.json"));
        Config.Update(c =>
        {
            c.RepositoryPath = harness.RepoDir;
            c.DeviceName = name;
        });
        Engine = new SyncEngine(Remote, State, Device, Clock);
    }

    public string Name { get; }

    public string SavesDir { get; }

    public FakeClock Clock { get; }

    public FakeProcesses Processes { get; } = new();

    public IRemoteStore Remote { get; }

    public SqliteLocalState State { get; }

    public DeviceIdentity Device { get; }

    public ConfigStore Config { get; }

    public SyncEngine Engine { get; }

    public List<SyncNotification> Notifications { get; } = [];

    public IReadOnlyDictionary<string, string> Bindings => new Dictionary<string, string> { ["saves"] = SavesDir };

    private SaveFileSync.Core.History.HistoryService? _history;

    public SaveFileSync.Core.History.HistoryService History =>
        _history ??= new SaveFileSync.Core.History.HistoryService(Remote, Engine, Device, Clock);

    /// <summary>Registers this PC in the repository, as the service does at startup.</summary>
    public void Register(DateTime? lastSeen = null) =>
        Remote.WriteDevice(
            new DeviceInfo
            {
                DeviceId = Device.DeviceId,
                ShortId = Device.ShortId,
                Name = Name,
                Fingerprint = Device.Fingerprint,
                LastSeenUtc = lastSeen ?? Clock.UtcNow,
            },
            Device.ShortId);

    /// <summary>Writes a file and syncs: one new version per call.</summary>
    public SyncResult Save(UnitDefinition unit, string relativePath, string content)
    {
        Write(relativePath, content);
        return Sync(unit);
    }

    /// <summary>Set before the service is first used, to simulate automatic setup on this PC.</summary>
    public ILocationResolver? Resolver { get; set; }

    public SyncService Service
    {
        get
        {
            if (_service is null)
            {
                _service = new SyncService(Engine, Config, Device, Processes, Clock, options: new SyncServiceOptions { UseWatchers = false }, resolver: Resolver);
                _service.Notification += n => Notifications.Add(n);
            }

            return _service;
        }
    }

    public SyncResult Sync(UnitDefinition unit) => Engine.Sync(unit, Bindings, SyncOptions.Full);

    public void Write(string relativePath, string content)
    {
        var path = Path.Combine(SavesDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        // Make sure (size, mtime) differs from the cached index even within the same tick.
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(Random.Shared.Next(1, 1000)));
    }

    public string? Read(string relativePath)
    {
        var path = Path.Combine(SavesDir, relativePath);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public UnitStatus Status(string unitId) => Service.CurrentStatus.Single(s => s.UnitId == unitId);

    public void Dispose()
    {
        _service?.Dispose();
        State.Dispose();
    }
}
