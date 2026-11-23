using SaveFileSync.Core.Model;

namespace SaveFileSync.Manifest;

/// <summary>What the "Add game" window needs to create a unit from a manifest game.</summary>
public sealed record UnitDraft(
    string Name,
    IReadOnlyList<LocationDefinition> Locations,
    IReadOnlyDictionary<string, string> Bindings,
    UnitSource Source);

public static class UnitFactory
{
    /// <summary>
    /// Locations (ids unique within the unit), their bindings on this PC, and the source
    /// info that lets another PC find the same folders (PLAN.md §7).
    /// </summary>
    public static UnitDraft FromGame(GameRecord game, IReadOnlyList<LocationCandidate> chosen)
    {
        var locations = new List<LocationDefinition>();
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in chosen)
        {
            var id = MakeId(c.Label, locations.Select(l => l.Id));
            locations.Add(new LocationDefinition
            {
                Id = id,
                Label = c.Label,
                Includes = c.Includes,
                Templates = [c.RootTemplate],
            });
            bindings[id] = c.AbsoluteRoot;
        }

        return new UnitDraft(
            game.Name,
            locations,
            bindings,
            new UnitSource
            {
                Kind = UnitSource.ManifestKind,
                ManifestName = game.Name,
                SteamId = game.SteamId,
                InstallDirs = game.InstallDirs,
                RegistryKeys = game.RegistryKeys.Count,
            });
    }

    /// <summary>Short stable id from a folder name ("SaveGames" → "savegames", "savegames-2").</summary>
    public static string MakeId(string name, IEnumerable<string> taken)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0)
        {
            slug = "saves";
        }

        if (slug.Length > 24)
        {
            slug = slug[..24].TrimEnd('-');
        }

        var set = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = slug;
        for (var i = 2; set.Contains(id); i++)
        {
            id = $"{slug}-{i}";
        }

        return id;
    }
}
