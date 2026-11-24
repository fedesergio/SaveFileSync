using SaveFileSync.Core.Model;
using SaveFileSync.Core.Sync;

namespace SaveFileSync.Simulation.Tests;

/// <summary>Phase 5: Steam units are backed up read-only. PCs never pull, merge or conflict (PLAN.md §11).</summary>
public sealed class BackupOnlyTests : IDisposable
{
    private readonly Harness _h = new();
    private readonly TestPc _a;
    private readonly TestPc _b;
    private readonly UnitDefinition _unit = new()
    {
        UnitId = "steam-hades-abc123",
        Name = "Hades",
        Locations = [new LocationDefinition { Id = "steam-cloud" }],
        Mode = UnitMode.BackupOnly,
        Source = new UnitSource { Kind = UnitSource.SteamKind, SteamId = 1145360 },
    };

    public BackupOnlyTests()
    {
        _a = _h.CreatePc("desktop");
        _b = _h.CreatePc("laptop");
    }

    public void Dispose() => _h.Dispose();

    private IReadOnlyDictionary<string, string> Bind(TestPc pc) => new Dictionary<string, string> { ["steam-cloud"] = pc.SavesDir };

    private SyncResult Sync(TestPc pc) => pc.Engine.Sync(_unit, Bind(pc), SyncOptions.Full);

    private (VersionGraph Graph, int Incomplete) Graph(TestPc pc) => pc.Engine.LoadGraph(_unit.UnitId);

    [Fact]
    public void EachPcBacksUpItsOwnSaves_NoPull_NoConflict_NoMerge()
    {
        _a.Write("p.sav", "from desktop");
        _b.Write("p.sav", "from laptop");

        Assert.Equal(SyncResultKind.Pushed, Sync(_a).Kind);
        Assert.Equal(SyncResultKind.Pushed, Sync(_b).Kind);
        // And again, after both have seen each other's versions.
        Assert.Equal(SyncResultKind.UpToDate, Sync(_a).Kind);
        Assert.Equal(SyncResultKind.UpToDate, Sync(_b).Kind);

        Assert.Equal("from desktop", _a.Read("p.sav"));
        Assert.Equal("from laptop", _b.Read("p.sav"));
        Assert.Equal(2, Graph(_a).Graph.Tips.Count);
        Assert.Null(_a.Engine.GetConflict(_unit.UnitId));
        Assert.Null(_b.Engine.GetConflict(_unit.UnitId));
    }

    [Fact]
    public void ANewerBackupFromAnotherPc_NeverChangesLocalFiles()
    {
        _a.Write("p.sav", "v1");
        Sync(_a);
        _b.Write("p.sav", "local on laptop");
        Sync(_b);

        _a.Write("p.sav", "v2");
        Sync(_a);

        Assert.Equal(SyncResultKind.UpToDate, Sync(_b).Kind);
        Assert.Equal("local on laptop", _b.Read("p.sav"));
    }

    [Fact]
    public void ChangesMakeAChain_OnThisPc()
    {
        _a.Write("p.sav", "v1");
        Sync(_a);
        _a.Write("p.sav", "v2");
        Sync(_a);
        _a.Write("p.sav", "v3");
        Sync(_a);

        var (graph, _) = Graph(_a);
        Assert.Equal(3, graph.Versions.Count);
        Assert.Single(graph.Tips);
        Assert.Equal(VersionKind.Initial, graph.Versions.Single(v => v.Parents.Count == 0).Kind);
    }

    [Fact]
    public void IdenticalContentOnAnotherPc_IsNotStoredTwice()
    {
        _a.Write("p.sav", "same");
        Sync(_a);
        _b.Write("p.sav", "same");

        Assert.Equal(SyncResultKind.UpToDate, Sync(_b).Kind);

        Assert.Single(Graph(_b).Graph.Versions);
    }

    [Fact]
    public void AnEmptiedFolder_IsNeverBackedUpAsEmpty()
    {
        _a.Write("p.sav", "v1");
        Sync(_a);
        File.Delete(Path.Combine(_a.SavesDir, "p.sav"));

        Assert.Equal(SyncResultKind.UpToDate, Sync(_a).Kind);

        var (graph, _) = Graph(_a);
        Assert.Single(graph.Versions);
        Assert.False(graph.Tips[0].Content.IsEmpty);
    }

    [Fact]
    public void ALocationThatIsMissing_IsReported_NotBackedUp()
    {
        var missing = new Dictionary<string, string> { ["steam-cloud"] = Path.Combine(_a.SavesDir, "does-not-exist") };
        Assert.Equal(SyncResultKind.LocationMissing, _a.Engine.Sync(_unit, missing, SyncOptions.Full).Kind);
    }

    [Fact]
    public void PushIsDeferred_WhileTheGameRuns()
    {
        _a.Write("p.sav", "v1");
        Assert.Equal(SyncResultKind.PushDeferred, _a.Engine.Sync(_unit, Bind(_a), new SyncOptions(AllowPush: false, AllowApply: false)).Kind);
        Assert.True(Graph(_a).Graph.IsEmpty);
    }

    [Fact]
    public void Restore_ContinuesThisPcsOwnChain_AndLeavesOtherPcsAlone()
    {
        _a.Write("p.sav", "v1");
        Sync(_a);
        _a.Write("p.sav", "v2");
        Sync(_a);
        _b.Write("p.sav", "other pc");
        Sync(_b);
        var v1 = Graph(_a).Graph.Versions.Single(v => v.Parents.Count == 0 && v.DeviceId == _a.Device.DeviceId);

        var result = _a.Engine.RestoreVersion(_unit, Bind(_a), v1.VersionId);

        Assert.Equal(SyncResultKind.Pulled, result.Kind);
        Assert.Equal("v1", _a.Read("p.sav"));
        var (graph, _) = Graph(_a);
        Assert.Equal(2, graph.Tips.Count); // desktop's restore and laptop's own backup: still two chains
        var restore = graph.Versions.Single(v => v.Kind == VersionKind.Restore);
        Assert.Single(restore.Parents);
        Assert.Equal(_a.Device.DeviceId, graph.Get(restore.Parents[0])!.DeviceId);
        Assert.Equal("other pc", _b.Read("p.sav"));
        Assert.Equal(SyncResultKind.UpToDate, Sync(_a).Kind);
    }

    [Fact]
    public void ServiceRestore_IsRefusedWhileSteamRuns()
    {
        var unitId = _a.Service.AddUnitAsync("Hades", _unit.Locations, [], Bind(_a), mode: UnitMode.BackupOnly, source: _unit.Source).Await(_a);
        _a.Service.Tick();
        _a.Write("p.sav", "v1");
        _a.Service.NotifyLocalChange(unitId);
        _a.Clock.Advance(TimeSpan.FromMinutes(3));
        _a.Service.Tick();
        _a.Write("p.sav", "v2");
        _a.Service.NotifyLocalChange(unitId);
        _a.Clock.Advance(TimeSpan.FromMinutes(3));
        _a.Service.Tick();
        var history = _a.Service.GetHistoryAsync(unitId).Await(_a);
        Assert.Equal(2, history.Count);
        Assert.True(_a.Status(unitId).IsBackupOnly);

        _a.Processes.Running.Add("steam");
        var refused = _a.Service.RestoreVersionAsync(unitId, history.Last().VersionId).Await(_a);
        Assert.Equal(SyncResultKind.Refused, refused.Kind);
        Assert.Contains("Steam", refused.Message);
        Assert.Equal("v2", _a.Read("p.sav"));

        _a.Processes.Running.Clear();
        _a.Clock.Advance(TimeSpan.FromSeconds(30));
        _a.Service.Tick();
        var done = _a.Service.RestoreVersionAsync(unitId, history.Last().VersionId).Await(_a);
        Assert.Equal(SyncResultKind.Pulled, done.Kind);
        Assert.Equal("v1", _a.Read("p.sav"));
    }
}
