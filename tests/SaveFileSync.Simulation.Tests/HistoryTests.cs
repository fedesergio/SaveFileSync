using SaveFileSync.Core.History;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Sync;
using SaveFileSync.Repository;

namespace SaveFileSync.Simulation.Tests;

/// <summary>Phase 3: history, restore, retention (archive), cleanup, pins, devices (PLAN.md §11).</summary>
public sealed class HistoryTests : IDisposable
{
    private readonly Harness _h = new();
    private readonly UnitDefinition _unit = Harness.Unit();
    private readonly TestPc _a;
    private readonly TestPc _b;

    public HistoryTests()
    {
        _a = _h.CreatePc("desktop");
        _b = _h.CreatePc("ally");
        _a.Register();
        _b.Register();
        _a.Remote.WriteUnitDefinition(_unit, _a.Device.ShortId);
    }

    public void Dispose() => _h.Dispose();

    private string Unit => _unit.UnitId;

    private int Versions() => _a.Remote.ListVersionIds(Unit).Count;

    private int BlobCount() => Directory.GetFiles(Path.Combine(_h.RepoDir, "blobs"), "*", SearchOption.AllDirectories).Length;

    /// <summary>Creates <paramref name="count"/> versions "v1".."vN" of slot1.sav on PC A.</summary>
    private void History(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            Assert.Equal(SyncResultKind.Pushed, _a.Save(_unit, "slot1.sav", $"v{i}").Kind);
        }
    }

    private string VersionWith(string content)
    {
        var sha = TestSha(content);
        return _a.History.GetHistory(Unit).First(e => _a.Remote.ReadVersion(Unit, e.VersionId)!.Files.Any(f => f.Sha256 == sha)).VersionId;
    }

    private static string TestSha(string content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    // ---------------------------------------------------------------- history list

    [Fact]
    public void HistoryListsVersionsNewestFirst_WithDeviceAndState()
    {
        History(3);
        _b.Sync(_unit);
        _b.Save(_unit, "slot1.sav", "from-ally");

        var list = _a.History.GetHistory(Unit);

        Assert.Equal(4, list.Count);
        Assert.True(list[0].IsLatest);
        Assert.Equal("ally", list[0].DeviceName);
        Assert.Equal("This PC", list[1].DeviceName);
        Assert.True(list.All(e => e.Restorable));
        Assert.Single(list, e => e.IsLatest);
    }

    // ---------------------------------------------------------------- restore

    [Fact]
    public void Restore_PutsTheOldContentOnTop_AndOtherPcsReceiveItNormally()
    {
        History(3);
        _b.Sync(_unit);
        var v1 = VersionWith("v1");

        var result = _a.Engine.RestoreVersion(_unit, _a.Bindings, v1);

        Assert.Equal(SyncResultKind.Pulled, result.Kind);
        Assert.Equal("v1", _a.Read("slot1.sav"));
        Assert.Equal(VersionKind.Restore, result.RemoteVersion!.Kind);
        Assert.Equal(4, Versions());
        Assert.Equal(SyncResultKind.UpToDate, _a.Sync(_unit).Kind);

        Assert.Equal(SyncResultKind.Pulled, _b.Sync(_unit).Kind);
        Assert.Equal("v1", _b.Read("slot1.sav"));
        Assert.Null(_b.Engine.GetConflict(Unit));

        // The replaced version is still in the history and can be restored again.
        var v3 = VersionWith("v3");
        Assert.Equal(SyncResultKind.Pulled, _a.Engine.RestoreVersion(_unit, _a.Bindings, v3).Kind);
        Assert.Equal("v3", _a.Read("slot1.sav"));
    }

    [Fact]
    public void Restore_FirstSavesPendingLocalChanges_SoNothingIsLost()
    {
        History(2);
        var v1 = VersionWith("v1");
        _a.Write("slot1.sav", "unsaved-progress");

        Assert.Equal(SyncResultKind.Pulled, _a.Engine.RestoreVersion(_unit, _a.Bindings, v1).Kind);

        Assert.Equal("v1", _a.Read("slot1.sav"));
        var kept = _a.History.GetHistory(Unit).Any(e => e.Restorable && _a.Remote.ReadVersion(Unit, e.VersionId)!.Files.Any(f => f.Sha256 == TestSha("unsaved-progress")));
        Assert.True(kept, "the unsaved local state must have been pushed before it was replaced");
    }

    [Fact]
    public void Restore_ToTheCurrentVersion_DoesNothing()
    {
        History(2);
        var result = _a.Engine.RestoreVersion(_unit, _a.Bindings, VersionWith("v2"));
        Assert.Equal(SyncResultKind.UpToDate, result.Kind);
        Assert.Equal(2, Versions());
    }

    [Fact]
    public void Restore_IsRefusedDuringAConflict_AndForAnUnknownVersion()
    {
        _a.Save(_unit, "slot1.sav", "base");
        _b.Sync(_unit);
        _a.Save(_unit, "slot1.sav", "desktop");
        _b.Write("slot1.sav", "ally");
        Assert.Equal(SyncResultKind.Conflict, _b.Sync(_unit).Kind);

        var refused = _b.Engine.RestoreVersion(_unit, _b.Bindings, VersionWith("base"));
        Assert.Equal(SyncResultKind.Refused, refused.Kind);
        Assert.Contains("conflict", refused.Message, StringComparison.OrdinalIgnoreCase);

        var unknown = _a.Engine.RestoreVersion(_unit, _a.Bindings, "ffffffff-000099");
        Assert.Equal(SyncResultKind.Refused, unknown.Kind);
    }

    // ---------------------------------------------------------------- retention / archive

    [Fact]
    public void Retention_ArchivesOldVersions_ButKeepsTheHistoryConnected()
    {
        History(15);
        var keepUnit = _unit; // default keep = 10

        var archived = _a.History.ApplyRetention(keepUnit);

        Assert.Equal(5, archived);
        var list = _a.History.GetHistory(Unit);
        Assert.Equal(15, list.Count);
        Assert.Equal(5, list.Count(e => e.Archived));
        Assert.Equal(10, list.Count(e => e.Restorable));
        Assert.All(list.Where(e => e.Archived), e => Assert.False(e.Restorable));

        // Manifests are all still there: the graph keeps its shape, with a single tip.
        var (graph, incomplete) = _a.Engine.LoadGraph(Unit);
        Assert.Equal(15, graph.Versions.Count);
        Assert.Equal(0, incomplete);
        Assert.Single(graph.Tips);
        Assert.Equal(SyncResultKind.UpToDate, _a.Sync(_unit).Kind);

        // Running it again changes nothing.
        Assert.Equal(0, _a.History.ApplyRetention(keepUnit));
    }

    [Fact]
    public void Retention_UsesTheKeepSettingOfTheUnit()
    {
        History(8);
        var unit = _unit.Revised(_a.Device.ShortId, _a.Clock.UtcNow, keepVersions: 3);
        Assert.Equal(5, _a.History.ApplyRetention(unit));
        Assert.Equal(3, _a.History.GetHistory(Unit).Count(e => e.Restorable));
    }

    [Fact]
    public void ADeviceFarBehind_StillSyncsAfterManyVersionsWereArchived()
    {
        History(1);
        _b.Sync(_unit); // the ally stays at version 1 while the desktop plays on
        for (var i = 2; i <= 20; i++)
        {
            _a.Save(_unit, "slot1.sav", $"v{i}");
            _a.History.ApplyRetention(_unit.Revised(_a.Device.ShortId, _a.Clock.UtcNow, keepVersions: 3));
        }

        // The ally's current version was kept (it is a head of an active device).
        var ally = _a.History.GetHistory(Unit).Single(e => e.VersionId == _b.Engine.GetBaseVersionId(Unit));
        Assert.True(ally.Restorable);

        Assert.Equal(SyncResultKind.Pulled, _b.Sync(_unit).Kind);
        Assert.Equal("v20", _b.Read("slot1.sav"));
        Assert.Null(_b.Engine.GetConflict(Unit));
    }

    [Fact]
    public void FolderMerge_StillFindsItsBase_AfterOldVersionsWereArchived()
    {
        var unit = Harness.Unit("rpcs3-merge", MergeGranularity.Folder);
        _a.Remote.WriteUnitDefinition(unit, _a.Device.ShortId);
        for (var i = 1; i <= 12; i++)
        {
            _a.Save(unit, "GAME1/SAVE.BIN", $"g1-v{i}");
        }

        _a.Save(unit, "GAME2/SAVE.BIN", "g2-v1");
        _b.Sync(unit);
        _a.History.ApplyRetention(unit.Revised(_a.Device.ShortId, _a.Clock.UtcNow, keepVersions: 2));

        // Both play different saves, then sync: the merge needs the common base, which is still in the graph.
        _a.Write("GAME1/SAVE.BIN", "g1-desktop");
        _b.Write("GAME2/SAVE.BIN", "g2-ally");
        _a.Sync(unit);
        _b.Sync(unit);
        _a.Sync(unit);

        Assert.Null(_a.Engine.GetConflict(unit.UnitId));
        Assert.Null(_b.Engine.GetConflict(unit.UnitId));
        Assert.Equal("g1-desktop", _b.Read("GAME1/SAVE.BIN"));
        Assert.Equal("g2-ally", _a.Read("GAME2/SAVE.BIN"));
    }

    [Fact]
    public void Retention_AnArchivedVersionCannotBeRestored()
    {
        History(14);
        _a.History.ApplyRetention(_unit);
        var oldest = _a.History.GetHistory(Unit).Last();
        Assert.True(oldest.Archived);

        var result = _a.Engine.RestoreVersion(_unit, _a.Bindings, oldest.VersionId);

        Assert.Equal(SyncResultKind.Refused, result.Kind);
        Assert.Contains("archived", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- pins

    [Fact]
    public void PinnedVersionsSurviveRetention_AndShowWhoPinnedThem()
    {
        History(14);
        var v1 = VersionWith("v1");
        _a.History.SetPinned(Unit, v1, true);

        _a.History.ApplyRetention(_unit);

        var entry = _b.History.GetHistory(Unit).Single(e => e.VersionId == v1);
        Assert.False(entry.Archived);
        Assert.True(entry.Restorable);
        Assert.Equal(["This PC"], _a.History.GetHistory(Unit).Single(e => e.VersionId == v1).PinnedBy);
        Assert.Equal(["desktop"], entry.PinnedBy);
        Assert.False(entry.PinnedHere);

        // Unpinning (on the PC that pinned it) lets the next pass archive it.
        _a.History.SetPinned(Unit, v1, false);
        _a.History.ApplyRetention(_unit);
        Assert.True(_a.History.GetHistory(Unit).Single(e => e.VersionId == v1).Archived);
    }

    [Fact]
    public void AnArchivedVersionCannotBePinned()
    {
        History(14);
        _a.History.ApplyRetention(_unit);
        var oldest = _a.History.GetHistory(Unit).Last();
        Assert.Throws<InvalidOperationException>(() => _a.History.SetPinned(Unit, oldest.VersionId, true));
    }

    // ---------------------------------------------------------------- devices

    [Fact]
    public void AnInactiveDevice_NoLongerProtectsItsVersion_AndItsOwnVersionsCanBeArchivedByOthers()
    {
        // The ally registers, pushes once, then is never seen again.
        _b.Register(_b.Clock.UtcNow);
        _b.Save(_unit, "slot1.sav", "ally-only");
        _a.Sync(_unit);
        History(12);
        var allyVersion = VersionWith("ally-only");

        _a.Clock.Advance(TimeSpan.FromDays(30));
        _a.Register();
        _a.History.ApplyRetention(_unit);
        Assert.False(_a.History.GetHistory(Unit).Single(e => e.VersionId == allyVersion).Archived); // ally still counts (30 days)

        _a.Clock.Advance(TimeSpan.FromDays(70)); // 100 days without the ally
        _a.Register();
        _a.History.ApplyRetention(_unit);

        Assert.True(_a.History.GetHistory(Unit).Single(e => e.VersionId == allyVersion).Archived);
        Assert.Equal(DeviceState.Inactive, _a.History.GetDevices().Single(d => d.Info.Name == "ally").State);
    }

    [Fact]
    public void RetiringADevice_ReleasesItImmediately_AndItCanComeBack()
    {
        _b.Save(_unit, "slot1.sav", "ally-only");
        _a.Sync(_unit);
        History(12);
        var allyVersion = VersionWith("ally-only");

        _a.History.RetireDevice(_b.Device.DeviceId);
        Assert.Equal(DeviceState.Retired, _a.History.GetDevices().Single(d => d.Info.Name == "ally").State);
        _a.History.ApplyRetention(_unit);
        Assert.True(_a.History.GetHistory(Unit).Single(e => e.VersionId == allyVersion).Archived);

        // The ally is used again: it registers (newer lastSeen) and is active once more.
        _b.Clock.Advance(TimeSpan.FromDays(1));
        _b.Register();
        Assert.Equal(DeviceState.Active, _a.History.GetDevices().Single(d => d.Info.Name == "ally").State);
        Assert.Throws<InvalidOperationException>(() => _a.History.RetireDevice(_a.Device.DeviceId));
    }

    // ---------------------------------------------------------------- cleanup (blob GC)

    private GcReport Gc(TestPc pc) => BlobGarbageCollector.Run(pc.Remote, pc.Device.DeviceId, pc.Device.ShortId, pc.Clock.UtcNow);

    [Fact]
    public void Cleanup_MarksFirst_AndDeletesOnlyAfterTheGracePeriod()
    {
        History(14);
        _a.History.ApplyRetention(_unit); // v1..v4 archived
        var blobsBefore = BlobCount();
        Assert.Equal(14, blobsBefore);

        var first = Gc(_a);
        Assert.Equal(4, first.Marked);
        Assert.Equal(0, first.Deleted);
        Assert.Equal(blobsBefore, BlobCount());

        _a.Clock.Advance(TimeSpan.FromDays(13));
        Assert.Equal(0, Gc(_a).Deleted);

        _a.Clock.Advance(TimeSpan.FromDays(2));
        // The files are "old" only if their own time says so: make the blobs look older than the grace period.
        foreach (var f in Directory.GetFiles(Path.Combine(_h.RepoDir, "blobs"), "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(f, _a.Clock.UtcNow.AddDays(-30));
        }

        var last = Gc(_a);
        Assert.Equal(4, last.Deleted);
        Assert.Equal(10, BlobCount());

        // Everything that is still restorable works; the tip is intact on every PC.
        Assert.Equal(SyncResultKind.Pulled, _b.Sync(_unit).Kind);
        Assert.Equal("v14", _b.Read("slot1.sav"));
    }

    [Fact]
    public void Cleanup_NeverDeletesABlobThatABecameReferencedAgain()
    {
        _a.Save(_unit, "slot1.sav", "AAA");
        _a.Save(_unit, "slot1.sav", "BBB");
        for (var i = 1; i <= 11; i++)
        {
            _a.Save(_unit, "slot1.sav", $"filler-{i}");
        }

        _a.History.ApplyRetention(_unit); // the versions holding AAA and BBB are archived
        Assert.True(Gc(_a).Marked >= 2);

        // Days later the player is back to AAA: a new version references the old blob again.
        _a.Clock.Advance(TimeSpan.FromDays(5));
        _a.Save(_unit, "slot1.sav", "AAA");

        _a.Clock.Advance(TimeSpan.FromDays(20));
        foreach (var f in Directory.GetFiles(Path.Combine(_h.RepoDir, "blobs"), "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(f, _a.Clock.UtcNow.AddDays(-60));
        }

        Gc(_a);

        Assert.True(File.Exists(BlobPath("AAA")), "a referenced blob must never be deleted");
        Assert.False(File.Exists(BlobPath("BBB")));
        Assert.Equal(SyncResultKind.Pulled, _b.Sync(_unit).Kind);
        Assert.Equal("AAA", _b.Read("slot1.sav"));
    }

    private string BlobPath(string content)
    {
        var sha = TestSha(content);
        return Path.Combine(_h.RepoDir, "blobs", sha[..2], sha);
    }

    [Fact]
    public void Cleanup_DoesNothing_WhenTheRepositoryLooksUnsafe()
    {
        History(14);
        _a.History.ApplyRetention(_unit);
        _a.Clock.Advance(TimeSpan.FromDays(30));

        // A version file that cannot be read (still syncing).
        var versions = Path.Combine(_h.RepoDir, "units", Unit, "versions");
        var broken = Path.Combine(versions, "deadbeef-000001.json");
        File.WriteAllText(broken, "{ half a file");
        var skipped = Gc(_a);
        Assert.NotNull(skipped.SkippedReason);
        Assert.Equal(0, skipped.Deleted);
        File.Delete(broken);

        // A write in progress.
        var temp = Path.Combine(_h.RepoDir, "blobs", "ab", "x.tmp-aaaa-1111");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.WriteAllText(temp, "partial");
        File.SetLastWriteTimeUtc(temp, _a.Clock.UtcNow);
        Assert.NotNull(Gc(_a).SkippedReason);
        File.Delete(temp);

        Assert.Null(Gc(_a).SkippedReason);
    }

    [Fact]
    public void Cleanup_OnlyCountsItsOwnMarks_SoAStaleMarkCannotMakeADeletionInstant()
    {
        _a.Save(_unit, "slot1.sav", "AAA");
        for (var i = 1; i <= 12; i++)
        {
            _a.Save(_unit, "slot1.sav", $"filler-{i}");
        }

        _a.History.ApplyRetention(_unit);
        Gc(_b); // the ally marks the archived blob "today"
        _a.Clock.Advance(TimeSpan.FromDays(20));
        _b.Clock.Advance(TimeSpan.FromDays(20));

        // The desktop sees the blob for the first time on day 20: it must mark it, not delete it.
        foreach (var f in Directory.GetFiles(Path.Combine(_h.RepoDir, "blobs"), "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(f, _a.Clock.UtcNow.AddDays(-60));
        }

        var report = Gc(_a);

        Assert.Equal(0, report.Deleted);
        Assert.True(File.Exists(BlobPath("AAA")));
    }

    // ---------------------------------------------------------------- repair

    [Fact]
    public void ABlobThatDisappearedFromTheRepository_IsReuploadedByThePcThatHasIt()
    {
        _a.Save(_unit, "slot1.sav", "important");
        var sha = TestSha("important");
        Assert.True(_a.Remote.DeleteBlob(sha)); // e.g. cleaned up while this PC was offline

        Assert.Equal(SyncResultKind.Incomplete, _b.Sync(_unit).Kind);
        Assert.Equal(1, _a.Engine.RepairMissingBlobs(_unit, _a.Bindings));

        Assert.Equal(SyncResultKind.Pulled, _b.Sync(_unit).Kind);
        Assert.Equal("important", _b.Read("slot1.sav"));
        Assert.Equal(0, _a.Engine.RepairMissingBlobs(_unit, _a.Bindings));
    }

    [Fact]
    public void Repair_DoesNothing_WhenTheLocalFileChangedMeanwhile()
    {
        _a.Save(_unit, "slot1.sav", "important");
        _a.Remote.DeleteBlob(TestSha("important"));
        _a.Write("slot1.sav", "something else");

        Assert.Equal(0, _a.Engine.RepairMissingBlobs(_unit, _a.Bindings));
    }

    // ---------------------------------------------------------------- recovery CLI

    [Fact]
    public void RecoveryCommands_UnderstandArchivedVersions_AndStillExportTheLatest()
    {
        History(14);
        _a.History.ApplyRetention(_unit);
        var archived = _a.History.GetHistory(Unit).Where(e => e.Archived).ToList();
        foreach (var e in archived)
        {
            foreach (var f in _a.Remote.ReadVersion(Unit, e.VersionId)!.Files)
            {
                _a.Remote.DeleteBlob(f.Sha256); // as the cleanup does after the grace period
            }
        }

        var recovery = new SaveFileSync.Core.Recovery.RecoveryService(new FolderRemoteStore(_h.RepoDir));
        var listed = recovery.ListUnits().Single();
        Assert.Equal(4, listed.Versions.Count(v => v.IsArchived));
        Assert.Equal(10, listed.Versions.Count(v => v.IsComplete));
        Assert.Single(listed.Versions, v => v.IsTip);

        var target = Path.Combine(_h.Root, "export-latest");
        Assert.Empty(recovery.Export(Unit, target).Problems);
        Assert.Equal("v14", File.ReadAllText(Path.Combine(target, "saves", "slot1.sav")));

        var oldest = archived[^1].VersionId;
        var ex = Assert.Throws<InvalidOperationException>(() => recovery.Export(Unit, Path.Combine(_h.Root, "x"), oldest));
        Assert.Contains("archived", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- export of an old version

    [Fact]
    public void AnOldRestorableVersionCanBeExported()
    {
        History(3);
        var v2 = VersionWith("v2");
        var target = Path.Combine(_h.Root, "export-v2");

        var report = new SaveFileSync.Core.Recovery.RecoveryService(new FolderRemoteStore(_h.RepoDir)).Export(Unit, target, v2);

        Assert.Empty(report.Problems);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(target, "saves", "slot1.sav")));
    }
}
