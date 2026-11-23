using SaveFileSync.Core.Config;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Paths;
using SaveFileSync.Core.Service;
using SaveFileSync.Manifest;

namespace SaveFileSync.Simulation.Tests;

/// <summary>
/// Phase 2: a game added on one PC is found and set up on another PC that has a different
/// user name, drive and Documents location, without anyone choosing a path (PLAN.md §7).
/// </summary>
public sealed class AutoSetupTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private static KnownFolderSet Folders(string root, string user, string? documents = null) => new()
    {
        Home = Path.Combine(root, "Users", user),
        AppData = Path.Combine(root, "Users", user, "AppData", "Roaming"),
        LocalAppData = Path.Combine(root, "Users", user, "AppData", "Local"),
        LocalAppDataLow = Path.Combine(root, "Users", user, "AppData", "LocalLow"),
        Documents = documents ?? Path.Combine(root, "Users", user, "Documents"),
        Public = Path.Combine(root, "Users", "Public"),
        ProgramData = Path.Combine(root, "ProgramData"),
        WinDir = Path.Combine(root, "Windows"),
        UserName = user,
    };

    private static void Tick(TestPc pc, TimeSpan advance = default)
    {
        pc.Clock.Advance(advance);
        pc.Service.Tick();
    }

    [Fact]
    public void SecondPc_FindsTheGameItself_AndReceivesTheSaves()
    {
        var desktop = _h.CreatePc("desktop");
        var folders = Folders(Path.Combine(_h.Root, "desktop-disk"), "federico");
        Directory.CreateDirectory(Path.Combine(folders.AppData, "Hero", "saves"));
        File.WriteAllText(Path.Combine(folders.AppData, "Hero", "saves", "slot1.sav"), "desktop-save");

        var game = new GameRecord
        {
            Name = "Hero",
            InstallDirs = ["Hero"],
            Paths = [new PathRule("<winAppData>/Hero/saves", Save: true, Config: false, SteamOnly: false)],
            RegistryKeys = ["HKEY_CURRENT_USER/Software/Hero"],
        };
        var ctx = new ResolveContext { Folders = folders };
        var draft = UnitFactory.FromGame(game, LocationFinder.Find(game, ctx));
        var unitId = desktop.Service.AddUnitAsync(draft.Name, draft.Locations, [], draft.Bindings, source: draft.Source).Await(desktop);
        Tick(desktop);
        Assert.Equal(UnitState.Synced, desktop.Status(unitId).State);
        Assert.True(desktop.Status(unitId).HasRegistryData);

        // The ally has another user name and its Documents elsewhere; the game was never set up there.
        var ally = _h.CreatePc("ally");
        var allyFolders = Folders(Path.Combine(_h.Root, "ally-disk"), "gamer", documents: Path.Combine(_h.Root, "ally-docs"));
        ally.Resolver = new LocationResolver(() => allyFolders, () => ally.Config.Snapshot());

        // Nothing there yet: the game stays "available" and nothing is created or downloaded.
        Tick(ally);
        Assert.Equal(UnitState.NotSetUp, ally.Status(unitId).State);

        // The game is installed and has a save folder: the next check binds it by itself.
        Directory.CreateDirectory(Path.Combine(allyFolders.AppData, "Hero", "saves"));
        Tick(ally);
        Tick(ally, TimeSpan.FromMinutes(3));

        Assert.Equal(UnitState.Synced, ally.Status(unitId).State);
        Assert.Equal("desktop-save", File.ReadAllText(Path.Combine(allyFolders.AppData, "Hero", "saves", "slot1.sav")));
        Assert.Equal(Path.Combine(allyFolders.AppData, "Hero", "saves"), ally.Config.Snapshot().Bindings[unitId]["saves"]);
        Assert.Contains(ally.Service.RecentActivity, a => a.Message.Contains("automatically", StringComparison.OrdinalIgnoreCase));
        Assert.True(ally.Status(unitId).HasRegistryData);
    }

    [Fact]
    public void ExistingDifferentSaves_AreNeverOverwritten_ButBecomeAConflict()
    {
        var desktop = _h.CreatePc("desktop");
        var folders = Folders(Path.Combine(_h.Root, "desktop-disk"), "a");
        Directory.CreateDirectory(Path.Combine(folders.AppData, "Hero"));
        File.WriteAllText(Path.Combine(folders.AppData, "Hero", "slot.sav"), "desktop");
        var game = new GameRecord { Name = "Hero", Paths = [new PathRule("<winAppData>/Hero", true, false, false)] };
        var draft = UnitFactory.FromGame(game, LocationFinder.Find(game, new ResolveContext { Folders = folders }));
        var unitId = desktop.Service.AddUnitAsync(draft.Name, draft.Locations, [], draft.Bindings, source: draft.Source).Await(desktop);
        Tick(desktop);

        var ally = _h.CreatePc("ally");
        var allyFolders = Folders(Path.Combine(_h.Root, "ally-disk"), "b");
        Directory.CreateDirectory(Path.Combine(allyFolders.AppData, "Hero"));
        File.WriteAllText(Path.Combine(allyFolders.AppData, "Hero", "slot.sav"), "older-ally-data");
        ally.Resolver = new LocationResolver(() => allyFolders, () => ally.Config.Snapshot());

        Tick(ally);
        Tick(ally, TimeSpan.FromMinutes(3));

        Assert.Equal(UnitState.Conflict, ally.Status(unitId).State);
        Assert.Equal("older-ally-data", File.ReadAllText(Path.Combine(allyFolders.AppData, "Hero", "slot.sav")));
    }

    [Fact]
    public void AutoSetupCanBeSwitchedOff()
    {
        var desktop = _h.CreatePc("desktop");
        var folders = Folders(Path.Combine(_h.Root, "desktop-disk"), "a");
        Directory.CreateDirectory(Path.Combine(folders.AppData, "Hero"));
        File.WriteAllText(Path.Combine(folders.AppData, "Hero", "slot.sav"), "x");
        var game = new GameRecord { Name = "Hero", Paths = [new PathRule("<winAppData>/Hero", true, false, false)] };
        var draft = UnitFactory.FromGame(game, LocationFinder.Find(game, new ResolveContext { Folders = folders }));
        var unitId = desktop.Service.AddUnitAsync(draft.Name, draft.Locations, [], draft.Bindings, source: draft.Source).Await(desktop);
        Tick(desktop);

        var ally = _h.CreatePc("ally");
        var allyFolders = Folders(Path.Combine(_h.Root, "ally-disk"), "b");
        Directory.CreateDirectory(Path.Combine(allyFolders.AppData, "Hero"));
        ally.Config.Update(c => c.AutoSetup = false);
        ally.Resolver = new LocationResolver(() => allyFolders, () => ally.Config.Snapshot());

        Tick(ally);
        Tick(ally, TimeSpan.FromMinutes(3));

        Assert.Equal(UnitState.NotSetUp, ally.Status(unitId).State);
        Assert.False(ally.Config.Snapshot().Bindings.ContainsKey(unitId));
    }

    [Fact]
    public void EmulatorPreset_IsFoundOnTheSecondPc_AndMergesPerSaveFolder()
    {
        var desktop = _h.CreatePc("desktop");
        var dDir = Path.Combine(_h.Root, "desktop-disk", "RPCS3");
        var dSave = Path.Combine(dDir, "dev_hdd0", "home", "00000001", "savedata");
        Directory.CreateDirectory(Path.Combine(dSave, "GAME1-SAVE"));
        File.WriteAllText(Path.Combine(dDir, "rpcs3.exe"), "");
        File.WriteAllText(Path.Combine(dSave, "GAME1-SAVE", "SAVE.BIN"), "g1");

        var preset = EmulatorPresets.Rpcs3;
        var unitId = desktop.Service.AddUnitAsync(
            "RPCS3 saves", preset.Locations, preset.ProcessNames,
            new Dictionary<string, string> { ["savedata"] = dSave }, preset.Granularity, preset.Source).Await(desktop);
        Tick(desktop);

        var ally = _h.CreatePc("ally");
        var aRoot = Path.Combine(_h.Root, "ally-disk", "Games");
        var aDir = Path.Combine(aRoot, "RPCS3");
        var aSave = Path.Combine(aDir, "dev_hdd0", "home", "00000001", "savedata");
        Directory.CreateDirectory(aSave);
        File.WriteAllText(Path.Combine(aDir, "rpcs3.exe"), "");
        ally.Config.Update(c => c.LibraryRoots = [aRoot]);
        ally.Resolver = new LocationResolver(() => Folders(Path.Combine(_h.Root, "ally-disk"), "b"), () => ally.Config.Snapshot());

        Tick(ally);
        Tick(ally, TimeSpan.FromMinutes(3));
        Assert.Equal("g1", File.ReadAllText(Path.Combine(aSave, "GAME1-SAVE", "SAVE.BIN")));

        // Different saves on each PC merge by themselves.
        Directory.CreateDirectory(Path.Combine(dSave, "GAME2-SAVE"));
        File.WriteAllText(Path.Combine(dSave, "GAME2-SAVE", "SAVE.BIN"), "from-desktop");
        Directory.CreateDirectory(Path.Combine(aSave, "GAME3-SAVE"));
        File.WriteAllText(Path.Combine(aSave, "GAME3-SAVE", "SAVE.BIN"), "from-ally");
        desktop.Service.NotifyLocalChange(unitId);
        ally.Service.NotifyLocalChange(unitId);
        Tick(desktop, TimeSpan.FromMinutes(3));
        Tick(ally, TimeSpan.FromMinutes(3));
        Tick(desktop, TimeSpan.FromMinutes(3));
        Tick(ally, TimeSpan.FromMinutes(3));

        Assert.Equal("from-desktop", File.ReadAllText(Path.Combine(aSave, "GAME2-SAVE", "SAVE.BIN")));
        Assert.Equal("from-ally", File.ReadAllText(Path.Combine(dSave, "GAME3-SAVE", "SAVE.BIN")));
    }
}
