using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SaveFileSync.Core.Config;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Sync;

namespace SaveFileSync.Core.History;

/// <summary>One line of the history list (PLAN.md §8, item 4).</summary>
public sealed record HistoryEntry(
    string VersionId,
    string DeviceId,
    string DeviceName,
    DateTime CreatedUtc,
    int FileCount,
    long TotalSize,
    VersionKind Kind,
    string? Note,
    bool IsLatest,
    bool IsCurrentHere,
    bool Archived,
    bool Restorable,
    IReadOnlyList<string> PinnedBy,
    bool PinnedHere);

/// <param name="Archived">Versions archived this run, over all games.</param>
/// <param name="Repaired">Blobs re-uploaded because they had disappeared from the repository.</param>
public sealed record MaintenanceReport(int Archived, int Repaired, GcReport Cleanup);

/// <summary>
/// History, pins, retention and the device list on top of the repository (PLAN.md §11, phase 3).
/// Not thread-safe: <c>SyncService</c> calls it from its worker.
/// </summary>
public sealed class HistoryService
{
    private readonly IRemoteStore _remote;
    private readonly SyncEngine _engine;
    private readonly DeviceIdentity _device;
    private readonly IClock _clock;
    private readonly ILogger _log;

    public HistoryService(IRemoteStore remote, SyncEngine engine, DeviceIdentity device, IClock clock, ILogger? log = null)
    {
        _remote = remote;
        _engine = engine;
        _device = device;
        _clock = clock;
        _log = log ?? NullLogger.Instance;
    }

    // ---------------------------------------------------------------- history list

    public IReadOnlyList<HistoryEntry> GetHistory(string unitId)
    {
        var names = DeviceNames();
        var scan = _remote.ScanVersions(unitId);
        var (graph, _) = _engine.LoadGraph(unitId);
        var archived = _engine.LoadArchivedIds(unitId);
        var pins = _remote.ReadPins(unitId);
        var tips = graph.Tips.Select(t => t.VersionId).ToHashSet(StringComparer.Ordinal);
        var currentHere = _engine.GetBaseVersionId(unitId);

        // Order by position in the history (not by device clocks, which may be hours apart).
        var ordered = scan.Versions
            .OrderByDescending(v => graph.Contains(v.VersionId) ? graph.Generation(v) : int.MaxValue)
            .ThenByDescending(v => v.CreatedUtc)
            .ThenByDescending(v => v.VersionId, StringComparer.Ordinal);

        return ordered.Select(v =>
        {
            var isArchived = archived.Contains(v.VersionId);
            var pinnedBy = pins.Where(p => p.VersionIds.Contains(v.VersionId)).Select(p => NameOf(names, p.DeviceId)).ToList();
            return new HistoryEntry(
                v.VersionId,
                v.DeviceId,
                NameOf(names, v.DeviceId),
                v.CreatedUtc,
                v.Files.Count,
                v.TotalSize,
                v.Kind,
                v.Note,
                tips.Contains(v.VersionId),
                v.VersionId == currentHere,
                isArchived,
                !isArchived && v.Files.All(f => _remote.GetBlobState(f.Sha256, f.Size) is BlobState.Present or BlobState.CloudOnly),
                pinnedBy,
                pins.Any(p => p.DeviceId == _device.DeviceId && p.VersionIds.Contains(v.VersionId)));
        }).ToList();
    }

    // ---------------------------------------------------------------- pins

    /// <summary>
    /// Pins or unpins a version on behalf of this PC. A pin keeps the version restorable; another PC's
    /// pin can only be removed on that PC.
    /// </summary>
    public void SetPinned(string unitId, string versionId, bool pinned)
    {
        var own = _remote.ReadPins(unitId).FirstOrDefault(p => p.DeviceId == _device.DeviceId);
        var ids = (own?.VersionIds ?? []).ToList();
        if (pinned == ids.Contains(versionId))
        {
            return;
        }

        if (pinned)
        {
            if (_engine.LoadArchivedIds(unitId).Contains(versionId))
            {
                throw new InvalidOperationException("That version is archived: its contents were already removed.");
            }

            ids.Add(versionId);
        }
        else
        {
            ids.Remove(versionId);
        }

        _remote.WritePins(unitId, new IdSetDocument { DeviceId = _device.DeviceId, VersionIds = ids, UpdatedUtc = _clock.UtcNow }, _device.ShortId);
    }

    // ---------------------------------------------------------------- retention

    /// <summary>
    /// Archives the versions of one unit that are outside the retention rules. This PC archives its own
    /// versions and those of devices that are inactive or retired; other PCs do the same for theirs.
    /// </summary>
    public int ApplyRetention(UnitDefinition unit)
    {
        var (graph, _) = _engine.LoadGraph(unit.UnitId);
        if (graph.IsEmpty)
        {
            return 0;
        }

        var now = _clock.UtcNow;
        var devices = _remote.ReadDevices();
        var retired = _remote.ReadRetired();
        var inactive = DeviceRoster.InactiveDeviceIds(devices, retired, _device.DeviceId, now);

        var archived = _engine.LoadArchivedIds(unit.UnitId);
        var pinned = _remote.ReadPins(unit.UnitId).SelectMany(p => p.VersionIds).ToHashSet(StringComparer.Ordinal);
        var heads = _remote.ReadHeads(unit.UnitId)
            .Where(h => !inactive.Contains(h.DeviceId))
            .Select(h => h.VersionId)
            .ToHashSet(StringComparer.Ordinal);

        var toArchive = RetentionPlanner.SelectToArchive(new RetentionInput(graph, archived, pinned, heads, unit.EffectiveKeepVersions));
        var mine = toArchive
            .Where(v => v.DeviceId == _device.DeviceId || inactive.Contains(v.DeviceId))
            .Select(v => v.VersionId)
            .ToList();
        if (mine.Count == 0)
        {
            return 0;
        }

        var own = _remote.ReadArchive(unit.UnitId).FirstOrDefault(d => d.DeviceId == _device.DeviceId);
        var all = (own?.VersionIds ?? []).Union(mine, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        _remote.WriteArchive(unit.UnitId, new IdSetDocument { DeviceId = _device.DeviceId, VersionIds = all, UpdatedUtc = now }, _device.ShortId);
        _log.LogInformation("Unit {Unit}: archived {Count} old version(s), keeping {Keep}", unit.UnitId, mine.Count, unit.EffectiveKeepVersions);
        return mine.Count;
    }

    // ---------------------------------------------------------------- devices

    public IReadOnlyList<DeviceRow> GetDevices()
    {
        var now = _clock.UtcNow;
        var retired = _remote.ReadRetired();
        return _remote.ReadDevices()
            .Select(d => new DeviceRow(d, DeviceRoster.StateOf(d, retired, _device.DeviceId, now), d.DeviceId == _device.DeviceId))
            .OrderByDescending(r => r.IsThisPc)
            .ThenBy(r => r.Info.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Marks another PC as no longer in use, so its old versions can be cleaned up without waiting 90 days.
    /// If that PC is used again it re-registers itself and counts as active.
    /// </summary>
    public void RetireDevice(string deviceId)
    {
        if (deviceId == _device.DeviceId)
        {
            throw new InvalidOperationException("This PC cannot retire itself.");
        }

        _remote.WriteRetired(
            new RetiredDevice { DeviceId = deviceId, RetiredUtc = _clock.UtcNow, RetiredByDeviceId = _device.DeviceId },
            _device.ShortId);
        _log.LogInformation("Device {Device} retired", deviceId);
    }

    // ---------------------------------------------------------------- helpers

    private Dictionary<string, string> DeviceNames() =>
        _remote.ReadDevices().GroupBy(d => d.DeviceId).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

    private string NameOf(IReadOnlyDictionary<string, string> names, string deviceId) =>
        deviceId == _device.DeviceId ? "This PC" : names.GetValueOrDefault(deviceId, deviceId[..Math.Min(8, deviceId.Length)]);
}
