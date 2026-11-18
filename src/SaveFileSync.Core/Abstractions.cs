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
