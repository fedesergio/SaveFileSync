using SaveFileSync.Core.Model;
using SaveFileSync.Core.Paths;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace SaveFileSync.Manifest;

/// <summary>
/// An emulator whose save area is not described by the manifest. Presets are written by us
/// (PLAN.md §7.3); the unit keeps only logical locations, and each PC finds its own folders.
/// </summary>
public abstract class EmulatorPreset
{
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    /// <summary>Name of the emulator's exe, used to find its folder.</summary>
    public abstract string ExeName { get; }

    public abstract IReadOnlyList<string> ProcessNames { get; }

    public abstract MergeGranularity Granularity { get; }

    public abstract IReadOnlyList<LocationDefinition> Locations { get; }

    /// <summary>One-line description of what is synced, for the GUI.</summary>
    public abstract string Summary { get; }

    /// <summary>
    /// Location id → folder on this PC for an emulator folder (may be null for emulators that keep
    /// their data in AppData). Returns null when the data folders cannot be found. The folders
    /// need not contain anything yet, but their parent must exist.
    /// </summary>
    public abstract IReadOnlyDictionary<string, string>? Bind(string? emulatorDir, KnownFolderSet folders);

    public UnitSource Source => new() { Kind = UnitSource.PresetKind, PresetId = Id };

    /// <summary>
    /// Folders that contain the emulator's exe: the one saved in the settings, the one of a
    /// running instance, then the library folders (and one level below them).
    /// </summary>
    public IReadOnlyList<string> FindEmulatorDirs(
        IReadOnlyDictionary<string, string>? saved,
        IReadOnlyList<string> libraryRoots,
        IProcessPaths? processes)
    {
        var found = new List<string>();
        void Add(string? dir)
        {
            if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, ExeName)) && !found.Contains(dir, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(dir);
            }
        }

        if (saved is not null && saved.TryGetValue(Id, out var savedDir))
        {
            Add(savedDir);
        }

        foreach (var name in ProcessNames)
        {
            Add(processes?.FindFolder(name));
        }

        foreach (var root in libraryRoots.Where(Directory.Exists))
        {
            Add(root);
            foreach (var child in SafeDirectories(root))
            {
                Add(child);
                if (Path.GetFileName(child).Contains("emulator", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var grandChild in SafeDirectories(child))
                    {
                        Add(grandChild);
                    }
                }
            }
        }

        return found;
    }

    internal static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

public static class EmulatorPresets
{
    public static readonly EmulatorPreset Rpcs3 = new Rpcs3Preset();

    public static readonly EmulatorPreset Ryujinx = new RyujinxPreset();

    public static IReadOnlyList<EmulatorPreset> All { get; } = [Rpcs3, Ryujinx];

    public static EmulatorPreset? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// RPCS3 (PS3): <c>dev_hdd0\home\&lt;user&gt;\savedata\&lt;SAVE_DIR&gt;</c> — one self-contained
/// folder per save, so changes are merged per save folder. The location of <c>dev_hdd0</c>
/// follows <c>config\vfs.yml</c>. Trophies are not synced by default.
/// </summary>
internal sealed class Rpcs3Preset : EmulatorPreset
{
    public const string DefaultUser = "00000001";

    public override string Id => "rpcs3";

    public override string DisplayName => "RPCS3 (PlayStation 3)";

    public override string ExeName => "rpcs3.exe";

    public override IReadOnlyList<string> ProcessNames => ["rpcs3"];

    public override MergeGranularity Granularity => MergeGranularity.Folder;

    public override string Summary => "Save data of every game (dev_hdd0\\home\\<user>\\savedata). Saves of different games merge automatically.";

    public override IReadOnlyList<LocationDefinition> Locations { get; } =
        [new LocationDefinition { Id = "savedata", Label = "RPCS3 savedata", Includes = ["**"] }];

    public override IReadOnlyDictionary<string, string>? Bind(string? emulatorDir, KnownFolderSet folders)
    {
        if (string.IsNullOrWhiteSpace(emulatorDir) || !Directory.Exists(emulatorDir))
        {
            return null;
        }

        var hdd0 = ResolveHdd0(emulatorDir);
        var home = Path.Combine(hdd0, "home");
        if (!Directory.Exists(home))
        {
            return null;
        }

        var users = SafeDirectories(home).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList();
        var user = users.FirstOrDefault(u => u.Equals(DefaultUser, StringComparison.OrdinalIgnoreCase)) ?? users.FirstOrDefault();
        return user is null
            ? null
            : new Dictionary<string, string> { ["savedata"] = Path.Combine(home, user, "savedata") };
    }

    /// <summary>The folder mapped to <c>/dev_hdd0/</c> in <c>config\vfs.yml</c>; <c>dev_hdd0</c> next to the exe by default.</summary>
    internal static string ResolveHdd0(string emulatorDir)
    {
        var fallback = Path.Combine(emulatorDir, "dev_hdd0");
        var vfs = Path.Combine(emulatorDir, "config", "vfs.yml");
        if (!File.Exists(vfs))
        {
            return fallback;
        }

        try
        {
            var map = new DeserializerBuilder().Build().Deserialize<Dictionary<string, string?>>(File.ReadAllText(vfs));
            if (map is null || !map.TryGetValue("/dev_hdd0/", out var value) || string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var emulatorDirValue = map.TryGetValue("$(EmulatorDir)", out var ed) && !string.IsNullOrWhiteSpace(ed) ? ed! : emulatorDir;
            var expanded = value.Replace("$(EmulatorDir)", emulatorDirValue.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                .Replace('/', Path.DirectorySeparatorChar);
            var full = Path.IsPathRooted(expanded) ? expanded : Path.Combine(emulatorDir, expanded);
            return Path.GetFullPath(full).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is YamlException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return fallback;
        }
    }
}

/// <summary>
/// Ryujinx (Switch): saves live under <c>bis\user\save</c> and are tied to <c>bis\user\saveMeta</c>
/// and the save index in <c>bis\system\save\8000000000000000</c>, so everything is one unit.
/// The data folder is <c>%APPDATA%\Ryujinx</c>, or <c>portable</c> next to the exe if it exists.
/// This layout is an assumption to check on real data (PLAN.md A6).
/// </summary>
internal sealed class RyujinxPreset : EmulatorPreset
{
    public override string Id => "ryujinx";

    public override string DisplayName => "Ryujinx (Nintendo Switch)";

    public override string ExeName => "Ryujinx.exe";

    public override IReadOnlyList<string> ProcessNames => ["ryujinx", "ryujinx.ava"];

    public override MergeGranularity Granularity => MergeGranularity.Unit;

    public override string Summary => "Switch saves, their metadata, the save index and user profiles (bis\\user\\save, saveMeta, bis\\system\\save\\8000000000000000, system\\Profiles.json). Always handled as one whole.";

    public override IReadOnlyList<LocationDefinition> Locations { get; } =
    [
        new LocationDefinition
        {
            Id = "data",
            Label = "Ryujinx data",
            Includes =
            [
                "bis/user/save/**",
                "bis/user/saveMeta/**",
                "bis/system/save/8000000000000000/**",
                "system/Profiles.json",
            ],
        },
    ];

    public override IReadOnlyDictionary<string, string>? Bind(string? emulatorDir, KnownFolderSet folders)
    {
        if (!string.IsNullOrWhiteSpace(emulatorDir))
        {
            var portable = Path.Combine(emulatorDir, "portable");
            if (Directory.Exists(portable))
            {
                return new Dictionary<string, string> { ["data"] = portable };
            }
        }

        var appData = Path.Combine(folders.AppData, "Ryujinx");
        return Directory.Exists(appData) ? new Dictionary<string, string> { ["data"] = appData } : null;
    }
}
