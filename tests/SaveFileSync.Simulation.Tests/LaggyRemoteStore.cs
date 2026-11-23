using SaveFileSync.Core;
using SaveFileSync.Core.Model;

namespace SaveFileSync.Simulation.Tests;

/// <summary>
/// Simulated OneDrive delivery between PCs (PLAN.md §10.3). Everything a PC writes is
/// immediately visible to that PC, and becomes visible to the others only when the test
/// delivers it, possibly out of order (e.g. a version manifest before its blobs).
/// </summary>
public sealed class LagNetwork
{
    private readonly Dictionary<string, (string Writer, HashSet<string> DeliveredTo)> _inFlight = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Blobs that exist but fail to read (cloud-only placeholders while OneDrive is offline).</summary>
    public HashSet<string> UnreadableBlobs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string item, string writer)
    {
        lock (_lock)
        {
            _inFlight.TryAdd(item, (writer, new HashSet<string>(StringComparer.Ordinal)));
        }
    }

    public bool IsVisible(string item, string device)
    {
        lock (_lock)
        {
            return !_inFlight.TryGetValue(item, out var entry) || entry.Writer == device || entry.DeliveredTo.Contains(device);
        }
    }

    /// <summary>Delivers every pending item (optionally only those matching <paramref name="filter"/>) to every PC.</summary>
    public void Deliver(Func<string, bool>? filter = null)
    {
        lock (_lock)
        {
            foreach (var key in _inFlight.Keys.Where(k => filter is null || filter(k)).ToList())
            {
                _inFlight.Remove(key);
            }
        }
    }

    public void DeliverVersionsOnly() => Deliver(k => k.StartsWith("version:", StringComparison.Ordinal));
}

public sealed class LaggyRemoteStore(IRemoteStore inner, LagNetwork network, string device) : IRemoteStore, IRepositoryDiagnostics
{
    public string Root => inner.Root;

    public bool IsAvailable() => inner.IsAvailable();

    public RepoInfo? ReadRepoInfo() => inner.ReadRepoInfo();

    public RepoInfo Initialize(DateTime utcNow) => inner.Initialize(utcNow);

    public void WriteDevice(DeviceInfo info, string deviceShort) => inner.WriteDevice(info, deviceShort);

    public IReadOnlyList<DeviceInfo> ReadDevices() => inner.ReadDevices();

    public IReadOnlyList<string> ListUnitIds() => inner.ListUnitIds();

    public UnitDefinition? ReadUnitDefinition(string unitId) => inner.ReadUnitDefinition(unitId);

    public void WriteUnitDefinition(UnitDefinition definition, string deviceShort) => inner.WriteUnitDefinition(definition, deviceShort);

    public IReadOnlyList<string> ListVersionIds(string unitId) =>
        inner.ListVersionIds(unitId).Where(id => network.IsVisible(VersionKey(unitId, id), device)).ToList();

    public VersionManifest? ReadVersion(string unitId, string versionId) =>
        network.IsVisible(VersionKey(unitId, versionId), device) ? inner.ReadVersion(unitId, versionId) : null;

    public void WriteVersion(VersionManifest version, string deviceShort)
    {
        inner.WriteVersion(version, deviceShort);
        network.Register(VersionKey(version.UnitId, version.VersionId), device);
    }

    public void WriteHead(string unitId, HeadInfo head, string deviceShort) => inner.WriteHead(unitId, head, deviceShort);

    public IReadOnlyList<HeadInfo> ReadHeads(string unitId) => inner.ReadHeads(unitId);

    public BlobState GetBlobState(string sha256, long expectedSize) =>
        network.IsVisible(BlobKey(sha256), device) ? inner.GetBlobState(sha256, expectedSize) : BlobState.Missing;

    public void PutBlob(string sha256, long size, Func<Stream> openSource, string deviceShort)
    {
        var existed = inner.GetBlobState(sha256, size) != BlobState.Missing;
        inner.PutBlob(sha256, size, openSource, deviceShort);
        if (!existed)
        {
            network.Register(BlobKey(sha256), device);
        }
    }

    public Stream OpenBlob(string sha256)
    {
        if (!network.IsVisible(BlobKey(sha256), device))
        {
            throw new FileNotFoundException("Blob not delivered yet.", sha256);
        }

        if (network.UnreadableBlobs.Contains(sha256))
        {
            throw new IOException("The cloud file provider is not running.");
        }

        return inner.OpenBlob(sha256);
    }

    // History data is not lagged here: lag scenarios target pushes; GC tests build their own timelines.
    public IReadOnlyList<IdSetDocument> ReadPins(string unitId) => inner.ReadPins(unitId);

    public void WritePins(string unitId, IdSetDocument document, string deviceShort) => inner.WritePins(unitId, document, deviceShort);

    public IReadOnlyList<IdSetDocument> ReadArchive(string unitId) => inner.ReadArchive(unitId);

    public void WriteArchive(string unitId, IdSetDocument document, string deviceShort) => inner.WriteArchive(unitId, document, deviceShort);

    public IReadOnlyList<GcMarkDocument> ReadGcMarks() => inner.ReadGcMarks();

    public void WriteGcMarks(GcMarkDocument document, string deviceShort) => inner.WriteGcMarks(document, deviceShort);

    public IReadOnlyList<RetiredDevice> ReadRetired() => inner.ReadRetired();

    public void WriteRetired(RetiredDevice document, string deviceShort) => inner.WriteRetired(document, deviceShort);

    public VersionScan ScanVersions(string unitId)
    {
        var scan = inner.ScanVersions(unitId);
        var visible = scan.Versions.Where(v => network.IsVisible(VersionKey(unitId, v.VersionId), device)).ToList();
        return new VersionScan(visible, scan.Unreadable);
    }

    public int CountRecentTempFiles(TimeSpan maxAge, DateTime utcNow) => inner.CountRecentTempFiles(maxAge, utcNow);

    public IReadOnlyList<BlobInfo> EnumerateBlobs() =>
        inner.EnumerateBlobs().Where(b => network.IsVisible(BlobKey(b.Sha256), device)).ToList();

    public bool DeleteBlob(string sha256) => inner.DeleteBlob(sha256);

    public IReadOnlyList<string> FindIgnoredFiles() =>
        inner is IRepositoryDiagnostics d ? d.FindIgnoredFiles() : [];

    private static string VersionKey(string unitId, string versionId) => $"version:{unitId}/{versionId}";

    private static string BlobKey(string sha256) => $"blob:{sha256.ToLowerInvariant()}";
}
