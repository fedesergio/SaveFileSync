using SaveFileSync.Core.Config;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Paths;
using static SaveFileSync.Manifest.Tests.Games;

namespace SaveFileSync.Manifest.Tests;

/// <summary>Synthetic folder trees that follow PLAN.md §7.3; real emulator layouts are verified by hand (A6, A7).</summary>
public class PresetTests
{
    [Fact]
    public void Rpcs3_DefaultLayout_UsesDevHdd0NextToTheExe()
    {
        using var pc = new FakePc();
        var dir = pc.Dir("Emu/RPCS3");
        pc.File("Emu/RPCS3/rpcs3.exe");
        pc.File("Emu/RPCS3/dev_hdd0/home/00000001/savedata/BLUS30443-AUTOSAVE/SAVE.BIN");

        var bound = EmulatorPresets.Rpcs3.Bind(dir, pc.Folders)!;

        Assert.Equal(Path.Combine(dir, "dev_hdd0", "home", "00000001", "savedata"), bound["savedata"]);
        Assert.Equal(MergeGranularity.Folder, EmulatorPresets.Rpcs3.Granularity);
    }

    [Fact]
    public void Rpcs3_VfsYmlRedirectsDevHdd0()
    {
        using var pc = new FakePc();
        var dir = pc.Dir("Emu/RPCS3");
        var other = pc.Dir("Data/ps3hdd");
        pc.Dir("Data/ps3hdd/home/00000001");
        pc.File("Emu/RPCS3/config/vfs.yml", $"$(EmulatorDir): \"\"\n/dev_hdd0/: \"{other.Replace('\\', '/')}/\"\n/dev_bdvd/: \"\"\n");

        var bound = EmulatorPresets.Rpcs3.Bind(dir, pc.Folders)!;

        Assert.Equal(Path.Combine(other, "home", "00000001", "savedata"), bound["savedata"]);
    }

    [Fact]
    public void Rpcs3_VfsYmlWithEmulatorDirVariable()
    {
        using var pc = new FakePc();
        var dir = pc.Dir("Emu/RPCS3");
        pc.Dir("Emu/RPCS3/custom_hdd/home/00000001");
        pc.File("Emu/RPCS3/config/vfs.yml", "/dev_hdd0/: \"$(EmulatorDir)custom_hdd/\"\n");

        var bound = EmulatorPresets.Rpcs3.Bind(dir, pc.Folders)!;

        Assert.Equal(Path.Combine(dir, "custom_hdd", "home", "00000001", "savedata"), bound["savedata"]);
    }

    [Fact]
    public void Rpcs3_BrokenVfsYml_FallsBackToTheDefault()
    {
        using var pc = new FakePc();
        var dir = pc.Dir("Emu/RPCS3");
        pc.Dir("Emu/RPCS3/dev_hdd0/home/00000001");
        pc.File("Emu/RPCS3/config/vfs.yml", "{{{ not yaml");

        Assert.NotNull(EmulatorPresets.Rpcs3.Bind(dir, pc.Folders));
    }

    [Fact]
    public void Rpcs3_PicksTheDefaultUser_OrTheOnlyOne()
    {
        using var pc = new FakePc();
        var dir = pc.Dir("Emu/RPCS3");
        pc.Dir("Emu/RPCS3/dev_hdd0/home/00000002");
        Assert.EndsWith("00000002\\savedata", EmulatorPresets.Rpcs3.Bind(dir, pc.Folders)!["savedata"]);

        pc.Dir("Emu/RPCS3/dev_hdd0/home/00000001");
        Assert.EndsWith("00000001\\savedata", EmulatorPresets.Rpcs3.Bind(dir, pc.Folders)!["savedata"]);
    }

    [Fact]
    public void Rpcs3_NoHome_CannotBeBound()
    {
        using var pc = new FakePc();
        Assert.Null(EmulatorPresets.Rpcs3.Bind(pc.Dir("Emu/RPCS3"), pc.Folders));
        Assert.Null(EmulatorPresets.Rpcs3.Bind(null, pc.Folders));
    }

    [Fact]
    public void Ryujinx_UsesAppData_OrPortableNextToTheExe()
    {
        using var pc = new FakePc();
        var exeDir = pc.Dir("Emu/Ryujinx");
        Assert.Null(EmulatorPresets.Ryujinx.Bind(exeDir, pc.Folders));

        var appData = pc.Dir("Users/tester/AppData/Roaming/Ryujinx");
        Assert.Equal(appData, EmulatorPresets.Ryujinx.Bind(exeDir, pc.Folders)!["data"]);
        Assert.Equal(appData, EmulatorPresets.Ryujinx.Bind(null, pc.Folders)!["data"]);

        var portable = pc.Dir("Emu/Ryujinx/portable");
        Assert.Equal(portable, EmulatorPresets.Ryujinx.Bind(exeDir, pc.Folders)!["data"]);
    }

    [Fact]
    public void Ryujinx_IsOneWholeUnit_WithTheSaveIndex()
    {
        var preset = EmulatorPresets.Ryujinx;
        Assert.Equal(MergeGranularity.Unit, preset.Granularity);
        var includes = preset.Locations.Single().Includes;
        Assert.Contains("bis/user/save/**", includes);
        Assert.Contains("bis/user/saveMeta/**", includes);
        Assert.Contains("bis/system/save/8000000000000000/**", includes);
        Assert.DoesNotContain(includes, i => i.Contains("keys", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindEmulatorDirs_SearchesSavedFolder_LibraryRootsAndOneLevelDown()
    {
        using var pc = new FakePc();
        var saved = pc.Dir("Somewhere/RPCS3");
        pc.File("Somewhere/RPCS3/rpcs3.exe");
        var root = pc.Dir("Games");
        pc.File("Games/rpcs3-portable/rpcs3.exe");
        pc.File("Games/Emulators/Other/rpcs3.exe");
        pc.File("Games/Hero/hero.exe");

        var found = EmulatorPresets.Rpcs3.FindEmulatorDirs(
            new Dictionary<string, string> { ["rpcs3"] = saved }, [root], processes: null);

        Assert.Equal(saved, found[0]);
        Assert.Contains(Path.Combine(root, "rpcs3-portable"), found);
        Assert.Contains(Path.Combine(root, "Emulators", "Other"), found);
        Assert.DoesNotContain(Path.Combine(root, "Hero"), found);
    }

    private sealed class RunningRpcs3(string folder) : IProcessPaths
    {
        public string? FindFolder(string processName) => processName == "rpcs3" ? folder : null;
    }

    [Fact]
    public void FindEmulatorDirs_UsesARunningInstance()
    {
        using var pc = new FakePc();
        var dir = pc.Dir("Anywhere/RPCS3");
        pc.File("Anywhere/RPCS3/rpcs3.exe");

        Assert.Equal([dir], EmulatorPresets.Rpcs3.FindEmulatorDirs(null, [], new RunningRpcs3(dir)));
    }
}

public class ResolverTests
{
    private static LocationResolver Resolver(FakePc pc, AppConfig? config = null) =>
        new(() => pc.Folders, () => config ?? new AppConfig());

    private static UnitDefinition Unit(UnitSource? source, params LocationDefinition[] locations) => new()
    {
        UnitId = "u",
        Name = "Game",
        Locations = locations,
        Source = source,
    };

    private static LocationDefinition Location(string id, params string[] templates) => new() { Id = id, Templates = templates };

    private static readonly UnitSource ManifestSource = new() { Kind = UnitSource.ManifestKind, InstallDirs = ["Hero"] };

    [Fact]
    public void ResolvesTemplatesOnAnotherPcWithDifferentUserAndDrive()
    {
        // The unit was created on a PC with another user name; templates carry no such detail.
        using var pc = new FakePc();
        pc.Dir("Users/tester/AppData/Roaming/Hero/saves");
        var unit = Unit(ManifestSource, Location("saves", "<winAppData>/Hero/saves"));

        var bound = Resolver(pc).TryResolve(unit)!;

        Assert.Equal(pc.Dir("Users/tester/AppData/Roaming/Hero/saves"), bound["saves"]);
    }

    [Fact]
    public void ResolvesBaseTemplatesThroughLibraryRoots()
    {
        using var pc = new FakePc();
        var root = pc.Dir("D-games");
        pc.Dir("D-games/Hero/save");
        var unit = Unit(ManifestSource, Location("save", "<base>/save"));

        var bound = Resolver(pc, new AppConfig { LibraryRoots = [root] }).TryResolve(unit)!;

        Assert.Equal(Path.Combine(root, "Hero", "save"), bound["save"]);
    }

    [Fact]
    public void MissingFolder_OrAmbiguousFolders_OrManualLocations_ReturnNull()
    {
        using var pc = new FakePc();
        var missing = Unit(ManifestSource, Location("saves", "<winAppData>/Hero/saves"));
        Assert.Null(Resolver(pc).TryResolve(missing));

        var rootA = pc.Dir("A");
        var rootB = pc.Dir("B");
        pc.Dir("A/Hero/save");
        pc.Dir("B/Hero/save");
        var ambiguous = Unit(ManifestSource, Location("save", "<base>/save"));
        Assert.Null(Resolver(pc, new AppConfig { LibraryRoots = [rootA, rootB] }).TryResolve(ambiguous));

        var manual = Unit(null, new LocationDefinition { Id = "saves" });
        Assert.Null(Resolver(pc).TryResolve(manual));
    }

    [Fact]
    public void EveryLocationMustResolve()
    {
        using var pc = new FakePc();
        pc.Dir("Users/tester/AppData/Roaming/Hero/saves");
        var unit = Unit(ManifestSource, Location("saves", "<winAppData>/Hero/saves"), Location("cfg", "<winAppData>/Hero/cfg"));
        Assert.Null(Resolver(pc).TryResolve(unit));
    }

    [Fact]
    public void PresetUnits_AreResolvedByTheEmulatorLogic()
    {
        using var pc = new FakePc();
        var root = pc.Dir("Games");
        pc.File("Games/RPCS3/rpcs3.exe");
        pc.Dir("Games/RPCS3/dev_hdd0/home/00000001/savedata");
        var unit = Unit(EmulatorPresets.Rpcs3.Source, EmulatorPresets.Rpcs3.Locations.ToArray());

        var bound = Resolver(pc, new AppConfig { LibraryRoots = [root] }).TryResolve(unit)!;

        Assert.EndsWith("00000001\\savedata", bound["savedata"]);
    }

    [Fact]
    public void PresetWithoutExistingSaveFolder_IsNotAutoBound()
    {
        using var pc = new FakePc();
        var root = pc.Dir("Games");
        pc.File("Games/RPCS3/rpcs3.exe");
        pc.Dir("Games/RPCS3/dev_hdd0/home/00000001"); // no savedata yet
        var unit = Unit(EmulatorPresets.Rpcs3.Source, EmulatorPresets.Rpcs3.Locations.ToArray());

        Assert.Null(Resolver(pc, new AppConfig { LibraryRoots = [root] }).TryResolve(unit));
    }

    [Fact]
    public void UnitFactory_StoresTemplatesNotAbsolutePaths()
    {
        using var pc = new FakePc();
        pc.File("Users/tester/AppData/Roaming/Hero/saves/a.sav");
        pc.File("Users/tester/Documents/Hero/profile.dat");
        var game = new GameRecord
        {
            Name = "Hero",
            SteamId = 7,
            InstallDirs = ["Hero"],
            Paths = [Save("<winAppData>/Hero/saves"), Save("<winDocuments>/Hero/profile.dat")],
            RegistryKeys = ["HKEY_CURRENT_USER/Software/Hero"],
        };

        var draft = UnitFactory.FromGame(game, LocationFinder.Find(game, pc.Context()));

        Assert.Equal(2, draft.Locations.Count);
        Assert.All(draft.Locations, l => Assert.DoesNotContain(pc.Root, string.Join(";", l.Templates!)));
        Assert.All(draft.Locations, l => Assert.DoesNotContain("tester", string.Join(";", l.Templates!)));
        Assert.Equal(2, draft.Bindings.Count);
        Assert.Equal(UnitSource.ManifestKind, draft.Source.Kind);
        Assert.Equal(1, draft.Source.RegistryKeys);
        Assert.Equal(["Hero"], draft.Source.InstallDirs);
        Assert.Equal(draft.Locations.Select(l => l.Id).Distinct().Count(), draft.Locations.Count);
    }
}
