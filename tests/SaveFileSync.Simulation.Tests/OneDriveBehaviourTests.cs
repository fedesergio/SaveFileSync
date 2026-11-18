using SaveFileSync.Core.Config;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Sync;
using SaveFileSync.Repository;

namespace SaveFileSync.Simulation.Tests;

/// <summary>
/// Full simulation scenarios of PLAN.md §10.3 (phase 1b): OneDrive lag, out-of-order
/// delivery, placeholders, conflict copies, folder-level merge, three PCs, crashes.
/// </summary>
public sealed class OneDriveBehaviourTests : IDisposable
{
    private readonly Harness _h = new();
    private readonly LagNetwork _net = new();

    public void Dispose() => _h.Dispose();

    private (TestPc A, TestPc B, UnitDefinition Unit) TwoPcs(MergeGranularity granularity = MergeGranularity.Unit)
    {
        var a = _h.CreatePc("desktop", network: _net);
        var b = _h.CreatePc("ally", TimeSpan.FromHours(-7), _net);
        var unit = Harness.Unit(granularity: granularity);
        a.Remote.WriteUnitDefinition(unit, a.Device.ShortId);
        return (a, b, unit);
    }

    [Fact]
    public void ManifestArrivingBeforeBlobs_IsIgnoredUntilComplete()
    {
        var (a, b, unit) = TwoPcs();
        a.Write("slot1.sav", "a1");
        a.Sync(unit);

        _net.DeliverVersionsOnly();
        Assert.Equal(SyncResultKind.Incomplete, b.Sync(unit).Kind);
        Assert.Null(b.Read("slot1.sav"));

        _net.Deliver();
        Assert.Equal(SyncResultKind.Pulled, b.Sync(unit).Kind);
        Assert.Equal("a1", b.Read("slot1.sav"));
    }

    [Fact]
    public void CloudOnlyBlobThatCannotBeRead_IsRetried()
    {
        var (a, b, unit) = TwoPcs();
        a.Write("slot1.sav", "a1");
        a.Sync(unit);
        _net.Deliver();

        var sha = a.Engine.LoadGraph(unit.UnitId).Graph.Tips.Single().Files.Single().Sha256;
        _net.UnreadableBlobs.Add(sha);
        Assert.Equal(SyncResultKind.Incomplete, b.Sync(unit).Kind);
        Assert.Empty(Directory.GetFiles(b.SavesDir, "*.sfs-tmp", SearchOption.AllDirectories));

        _net.UnreadableBlobs.Clear();
        Assert.Equal(SyncResultKind.Pulled, b.Sync(unit).Kind);
        Assert.Equal("a1", b.Read("slot1.sav"));
    }

    [Fact]
    public void EditsMadeWhileOffline_BecomeConflictOnDelivery_AndConverge()
    {
        var (a, b, unit) = TwoPcs();
        a.Write("slot1.sav", "base");
        a.Sync(unit);
        _net.Deliver();
        b.Sync(unit);

        // Both play before OneDrive delivers anything: each push succeeds locally.
        a.Write("slot1.sav", "desktop");
        b.Write("slot1.sav", "ally");
        Assert.Equal(SyncResultKind.Pushed, a.Sync(unit).Kind);
        Assert.Equal(SyncResultKind.Pushed, b.Sync(unit).Kind);

        _net.Deliver();
        Assert.Equal(SyncResultKind.Conflict, a.Sync(unit).Kind);
        Assert.Equal(SyncResultKind.Conflict, b.Sync(unit).Kind);

        var allyVersion = a.Engine.GetConflict(unit.UnitId)!.CandidateVersionIds.Single(id => id.StartsWith(b.Device.ShortId, StringComparison.Ordinal));
        a.Engine.Resolve(unit, a.Bindings, allyVersion);
        Assert.Equal("ally", a.Read("slot1.sav"));

        _net.Deliver();
        Assert.Equal(SyncResultKind.UpToDate, b.Sync(unit).Kind);
        Assert.Null(b.Engine.GetConflict(unit.UnitId));
        Assert.Equal(SyncResultKind.UpToDate, a.Sync(unit).Kind);
    }

    [Fact]
    public void SameConflictResolvedDifferentlyOnTwoPcs_AsksAgain()
    {
        var (a, b, unit) = TwoPcs();
        a.Write("slot1.sav", "base");
        a.Sync(unit);
        _net.Deliver();
        b.Sync(unit);
        a.Write("slot1.sav", "desktop");
        b.Write("slot1.sav", "ally");
        a.Sync(unit);
        b.Sync(unit);
        _net.Deliver();
        a.Sync(unit);
        b.Sync(unit);

        var conflict = a.Engine.GetConflict(unit.UnitId)!;
        var desktopV = conflict.CandidateVersionIds.Single(id => id.StartsWith(a.Device.ShortId, StringComparison.Ordinal));
        var allyV = conflict.CandidateVersionIds.Single(id => id.StartsWith(b.Device.ShortId, StringComparison.Ordinal));
        a.Engine.Resolve(unit, a.Bindings, desktopV);
        b.Engine.Resolve(unit, b.Bindings, allyV);

        _net.Deliver();
        Assert.Equal(SyncResultKind.Conflict, a.Sync(unit).Kind);
        Assert.Equal(SyncResultKind.Conflict, b.Sync(unit).Kind);
        Assert.Equal("desktop", a.Read("slot1.sav"));
        Assert.Equal("ally", b.Read("slot1.sav"));
    }

    [Fact]
    public void DisjointSaveFolders_MergeAutomatically_Rpcs3Like()
    {
        var (a, b, unit) = TwoPcs(MergeGranularity.Folder);
        a.Write("BLUS00001-SAVE/SAVEDATA.BIN", "game1-v1");
        a.Write("BLUS00002-SAVE/SAVEDATA.BIN", "game2-v1");
        a.Sync(unit);
        _net.Deliver();
        b.Sync(unit);

        a.Write("BLUS00001-SAVE/SAVEDATA.BIN", "game1-desktop");
        b.Write("BLUS00002-SAVE/SAVEDATA.BIN", "game2-ally");
        b.Write("BLUS00003-SAVE/SAVEDATA.BIN", "game3-ally");
        a.Sync(unit);
        b.Sync(unit);
        _net.Deliver();

        Assert.Equal(SyncResultKind.Pulled, a.Sync(unit).Kind);
        _net.Deliver();
        Assert.Equal(SyncResultKind.Pulled, b.Sync(unit).Kind);

        foreach (var pc in new[] { a, b })
        {
            Assert.Equal("game1-desktop", pc.Read("BLUS00001-SAVE/SAVEDATA.BIN"));
            Assert.Equal("game2-ally", pc.Read("BLUS00002-SAVE/SAVEDATA.BIN"));
            Assert.Equal("game3-ally", pc.Read("BLUS00003-SAVE/SAVEDATA.BIN"));
            Assert.Null(pc.Engine.GetConflict(unit.UnitId));
        }

        Assert.Equal(SyncResultKind.UpToDate, a.Sync(unit).Kind);
        Assert.Equal(SyncResultKind.UpToDate, b.Sync(unit).Kind);
    }

    [Fact]
    public void SameSaveFolder_ConflictsOnlyForIt_AndCanBeResolvedPerGroup()
    {
        var (a, b, unit) = TwoPcs(MergeGranularity.Folder);
        a.Write("GAME1/SAVE.BIN", "g1-v1");
        a.Write("GAME2/SAVE.BIN", "g2-v1");
        a.Sync(unit);
        _net.Deliver();
        b.Sync(unit);

        a.Write("GAME1/SAVE.BIN", "g1-desktop");
        a.Write("GAME2/SAVE.BIN", "g2-desktop");
        b.Write("GAME1/SAVE.BIN", "g1-ally");
        b.Write("GAME3/SAVE.BIN", "g3-ally");
        a.Sync(unit);
        b.Sync(unit);
        _net.Deliver();

        Assert.Equal(SyncResultKind.Conflict, a.Sync(unit).Kind);
        var conflict = a.Engine.GetConflict(unit.UnitId)!;
        Assert.Equal(["saves/GAME1"], conflict.Groups);

        var desktopV = conflict.CandidateVersionIds.Single(id => id.StartsWith(a.Device.ShortId, StringComparison.Ordinal));
        var allyV = conflict.CandidateVersionIds.Single(id => id.StartsWith(b.Device.ShortId, StringComparison.Ordinal));
        a.Engine.Resolve(unit, a.Bindings, desktopV, new Dictionary<string, string> { ["saves/GAME1"] = allyV });

        Assert.Equal("g1-ally", a.Read("GAME1/SAVE.BIN"));
        Assert.Equal("g2-desktop", a.Read("GAME2/SAVE.BIN"));
        Assert.Equal("g3-ally", a.Read("GAME3/SAVE.BIN"));

        _net.Deliver();
        b.Sync(unit);
        Assert.Equal("g1-ally", b.Read("GAME1/SAVE.BIN"));
        Assert.Equal("g2-desktop", b.Read("GAME2/SAVE.BIN"));
        Assert.Equal("g3-ally", b.Read("GAME3/SAVE.BIN"));
    }

    [Fact]
    public void ThreePcs_EditingDifferentSaves_Converge()
    {
        var a = _h.CreatePc("desktop", network: _net);
        var b = _h.CreatePc("ally", TimeSpan.FromHours(2), _net);
        var c = _h.CreatePc("laptop", TimeSpan.FromMinutes(-30), _net);
        var unit = Harness.Unit(granularity: MergeGranularity.Folder);
        a.Remote.WriteUnitDefinition(unit, a.Device.ShortId);
        a.Write("G0/S.BIN", "shared");
        a.Sync(unit);
        _net.Deliver();
        b.Sync(unit);
        c.Sync(unit);

        a.Write("GA/S.BIN", "from-a");
        b.Write("GB/S.BIN", "from-b");
        c.Write("GC/S.BIN", "from-c");
        foreach (var pc in new[] { a, b, c })
        {
            pc.Sync(unit);
        }

        for (var round = 0; round < 4; round++)
        {
            _net.Deliver();
            foreach (var pc in new[] { a, b, c })
            {
                pc.Sync(unit);
            }
        }

        foreach (var pc in new[] { a, b, c })
        {
            Assert.Equal("from-a", pc.Read("GA/S.BIN"));
            Assert.Equal("from-b", pc.Read("GB/S.BIN"));
            Assert.Equal("from-c", pc.Read("GC/S.BIN"));
            Assert.Equal("shared", pc.Read("G0/S.BIN"));
            Assert.Null(pc.Engine.GetConflict(unit.UnitId));
        }

        var tips = a.Engine.LoadGraph(unit.UnitId).Graph.Tips;
        Assert.Single(tips.Select(t => t.Content.ContentId).Distinct());
    }

    [Fact]
    public void OneDriveConflictCopies_AreIgnored()
    {
        var a = _h.CreatePc("desktop");
        var unit = Harness.Unit();
        a.Remote.WriteUnitDefinition(unit, a.Device.ShortId);
        a.Write("slot1.sav", "a1");
        a.Sync(unit);
        a.Service.Tick(); // registers the device

        var versions = Path.Combine(_h.RepoDir, "units", unit.UnitId, "versions");
        var original = Directory.GetFiles(versions).Single();
        File.Copy(original, Path.Combine(versions, Path.GetFileNameWithoutExtension(original) + "-DESKTOP.json"));
        var devices = Path.Combine(_h.RepoDir, "devices");
        var deviceFile = Directory.GetFiles(devices).Single();
        File.Copy(deviceFile, Path.Combine(devices, Path.GetFileNameWithoutExtension(deviceFile) + "-DESKTOP.json"));

        Assert.Equal(SyncResultKind.UpToDate, a.Sync(unit).Kind);
        var store = new FolderRemoteStore(_h.RepoDir);
        Assert.Single(store.ReadDevices());
        Assert.Equal(2, store.FindIgnoredFiles().Count);
        a.Service.Tick();
        Assert.Null(a.Service.FatalError);
    }

    [Fact]
    public void ClonedDeviceIdentity_StopsTheSecondPc()
    {
        var a = _h.CreatePc("desktop");
        a.Service.Tick();
        var clone = _h.CreatePc("clone", identity: a.Device);
        clone.Service.Tick();

        Assert.Null(a.Service.FatalError);
        Assert.NotNull(clone.Service.FatalError);
    }

    [Fact]
    public void CrashAfterVersionWritten_BeforeLocalStateUpdate_DoesNotDuplicate()
    {
        var a = _h.CreatePc("desktop");
        var unit = Harness.Unit();
        a.Remote.WriteUnitDefinition(unit, a.Device.ShortId);
        a.Write("slot1.sav", "v1");
        a.Sync(unit);
        var firstBase = a.Engine.GetBaseVersionId(unit.UnitId);

        a.Write("slot1.sav", "v2");
        a.Sync(unit);
        // Simulate losing the local state update of the second push.
        a.State.SaveUnitState(new Core.State.UnitStateRecord(unit.UnitId, firstBase, null, null));

        var before = a.Remote.ListVersionIds(unit.UnitId).Count;
        Assert.Equal(SyncResultKind.UpToDate, a.Sync(unit).Kind);
        Assert.Equal(before, a.Remote.ListVersionIds(unit.UnitId).Count);
        Assert.NotEqual(firstBase, a.Engine.GetBaseVersionId(unit.UnitId));
    }

    [Fact]
    public void LostLocalState_ReusesNoSequenceNumber()
    {
        var a = _h.CreatePc("desktop");
        var unit = Harness.Unit();
        a.Remote.WriteUnitDefinition(unit, a.Device.ShortId);
        a.Write("slot1.sav", "v1");
        a.Sync(unit);

        // Fresh state database on the same device identity (e.g. state.db deleted).
        using var freshState = new Core.State.SqliteLocalState(Path.Combine(_h.Root, "fresh-state.db"));
        var engine = new SyncEngine(a.Remote, freshState, a.Device, a.Clock);
        a.Write("slot1.sav", "v2");
        var result = engine.Sync(unit, a.Bindings, SyncOptions.Full);

        Assert.Equal(SyncResultKind.Conflict, result.Kind);
        Assert.Equal(2, a.Remote.ListVersionIds(unit.UnitId).Distinct().Count());
    }
}
