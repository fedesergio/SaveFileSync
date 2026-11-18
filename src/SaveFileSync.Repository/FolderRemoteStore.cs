using System.Text.Json;
using SaveFileSync.Core;
using SaveFileSync.Core.IO;
using SaveFileSync.Core.Model;

namespace SaveFileSync.Repository;

/// <summary>
/// Repository format v1 on a folder synced by the OneDrive client (PLAN.md §4).
/// Every file has a single writer; every write is temp + rename; readers ignore temps.
/// </summary>
public sealed class FolderRemoteStore : IRemoteStore
{
    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS / FILE_ATTRIBUTE_RECALL_ON_OPEN (cloud files API).
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    public FolderRemoteStore(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    private string RepoFile => Path.Combine(Root, "repo.json");

    private string DevicesDir => Path.Combine(Root, "devices");

    private string UnitsDir => Path.Combine(Root, "units");

    private string BlobsDir => Path.Combine(Root, "blobs");

    public bool IsAvailable() => File.Exists(RepoFile);

    public RepoInfo? ReadRepoInfo() => ReadJson<RepoInfo>(RepoFile);

    public RepoInfo Initialize(DateTime utcNow)
    {
        Directory.CreateDirectory(Root);
        var info = ReadRepoInfo();
        if (info is null)
        {
            info = new RepoInfo { RepoId = Guid.NewGuid().ToString("N"), CreatedUtc = utcNow };
            AtomicFile.WriteAllBytes(RepoFile, Json.Serialize(info), "init", overwrite: false);
        }

        var readme = Path.Combine(Root, "README.txt");
        if (!File.Exists(readme))
        {
            using var stream = typeof(FolderRemoteStore).Assembly
                .GetManifestResourceStream("SaveFileSync.Repository.RepositoryReadme.txt")!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            AtomicFile.WriteAllBytes(readme, ms.ToArray(), "init");
        }

        Directory.CreateDirectory(DevicesDir);
        Directory.CreateDirectory(UnitsDir);
        Directory.CreateDirectory(BlobsDir);
        return info;
    }

    public void WriteDevice(DeviceInfo device, string deviceShort) =>
        AtomicFile.WriteAllBytes(Path.Combine(DevicesDir, device.DeviceId + ".json"), Json.Serialize(device), deviceShort);

    public IReadOnlyList<DeviceInfo> ReadDevices() =>
        EnumerateJson(DevicesDir).Select(ReadJson<DeviceInfo>).OfType<DeviceInfo>().ToList();

    public IReadOnlyList<string> ListUnitIds() =>
        Directory.Exists(UnitsDir)
            ? Directory.EnumerateDirectories(UnitsDir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];

    public UnitDefinition? ReadUnitDefinition(string unitId)
    {
        var dir = Path.Combine(UnitsDir, unitId, "def");
        return EnumerateJson(dir)
            .Select(ReadJson<UnitDefinition>)
            .OfType<UnitDefinition>()
            .Where(d => d.UnitId == unitId)
            .OrderByDescending(d => d.Revision)
            .ThenByDescending(d => d.RevisionDevice, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public void WriteUnitDefinition(UnitDefinition definition, string deviceShort)
    {
        ValidateId(definition.UnitId);
        var file = Path.Combine(UnitsDir, definition.UnitId, "def", $"{definition.Revision:D6}-{deviceShort}.json");
        AtomicFile.WriteAllBytes(file, Json.Serialize(definition), deviceShort, overwrite: false);
    }

    public IReadOnlyList<string> ListVersionIds(string unitId) =>
        EnumerateJson(Path.Combine(UnitsDir, unitId, "versions"))
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();

    public VersionManifest? ReadVersion(string unitId, string versionId)
    {
        var v = ReadJson<VersionManifest>(VersionPath(unitId, versionId));
        return v is not null && v.UnitId == unitId && v.VersionId == versionId ? v : null;
    }

    public void WriteVersion(VersionManifest version, string deviceShort)
    {
        ValidateId(version.UnitId);
        ValidateId(version.VersionId);
        AtomicFile.WriteAllBytes(VersionPath(version.UnitId, version.VersionId), Json.Serialize(version), deviceShort, overwrite: false);
    }

    public void WriteHead(string unitId, HeadInfo head, string deviceShort) =>
        AtomicFile.WriteAllBytes(Path.Combine(UnitsDir, unitId, "heads", head.DeviceId + ".json"), Json.Serialize(head), deviceShort);

    public IReadOnlyList<HeadInfo> ReadHeads(string unitId) =>
        EnumerateJson(Path.Combine(UnitsDir, unitId, "heads")).Select(ReadJson<HeadInfo>).OfType<HeadInfo>().ToList();

    public BlobState GetBlobState(string sha256, long expectedSize)
    {
        var info = new FileInfo(BlobPath(sha256));
        if (!info.Exists)
        {
            return BlobState.Missing;
        }

        if (info.Length != expectedSize)
        {
            return BlobState.SizeMismatch;
        }

        var attributes = info.Attributes;
        return (attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0
            ? BlobState.CloudOnly
            : BlobState.Present;
    }

    public void PutBlob(string sha256, long size, Func<Stream> openSource, string deviceShort)
    {
        var final = BlobPath(sha256);
        if (GetBlobState(sha256, size) is BlobState.Present or BlobState.CloudOnly)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var tmp = AtomicFile.TempPathFor(final, deviceShort);
        try
        {
            string actual;
            using (var source = openSource())
            using (var target = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                actual = Hashing.CopyAndHash(source, target);
                target.Flush(flushToDisk: true);
            }

            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new BlobHashMismatchException(sha256, actual);
            }

            try
            {
                File.Move(tmp, final, overwrite: false);
            }
            catch (IOException) when (File.Exists(final))
            {
                // Same content written concurrently (e.g. another unit in this process): keep the existing one.
            }
        }
        finally
        {
            AtomicFile.TryDelete(tmp);
        }
    }

    public Stream OpenBlob(string sha256) =>
        new FileStream(BlobPath(sha256), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);

    private string VersionPath(string unitId, string versionId) => Path.Combine(UnitsDir, unitId, "versions", versionId + ".json");

    private string BlobPath(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException($"Invalid SHA-256: '{sha256}'.", nameof(sha256));
        }

        var lower = sha256.ToLowerInvariant();
        return Path.Combine(BlobsDir, lower[..2], lower);
    }

    private static IEnumerable<string> EnumerateJson(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !AtomicFile.IsTempName(Path.GetFileName(f)))
            : [];

    private static T? ReadJson<T>(string path)
        where T : class
    {
        try
        {
            return File.Exists(path) ? Json.Deserialize<T>(File.ReadAllBytes(path)) : null;
        }
        catch (JsonException)
        {
            // Half-synced or foreign file: treat as not there yet.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains(".."))
        {
            throw new ArgumentException($"Invalid id '{id}'.", nameof(id));
        }
    }
}
