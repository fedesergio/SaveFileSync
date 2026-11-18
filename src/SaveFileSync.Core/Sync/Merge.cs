using SaveFileSync.Core.Model;

namespace SaveFileSync.Core.Sync;

/// <summary>Result of a three-way merge: the merged files and the groups that conflict.</summary>
public sealed record MergeResult(Snapshot Merged, IReadOnlyList<string> ConflictGroups)
{
    public bool HasConflicts => ConflictGroups.Count > 0;
}

/// <summary>
/// Three-way merge by groups (PLAN.md §6.4). Files are grouped by the unit's granularity;
/// each group is taken from the side that changed it, and is a conflict when both sides
/// changed it differently (modify-versus-delete included).
/// </summary>
public static class Merge
{
    /// <summary>Group key of a file: the whole unit, its first-level folder, or the file itself.</summary>
    public static string GroupOf(FileEntry file, MergeGranularity granularity) => granularity switch
    {
        MergeGranularity.Unit => "",
        MergeGranularity.Folder => file.Loc + "/" + (file.Path.IndexOf('/') is var i and > 0 ? file.Path[..i] : file.Path),
        _ => file.Key,
    };

    /// <summary>
    /// Three-way merge of <paramref name="lineage"/> (whose content is <paramref name="ours"/>)
    /// with version <paramref name="theirsId"/>, using the graph to find the merge base.
    /// With several best common ancestors that disagree on a group, that group has no
    /// reliable base: it is a conflict unless both sides agree.
    /// </summary>
    public static MergeResult WithGraph(VersionGraph graph, IEnumerable<string> lineage, Snapshot ours, string theirsId, MergeGranularity granularity)
    {
        var theirs = graph.Get(theirsId)!.Content;
        var bases = graph.MaximalCommonAncestors(lineage, theirsId);
        if (bases.Count <= 1)
        {
            return ThreeWay(bases.Count == 0 ? Snapshot.Empty : bases[0].Content, ours, theirs, granularity);
        }

        var perBase = bases.Select(b => GroupFiles(b.Content, granularity)).ToList();
        var groups = perBase.SelectMany(g => g.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var ids = perBase.Select(g => (g.GetValueOrDefault(group) ?? Snapshot.Empty).ContentId).Distinct().Count();
            if (ids > 1)
            {
                ambiguous.Add(group);
            }
        }

        return ThreeWay(bases[0].Content, ours, theirs, granularity, ambiguous);
    }

    public static MergeResult ThreeWay(
        Snapshot baseContent,
        Snapshot ours,
        Snapshot theirs,
        MergeGranularity granularity,
        IReadOnlySet<string>? ambiguousGroups = null)
    {
        var baseGroups = GroupFiles(baseContent, granularity);
        var ourGroups = GroupFiles(ours, granularity);
        var theirGroups = GroupFiles(theirs, granularity);

        var merged = new List<FileEntry>();
        var conflicts = new List<string>();
        var allGroups = baseGroups.Keys.Concat(ourGroups.Keys).Concat(theirGroups.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var group in allGroups)
        {
            var b = baseGroups.GetValueOrDefault(group) ?? Snapshot.Empty;
            var o = ourGroups.GetValueOrDefault(group) ?? Snapshot.Empty;
            var t = theirGroups.GetValueOrDefault(group) ?? Snapshot.Empty;

            if (o.ContentEquals(t))
            {
                merged.AddRange(o.Files);
            }
            else if (ambiguousGroups?.Contains(group) == true)
            {
                conflicts.Add(group);
                merged.AddRange(o.Files);
            }
            else if (t.ContentEquals(b))
            {
                merged.AddRange(o.Files);
            }
            else if (o.ContentEquals(b))
            {
                merged.AddRange(t.Files);
            }
            else
            {
                conflicts.Add(group);
                merged.AddRange(o.Files);
            }
        }

        return new MergeResult(new Snapshot(merged), conflicts);
    }

    /// <summary>Files of <paramref name="content"/> that belong to <paramref name="group"/>.</summary>
    public static IReadOnlyList<FileEntry> FilesInGroup(Snapshot content, string group, MergeGranularity granularity) =>
        content.Files.Where(f => string.Equals(GroupOf(f, granularity), group, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Replaces the files of <paramref name="group"/> in <paramref name="content"/> with those of <paramref name="source"/>.</summary>
    public static Snapshot ReplaceGroup(Snapshot content, Snapshot source, string group, MergeGranularity granularity) =>
        new(content.Files
            .Where(f => !string.Equals(GroupOf(f, granularity), group, StringComparison.OrdinalIgnoreCase))
            .Concat(FilesInGroup(source, group, granularity)));

    private static Dictionary<string, Snapshot> GroupFiles(Snapshot content, MergeGranularity granularity) =>
        content.Files
            .GroupBy(f => GroupOf(f, granularity), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new Snapshot(g), StringComparer.OrdinalIgnoreCase);
}
