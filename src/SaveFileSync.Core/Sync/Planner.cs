using SaveFileSync.Core.Model;

namespace SaveFileSync.Core.Sync;

public abstract record SyncPlan
{
    /// <summary>Nothing to transfer. If <see cref="AdoptBaseId"/> is set, record it as the new base.</summary>
    public sealed record NoOp(string? AdoptBaseId = null) : SyncPlan;

    /// <summary>Create a new version from the local files with the given parents.</summary>
    public sealed record Push(IReadOnlyList<string> Parents, VersionKind Kind) : SyncPlan;

    /// <summary>Apply a remote version locally and make it the base.</summary>
    public sealed record Pull(string TargetVersionId) : SyncPlan;

    /// <summary>
    /// The user must choose. When <see cref="PreserveLocalFirst"/> is set, the local files
    /// differ from every candidate and must be pushed (with <see cref="PreserveParents"/>)
    /// before the conflict is recorded, so that local work is never lost (PLAN.md §6.2).
    /// </summary>
    public sealed record Conflict(
        IReadOnlyList<string> CandidateVersionIds,
        bool PreserveLocalFirst,
        IReadOnlyList<string> PreserveParents,
        ConflictReason Reason) : SyncPlan;

    /// <summary>
    /// All local files disappeared although the base has files. Never propagated
    /// automatically: a missing drive or a wrong path must not delete saves on every PC.
    /// </summary>
    public sealed record LocalEmptied(string BaseVersionId) : SyncPlan;
}

public enum ConflictReason
{
    /// <summary>Both this PC and another PC changed the unit since the common base.</summary>
    BothChanged,
    /// <summary>First setup on this PC: local files exist and differ from the repository.</summary>
    InitialSetup,
    /// <summary>Two other PCs diverged and this PC has nothing to add.</summary>
    RemoteDiverged,
}

/// <summary>
/// Pure decision function: base + local snapshot + remote DAG → plan (PLAN.md §6.2).
/// Phase 1a implements <see cref="MergeGranularity.Unit"/> only: the whole unit is
/// compared as one group.
/// </summary>
public static class Planner
{
    public static SyncPlan Decide(string? baseVersionId, Snapshot local, VersionGraph graph)
    {
        if (graph.IsEmpty)
        {
            return local.IsEmpty ? new SyncPlan.NoOp() : new SyncPlan.Push([], VersionKind.Initial);
        }

        var tips = graph.Tips;
        var baseVersion = graph.Get(baseVersionId);

        if (baseVersion is null)
        {
            var unknown = DecideWithUnknownBase(local, graph, tips);
            if (unknown.Plan is not null)
            {
                return unknown.Plan;
            }

            baseVersion = unknown.AdoptedBase!;
        }

        if (local.IsEmpty && !baseVersion.Content.IsEmpty)
        {
            // Accept the empty state only if another PC already deleted everything on purpose.
            var emptyTip = tips.FirstOrDefault(t => t.VersionId != baseVersion.VersionId && t.Content.IsEmpty);
            return emptyTip is not null
                ? new SyncPlan.NoOp(emptyTip.VersionId)
                : new SyncPlan.LocalEmptied(baseVersion.VersionId);
        }

        var localChanged = !local.ContentEquals(baseVersion.Content);

        // Fold every other tip into "current", which starts as the local content.
        var current = local;
        VersionManifest? pulledTip = null;
        var lineage = new List<string> { baseVersion.VersionId };
        var conflicting = new List<VersionManifest>();

        foreach (var tip in tips.Where(t => t.VersionId != baseVersion.VersionId))
        {
            if (tip.Content.ContentEquals(current))
            {
                lineage.Add(tip.VersionId);
                continue;
            }

            var lca = graph.LowestCommonAncestor(lineage, tip.VersionId);
            var mergeBase = lca?.Content ?? Snapshot.Empty;
            var currentChanged = !current.ContentEquals(mergeBase);
            var tipChanged = !tip.Content.ContentEquals(mergeBase);

            if (!tipChanged)
            {
                // The other side has nothing new (e.g. it only adopted an old version).
                lineage.Add(tip.VersionId);
            }
            else if (!currentChanged)
            {
                current = tip.Content;
                pulledTip = tip;
                lineage.Add(tip.VersionId);
            }
            else
            {
                conflicting.Add(tip);
            }
        }

        if (conflicting.Count > 0)
        {
            var preserve = localChanged && !tips.Any(t => t.Content.ContentEquals(local));
            var candidates = new List<VersionManifest>();
            if (!preserve)
            {
                // The local content is already in the repository: offer the version that holds it.
                candidates.Add(pulledTip is null
                    ? tips.FirstOrDefault(t => t.Content.ContentEquals(local)) ?? baseVersion
                    : pulledTip);
            }
            else if (pulledTip is not null)
            {
                candidates.Add(pulledTip);
            }

            candidates.AddRange(conflicting);
            return new SyncPlan.Conflict(
                DistinctByContent(candidates).Select(v => v.VersionId).ToList(),
                preserve,
                preserve ? [baseVersion.VersionId] : [],
                ConflictReason.BothChanged);
        }

        if (pulledTip is null)
        {
            if (!localChanged)
            {
                // If the base was superseded by a tip with identical content (e.g. a conflict
                // resolved elsewhere in favour of this PC), move the base onto that tip.
                var baseIsTip = tips.Any(t => t.VersionId == baseVersion.VersionId);
                var sameContentTip = lineage.Skip(1).Select(id => graph.Get(id)!).FirstOrDefault(v => v.Content.ContentEquals(local));
                if (!baseIsTip && sameContentTip is not null)
                {
                    return new SyncPlan.NoOp(sameContentTip.VersionId);
                }

                return new SyncPlan.NoOp(baseVersionId == baseVersion.VersionId ? null : baseVersion.VersionId);
            }

            // Another PC already published exactly this content and nothing else is pending.
            var others = lineage.Skip(1).Select(id => graph.Get(id)!).ToList();
            if (others.Count == 1 && others[0].Content.ContentEquals(local))
            {
                return new SyncPlan.NoOp(others[0].VersionId);
            }

            return new SyncPlan.Push(lineage, lineage.Count > 1 ? VersionKind.Merge : VersionKind.Normal);
        }

        if (localChanged)
        {
            // Local edits that "equal the old merge base" (e.g. a manual revert) would be
            // overwritten by the pull: push them first so they are preserved, then re-plan.
            return new SyncPlan.Push([baseVersion.VersionId], VersionKind.Normal);
        }

        return new SyncPlan.Pull(pulledTip.VersionId);
    }

    private static (SyncPlan? Plan, VersionManifest? AdoptedBase) DecideWithUnknownBase(
        Snapshot local, VersionGraph graph, IReadOnlyList<VersionManifest> tips)
    {
        var distinctTips = DistinctByContent(tips);

        if (local.IsEmpty)
        {
            return distinctTips.Count == 1
                ? (new SyncPlan.Pull(distinctTips[0].VersionId), null)
                : (new SyncPlan.Conflict(distinctTips.Select(t => t.VersionId).ToList(), false, [], ConflictReason.RemoteDiverged), null);
        }

        var equalTip = tips.FirstOrDefault(t => t.Content.ContentEquals(local));
        if (equalTip is not null)
        {
            return (new SyncPlan.NoOp(equalTip.VersionId), null);
        }

        var known = graph.Versions
            .Where(v => v.Content.ContentEquals(local))
            .OrderByDescending(graph.Generation)
            .ThenByDescending(v => v.VersionId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (known is not null)
        {
            return (null, known);
        }

        return (new SyncPlan.Conflict(
            distinctTips.Select(t => t.VersionId).ToList(),
            PreserveLocalFirst: true,
            PreserveParents: [],
            ConflictReason.InitialSetup), null);
    }

    private static List<VersionManifest> DistinctByContent(IEnumerable<VersionManifest> versions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return versions.Where(v => seen.Add(v.Content.ContentId)).ToList();
    }
}
