using SaveFileSync.Core.Model;
using SaveFileSync.Core.Sync;
using static SaveFileSync.Core.Tests.TestData;

namespace SaveFileSync.Core.Tests;

/// <summary>Three-way merge by groups and the planner at folder/file granularity (phase 1b).</summary>
public class MergeTests
{
    // RPCS3-like layout: each first-level folder is one save.
    private static readonly Snapshot Base = Snap(("GAME1/SAVE.DAT", "g1-v1"), ("GAME2/SAVE.DAT", "g2-v1"));

    [Theory]
    [InlineData(MergeGranularity.Unit, "")]
    [InlineData(MergeGranularity.Folder, "saves/GAME1")]
    [InlineData(MergeGranularity.File, "saves/GAME1/SAVE.DAT")]
    public void GroupOf_FollowsGranularity(MergeGranularity granularity, string expected)
    {
        var file = new FileEntry("saves", "GAME1/SAVE.DAT", 1, Sha("x"), DateTime.UtcNow);
        Assert.Equal(expected, Merge.GroupOf(file, granularity));
    }

    [Fact]
    public void GroupOf_FileAtLocationRoot_IsItsOwnFolderGroup()
    {
        var file = new FileEntry("saves", "settings.ini", 1, Sha("x"), DateTime.UtcNow);
        Assert.Equal("saves/settings.ini", Merge.GroupOf(file, MergeGranularity.Folder));
    }

    [Fact]
    public void DisjointFolders_MergeWithoutConflict()
    {
        var ours = Snap(("GAME1/SAVE.DAT", "g1-ours"), ("GAME2/SAVE.DAT", "g2-v1"));
        var theirs = Snap(("GAME1/SAVE.DAT", "g1-v1"), ("GAME2/SAVE.DAT", "g2-theirs"), ("GAME3/SAVE.DAT", "g3-new"));

        var result = Merge.ThreeWay(Base, ours, theirs, MergeGranularity.Folder);

        Assert.False(result.HasConflicts);
        Assert.True(result.Merged.ContentEquals(Snap(
            ("GAME1/SAVE.DAT", "g1-ours"), ("GAME2/SAVE.DAT", "g2-theirs"), ("GAME3/SAVE.DAT", "g3-new"))));
    }

    [Fact]
    public void SameFolderChangedOnBothSides_ConflictsOnlyThere()
    {
        var ours = Snap(("GAME1/SAVE.DAT", "g1-ours"), ("GAME2/SAVE.DAT", "g2-v1"));
        var theirs = Snap(("GAME1/SAVE.DAT", "g1-theirs"), ("GAME2/SAVE.DAT", "g2-theirs"));

        var result = Merge.ThreeWay(Base, ours, theirs, MergeGranularity.Folder);

        Assert.Equal(["saves/GAME1"], result.ConflictGroups);
    }

    [Fact]
    public void FilesOfOneFolderChangedSeparately_ConflictAtFolderGranularity_MergeAtFileGranularity()
    {
        var b = Snap(("GAME1/A.DAT", "a1"), ("GAME1/B.DAT", "b1"));
        var ours = Snap(("GAME1/A.DAT", "a2"), ("GAME1/B.DAT", "b1"));
        var theirs = Snap(("GAME1/A.DAT", "a1"), ("GAME1/B.DAT", "b2"));

        Assert.True(Merge.ThreeWay(b, ours, theirs, MergeGranularity.Folder).HasConflicts);
        var byFile = Merge.ThreeWay(b, ours, theirs, MergeGranularity.File);
        Assert.False(byFile.HasConflicts);
        Assert.True(byFile.Merged.ContentEquals(Snap(("GAME1/A.DAT", "a2"), ("GAME1/B.DAT", "b2"))));
    }

    [Fact]
    public void DeleteVersusModify_IsAConflict()
    {
        var ours = Snap(("GAME2/SAVE.DAT", "g2-v1"));
        var theirs = Snap(("GAME1/SAVE.DAT", "g1-theirs"), ("GAME2/SAVE.DAT", "g2-v1"));
        Assert.Equal(["saves/GAME1"], Merge.ThreeWay(Base, ours, theirs, MergeGranularity.Folder).ConflictGroups);
    }

    [Fact]
    public void DeletionOnOneSide_IsMerged()
    {
        var ours = Snap(("GAME2/SAVE.DAT", "g2-v1"));
        var result = Merge.ThreeWay(Base, ours, Base, MergeGranularity.Folder);
        Assert.False(result.HasConflicts);
        Assert.True(result.Merged.ContentEquals(ours));
    }

    [Fact]
    public void Planner_DisjointRemoteTips_ProducesMergeApply()
    {
        var graph = new VersionGraph([
            V("pc1-000001", Base),
            V("pc2-000001", Snap(("GAME1/SAVE.DAT", "g1-pc2"), ("GAME2/SAVE.DAT", "g2-v1")), "pc1-000001"),
            V("pc3-000001", Snap(("GAME1/SAVE.DAT", "g1-v1"), ("GAME2/SAVE.DAT", "g2-pc3")), "pc1-000001"),
        ]);

        var plan = Assert.IsType<SyncPlan.MergeApply>(Planner.Decide("pc1-000001", Base, graph, MergeGranularity.Folder));
        Assert.Equal(["pc1-000001", "pc2-000001", "pc3-000001"], plan.Parents);
        Assert.True(plan.Content.ContentEquals(Snap(("GAME1/SAVE.DAT", "g1-pc2"), ("GAME2/SAVE.DAT", "g2-pc3"))));
    }

    [Fact]
    public void Planner_SameTipsAtUnitGranularity_Conflict()
    {
        var graph = new VersionGraph([
            V("pc1-000001", Base),
            V("pc2-000001", Snap(("GAME1/SAVE.DAT", "g1-pc2"), ("GAME2/SAVE.DAT", "g2-v1")), "pc1-000001"),
            V("pc3-000001", Snap(("GAME1/SAVE.DAT", "g1-v1"), ("GAME2/SAVE.DAT", "g2-pc3")), "pc1-000001"),
        ]);

        Assert.IsType<SyncPlan.Conflict>(Planner.Decide("pc1-000001", Base, graph, MergeGranularity.Unit));
    }

    [Fact]
    public void Planner_LocalAndRemoteDisjoint_PushesLocalFirst_ThenMerges()
    {
        var local = Snap(("GAME1/SAVE.DAT", "g1-local"), ("GAME2/SAVE.DAT", "g2-v1"));
        var remote = Snap(("GAME1/SAVE.DAT", "g1-v1"), ("GAME2/SAVE.DAT", "g2-remote"));
        var graph = new VersionGraph([V("pc1-000001", Base), V("pc2-000001", remote, "pc1-000001")]);

        var push = Assert.IsType<SyncPlan.Push>(Planner.Decide("pc1-000001", local, graph, MergeGranularity.Folder));
        Assert.Equal(["pc1-000001"], push.Parents);

        var afterPush = graph.With(V("pc1-000002", local, "pc1-000001"));
        var merge = Assert.IsType<SyncPlan.MergeApply>(Planner.Decide("pc1-000002", local, afterPush, MergeGranularity.Folder));
        Assert.Equal(["pc1-000002", "pc2-000001"], merge.Parents);
        Assert.True(merge.Content.ContentEquals(Snap(("GAME1/SAVE.DAT", "g1-local"), ("GAME2/SAVE.DAT", "g2-remote"))));
    }

    [Fact]
    public void Planner_ConflictReportsGroups()
    {
        var local = Snap(("GAME1/SAVE.DAT", "g1-local"), ("GAME2/SAVE.DAT", "g2-v1"));
        var remote = Snap(("GAME1/SAVE.DAT", "g1-remote"), ("GAME2/SAVE.DAT", "g2-remote"));
        var graph = new VersionGraph([
            V("pc1-000001", Base),
            V("pc2-000001", remote, "pc1-000001"),
            V("pc1-000002", local, "pc1-000001"),
        ]);

        var conflict = Assert.IsType<SyncPlan.Conflict>(Planner.Decide("pc1-000002", local, graph, MergeGranularity.Folder));
        Assert.Equal(["saves/GAME1"], conflict.Groups);
    }
}
