using SaveFileSync.Core.Model;

namespace SaveFileSync.Core;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;
}

public interface IProcessMonitor
{
    /// <summary>Names (without ".exe", case-insensitive) of the processes running now.</summary>
    IReadOnlySet<string> GetRunningProcessNames();
}

public enum BlobState
{
    Missing,
    Present,
    /// <summary>Present as an OneDrive cloud-only placeholder; reading it triggers a download.</summary>
    CloudOnly,
    SizeMismatch,
}

/// <summary>
/// The shared repository (PLAN.md §4). The phase 1 implementation is a folder that the
/// OneDrive client syncs; a Graph-based implementation could be added later.
/// </summary>
public interface IRemoteStore
{
    string Root { get; }

    bool IsAvailable();

    RepoInfo? ReadRepoInfo();

    /// <summary>Creates <c>repo.json</c> and <c>README.txt</c> if missing; returns the repo info.</summary>
    RepoInfo Initialize(DateTime utcNow);

    void WriteDevice(DeviceInfo device, string deviceShort);

    IReadOnlyList<DeviceInfo> ReadDevices();

    IReadOnlyList<string> ListUnitIds();

    UnitDefinition? ReadUnitDefinition(string unitId);

    void WriteUnitDefinition(UnitDefinition definition, string deviceShort);

    IReadOnlyList<string> ListVersionIds(string unitId);

    VersionManifest? ReadVersion(string unitId, string versionId);

    /// <summary>Writes an immutable version. Throws <see cref="IOException"/> if the id already exists.</summary>
    void WriteVersion(VersionManifest version, string deviceShort);

    void WriteHead(string unitId, HeadInfo head, string deviceShort);

    IReadOnlyList<HeadInfo> ReadHeads(string unitId);

    BlobState GetBlobState(string sha256, long expectedSize);

    /// <summary>
    /// Copies a blob from <paramref name="openSource"/>, verifying its SHA-256 while copying.
    /// Throws <see cref="BlobHashMismatchException"/> if the content does not match.
    /// Does nothing if the blob already exists.
    /// </summary>
    void PutBlob(string sha256, long size, Func<Stream> openSource, string deviceShort);

    Stream OpenBlob(string sha256);

    // ---- history (phase 3): pins, archive, cleanup, retired devices ----

    IReadOnlyList<IdSetDocument> ReadPins(string unitId);

    void WritePins(string unitId, IdSetDocument document, string deviceShort);

    /// <summary>Versions whose contents were intentionally removed; their manifests stay so history keeps its shape.</summary>
    IReadOnlyList<IdSetDocument> ReadArchive(string unitId);

    void WriteArchive(string unitId, IdSetDocument document, string deviceShort);

    IReadOnlyList<GcMarkDocument> ReadGcMarks();

    void WriteGcMarks(GcMarkDocument document, string deviceShort);

    IReadOnlyList<RetiredDevice> ReadRetired();

    void WriteRetired(RetiredDevice document, string deviceShort);

    /// <summary>Every version manifest of a unit, complete or not, and how many could not be read.</summary>
    VersionScan ScanVersions(string unitId);

    /// <summary>Temporary files (writes in progress somewhere in the repository) younger than <paramref name="maxAge"/>.</summary>
    int CountRecentTempFiles(TimeSpan maxAge, DateTime utcNow);

    IReadOnlyList<BlobInfo> EnumerateBlobs();

    /// <summary>Deletes a blob; false if it was not there.</summary>
    bool DeleteBlob(string sha256);
}

public sealed record BlobInfo(string Sha256, long Size, DateTime LastWriteUtc);

/// <param name="Versions">Manifests that could be read.</param>
/// <param name="Unreadable">Version files with a valid name that could not be parsed (maybe still syncing).</param>
public sealed record VersionScan(IReadOnlyList<VersionManifest> Versions, int Unreadable);

/// <summary>Optional diagnostics offered by a repository implementation.</summary>
public interface IRepositoryDiagnostics
{
    /// <summary>Files ignored because they look like OneDrive conflict copies or foreign files.</summary>
    IReadOnlyList<string> FindIgnoredFiles();
}

/// <summary>A blob exists but cannot be read now (e.g. OneDrive is not running to download it).</summary>
public sealed class BlobUnavailableException(string sha256, Exception inner)
    : IOException($"Blob {sha256} cannot be read yet: {inner.Message}", inner);

public sealed class BlobHashMismatchException(string expected, string actual)
    : IOException($"Content hash mismatch: expected {expected}, got {actual}.")
{
    public string Expected { get; } = expected;

    public string Actual { get; } = actual;
}
