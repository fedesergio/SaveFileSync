using SaveFileSync.Core.Config;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Paths;
using SaveFileSync.Manifest;

namespace SaveFileSync.Steam.Tests;

public class VdfTests
{
    [Fact]
    public void ParsesNestedBlocks_Comments_AndEscapes()
    {
        var root = Vdf.TryParse("""
            // comment
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "220" "123" } }
                "1" { "path" "D:\\SteamLibrary" }
            }
            """)!;

        var folders = root["libraryfolders"]!;
        Assert.Equal(@"C:\Program Files (x86)\Steam", folders["0"]!.String("path"));
        Assert.Equal("123", folders["0"]!["apps"]!.String("220"));
        Assert.Equal(@"D:\SteamLibrary", folders["1"]!.String("path"));
    }

    [Theory]
    [InlineData("\"a\" {")]
    [InlineData("\"a\" \"b")]
    [InlineData("}")]
    [InlineData("a b")]
    public void Garbage_ReturnsNull_NotAnException(string text) => Assert.Null(Vdf.TryParse(text));
}

public sealed class SteamFolder : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sfs-steam-" + Guid.NewGuid().ToString("N")[..8]);

    public string Steam => Path.Combine(Root, "Steam");

    public string Library2 => Path.Combine(Root, "SteamLibrary");

    public SteamFolder()
    {
        Directory.CreateDirectory(Path.Combine(Steam, "steamapps", "common"));
        Directory.CreateDirectory(Path.Combine(Library2, "steamapps", "common"));
        File.WriteAllText(
            Path.Combine(Steam, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\" { \"0\" { \"path\" \"" + Steam.Replace("\\", "\\\\") + "\" } \"1\" { \"path\" \"" + Library2.Replace("\\", "\\\\") + "\" } }");
    }

    public void App(string library, int id, string name, string installDir)
    {
        File.WriteAllText(
            Path.Combine(library, "steamapps", $"appmanifest_{id}.acf"),
            $"\"AppState\" {{ \"appid\" \"{id}\" \"name\" \"{name}\" \"installdir\" \"{installDir}\" }}");
        Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", installDir));
    }

    public string Remote(string account, int appId, string file, string content = "x")
    {
        var path = Path.Combine(Steam, "userdata", account, appId.ToString(), "remote", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class SteamInstallTests
{
    [Fact]
    public void FindsLibraries_Apps_AndAccounts()
    {
        using var f = new SteamFolder();
        f.App(f.Steam, 1145360, "Hades", "Hades");
        f.App(f.Library2, 367520, "Hollow Knight", "Hollow Knight");
        f.Remote("123456", 1145360, "Profile1.sav");
        Directory.CreateDirectory(Path.Combine(f.Steam, "userdata", "0"));
        Directory.CreateDirectory(Path.Combine(f.Steam, "userdata", "anonymous"));

        var steam = SteamInstall.Discover(f.Steam)!;

        Assert.Equal(2, steam.Libraries.Count);
        Assert.Equal(["Hades", "Hollow Knight"], steam.Apps.Select(a => a.Name));
        Assert.Equal(Path.Combine(f.Library2, "steamapps", "common", "Hollow Knight"), steam.Apps[1].InstallPath);
        Assert.Equal(["123456"], steam.Accounts.Select(a => a.Id));
    }

    [Fact]
    public void NotSteam_IsNull()
    {
        Assert.Null(SteamInstall.Discover(null));
        Assert.Null(SteamInstall.Discover(Path.Combine(Path.GetTempPath(), "definitely-not-steam-" + Guid.NewGuid())));
    }

    [Fact]
    public void ABrokenAppManifest_IsSkipped()
    {
        using var f = new SteamFolder();
        f.App(f.Steam, 1, "Good", "Good");
        File.WriteAllText(Path.Combine(f.Steam, "steamapps", "appmanifest_2.acf"), "\"AppState\" { \"appid\" ");
        Assert.Equal(["Good"], SteamInstall.Discover(f.Steam)!.Apps.Select(a => a.Name));
    }
}

public class SteamBackupFinderTests
{
    private static KnownFolderSet Folders(string root) => new()
    {
        Home = root, AppData = Path.Combine(root, "AppData"), LocalAppData = Path.Combine(root, "Local"),
        LocalAppDataLow = Path.Combine(root, "LocalLow"), Documents = Path.Combine(root, "Docs"), Public = root,
        ProgramData = root, WinDir = root, UserName = "u",
    };

    private static GameRecord Game(string name, int steamId, params PathRule[] rules) => new()
    {
        Name = name, SteamId = steamId, InstallDirs = [name], Executables = [name + ".exe"], Paths = rules,
    };

    [Fact]
    public void CloudFolder_IsFound_ForInstalledAndUninstalledGames()
    {
        using var f = new SteamFolder();
        f.App(f.Steam, 1145360, "Hades", "Hades");
        f.Remote("111", 1145360, "Profile1.sav");
        f.Remote("111", 999, "slot.dat"); // not installed any more, saves still in userdata
        f.Remote("111", 760, "screenshot.jpg"); // Steam's screenshots are not a game
        var steam = SteamInstall.Discover(f.Steam)!;

        var found = SteamBackupFinder.Find(steam, [], Folders(f.Root));

        Assert.Equal(["Hades", "Steam app 999"], found.Select(c => c.Name));
        var hades = found[0];
        Assert.True(hades.Installed);
        Assert.True(hades.ConfirmedByCloud);
        var location = Assert.Single(hades.Draft.Locations);
        Assert.Equal("<steamUserdata>/1145360/remote", location.Templates!.Single());
        Assert.Equal(Path.Combine(f.Steam, "userdata", "111", "1145360", "remote"), hades.Draft.Bindings[location.Id]);
        Assert.Equal(UnitSource.SteamKind, hades.Draft.Source.Kind);
        Assert.Equal(1145360, hades.Draft.Source.SteamId);
        Assert.False(found[1].Installed);
    }

    [Fact]
    public void AutoCloudGames_UseTheManifestFolder_ConfirmedByRemoteCache()
    {
        using var f = new SteamFolder();
        f.App(f.Library2, 42, "Some Game", "Some Game");
        var saves = Path.Combine(f.Library2, "steamapps", "common", "Some Game", "saves");
        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(saves, "slot1.sav"), "data");
        var cache = Path.Combine(f.Steam, "userdata", "111", "42");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "remotecache.vdf"), "\"42\" { \"ChangeNumber\" \"7\" \"saves/slot1.sav\" { \"root\" \"2\" \"size\" \"4\" } }");
        var steam = SteamInstall.Discover(f.Steam)!;
        var game = Game("Some Game", 42, new PathRule("<base>/saves", Save: true, Config: false, SteamOnly: false));

        var candidate = Assert.Single(SteamBackupFinder.Find(steam, [game], Folders(f.Root)));

        Assert.True(candidate.ConfirmedByCloud);
        Assert.Equal(["Some Game"], candidate.ProcessNames);
        var location = Assert.Single(candidate.Draft.Locations);
        Assert.Equal(saves, candidate.Draft.Bindings[location.Id]);
        Assert.All(candidate.Draft.Locations.SelectMany(l => l.Templates!), t => Assert.DoesNotContain(f.Root, t));
    }

    [Fact]
    public void ManifestFolderNotTrackedBySteam_IsStillOffered_ButNotConfirmed()
    {
        using var f = new SteamFolder();
        f.App(f.Steam, 42, "Some Game", "Some Game");
        var saves = Path.Combine(f.Steam, "steamapps", "common", "Some Game", "saves");
        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(saves, "a.sav"), "data");
        var game = Game("Some Game", 42, new PathRule("<base>/saves", true, false, false));

        var candidate = Assert.Single(SteamBackupFinder.Find(SteamInstall.Discover(f.Steam)!, [game], Folders(f.Root)));

        Assert.False(candidate.ConfirmedByCloud);
    }

    [Fact]
    public void GamesWithoutAnySaves_AreNotListed()
    {
        using var f = new SteamFolder();
        f.App(f.Steam, 5, "Empty", "Empty");
        Assert.Empty(SteamBackupFinder.Find(SteamInstall.Discover(f.Steam)!, [], Folders(f.Root)));
    }

    [Fact]
    public void WithSeveralAccounts_TheMostRecentlyUsedOneIsBound()
    {
        using var f = new SteamFolder();
        f.App(f.Steam, 5, "G", "G");
        var old = f.Remote("111", 5, "a.sav");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        f.Remote("222", 5, "a.sav");

        var candidate = Assert.Single(SteamBackupFinder.Find(SteamInstall.Discover(f.Steam)!, [], Folders(f.Root)));

        Assert.Contains(Path.Combine("userdata", "222"), candidate.Draft.Bindings.Values.Single());
    }

    [Fact]
    public void Templates_ResolveOnAnotherPc_OnlyWhenTheAccountIsUnique()
    {
        using var f = new SteamFolder();
        f.App(f.Steam, 1145360, "Hades", "Hades");
        f.Remote("111", 1145360, "Profile1.sav");
        var steam = SteamInstall.Discover(f.Steam)!;
        var draft = SteamBackupFinder.Find(steam, [], Folders(f.Root)).Single().Draft;
        var unit = new UnitDefinition
        {
            UnitId = "u", Name = "Hades", Locations = draft.Locations, Source = draft.Source, Mode = UnitMode.BackupOnly,
        };
        var config = new AppConfig();
        var resolver = new LocationResolver(() => Folders(f.Root), () => config, null,
            () => (steam.CommonDirs.ToList(), steam.UserdataDirs.ToList()));

        var bound = resolver.TryResolve(unit)!;
        Assert.Equal(draft.Bindings.Values.Single(), bound.Values.Single());

        // A second account that also has this game: ambiguous, the user must choose.
        f.Remote("222", 1145360, "Profile1.sav");
        var steam2 = SteamInstall.Discover(f.Steam)!;
        var ambiguous = new LocationResolver(() => Folders(f.Root), () => config, null,
            () => (steam2.CommonDirs.ToList(), steam2.UserdataDirs.ToList()));
        Assert.Null(ambiguous.TryResolve(unit));
    }
}
