using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using SaveFileSync.Core.Paths;

namespace SaveFileSync.Manifest;

// DTOs for the Ludusavi manifest schema (data/schema.yaml of mtkennerly/ludusavi-manifest).
// Only the fields this app needs are mapped; everything else is ignored.
internal sealed class RawWhen
{
    public string? Os { get; set; }

    public string? Store { get; set; }
}

internal sealed class RawPath
{
    public List<string>? Tags { get; set; }

    public List<RawWhen>? When { get; set; }
}

internal sealed class RawLaunch
{
    public List<RawWhen>? When { get; set; }
}

internal sealed class RawSteam
{
    public int? Id { get; set; }
}

internal sealed class RawGog
{
    public int? Id { get; set; }
}

internal sealed class RawEntry
{
    public Dictionary<string, RawPath?>? Files { get; set; }

    public Dictionary<string, RawPath?>? Registry { get; set; }

    public Dictionary<string, List<RawLaunch>?>? Launch { get; set; }

    public Dictionary<string, object?>? InstallDir { get; set; }

    public RawSteam? Steam { get; set; }

    public RawGog? Gog { get; set; }

    public string? Alias { get; set; }
}

/// <summary>
/// Streams the manifest entry by entry (it is ~17 MB) and keeps only what applies to
/// Windows without a Game Pass store (PLAN.md §7.3). Never holds the whole document in memory.
/// </summary>
public static class ManifestParser
{
    public static IReadOnlyList<GameRecord> Parse(TextReader reader)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var parser = new Parser(reader);
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        parser.Consume<MappingStart>();

        var games = new List<GameRecord>();
        var aliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var name = parser.Consume<Scalar>().Value;
            var raw = deserializer.Deserialize<RawEntry?>(parser);
            if (raw is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(raw.Alias))
            {
                if (!aliases.TryGetValue(raw.Alias, out var list))
                {
                    aliases[raw.Alias] = list = [];
                }

                list.Add(name);
                continue;
            }

            var record = Convert(name, raw);
            if (record is not null)
            {
                games.Add(record);
            }
        }

        // Attach alias names to the games they point to.
        // Names can differ only by case in the real manifest, so the lookup must tolerate duplicates.
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < games.Count; i++)
        {
            byName.TryAdd(games[i].Name, i);
        }

        foreach (var (target, names) in aliases)
        {
            if (byName.TryGetValue(target, out var index))
            {
                var g = games[index];
                games[index] = new GameRecord
                {
                    Name = g.Name,
                    SteamId = g.SteamId,
                    GogId = g.GogId,
                    InstallDirs = g.InstallDirs,
                    Executables = g.Executables,
                    Paths = g.Paths,
                    RegistryKeys = g.RegistryKeys,
                    Aliases = g.Aliases.Concat(names).ToList(),
                };
            }
        }

        return games;
    }

    private static GameRecord? Convert(string name, RawEntry raw)
    {
        var paths = new List<PathRule>();
        foreach (var (template, rule) in raw.Files ?? [])
        {
            if (!AppliesToWindows(rule?.When) || PathTemplate.IsForeignToken(template))
            {
                continue;
            }

            var tags = rule?.Tags ?? [];
            var save = tags.Contains("save");
            var config = tags.Contains("config");
            if (!save && !config)
            {
                continue;
            }

            var normalized = PathTemplate.Normalize(template);
            var steamOnly = normalized.StartsWith("<root>/userdata", StringComparison.OrdinalIgnoreCase)
                            || normalized.StartsWith("<root>/steamapps", StringComparison.OrdinalIgnoreCase);
            paths.Add(new PathRule(normalized, save, config, steamOnly));
        }

        var registry = (raw.Registry ?? [])
            .Where(kv => AppliesToWindows(kv.Value?.When))
            .Select(kv => kv.Key.Replace('\\', '/'))
            .ToList();

        if (paths.Count == 0 && registry.Count == 0)
        {
            return null;
        }

        var executables = new List<string>();
        foreach (var (template, launches) in raw.Launch ?? [])
        {
            var applies = launches is null or { Count: 0 } || launches.Any(l => AppliesToWindows(l.When));
            var file = PathTemplate.LastSegment(template);
            if (applies && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !file.Contains('*'))
            {
                executables.Add(file);
            }
        }

        return new GameRecord
        {
            Name = name,
            SteamId = raw.Steam?.Id,
            GogId = raw.Gog?.Id,
            InstallDirs = (raw.InstallDir ?? []).Keys.ToList(),
            Executables = executables.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Paths = paths,
            RegistryKeys = registry,
        };
    }

    /// <summary>
    /// "when" items are alternatives; within an item all conditions must hold. An entry
    /// applies to us if some alternative allows Windows and is not only for the Microsoft
    /// store (Game Pass has its own cloud). Store constraints are otherwise ignored: a
    /// DRM-free copy usually keeps its saves where the Steam/GOG copy does.
    /// </summary>
    internal static bool AppliesToWindows(List<RawWhen>? when)
    {
        if (when is null or { Count: 0 })
        {
            return true;
        }

        return when.Any(w =>
            (w.Os is null || w.Os.Equals("windows", StringComparison.OrdinalIgnoreCase))
            && !string.Equals(w.Store, "microsoft", StringComparison.OrdinalIgnoreCase));
    }
}
