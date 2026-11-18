using SaveFileSync.Core.Model;
using SaveFileSync.Core.Sync;
using static SaveFileSync.Core.Tests.TestData;

namespace SaveFileSync.Core.Tests;

/// <summary>Decision table of PLAN.md §6.2 at unit granularity (phase 1a).</summary>
public class PlannerTests
{
    private static readonly Snapshot A = Snap(("slot1.sav", "a"));
    private static readonly Snapshot B = Snap(("slot1.sav", "b"));
    private static readonly Snapshot C = Snap(("slot1.sav", "c"));
    private static readonly Snapshot D = Snap(("slot1.sav", "d"));

    [Fact]
    public void EmptyRepository_EmptyLocal_DoesNothing()
    {
        var plan = Planner.Decide(null, Snapshot.Empty, VersionGraph.Empty);
        Assert.Equal(new SyncPlan.NoOp(), plan);
    }

    [Fact]
    public void EmptyRepository_LocalFiles_PushesInitialVersion()
    {
        var plan = Assert.IsType<SyncPlan.Push>(Planner.Decide(null, A, VersionGraph.Empty));
        Assert.Empty(plan.Parents);
        Assert.Equal(VersionKind.Initial, plan.Kind);
    }

    [Fact]
    public void NothingChanged_DoesNothing()
    {
        var graph = new VersionGraph([V("pc1-000001", A)]);
        Assert.Equal(new SyncPlan.NoOp(), Planner.Decide("pc1-000001", A, graph));
    }

    [Fact]
    public void LocalChanged_RemoteUnchanged_Pushes()
    {
        var graph = new VersionGraph([V("pc1-000001", A)]);
        var plan = Assert.IsType<SyncPlan.Push>(Planner.Decide("pc1-000001", B, graph));
        Assert.Equal(["pc1-000001"], plan.Parents);
        Assert.Equal(VersionKind.Normal, plan.Kind);
    }

    [Fact]
    public void RemoteChanged_LocalUnchanged_PullsTip()
    {
        var graph = new VersionGraph([V("pc1-000001", A), V("pc2-000001", B, "pc1-000001")]);
        Assert.Equal(new SyncPlan.Pull("pc2-000001"), Planner.Decide("pc1-000001", A, graph));
    }

    [Fact]
    public void RemoteChangedSeveralTimes_PullsLatestTip()
    {
        var graph = new VersionGraph([
            V("pc1-000001", A),
            V("pc2-000001", B, "pc1-000001"),
            V("pc2-000002", C, "pc2-000001"),
        ]);
        Assert.Equal(new SyncPlan.Pull("pc2-000002"), Planner.Decide("pc1-000001", A, graph));
    }

    [Fact]
    public void BothChanged_PreservesLocalThenConflicts()
    {
        var graph = new VersionGraph([V("pc1-000001", A), V("pc2-000001", B, "pc1-000001")]);
        var plan = Assert.IsType<SyncPlan.Conflict>(Planner.Decide("pc1-000001", C, graph));
        Assert.True(plan.PreserveLocalFirst);
        Assert.Equal(["pc1-000001"], plan.PreserveParents);
        Assert.Equal(["pc2-000001"], plan.CandidateVersionIds);
        Assert.Equal(ConflictReason.BothChanged, plan.Reason);
    }

    [Fact]
    public void AfterPreservingLocal_ConflictListsBothVersions()
    {
        var graph = new VersionGraph([
            V("pc1-000001", A),
            V("pc2-000001", B, "pc1-000001"),
            V("pc1-000002", C, "pc1-000001"),
        ]);
        var plan = Assert.IsType<SyncPlan.Conflict>(Planner.Decide("pc1-000002", C, graph));
        Assert.False(plan.PreserveLocalFirst);
        Assert.Equal(["pc1-000002", "pc2-000001"], plan.CandidateVersionIds.Order());
    }

    [Fact]
    public void BothChangedToSameContent_AdoptsRemoteWithoutNewVersion()
    {
        var graph = new VersionGraph([V("pc1-000001", A), V("pc2-000001", B, "pc1-000001")]);
        Assert.Equal(new SyncPlan.NoOp("pc2-000001"), Planner.Decide("pc1-000001", B, graph));
    }

    [Fact]
    public void ForkedTips_LocalEqualsOwnTip_Conflicts()
    {
        // pc1 pushed pc1-2 while pc2 pushed pc2-1, both from pc1-1.
        var graph = new VersionGraph([
            V("pc1-000001", A),
            V("pc1-000002", B, "pc1-000001"),
            V("pc2-000001", C, "pc1-000001"),
        ]);
        var plan = Assert.IsType<SyncPlan.Conflict>(Planner.Decide("pc1-000002", B, graph));
        Assert.False(plan.PreserveLocalFirst);
        Assert.Equal(["pc1-000002", "pc2-000001"], plan.CandidateVersionIds.Order());
    }

    [Fact]
    public void ForkedTip_WithoutRealChanges_IsAbsorbedAndBecomesParentOfNextPush()
    {
        // pc2-1 has the same content as the common ancestor (nothing new).
        var graph = new VersionGraph([
            V("pc1-000001", A),
            V("pc1-000002", B, "pc1-000001"),
            V("pc2-000001", A, "pc1-000001"),
        ]);
        Assert.Equal(new SyncPlan.NoOp(), Planner.Decide("pc1-000002", B, graph));

        var push = Assert.IsType<SyncPlan.Push>(Planner.Decide("pc1-000002", C, graph));
        Assert.Equal(["pc1-000002", "pc2-000001"], push.Parents);
        Assert.Equal(VersionKind.Merge, push.Kind);
    }

    [Fact]
    public void IdenticalRemoteTips_PullDeterministically()
    {
        var graph = new VersionGraph([
            V("pc1-000001", A),
            V("pc2-000001", B, "pc1-000001"),
            V("pc3-000001", B, "pc1-000001"),
        ]);
        Assert.Equal(new SyncPlan.Pull("pc2-000001"), Planner.Decide("pc1-000001", A, graph));
    }

    [Fact]
    public void ResolvedConflict_PullsResolution()
    {
        var graph = new VersionGraph([
            V("pc1-000001", A),
            V("pc1-000002", B, "pc1-000001"),
            V("pc2-000001", C, "pc1-000001"),
            V("pc2-000002", C, "pc1-000002", "pc2-000001"),
        ]);
        Assert.Equal(new SyncPlan.Pull("pc2-000002"), Planner.Decide("pc1-000002", B, graph));
    }

    [Fact]
    public void ConcurrentIdenticalResolutions_Converge()
    {
        var graph = new VersionGraph([
            V("pc1-000001", A),
            V("pc1-000002", B, "pc1-000001"),
            V("pc2-000001", C, "pc1-000001"),
            V("pc1-000003", C, "pc1-000002", "pc2-000001"),
            V("pc2-000002", C, "pc1-000002", "pc2-000001"),
        ]);
        Assert.IsType<SyncPlan.NoOp>(Planner.Decide("pc1-000003", C, graph));
        Assert.IsType<SyncPlan.NoOp>(Planner.Decide("pc2-000002", C, graph));
    }

    [Fact]
    public void UnknownBase_EmptyLocal_PullsTip()
    {
        var graph = new VersionGraph([V("pc1-000001", A)]);
        Assert.Equal(new SyncPlan.Pull("pc1-000001"), Planner.Decide(null, Snapshot.Empty, graph));
    }

    [Fact]
    public void UnknownBase_LocalEqualsTip_AdoptsIt()
    {
        var graph = new VersionGraph([V("pc1-000001", A)]);
        Assert.Equal(new SyncPlan.NoOp("pc1-000001"), Planner.Decide(null, A, graph));
    }

    [Fact]
    public void UnknownBase_LocalEqualsOlderVersion_PullsNewer()
    {
        var graph = new VersionGraph([V("pc1-000001", A), V("pc1-000002", B, "pc1-000001")]);
        Assert.Equal(new SyncPlan.Pull("pc1-000002"), Planner.Decide(null, A, graph));
    }

    [Fact]
    public void UnknownBase_DifferentLocalData_IsInitialConflict()
    {
        var graph = new VersionGraph([V("pc1-000001", A)]);
        var plan = Assert.IsType<SyncPlan.Conflict>(Planner.Decide(null, D, graph));
        Assert.True(plan.PreserveLocalFirst);
        Assert.Empty(plan.PreserveParents);
        Assert.Equal(ConflictReason.InitialSetup, plan.Reason);
    }

    [Fact]
    public void UnknownBase_EmptyLocal_DivergedTips_Conflicts()
    {
        var graph = new VersionGraph([
            V("pc1-000001", A),
            V("pc1-000002", B, "pc1-000001"),
            V("pc2-000001", C, "pc1-000001"),
        ]);
        var plan = Assert.IsType<SyncPlan.Conflict>(Planner.Decide(null, Snapshot.Empty, graph));
        Assert.Equal(ConflictReason.RemoteDiverged, plan.Reason);
        Assert.False(plan.PreserveLocalFirst);
    }

    [Fact]
    public void LocalFilesDisappeared_IsNotPropagated()
    {
        var graph = new VersionGraph([V("pc1-000001", A)]);
        Assert.Equal(new SyncPlan.LocalEmptied("pc1-000001"), Planner.Decide("pc1-000001", Snapshot.Empty, graph));
    }

    [Fact]
    public void LocalFilesDisappeared_AfterDeletionElsewhere_IsAccepted()
    {
        var graph = new VersionGraph([V("pc1-000001", A), V("pc2-000001", Snapshot.Empty, "pc1-000001")]);
        Assert.Equal(new SyncPlan.NoOp("pc2-000001"), Planner.Decide("pc1-000001", Snapshot.Empty, graph));
    }

    [Fact]
    public void ContentComparison_IgnoresCaseOfPathsAndMtime()
    {
        var upper = new Snapshot([new FileEntry("saves", "SLOT1.SAV", 1, Sha("a"), DateTime.UtcNow)]);
        Assert.True(upper.ContentEquals(A));
    }
}
