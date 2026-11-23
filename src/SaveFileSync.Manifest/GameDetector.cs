using System.Collections.Concurrent;
using SaveFileSync.Core.Paths;

namespace SaveFileSync.Manifest;

/// <summary>Finds the games of the manifest that are present on this PC (PLAN.md §7.3).</summary>
public static class GameDetector
{
    /// <summary>
    /// Games with at least one save folder (not Steam's) that exists here. Runs in parallel and
    /// is meant for a background task; <paramref name="ct"/> stops it early.
    /// </summary>
    public static IReadOnlyList<GameRecord> DetectInstalled(ManifestIndex index, ResolveContext ctx, CancellationToken ct = default)
    {
        var cache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var found = new ConcurrentBag<GameRecord>();
        Parallel.ForEach(
            index.Games,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            game =>
            {
                try
                {
                    if (LocationFinder.Find(game, ctx, includeConfig: false, existsCache: cache, quick: true).Count > 0)
                    {
                        found.Add(game);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable folder just means "not detected".
                }
            });

        return found.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Install folders of a game that exist under the library roots.</summary>
    public static IReadOnlyList<string> FindInstallFolders(GameRecord game, IReadOnlyList<string> libraryRoots) =>
        libraryRoots
            .SelectMany(root => game.InstallDirs.Select(dir => Path.Combine(root, dir)))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Process names to suggest for a game (without ".exe"). When the game is installed under a
    /// library root, only executables that really exist there are suggested.
    /// </summary>
    public static IReadOnlyList<string> SuggestProcessNames(GameRecord game, IReadOnlyList<string> libraryRoots)
    {
        var candidates = game.Executables
            .Where(e => !IsHelper(e))
            .Select(e => Path.GetFileNameWithoutExtension(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var installs = FindInstallFolders(game, libraryRoots);
        if (installs.Count == 0)
        {
            return candidates;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        var present = installs
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.exe", options))
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existing = candidates.Where(present.Contains).ToList();
        return existing.Count > 0 ? existing : candidates;
    }

    private static bool IsHelper(string exe)
    {
        var n = Path.GetFileNameWithoutExtension(exe);
        return n.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
               || n.Contains("setup", StringComparison.OrdinalIgnoreCase)
               || n.Contains("install", StringComparison.OrdinalIgnoreCase)
               || n.Contains("crashreport", StringComparison.OrdinalIgnoreCase);
    }
}
