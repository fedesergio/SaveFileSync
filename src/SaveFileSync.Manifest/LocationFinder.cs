using System.Collections.Concurrent;
using SaveFileSync.Core.IO;
using SaveFileSync.Core.Paths;

namespace SaveFileSync.Manifest;

/// <summary>A save folder found on this PC for a manifest game.</summary>
/// <param name="RootTemplate">Template of the folder, stored in the repository (no absolute paths).</param>
/// <param name="AbsoluteRoot">The folder on this PC; it exists.</param>
/// <param name="Includes">Globs relative to the folder (<c>**</c> for everything).</param>
/// <param name="Label">Short name for the GUI (last folder name).</param>
/// <param name="Save">Tagged "save" (progress); otherwise only "config".</param>
public sealed record LocationCandidate(
    string RootTemplate,
    string AbsoluteRoot,
    IReadOnlyList<string> Includes,
    string Label,
    bool Save,
    int FileCount,
    long Bytes);

/// <summary>
/// Turns the paths of a manifest game into save folders that exist on this PC
/// (PLAN.md §7.1). A template is split at its first wildcard: the part before it becomes
/// the location's folder, the rest becomes include globs, so a location always has one folder.
/// </summary>
public static class LocationFinder
{
    private const int MaxEntriesPerLocation = 20_000;

    /// <param name="includeConfig">Also return paths tagged only "config".</param>
    /// <param name="existsCache">Shared cache of folder lookups, for scanning many games.</param>
    /// <param name="quick">Stop at the first hit and skip counting files (detection only).</param>
    public static IReadOnlyList<LocationCandidate> Find(
        GameRecord game,
        ResolveContext ctx,
        bool includeConfig = true,
        ConcurrentDictionary<string, bool>? existsCache = null,
        bool quick = false)
    {
        var withInstall = new ResolveContext { Folders = ctx.Folders, LibraryRoots = ctx.LibraryRoots, InstallDirs = game.InstallDirs };
        var found = new List<LocationCandidate>();

        foreach (var rule in game.Paths)
        {
            if (rule.SteamOnly || (!rule.Save && !includeConfig))
            {
                continue;
            }

            var (rootTemplate, relative) = PathTemplate.SplitAtWildcard(rule.Template);
            foreach (var root in PathTemplate.Expand(rootTemplate, withInstall))
            {
                var absolute = root.Replace('/', Path.DirectorySeparatorChar);
                if (relative is null)
                {
                    if (DirExists(absolute, existsCache))
                    {
                        found.Add(Candidate(rootTemplate, absolute, ["**"], rule.Save, quick));
                    }
                    else if (!quick && File.Exists(absolute) && PathTemplate.Parent(rootTemplate) is { } parentTemplate)
                    {
                        var parent = Path.GetDirectoryName(absolute)!;
                        found.Add(Candidate(parentTemplate, parent, [Path.GetFileName(absolute)], rule.Save, quick));
                    }
                    else if (quick && File.Exists(absolute))
                    {
                        found.Add(new LocationCandidate(rootTemplate, absolute, ["**"], PathTemplate.LastSegment(rootTemplate), rule.Save, 1, 0));
                    }
                }
                else if (DirExists(absolute, existsCache))
                {
                    // "*" and "**" both mean "everything below": one include is enough.
                    var includes = relative is "*" or "**"
                        ? ["**"]
                        : new[] { relative, relative.EndsWith("**", StringComparison.Ordinal) ? relative : relative + "/**" }.Distinct().ToList();

                    if (quick)
                    {
                        if (HasAnyMatch(absolute, includes))
                        {
                            found.Add(new LocationCandidate(rootTemplate, absolute, includes, PathTemplate.LastSegment(rootTemplate), rule.Save, 1, 0));
                        }
                    }
                    else
                    {
                        var candidate = Candidate(rootTemplate, absolute, includes, rule.Save, quick: false);
                        if (candidate.FileCount > 0)
                        {
                            found.Add(candidate);
                        }
                    }
                }

                if (quick && found.Count > 0)
                {
                    return found;
                }
            }
        }

        return quick ? found : Merge(found);
    }

    /// <summary>
    /// Count and total size of the files that a location would sync; stops after a
    /// bounded number of entries so a huge folder cannot stall the GUI.
    /// </summary>
    public static (int Files, long Bytes) Measure(string absoluteRoot, IReadOnlyList<string> includes, IReadOnlyList<string>? excludes = null)
    {
        var filter = new PathFilter(includes, excludes ?? []);
        var files = 0;
        long bytes = 0;
        var seen = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
        foreach (var file in new DirectoryInfo(absoluteRoot).EnumerateFiles("*", options))
        {
            if (++seen > MaxEntriesPerLocation)
            {
                break;
            }

            var relative = Path.GetRelativePath(absoluteRoot, file.FullName).Replace('\\', '/');
            if (filter.IsIncluded(relative))
            {
                files++;
                bytes += file.Length;
            }
        }

        return (files, bytes);
    }

    /// <summary>True if at least one file matches; stops at the first match or after a bounded number of entries.</summary>
    internal static bool HasAnyMatch(string absoluteRoot, IReadOnlyList<string> includes)
    {
        var filter = new PathFilter(includes, []);
        var seen = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
        foreach (var file in new DirectoryInfo(absoluteRoot).EnumerateFiles("*", options))
        {
            if (++seen > MaxEntriesPerLocation)
            {
                return false;
            }

            if (filter.IsIncluded(Path.GetRelativePath(absoluteRoot, file.FullName).Replace('\\', '/')))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Candidates with the same folder are merged (union of includes); a folder inside
    /// another one that already takes everything is dropped.
    /// </summary>
    internal static IReadOnlyList<LocationCandidate> Merge(IReadOnlyList<LocationCandidate> candidates)
    {
        var merged = candidates
            .GroupBy(c => c.AbsoluteRoot, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var includes = g.SelectMany(c => c.Includes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                // With several rules for one folder the counts must be measured again, not added up.
                var (files, bytes) = g.Count() == 1 ? (g.First().FileCount, g.First().Bytes) : Measure(g.Key, includes);
                return new LocationCandidate(g.First().RootTemplate, g.Key, includes, g.First().Label, g.Any(c => c.Save), files, bytes);
            })
            .ToList();

        return merged
            .Where(c => !merged.Any(other =>
                !ReferenceEquals(other, c)
                && other.Includes.Contains("**")
                && IsInside(c.AbsoluteRoot, other.AbsoluteRoot)))
            .OrderByDescending(c => c.Save)
            .ThenBy(c => c.AbsoluteRoot, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static LocationCandidate Candidate(string template, string absolute, IReadOnlyList<string> includes, bool save, bool quick)
    {
        var (files, bytes) = quick ? (1, 0L) : Measure(absolute, includes);
        return new LocationCandidate(template, absolute, includes, PathTemplate.LastSegment(template), save, files, bytes);
    }

    private static bool DirExists(string path, ConcurrentDictionary<string, bool>? cache) =>
        cache is null ? Directory.Exists(path) : cache.GetOrAdd(path, static p => Directory.Exists(p));

    private static bool IsInside(string path, string parent)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var q = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.Length > q.Length && p.StartsWith(q, StringComparison.OrdinalIgnoreCase);
    }
}
