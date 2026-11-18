using SaveFileSync.Core.Recovery;
using SaveFileSync.Core.Sync;

namespace SaveFileSync.Simulation.Tests;

/// <summary>Minimal two-device integration tests on the engine (phase 1a).</summary>
public sealed class TwoPcEngineTests : IDisposable
{
    private readonly Harness _h = new();
    private readonly TestPc _a;
    private readonly TestPc _b;
    private readonly Core.Model.UnitDefinition _unit = Harness.Unit();

    public TwoPcEngineTests()
    {
        // Clocks hours apart: ordering must not depend on them.
        _a = _h.CreatePc("desktop", TimeSpan.FromHours(3));
        _b = _h.CreatePc("ally");
        _a.Remote.WriteUnitDefinition(_unit, _a.Device.ShortId);
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public void ChangeOnOnePc_ReachesTheOther_BothWays()
    {
        _a.Write("slot1.sav", "a1");
        Assert.Equal(SyncResultKind.Pushed, _a.Sync(_unit).Kind);

        Assert.Equal(SyncResultKind.Pulled, _b.Sync(_unit).Kind);
        Assert.Equal("a1", _b.Read("slot1.sav"));

        _b.Write("slot1.sav", "b1");
        _b.Write("sub/slot2.sav", "b2");
        Assert.Equal(SyncResultKind.Pushed, _b.Sync(_unit).Kind);

        Assert.Equal(SyncResultKind.Pulled, _a.Sync(_unit).Kind);
        Assert.Equal("b1", _a.Read("slot1.sav"));
        Assert.Equal("b2", _a.Read("sub/slot2.sav"));

        Assert.Equal(SyncResultKind.UpToDate, _a.Sync(_unit).Kind);
        Assert.Equal(SyncResultKind.UpToDate, _b.Sync(_unit).Kind);
    }

    [Fact]
    public void Deletion_PropagatesWhenOtherFilesRemain()
    {
        _a.Write("slot1.sav", "a1");
        _a.Write("slot2.sav", "a2");
        _a.Sync(_unit);
        _b.Sync(_unit);

        File.Delete(Path.Combine(_a.SavesDir, "slot2.sav"));
        Assert.Equal(SyncResultKind.Pushed, _a.Sync(_unit).Kind);
        Assert.Equal(SyncResultKind.Pulled, _b.Sync(_unit).Kind);
        Assert.Null(_b.Read("slot2.sav"));
        Assert.Equal("a1", _b.Read("slot1.sav"));
    }

    [Fact]
    public void ConcurrentEdits_GiveConflict_AndResolutionConverges()
    {
        _a.Write("slot1.sav", "base");
        _a.Sync(_unit);
        _b.Sync(_unit);

        _a.Write("slot1.sav", "from-desktop");
        _a.Sync(_unit);
        _b.Write("slot1.sav", "from-ally");

        Assert.Equal(SyncResultKind.Conflict, _b.Sync(_unit).Kind);
        var conflict = _b.Engine.GetConflict(_unit.UnitId)!;
        Assert.Equal(2, conflict.CandidateVersionIds.Count);

        // Both versions are preserved in the repository.
        var (graph, _) = _b.Engine.LoadGraph(_unit.UnitId);
        var contents = conflict.CandidateVersionIds.Select(id => graph.Get(id)!.Files.Single().Sha256).ToHashSet();
        Assert.Equal(2, contents.Count);

        var desktopVersion = conflict.CandidateVersionIds.Single(id => id.StartsWith(_a.Device.ShortId, StringComparison.Ordinal));
        Assert.Equal(SyncResultKind.Pushed, _b.Engine.Resolve(_unit, _b.Bindings, desktopVersion).Kind);
        Assert.Equal("from-desktop", _b.Read("slot1.sav"));
        Assert.Null(_b.Engine.GetConflict(_unit.UnitId));

        Assert.Equal(SyncResultKind.UpToDate, _a.Sync(_unit).Kind);
        Assert.Equal(SyncResultKind.UpToDate, _b.Sync(_unit).Kind);

        _a.Write("slot1.sav", "next");
        _a.Sync(_unit);
        Assert.Equal(SyncResultKind.Pulled, _b.Sync(_unit).Kind);
        Assert.Equal("next", _b.Read("slot1.sav"));
    }

    [Fact]
    public void FirstSetupWithDifferentLocalData_IsConflict_KeepLocal()
    {
        _a.Write("slot1.sav", "desktop-data");
        _a.Sync(_unit);
        _b.Write("slot1.sav", "ally-old-data");

        Assert.Equal(SyncResultKind.Conflict, _b.Sync(_unit).Kind);
        var conflict = _b.Engine.GetConflict(_unit.UnitId)!;
        Assert.Equal(Core.Sync.ConflictReason.BothChanged, conflict.Reason);
        var allyVersion = conflict.CandidateVersionIds.Single(id => id.StartsWith(_b.Device.ShortId, StringComparison.Ordinal));

        _b.Engine.Resolve(_unit, _b.Bindings, allyVersion);
        Assert.Equal("ally-old-data", _b.Read("slot1.sav"));

        Assert.Equal(SyncResultKind.Pulled, _a.Sync(_unit).Kind);
        Assert.Equal("ally-old-data", _a.Read("slot1.sav"));
    }

    [Fact]
    public void EmptiedSaveFolder_IsNotPropagated_AndCanBeRestored()
    {
        _a.Write("slot1.sav", "a1");
        _a.Sync(_unit);
        _b.Sync(_unit);

        File.Delete(Path.Combine(_a.SavesDir, "slot1.sav"));
        Assert.Equal(SyncResultKind.LocalEmptied, _a.Sync(_unit).Kind);
        Assert.Equal(SyncResultKind.UpToDate, _b.Sync(_unit).Kind);
        Assert.Equal("a1", _b.Read("slot1.sav"));

        Assert.Equal(SyncResultKind.Pulled, _a.Engine.RestoreBase(_unit, _a.Bindings).Kind);
        Assert.Equal("a1", _a.Read("slot1.sav"));
    }

    [Fact]
    public void MissingSaveFolder_IsReported_NotTreatedAsDeletion()
    {
        _a.Write("slot1.sav", "a1");
        _a.Sync(_unit);
        Directory.Delete(_a.SavesDir, recursive: true);

        Assert.Equal(SyncResultKind.LocationMissing, _a.Sync(_unit).Kind);
    }

    [Fact]
    public void InterruptedApply_IsCompletedOnNextSync()
    {
        _a.Write("slot1.sav", "v1-1");
        _a.Write("slot2.sav", "v1-2");
        _a.Sync(_unit);
        _b.Sync(_unit);

        _a.Write("slot1.sav", "v2-1");
        _a.Write("slot2.sav", "v2-2");
        _a.Sync(_unit);
        var (graph, _) = _b.Engine.LoadGraph(_unit.UnitId);
        var target = graph.Tips.Single().VersionId;

        // Simulate a crash half-way through the commit on the ally.
        File.WriteAllText(Path.Combine(_b.SavesDir, "slot1.sav"), "v2-1");
        File.WriteAllText(Path.Combine(_b.SavesDir, "slot2.sav.sfs-tmp"), "partial");
        _b.State.SetJournal(_unit.UnitId, target, DateTime.UtcNow);

        _b.Sync(_unit);

        Assert.Equal("v2-1", _b.Read("slot1.sav"));
        Assert.Equal("v2-2", _b.Read("slot2.sav"));
        Assert.False(File.Exists(Path.Combine(_b.SavesDir, "slot2.sav.sfs-tmp")));
        Assert.Null(_b.State.GetJournal(_unit.UnitId));
        Assert.Equal(target, _b.Engine.GetBaseVersionId(_unit.UnitId));
    }

    [Fact]
    public void IncompleteVersion_IsIgnoredUntilBlobsArrive()
    {
        _a.Write("slot1.sav", "a1");
        _a.Sync(_unit);
        _b.Sync(_unit);
        _a.Write("slot1.sav", "a2");
        _a.Sync(_unit);

        // Simulate OneDrive delivering the manifest before the blob.
        var blobs = Directory.GetFiles(Path.Combine(_h.RepoDir, "blobs"), "*", SearchOption.AllDirectories);
        var newest = blobs.OrderByDescending(File.GetCreationTimeUtc).First();
        var hidden = newest + ".held";
        File.Move(newest, hidden);

        Assert.Equal(SyncResultKind.Incomplete, _b.Sync(_unit).Kind);
        Assert.Equal("a1", _b.Read("slot1.sav"));

        File.Move(hidden, newest);
        Assert.Equal(SyncResultKind.Pulled, _b.Sync(_unit).Kind);
        Assert.Equal("a2", _b.Read("slot1.sav"));
    }

    [Fact]
    public void Export_RebuildsLatestVersion_WithoutLocalState()
    {
        _a.Write("slot1.sav", "a1");
        _a.Write("deep/dir/slot2.sav", "a2");
        _a.Sync(_unit);

        var target = Path.Combine(_h.Root, "export");
        var recovery = new RecoveryService(new Repository.FolderRemoteStore(_h.RepoDir));
        Assert.Equal(_unit.UnitId, recovery.FindUnit("test game"));
        var report = recovery.Export(_unit.UnitId, target);

        Assert.Empty(report.Problems);
        Assert.Equal(2, report.FilesWritten);
        Assert.Equal("a1", File.ReadAllText(Path.Combine(target, "saves", "slot1.sav")));
        Assert.Equal("a2", File.ReadAllText(Path.Combine(target, "saves", "deep", "dir", "slot2.sav")));
    }
}
