using SaveFileSync.Core.Model;
using SaveFileSync.Core.Sync;

namespace SaveFileSync.Core.History;

public enum DeviceState
{
    ThisPc,
    Active,
    /// <summary>Not seen for more than <see cref="DeviceRoster.InactiveAfter"/>.</summary>
    Inactive,
    /// <summary>Retired by the user from another PC and not seen again since.</summary>
    Retired,
}

public sealed record DeviceRow(DeviceInfo Info, DeviceState State, bool IsThisPc);

/// <summary>Which devices still count (PLAN.md §11, phase 3: 90 days, or retired by hand).</summary>
public static class DeviceRoster
{
    public static readonly TimeSpan InactiveAfter = TimeSpan.FromDays(90);

    public static DeviceState StateOf(DeviceInfo device, IReadOnlyList<RetiredDevice> retired, string thisDeviceId, DateTime utcNow)
    {
        if (device.DeviceId == thisDeviceId)
        {
            return DeviceState.ThisPc;
        }

        // Retired only until it shows up again: its lastSeen becomes newer than the retirement.
        if (retired.Any(r => r.DeviceId == device.DeviceId && r.RetiredUtc >= device.LastSeenUtc))
        {
            return DeviceState.Retired;
        }

        return utcNow - device.LastSeenUtc > InactiveAfter ? DeviceState.Inactive : DeviceState.Active;
    }

    /// <summary>
    /// Devices whose current version must stay restorable. A device that never registered is
    /// not in the list and counts as active: when in doubt, keep.
    /// </summary>
    public static HashSet<string> InactiveDeviceIds(IReadOnlyList<DeviceInfo> devices, IReadOnlyList<RetiredDevice> retired, string thisDeviceId, DateTime utcNow) =>
        devices
            .Where(d => StateOf(d, retired, thisDeviceId, utcNow) is DeviceState.Inactive or DeviceState.Retired)
            .Select(d => d.DeviceId)
            .ToHashSet(StringComparer.Ordinal);
}

/// <param name="Graph">All complete versions of the unit (archived ones included).</param>
/// <param name="Archived">Versions whose contents were already removed.</param>
/// <param name="Pinned">Versions pinned by any device.</param>
/// <param name="ProtectedHeads">Current versions of devices that are still in use.</param>
/// <param name="Keep">How many restorable versions to keep.</param>
public sealed record RetentionInput(
    VersionGraph Graph,
    IReadOnlySet<string> Archived,
    IReadOnlySet<string> Pinned,
    IReadOnlySet<string> ProtectedHeads,
    int Keep);

/// <summary>
/// Decides which versions lose their contents. A version is kept if it is a tip, the current
/// version of a device in use, pinned, or among the newest <see cref="RetentionInput.Keep"/>
/// restorable ones. Nothing here deletes a manifest: archived versions keep their place in the
/// history, so common ancestors for merges are never lost (PLAN.md §11, phase 3).
/// </summary>
public static class RetentionPlanner
{
    public static IReadOnlyList<VersionManifest> SelectToArchive(RetentionInput input)
    {
        var graph = input.Graph;
        var newestFirst = graph.Versions
            .OrderByDescending(graph.Generation)
            .ThenByDescending(v => v.CreatedUtc)
            .ThenByDescending(v => v.VersionId, StringComparer.Ordinal)
            .ToList();

        var keep = new HashSet<string>(graph.Tips.Select(t => t.VersionId), StringComparer.Ordinal);
        keep.UnionWith(input.ProtectedHeads);
        keep.UnionWith(input.Pinned);
        keep.UnionWith(newestFirst
            .Where(v => !input.Archived.Contains(v.VersionId))
            .Take(Math.Max(1, input.Keep))
            .Select(v => v.VersionId));

        return newestFirst
            .Where(v => !input.Archived.Contains(v.VersionId) && !keep.Contains(v.VersionId))
            .ToList();
    }
}
