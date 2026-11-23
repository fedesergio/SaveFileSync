using SaveFileSync.Core.History;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Sync;
using static SaveFileSync.Core.Tests.TestData;

namespace SaveFileSync.Core.Tests;

public class RetentionTests
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    /// <summary>A linear history pc1-000001 … pc1-0000NN.</summary>
    private static VersionGraph Chain(int count)
    {
        var versions = new List<VersionManifest>();
        for (var i = 1; i <= count; i++)
        {
            var parent = i == 1 ? [] : new[] { $"pc1-{i - 1:D6}" };
            versions.Add(V($"pc1-{i:D6}", Snap(("f", $"content-{i}")), parent));
        }

        return new VersionGraph(versions);
    }

    private static List<string> Archive(VersionGraph graph, int keep, IReadOnlySet<string>? archived = null,
        IReadOnlySet<string>? pinned = null, IReadOnlySet<string>? heads = null) =>
        RetentionPlanner.SelectToArchive(new RetentionInput(graph, archived ?? None, pinned ?? None, heads ?? None, keep))
            .Select(v => v.VersionId).Order().ToList();

    [Fact]
    public void KeepsTheNewestN_AndArchivesTheRest()
    {
        var result = Archive(Chain(15), keep: 10);
        Assert.Equal(["pc1-000001", "pc1-000002", "pc1-000003", "pc1-000004", "pc1-000005"], result);
    }

    [Fact]
    public void NothingToArchiveWhenHistoryIsShort()
    {
        Assert.Empty(Archive(Chain(4), keep: 10));
        Assert.Empty(Archive(Chain(10), keep: 10));
    }

    [Fact]
    public void TheLatestVersionIsNeverArchived_EvenWithKeepOne()
    {
        var result = Archive(Chain(5), keep: 1);
        Assert.DoesNotContain("pc1-000005", result);
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void KeepZeroOrNegative_StillKeepsOne()
    {
        Assert.DoesNotContain("pc1-000005", Archive(Chain(5), keep: 0));
    }

    [Fact]
    public void PinnedVersionsAndHeadsOfActiveDevicesAreKept()
    {
        var result = Archive(Chain(15), keep: 3,
            pinned: new HashSet<string> { "pc1-000002" },
            heads: new HashSet<string> { "pc1-000007" });

        Assert.DoesNotContain("pc1-000002", result);
        Assert.DoesNotContain("pc1-000007", result);
        Assert.Contains("pc1-000001", result);
        Assert.Contains("pc1-000006", result);
        Assert.DoesNotContain("pc1-000013", result);
    }

    [Fact]
    public void AlreadyArchivedVersionsDoNotCountTowardsTheLimit()
    {
        var archived = new HashSet<string> { "pc1-000001", "pc1-000002", "pc1-000003" };
        // 15 versions, 3 archived: the newest 5 restorable ones are 11..15.
        var result = Archive(Chain(15), keep: 5, archived: archived);
        Assert.Equal(7, result.Count);
        Assert.DoesNotContain(result, id => archived.Contains(id));
        Assert.Equal("pc1-000004", result[0]);
        Assert.Equal("pc1-000010", result[^1]);
    }

    [Fact]
    public void BothTipsOfABranchAreKept()
    {
        var graph = new VersionGraph([
            V("pc1-000001", Snap(("f", "1"))),
            V("pc1-000002", Snap(("f", "2")), "pc1-000001"),
            V("pc1-000003", Snap(("f", "3")), "pc1-000002"),
            V("pc2-000001", Snap(("f", "x")), "pc1-000001"),
        ]);

        var result = Archive(graph, keep: 1);

        Assert.DoesNotContain("pc1-000003", result);
        Assert.DoesNotContain("pc2-000001", result);
        Assert.Contains("pc1-000002", result);
    }

    [Fact]
    public void OrderComesFromTheHistory_NotFromDeviceClocks()
    {
        // The newest version has a clock far in the past: it must still be the one that is kept.
        var versions = Chain(6).Versions.ToList();
        var last = versions.Single(v => v.VersionId == "pc1-000006");
        var wrongClock = new VersionManifest
        {
            UnitId = last.UnitId, VersionId = last.VersionId, DeviceId = last.DeviceId, Seq = last.Seq,
            Parents = last.Parents, Kind = last.Kind, CreatedUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Files = last.Files, TotalSize = last.TotalSize,
        };
        var graph = new VersionGraph(versions.Where(v => v.VersionId != last.VersionId).Append(wrongClock));

        var result = Archive(graph, keep: 2);

        Assert.DoesNotContain("pc1-000006", result);
        Assert.DoesNotContain("pc1-000005", result);
        Assert.Equal(["pc1-000001", "pc1-000002", "pc1-000003", "pc1-000004"], result);
    }

    // ---------------------------------------------------------------- device roster

    private static DeviceInfo Device(string id, DateTime lastSeen) =>
        new() { DeviceId = id, ShortId = id[..4], Name = id, LastSeenUtc = lastSeen };

    [Fact]
    public void DeviceStates_ThisPcActiveInactiveRetired()
    {
        var now = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var me = Device("mmmm-this", now.AddDays(-200));
        var recent = Device("aaaa-recent", now.AddDays(-5));
        var old = Device("bbbb-old", now.AddDays(-91));
        var boundary = Device("cccc-boundary", now.AddDays(-90));
        var retired = Device("dddd-retired", now.AddDays(-3));
        var retiredDocs = new[] { new RetiredDevice { DeviceId = retired.DeviceId, RetiredUtc = now.AddDays(-1) } };

        Assert.Equal(DeviceState.ThisPc, DeviceRoster.StateOf(me, retiredDocs, me.DeviceId, now));
        Assert.Equal(DeviceState.Active, DeviceRoster.StateOf(recent, retiredDocs, me.DeviceId, now));
        Assert.Equal(DeviceState.Inactive, DeviceRoster.StateOf(old, retiredDocs, me.DeviceId, now));
        Assert.Equal(DeviceState.Active, DeviceRoster.StateOf(boundary, retiredDocs, me.DeviceId, now));
        Assert.Equal(DeviceState.Retired, DeviceRoster.StateOf(retired, retiredDocs, me.DeviceId, now));
    }

    [Fact]
    public void ARetiredDeviceThatComesBack_IsActiveAgain()
    {
        var now = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var docs = new[] { new RetiredDevice { DeviceId = "dddd", RetiredUtc = now.AddDays(-10) } };
        var back = Device("dddd", now.AddDays(-2)); // seen after the retirement

        Assert.Equal(DeviceState.Active, DeviceRoster.StateOf(back, docs, "mmmm", now));
        Assert.Empty(DeviceRoster.InactiveDeviceIds([back], docs, "mmmm", now));
    }
}
