using SaveFileSync.Core.Model;

namespace SaveFileSync.Core.Sync;

/// <summary>
/// The DAG of the complete versions of one unit. Ordering comes only from parent links,
/// never from timestamps (PLAN.md §6.1).
/// </summary>
public sealed class VersionGraph
{
    private readonly Dictionary<string, VersionManifest> _versions;
    private readonly Dictionary<string, int> _generation = new(StringComparer.Ordinal);

    public VersionGraph(IEnumerable<VersionManifest> completeVersions)
    {
        _versions = new Dictionary<string, VersionManifest>(StringComparer.Ordinal);
        foreach (var v in completeVersions)
        {
            _versions[v.VersionId] = v;
        }

        var referenced = new HashSet<string>(
            _versions.Values.SelectMany(v => v.Parents),
            StringComparer.Ordinal);

        Tips = _versions.Values
            .Where(v => !referenced.Contains(v.VersionId))
            .OrderBy(v => v.VersionId, StringComparer.Ordinal)
            .ToList();
    }

    public static VersionGraph Empty { get; } = new([]);

    public IReadOnlyCollection<VersionManifest> Versions => _versions.Values;

    /// <summary>Versions that are not the parent of any other version, sorted by id.</summary>
    public IReadOnlyList<VersionManifest> Tips { get; }

    public bool IsEmpty => _versions.Count == 0;

    public bool Contains(string versionId) => _versions.ContainsKey(versionId);

    public VersionManifest? Get(string? versionId) =>
        versionId is not null && _versions.TryGetValue(versionId, out var v) ? v : null;

    public VersionGraph With(VersionManifest added) => new(_versions.Values.Append(added));

    /// <summary>The given versions and all their ancestors present in the graph.</summary>
    public HashSet<string> AncestorsOrSelf(IEnumerable<string> ids)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(ids.Where(_versions.ContainsKey));
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!result.Add(id))
            {
                continue;
            }

            foreach (var p in _versions[id].Parents)
            {
                if (_versions.ContainsKey(p) && !result.Contains(p))
                {
                    stack.Push(p);
                }
            }
        }

        return result;
    }

    public bool IsAncestorOrSelf(string ancestorId, string versionId) =>
        AncestorsOrSelf([versionId]).Contains(ancestorId);

    /// <summary>
    /// The most recent common ancestor (highest generation, ties broken by id) of a set of
    /// versions and another version, or <c>null</c> if their histories do not meet.
    /// </summary>
    public VersionManifest? LowestCommonAncestor(IEnumerable<string> lineage, string versionId)
    {
        var a = AncestorsOrSelf(lineage);
        var b = AncestorsOrSelf([versionId]);
        a.IntersectWith(b);
        return a
            .Select(id => _versions[id])
            .OrderByDescending(Generation)
            .ThenByDescending(v => v.VersionId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Length of the longest parent chain to a root present in the graph.</summary>
    public int Generation(VersionManifest version)
    {
        if (_generation.TryGetValue(version.VersionId, out var g))
        {
            return g;
        }

        // Iterative post-order DFS to avoid deep recursion on long histories.
        var stack = new Stack<(string Id, bool Expanded)>();
        stack.Push((version.VersionId, false));
        while (stack.Count > 0)
        {
            var (id, expanded) = stack.Pop();
            if (_generation.ContainsKey(id))
            {
                continue;
            }

            var parents = _versions[id].Parents.Where(_versions.ContainsKey).ToList();
            if (!expanded)
            {
                stack.Push((id, true));
                foreach (var p in parents.Where(p => !_generation.ContainsKey(p)))
                {
                    stack.Push((p, false));
                }

                continue;
            }

            _generation[id] = parents.Count == 0 ? 0 : parents.Max(p => _generation.GetValueOrDefault(p)) + 1;
        }

        return _generation[version.VersionId];
    }
}
