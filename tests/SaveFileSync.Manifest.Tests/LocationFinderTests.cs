using static SaveFileSync.Manifest.Tests.Games;

namespace SaveFileSync.Manifest.Tests;

public class LocationFinderTests
{
    [Fact]
    public void FolderPath_BecomesALocationWithEverythingIncluded()
    {
        using var pc = new FakePc();
        pc.File("Users/tester/AppData/Roaming/Alpha/saves/slot1.sav", "12345");
        pc.File("Users/tester/AppData/Roaming/Alpha/saves/sub/slot2.sav", "123");

        var found = Assert.Single(LocationFinder.Find(Game("Alpha", Save("<winAppData>/Alpha/saves")), pc.Context()));

        Assert.Equal("<winAppData>/Alpha/saves", found.RootTemplate);
        Assert.Equal(pc.Dir("Users/tester/AppData/Roaming/Alpha/saves"), found.AbsoluteRoot);
        Assert.Equal(["**"], found.Includes);
        Assert.Equal(2, found.FileCount);
        Assert.Equal(8, found.Bytes);
        Assert.Equal("saves", found.Label);
        Assert.True(found.Save);
    }

    [Fact]
    public void FileGlob_BecomesFolderPlusInclude()
    {
        using var pc = new FakePc();
        pc.File("Users/tester/AppData/LocalLow/Studio/Game/a.dat");
        pc.File("Users/tester/AppData/LocalLow/Studio/Game/b.bak");
        pc.File("Users/tester/AppData/LocalLow/Studio/Game/c.txt");

        var game = Game("G", Save("<home>/AppData/LocalLow/Studio/Game/*.dat"), Save("<home>/AppData/LocalLow/Studio/Game/*.bak"));
        var found = Assert.Single(LocationFinder.Find(game, pc.Context()));

        Assert.Equal("<home>/AppData/LocalLow/Studio/Game", found.RootTemplate);
        Assert.Contains("*.dat", found.Includes);
        Assert.Contains("*.bak", found.Includes);
        Assert.Equal(2, found.FileCount);
    }

    [Fact]
    public void SinglePlainFile_BecomesParentFolderPlusFileName()
    {
        using var pc = new FakePc();
        pc.File("Users/tester/Documents/MyGame/profile.dat");
        pc.File("Users/tester/Documents/MyGame/other.dat");

        var found = Assert.Single(LocationFinder.Find(Game("MyGame", Save("<winDocuments>/MyGame/profile.dat")), pc.Context()));

        Assert.Equal("<winDocuments>/MyGame", found.RootTemplate);
        Assert.Equal(["profile.dat"], found.Includes);
    }

    [Fact]
    public void StoreUserIdWildcard_KeepsOneFolderAndGlobsBelowIt()
    {
        using var pc = new FakePc();
        pc.File("Users/tester/AppData/Local/G/76561/saves/one.sav");
        pc.File("Users/tester/AppData/Local/G/99999/saves/two.sav");

        var found = Assert.Single(LocationFinder.Find(Game("G", Save("<winLocalAppData>/G/<storeUserId>/saves")), pc.Context()));

        Assert.Equal("<winLocalAppData>/G", found.RootTemplate);
        Assert.Equal(["*/saves", "*/saves/**"], found.Includes);
        Assert.Equal(2, found.FileCount);
    }

    [Fact]
    public void StarOnly_IsNormalizedToDoubleStar()
    {
        using var pc = new FakePc();
        pc.File("Users/tester/AppData/Local/G/x/a.sav");
        var found = Assert.Single(LocationFinder.Find(Game("G", Save("<winLocalAppData>/G/*")), pc.Context()));
        Assert.Equal(["**"], found.Includes);
    }

    [Fact]
    public void MissingFolder_GivesNothing()
    {
        using var pc = new FakePc();
        Assert.Empty(LocationFinder.Find(Game("G", Save("<winAppData>/Nope")), pc.Context()));
    }

    [Fact]
    public void WildcardWithoutMatchingFiles_GivesNothing_AlsoWhenQuick()
    {
        using var pc = new FakePc();
        pc.File("Users/tester/Documents/G/readme.txt");
        var game = Game("G", Save("<winDocuments>/G/*.sav"));
        Assert.Empty(LocationFinder.Find(game, pc.Context()));
        Assert.Empty(LocationFinder.Find(game, pc.Context(), quick: true));
    }

    [Fact]
    public void ConfigPaths_AreOptional()
    {
        using var pc = new FakePc();
        pc.File("Users/tester/AppData/Roaming/G/saves/a.sav");
        pc.File("Users/tester/AppData/Roaming/G/config/c.ini");
        var game = Game("G", Save("<winAppData>/G/saves"), Config("<winAppData>/G/config"));

        var saveOnly = LocationFinder.Find(game, pc.Context(), includeConfig: false);
        Assert.Single(saveOnly);

        var both = LocationFinder.Find(game, pc.Context(), includeConfig: true);
        Assert.Equal(2, both.Count);
        Assert.True(both[0].Save);
        Assert.False(both[1].Save);
    }

    [Fact]
    public void FolderInsideAnotherFullFolder_IsDropped()
    {
        using var pc = new FakePc();
        pc.File("Users/tester/Documents/G/saves/a.sav");
        var game = Game("G", Save("<winDocuments>/G"), Save("<winDocuments>/G/saves"));

        var found = Assert.Single(LocationFinder.Find(game, pc.Context()));
        Assert.Equal("<winDocuments>/G", found.RootTemplate);
    }

    [Fact]
    public void BaseTemplate_NeedsTheGameInstalledUnderALibraryRoot()
    {
        using var pc = new FakePc();
        var root = pc.Dir("Games");
        pc.File("Games/Hero/save/slot.sav");
        var game = Game("Hero", Save("<base>/save"));

        Assert.Empty(LocationFinder.Find(game, pc.Context()));
        var found = Assert.Single(LocationFinder.Find(game, pc.Context([root])));
        Assert.Equal("<base>/save", found.RootTemplate);
        Assert.Equal(Path.Combine(root, "Hero", "save"), found.AbsoluteRoot);
    }

    [Fact]
    public void SteamOnlyPaths_AreNotUsedHere()
    {
        using var pc = new FakePc();
        var root = pc.Dir("Steam");
        pc.File("Steam/userdata/1/111/remote/a.sav");
        var game = Game("S", new PathRule("<root>/userdata/<storeUserId>/111/remote", Save: true, Config: false, SteamOnly: true));
        Assert.Empty(LocationFinder.Find(game, pc.Context([root])));
    }

    [Fact]
    public void DetectInstalled_ListsOnlyGamesPresentOnThisPc()
    {
        using var pc = new FakePc();
        pc.File("Users/tester/Documents/Present/save.sav");
        var index = new ManifestIndex([
            Game("Present", Save("<winDocuments>/Present")),
            Game("Absent", Save("<winDocuments>/Absent")),
        ]);

        var found = GameDetector.DetectInstalled(index, pc.Context());

        Assert.Equal(["Present"], found.Select(g => g.Name));
    }

    [Fact]
    public void SuggestProcessNames_PrefersExecutablesThatExistInTheInstallFolder()
    {
        using var pc = new FakePc();
        var root = pc.Dir("Games");
        pc.File("Games/Hero/bin/hero64.exe");
        var game = new GameRecord
        {
            Name = "Hero",
            InstallDirs = ["Hero"],
            Executables = ["hero.exe", "hero64.exe", "unins000.exe", "setup.exe"],
            Paths = [Save("<base>/save")],
        };

        Assert.Equal(["hero64"], GameDetector.SuggestProcessNames(game, [root]));
        Assert.Equal(["hero", "hero64"], GameDetector.SuggestProcessNames(game, []));
    }
}
