using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SaveFileSync.Core.Model;

namespace SaveFileSync.Core.History;

/// <param name="Marked">Blobs newly marked as unreferenced.</param>
/// <param name="Deleted">Blobs deleted this run.</param>
/// <param name="Waiting">Unreferenced blobs still inside the grace period.</param>
/// <param name="SkippedReason">Set when nothing was done because the repository did not look safe to clean.</param>
public sealed record GcReport(int Marked, int Deleted, long DeletedBytes, int Waiting, string? SkippedReason = null)
{
    public static GcReport Skipped(string reason) => new(0, 0, 0, 0, reason);
}

/// <summary>
/// Deletes blobs that no restorable version needs (PLAN.md §11, phase 3). It is deliberately slow
/// and cautious, because a deleted blob cannot come back:
/// <list type="bullet">
/// <item>it does nothing unless every version file can be read and no write is in progress;</item>
/// <item>a blob is first only <b>marked</b>; it is deleted at least <c>grace</c> later, if it is
/// still unreferenced in a fresh look and the file itself is older than <c>grace</c>;</item>
/// <item>marks are per device and dropped as soon as the blob is referenced again, so a stale mark
/// can never make a later deletion instant.</item>
/// </list>
/// A PC that stays offline for longer than the grace period can still have its current version
/// repaired afterwards (see <c>SyncEngine.RepairMissingBlobs</c>).
/// </summary>
public static class BlobGarbageCollector
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromDays(14);

    private static readonly TimeSpan TempFileMaxAge = TimeSpan.FromDays(2);

    public static GcReport Run(
        IRemoteStore remote,
        string deviceId,
        string deviceShort,
        DateTime utcNow,
        TimeSpan? grace = null,
        ILogger? log = null)
    {
        log ??= NullLogger.Instance;
        var graceTime = grace ?? DefaultGrace;

        if (remote.CountRecentTempFiles(TempFileMaxAge, utcNow) > 0)
        {
            return GcReport.Skipped("Files are still being written in the repository.");
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unitId in remote.ListUnitIds())
        {
            var scan = remote.ScanVersions(unitId);
            if (scan.Unreadable > 0)
            {
                return GcReport.Skipped($"{scan.Unreadable} version file(s) of '{unitId}' could not be read (still syncing?).");
            }

            var archived = remote.ReadArchive(unitId).SelectMany(d => d.VersionIds).ToHashSet(StringComparer.Ordinal);
            foreach (var version in scan.Versions.Where(v => !archived.Contains(v.VersionId)))
            {
                foreach (var file in version.Files)
                {
                    referenced.Add(file.Sha256);
                }
            }
        }

        var candidates = remote.EnumerateBlobs().Where(b => !referenced.Contains(b.Sha256)).ToList();

        var ownMarks = remote.ReadGcMarks().FirstOrDefault(d => d.DeviceId == deviceId)?.Marks
                       ?? new Dictionary<string, DateTime>();
        var newMarks = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var marked = 0;
        var deleted = 0;
        long deletedBytes = 0;
        var waiting = 0;

        foreach (var blob in candidates)
        {
            if (!ownMarks.TryGetValue(blob.Sha256, out var markedUtc))
            {
                markedUtc = utcNow;
                marked++;
            }

            if (utcNow - markedUtc >= graceTime && utcNow - blob.LastWriteUtc >= graceTime)
            {
                try
                {
                    if (remote.DeleteBlob(blob.Sha256))
                    {
                        deleted++;
                        deletedBytes += blob.Size;
                    }

                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.LogWarning(ex, "Could not delete blob {Blob}", blob.Sha256);
                }
            }

            newMarks[blob.Sha256] = markedUtc;
            waiting++;
        }

        // Only marks that are still valid are kept: a blob that became referenced loses its mark.
        if (!SameMarks(ownMarks, newMarks))
        {
            remote.WriteGcMarks(new GcMarkDocument { DeviceId = deviceId, Marks = newMarks, UpdatedUtc = utcNow }, deviceShort);
        }

        if (deleted > 0)
        {
            log.LogInformation("Cleanup deleted {Count} unused blob(s) ({Bytes} bytes)", deleted, deletedBytes);
        }

        return new GcReport(marked, deleted, deletedBytes, waiting);
    }

    private static bool SameMarks(IReadOnlyDictionary<string, DateTime> a, IReadOnlyDictionary<string, DateTime> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
