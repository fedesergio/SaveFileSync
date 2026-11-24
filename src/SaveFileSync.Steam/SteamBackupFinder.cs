using SaveFileSync.Core.Model;
using SaveFileSync.Core.Paths;
using SaveFileSync.Manifest;

namespace SaveFileSync.Steam;

/// <summary>A Steam game with saves that can be backed up (read-only).</summary>
/// <param name="ConfirmedByCloud">At least one found file is listed in Steam's own <c>remotecache.vdf</c>.</param>
public sealed record SteamCandidate(
    int AppId,
    string Name,
    UnitDraft Draft,
    IReadOnlyList<string> ProcessNames,
    bool Installed,
    bool ConfirmedByCloud,
    int FileCount)
{
    public string Where => string.Join(", ", Draft.Locations.Select(l => l.Label ?? l.Id));
}

/// <summary>
/// Finds what to back up for each Steam game (PLAN.md §11, phase 5):
/// 1. <c>userdata\&lt;account&gt;\&lt;appid&gt;\remote</c>: files uploaded through the Steam Cloud API;
/// 2. save folders from the Ludusavi manifest (matched by Steam id), cross-checked with <c>remotecache.vdf</c>,
///    which lists the files Steam itself tracks (this covers Auto-Cloud games).
/// </summary>
public static class SteamBackupFinder
{
    public const string CloudLocationId = "steam-cloud";

    /// <summary>Steam's own folders in <c>userdata</c>: the client (7), screenshots (760) and controller configs (241100), not game saves.</summary>
    private static readonly HashSet<int> NotGames = [0, 7, 760, 241100];

    public static IReadOnlyList<SteamCandidate> Find(SteamInstall steam, IReadOnlyList<GameRecord> manifestGames, KnownFolderSet folders)
    {
        var bySteamId = new Dictionary<int, GameRecord>();
        foreach (var game in manifestGames)
        {
            if (game.SteamId is { } id)
            {
                bySteamId.TryAdd(id, game);
            }
        }

        var installed = steam.Apps.ToDictionary(a => a.AppId);
        var ids = new HashSet<int>(installed.Keys);
        foreach (var account in steam.Accounts)
        {
            foreach (var dir in SafeDirectories(account.UserdataDir))
            {
                if (int.TryParse(Path.GetFileName(dir), out var appId) && !NotGames.Contains(appId))
                {
                    ids.Add(appId);
                }
            }
        }

        var result = new List<SteamCandidate>();
        foreach (var appId in ids.Where(i => !NotGames.Contains(i)).Order())
        {
            installed.TryGetValue(appId, out var app);
            bySteamId.TryGetValue(appId, out var game);
            if (Candidate(steam, appId, app, game, folders) is { } candidate)
            {
                result.Add(candidate);
            }
        }

        return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SteamCandidate? Candidate(SteamInstall steam, int appId, SteamApp? app, GameRecord? game, KnownFolderSet folders)
    {
        var name = app?.Name ?? game?.Name ?? $"Steam app {appId}";
        var locations = new List<LocationDefinition>();
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = 0;

        // 1. The Steam Cloud API folder: the account with the most recently changed files wins.
        var cloud = steam.Accounts
            .Select(a => (Account: a, Dir: Path.Combine(a.UserdataDir, appId.ToString(), "remote")))
            .Where(x => Directory.Exists(x.Dir))
            .Select(x => (x.Account, x.Dir, Info: Measure(x.Dir)))
            .Where(x => x.Info.Count > 0)
            .OrderByDescending(x => x.Info.Newest)
            .FirstOrDefault();
        if (cloud.Dir is not null)
        {
            locations.Add(new LocationDefinition
            {
                Id = CloudLocationId,
                Label = "Steam Cloud",
                Includes = ["**"],
                Templates = [$"<steamUserdata>/{appId}/remote"],
            });
            bindings[CloudLocationId] = cloud.Dir;
            files += cloud.Info.Count;
        }

        foreach (var account in steam.Accounts)
        {
            foreach (var key in RemoteCacheFiles(Path.Combine(account.UserdataDir, appId.ToString(), "remotecache.vdf"), appId))
            {
                tracked.Add(key);
            }
        }

        // 2. Folders the manifest knows for this game (Auto-Cloud saves live in game-defined places).
        var confirmed = false;
        if (game is not null)
        {
            var dirs = app is null ? game.InstallDirs : [app.InstallDir, .. game.InstallDirs];
            var withInstall = new GameRecord
            {
                Name = game.Name,
                SteamId = game.SteamId,
                GogId = game.GogId,
                InstallDirs = dirs,
                Executables = game.Executables,
                Paths = game.Paths,
                RegistryKeys = game.RegistryKeys,
                Aliases = game.Aliases,
            };
            var ctx = new ResolveContext { Folders = folders, LibraryRoots = steam.CommonDirs.ToList(), InstallDirs = dirs };
            var found = LocationFinder.Find(withInstall, ctx, includeConfig: false);
            if (found.Count > 0)
            {
                var draft = UnitFactory.FromGame(withInstall, found);
                foreach (var location in draft.Locations)
                {
                    var id = location.Id == CloudLocationId ? "game-saves" : location.Id;
                    locations.Add(new LocationDefinition { Id = id, Label = location.Label, Includes = location.Includes, Templates = location.Templates });
                    bindings[id] = draft.Bindings[location.Id];
                }

                files += found.Sum(f => f.FileCount);
                confirmed = tracked.Count > 0 && found.Any(f => ContainsTracked(f.AbsoluteRoot, tracked));
            }
        }

        if (locations.Count == 0)
        {
            return null;
        }

        var source = new UnitSource
        {
            Kind = UnitSource.SteamKind,
            ManifestName = game?.Name,
            SteamId = appId,
            InstallDirs = app is null ? [] : [app.InstallDir],
            RegistryKeys = game?.RegistryKeys.Count ?? 0,
        };
        var processes = game?.Executables.Select(e => Path.GetFileNameWithoutExtension(e)).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        return new SteamCandidate(appId, name, new UnitDraft(name, locations, bindings, source), processes, app is not null, confirmed || cloud.Dir is not null, files);
    }

    private static (int Count, DateTime Newest) Measure(string dir)
    {
        var count = 0;
        var newest = DateTime.MinValue;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                count++;
                var time = File.GetLastWriteTimeUtc(file);
                if (time > newest)
                {
                    newest = time;
                }

                if (count >= 5000)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (count, newest);
    }

    /// <summary>File names (relative paths) Steam tracks for this app, from <c>remotecache.vdf</c>.</summary>
    internal static IEnumerable<string> RemoteCacheFiles(string path, int appId)
    {
        var node = Vdf.TryParseFile(path)?[appId.ToString()];
        if (node is null)
        {
            return [];
        }

        // Entries are "<relative path>" { root, size, ... }; the app node also has plain values (ChangeNumber...).
        return node.Children.Where(c => c.Value.Children.Count > 0).Select(c => c.Key.Replace('\\', '/'));
    }

    private static bool ContainsTracked(string root, HashSet<string> tracked)
    {
        var names = tracked.Select(t => t[(t.LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(3000).Any(f => names.Contains(Path.GetFileName(f)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
