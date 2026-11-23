using SaveFileSync.Core.Config;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Paths;

namespace SaveFileSync.Manifest;

/// <summary>
/// Finds the save folders of a unit on this PC (PLAN.md §7): from its templates for games,
/// and through the emulator's own logic for presets.
/// </summary>
public sealed class LocationResolver : ILocationResolver
{
    private readonly Func<KnownFolderSet> _folders;
    private readonly Func<AppConfig> _config;
    private readonly IProcessPaths? _processes;

    public LocationResolver(Func<KnownFolderSet> folders, Func<AppConfig> config, IProcessPaths? processes = null)
    {
        _folders = folders;
        _config = config;
        _processes = processes;
    }

    public ResolveContext CreateContext(IReadOnlyList<string>? installDirs = null) => new()
    {
        Folders = _folders(),
        LibraryRoots = _config().LibraryRoots,
        InstallDirs = installDirs ?? [],
    };

    public IReadOnlyDictionary<string, string>? TryResolve(UnitDefinition unit)
    {
        if (unit.Locations.Count == 0)
        {
            return null;
        }

        var found = unit.Source?.Kind == UnitSource.PresetKind ? ResolvePreset(unit) : ResolveTemplates(unit);
        if (found is null || unit.Locations.Any(l => !found.TryGetValue(l.Id, out var p) || !Directory.Exists(p)))
        {
            return null;
        }

        return found;
    }

    /// <summary>Folders of an emulator preset on this PC, trying the emulator folder if known.</summary>
    public IReadOnlyDictionary<string, string>? BindPreset(EmulatorPreset preset, string? emulatorDir)
    {
        var config = _config();
        var dirs = emulatorDir is not null
            ? [emulatorDir]
            : preset.FindEmulatorDirs(config.EmulatorDirs, config.LibraryRoots, _processes);

        // Several emulator folders: use the first that yields data; Ryujinx may also live in AppData.
        foreach (var dir in dirs)
        {
            if (preset.Bind(dir, _folders()) is { } bound)
            {
                return bound;
            }
        }

        return preset.Bind(null, _folders());
    }

    private IReadOnlyDictionary<string, string>? ResolvePreset(UnitDefinition unit)
    {
        var preset = EmulatorPresets.Find(unit.Source?.PresetId);
        return preset is null ? null : BindPreset(preset, emulatorDir: null);
    }

    private IReadOnlyDictionary<string, string>? ResolveTemplates(UnitDefinition unit)
    {
        var ctx = CreateContext(unit.Source?.InstallDirs);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var location in unit.Locations)
        {
            if (location.Templates is not { Count: > 0 } templates)
            {
                return null;
            }

            var dirs = templates
                .SelectMany(t => PathTemplate.Expand(t, ctx))
                .Select(p => p.Replace('/', Path.DirectorySeparatorChar))
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Zero or several candidates: let the user choose.
            if (dirs.Count != 1)
            {
                return null;
            }

            result[location.Id] = dirs[0];
        }

        return result;
    }
}
