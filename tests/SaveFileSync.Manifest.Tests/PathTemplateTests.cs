using SaveFileSync.Core.Paths;

namespace SaveFileSync.Manifest.Tests;

public class PathTemplateTests
{
    [Theory]
    [InlineData("<winAppData>/Game/saves", "<winAppData>/Game/saves", null)]
    [InlineData("<winDocuments>/Game/*.sav", "<winDocuments>/Game", "*.sav")]
    [InlineData("<winLocalAppData>/Game/<storeUserId>/saves", "<winLocalAppData>/Game", "*/saves")]
    [InlineData("<base>/profiles/*/saves/**", "<base>/profiles", "*/saves/**")]
    [InlineData("<home>/Saved Games\\Game", "<home>/Saved Games/Game", null)]
    public void SplitAtWildcard_SeparatesFolderFromGlob(string template, string root, string? relative)
    {
        Assert.Equal((root, relative), PathTemplate.SplitAtWildcard(template));
    }

    [Fact]
    public void Expand_ReplacesKnownFolders_WithoutRelyingOnEnvironmentVariables()
    {
        using var pc = new FakePc();
        var ctx = pc.Context();

        var path = Assert.Single(PathTemplate.Expand("<winAppData>/Game/saves", ctx));
        Assert.Equal(pc.AppData.Replace('\\', '/') + "/Game/saves", path);

        Assert.Equal(pc.Documents.Replace('\\', '/') + "/My Games/X", Assert.Single(PathTemplate.Expand("<winDocuments>/My Games/X", ctx)));
        Assert.Equal(pc.LocalLow.Replace('\\', '/') + "/Studio", Assert.Single(PathTemplate.Expand("<winLocalAppDataLow>/Studio", ctx)));
        Assert.EndsWith("/tester/Saved Games/G", Assert.Single(PathTemplate.Expand("<home>/Saved Games/G", ctx)));
    }

    [Fact]
    public void Expand_DocumentsMovedToAnotherDrive_FollowsTheKnownFolder()
    {
        using var pc = new FakePc();
        var moved = pc.Dir("D-drive/Docs");
        var folders = new KnownFolderSet
        {
            Home = pc.Home,
            AppData = pc.AppData,
            LocalAppData = pc.LocalAppData,
            LocalAppDataLow = pc.LocalLow,
            Documents = moved,
            Public = pc.Home,
            ProgramData = pc.Home,
            WinDir = pc.Home,
            UserName = "tester",
        };

        var path = Assert.Single(PathTemplate.Expand("<winDocuments>/Game", new ResolveContext { Folders = folders }));
        Assert.StartsWith(moved.Replace('\\', '/'), path);
    }

    [Fact]
    public void Expand_StoreUserIdBecomesWildcard()
    {
        using var pc = new FakePc();
        var path = Assert.Single(PathTemplate.Expand("<winAppData>/G/<storeUserId>/saves", pc.Context()));
        Assert.EndsWith("/G/*/saves", path);
    }

    [Fact]
    public void Expand_ForeignTokens_GiveNothing()
    {
        using var pc = new FakePc();
        Assert.Empty(PathTemplate.Expand("<xdgData>/game", pc.Context()));
    }

    [Fact]
    public void Expand_Base_OnlyForInstallFoldersThatExist()
    {
        using var pc = new FakePc();
        var rootA = pc.Dir("Games");
        var rootB = pc.Dir("Other");
        pc.Dir("Games/Alpha");
        pc.Dir("Other/Alpha Quest");
        var ctx = pc.Context([rootA, rootB], ["Alpha", "Alpha Quest", "Missing"]);

        var paths = PathTemplate.Expand("<base>/saves", ctx);

        Assert.Equal(2, paths.Count);
        Assert.Contains(paths, p => p.EndsWith("/Games/Alpha/saves", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("/Other/Alpha Quest/saves", StringComparison.Ordinal));
        Assert.Empty(PathTemplate.Expand("<base>/saves", pc.Context([], ["Alpha"])));
    }

    [Fact]
    public void Expand_Root_UsesEveryLibraryFolder()
    {
        using var pc = new FakePc();
        var paths = PathTemplate.Expand("<root>/saves", pc.Context([pc.Dir("A"), pc.Dir("B")]));
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void ParentAndLastSegment()
    {
        Assert.Equal("<winAppData>/Game", PathTemplate.Parent("<winAppData>/Game/slot.sav"));
        Assert.Equal("slot.sav", PathTemplate.LastSegment("<winAppData>/Game/slot.sav"));
        Assert.Null(PathTemplate.Parent("single"));
    }
}
