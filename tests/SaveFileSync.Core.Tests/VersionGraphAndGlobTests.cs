using SaveFileSync.Core.IO;
using SaveFileSync.Core.Sync;
using static SaveFileSync.Core.Tests.TestData;

namespace SaveFileSync.Core.Tests;

public class VersionGraphTests
{
    private static readonly VersionGraph Graph = new([
        V("a-000001", Snap(("f", "1"))),
        V("a-000002", Snap(("f", "2")), "a-000001"),
        V("b-000001", Snap(("f", "3")), "a-000001"),
        V("b-000002", Snap(("f", "4")), "a-000002", "b-000001"),
        V("a-000003", Snap(("f", "5")), "a-000002"),
    ]);

    [Fact]
    public void Tips_AreVersionsWithoutChildren()
    {
        Assert.Equal(["a-000003", "b-000002"], Graph.Tips.Select(t => t.VersionId));
    }

    [Fact]
    public void LowestCommonAncestor_PrefersMostRecent()
    {
        Assert.Equal("a-000002", Graph.LowestCommonAncestor(["a-000003"], "b-000002")!.VersionId);
        Assert.Equal("a-000001", Graph.LowestCommonAncestor(["a-000002"], "b-000001")!.VersionId);
    }

    [Fact]
    public void LowestCommonAncestor_OfUnrelatedRoots_IsNull()
    {
        var graph = new VersionGraph([V("a-000001", Snap(("f", "1"))), V("b-000001", Snap(("f", "2")))]);
        Assert.Null(graph.LowestCommonAncestor(["a-000001"], "b-000001"));
    }

    [Fact]
    public void MissingParents_AreTolerated()
    {
        var graph = new VersionGraph([V("a-000005", Snap(("f", "1")), "a-000004")]);
        Assert.Single(graph.Tips);
        Assert.Equal(0, graph.Generation(graph.Tips[0]));
    }

    [Fact]
    public void Generation_FollowsLongestChain()
    {
        Assert.Equal(2, Graph.Generation(Graph.Get("b-000002")!));
        Assert.Equal(2, Graph.Generation(Graph.Get("a-000003")!));
    }
}

public class GlobTests
{
    [Theory]
    [InlineData("**", "a/b/c.sav", true)]
    [InlineData("*.sav", "slot.sav", true)]
    [InlineData("*.sav", "dir/slot.sav", false)]
    [InlineData("**/*.sav", "slot.sav", true)]
    [InlineData("**/*.sav", "dir/sub/slot.sav", true)]
    [InlineData("logs/**", "logs/today.txt", true)]
    [InlineData("**/cache/**", "games/x/cache/shader.bin", true)]
    [InlineData("save?.dat", "SAVE1.DAT", true)]
    [InlineData("save?.dat", "save10.dat", false)]
    public void Matches(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, new Glob(pattern).IsMatch(path));
    }

    [Fact]
    public void Filter_AppliesExcludesAfterIncludes()
    {
        var filter = new PathFilter(["**"], ["*.log", "cache/**"]);
        Assert.True(filter.IsIncluded("slot1.sav"));
        Assert.False(filter.IsIncluded("debug.log"));
        Assert.False(filter.IsIncluded("cache/x.bin"));
    }
}
