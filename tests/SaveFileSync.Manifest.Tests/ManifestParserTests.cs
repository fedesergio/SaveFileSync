namespace SaveFileSync.Manifest.Tests;

/// <summary>
/// Parser tests on a small synthetic manifest written for these tests; it follows the
/// documented schema. Real manifest data is never copied into the repository.
/// </summary>
public class ManifestParserTests
{
    private const string Yaml = """
        "Alpha Quest":
          files:
            "<winAppData>/AlphaQuest/saves":
              tags: [save]
              when:
                - os: windows
            "<winAppData>/AlphaQuest/settings.ini":
              tags: [config]
            "<xdgData>/alphaquest":
              tags: [save]
              when:
                - os: linux
            "<home>/Library/Application Support/AlphaQuest":
              tags: [save]
              when:
                - os: mac
            "<root>/userdata/<storeUserId>/111/remote":
              tags: [save]
              when:
                - os: windows
                  store: steam
            "<base>/cache":
              tags: []
          registry:
            HKEY_CURRENT_USER/Software/AlphaQuest:
              tags: [save]
          installDir:
            AlphaQuest: {}
          launch:
            "<base>/AlphaQuest.exe":
              - when:
                  - os: windows
            "<base>/setup.exe":
              - when:
                  - os: windows
            "<base>/AlphaQuest.sh":
              - when:
                  - os: linux
          steam:
            id: 111
          gog:
            id: 222
        "Game Pass Only":
          files:
            "<winLocalAppData>/Packages/GamePassOnly":
              tags: [save]
              when:
                - os: windows
                  store: microsoft
        "Nothing Useful":
          steam:
            id: 333
        "Alpha Quest Classic":
          alias: Alpha Quest
        "Babbdi":
          files:
            "<winDocuments>/Babbdi":
              tags: [save]
        "babbdi":
          files:
            "<winDocuments>/babbdi":
              tags: [save]
        "Registry Only":
          registry:
            HKEY_CURRENT_USER/Software/RegistryOnly:
              tags: [save]
        """;

    private static IReadOnlyList<GameRecord> Parse() => ManifestParser.Parse(new StringReader(Yaml));

    [Fact]
    public void KeepsOnlyGamesWithWindowsData()
    {
        var names = Parse().Select(g => g.Name).ToList();
        Assert.Contains("Alpha Quest", names);
        Assert.Contains("Registry Only", names);
        Assert.DoesNotContain("Nothing Useful", names);
        Assert.DoesNotContain("Game Pass Only", names);
    }

    [Fact]
    public void KeepsWindowsPathsAndDropsOtherPlatforms()
    {
        var alpha = Parse().Single(g => g.Name == "Alpha Quest");
        Assert.Equal(
            ["<winAppData>/AlphaQuest/saves", "<winAppData>/AlphaQuest/settings.ini", "<root>/userdata/<storeUserId>/111/remote"],
            alpha.Paths.Select(p => p.Template));
    }

    [Fact]
    public void TagsAndSteamOnlyFlagAreRecorded()
    {
        var alpha = Parse().Single(g => g.Name == "Alpha Quest");
        var saves = alpha.Paths.Single(p => p.Template.EndsWith("/saves", StringComparison.Ordinal));
        var settings = alpha.Paths.Single(p => p.Template.EndsWith("settings.ini", StringComparison.Ordinal));
        var steam = alpha.Paths.Single(p => p.Template.StartsWith("<root>/userdata", StringComparison.Ordinal));
        Assert.True(saves.Save);
        Assert.False(saves.Config);
        Assert.False(settings.Save);
        Assert.True(settings.Config);
        Assert.True(steam.SteamOnly);
        Assert.False(saves.SteamOnly);
    }

    [Fact]
    public void ReadsIdsInstallDirsRegistryAndExecutables()
    {
        var alpha = Parse().Single(g => g.Name == "Alpha Quest");
        Assert.Equal(111, alpha.SteamId);
        Assert.Equal(222, alpha.GogId);
        Assert.Equal(["AlphaQuest"], alpha.InstallDirs);
        Assert.Equal(["HKEY_CURRENT_USER/Software/AlphaQuest"], alpha.RegistryKeys);
        Assert.Equal(["AlphaQuest.exe", "setup.exe"], alpha.Executables);
    }

    [Fact]
    public void RegistryOnlyGameIsKept()
    {
        var game = Parse().Single(g => g.Name == "Registry Only");
        Assert.Empty(game.Paths);
        Assert.Single(game.RegistryKeys);
    }

    [Fact]
    public void AliasesAttachToTheirTarget()
    {
        var games = Parse();
        Assert.DoesNotContain(games, g => g.Name == "Alpha Quest Classic");
        Assert.Equal(["Alpha Quest Classic"], games.Single(g => g.Name == "Alpha Quest").Aliases);
    }

    [Fact]
    public void NamesDifferingOnlyByCase_DoNotBreakParsing()
    {
        var games = Parse();
        Assert.Contains(games, g => g.Name == "Babbdi");
        Assert.Contains(games, g => g.Name == "babbdi");
    }

    [Fact]
    public void EmptyDocumentGivesNoGames()
    {
        Assert.Empty(ManifestParser.Parse(new StringReader("{}")));
    }
}
