using System.Text.Json;
using SaveFileSync.Core;
using SaveFileSync.Core.IO;
using SaveFileSync.Core.Model;

namespace SaveFileSync.Repository;

/// <summary>
/// Repository format v1 on a folder synced by the OneDrive client (PLAN.md §4).
/// Every file has a single writer; every write is temp + rename; readers ignore temps.
/// </summary>
public sealed class FolderRemoteStore : IRemoteStore, IRepositoryDiagnostics
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

    /// <summary>
    /// Files whose name does not match their content (e.g. OneDrive conflict copies such as
    /// "&lt;id&gt;-DESKTOP.json") are ignored and counted in <see cref="IgnoredFiles"/>.
    /// </summary>
    public IReadOnlyList<DeviceInfo> ReadDevices() =>
        EnumerateJson(DevicesDir)
            .Select(f => (File: f, Device: ReadJson<DeviceInfo>(f)))
            .Where(x => x.Device is not null && NameMatches(x.File, x.Device.DeviceId))
            .Select(x => x.Device!)
            .ToList();

    /// <summary>Repository files ignored because they look like OneDrive conflict copies or foreign files.</summary>
    public IReadOnlyList<string> FindIgnoredFiles()
    {
        var ignored = new List<string>();
        ignored.AddRange(EnumerateJson(DevicesDir).Where(f => ReadJson<DeviceInfo>(f) is not { } d || !NameMatches(f, d.DeviceId)));
        foreach (var unitId in ListUnitIds())
        {
            ignored.AddRange(EnumerateJson(Path.Combine(UnitsDir, unitId, "versions"))
                .Where(f => !IsValidVersionId(Path.GetFileNameWithoutExtension(f))));
            ignored.AddRange(EnumerateJson(Path.Combine(UnitsDir, unitId, "heads"))
                .Where(f => ReadJson<HeadInfo>(f) is not { } h || !NameMatches(f, h.DeviceId)));
        }

        return ignored;
    }

    /// <summary>Version ids are "&lt;8 hex device short id&gt;-&lt;sequence, 6+ digits&gt;".</summary>
    public static bool IsValidVersionId(string id)
    {
        var dash = id.IndexOf('-');
        return dash == 8
            && id[..8].All(Uri.IsHexDigit)
            && id.Length >= 15
            && id[9..].All(char.IsAsciiDigit);
    }

    private static bool NameMatches(string file, string id) =>
        string.Equals(Path.GetFileNameWithoutExtension(file), id, StringComparison.OrdinalIgnoreCase);

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
            .Where(IsValidVersionId)
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
        EnumerateJson(Path.Combine(UnitsDir, unitId, "heads"))
            .Select(f => (File: f, Head: ReadJson<HeadInfo>(f)))
            .Where(x => x.Head is not null && NameMatches(x.File, x.Head.DeviceId))
            .Select(x => x.Head!)
            .ToList();

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

    // ---------------------------------------------------------------- history (phase 3)

    public IReadOnlyList<IdSetDocument> ReadPins(string unitId) => ReadIdSets(Path.Combine(UnitsDir, unitId, "pins"));

    public void WritePins(string unitId, IdSetDocument document, string deviceShort) =>
        AtomicFile.WriteAllBytes(Path.Combine(UnitsDir, unitId, "pins", document.DeviceId + ".json"), Json.Serialize(document), deviceShort);

    public IReadOnlyList<IdSetDocument> ReadArchive(string unitId) => ReadIdSets(Path.Combine(UnitsDir, unitId, "archive"));

    public void WriteArchive(string unitId, IdSetDocument document, string deviceShort) =>
        AtomicFile.WriteAllBytes(Path.Combine(UnitsDir, unitId, "archive", document.DeviceId + ".json"), Json.Serialize(document), deviceShort);

    public IReadOnlyList<GcMarkDocument> ReadGcMarks() =>
        EnumerateJson(Path.Combine(Root, "gc"))
            .Select(f => (File: f, Doc: ReadJson<GcMarkDocument>(f)))
            .Where(x => x.Doc is not null && NameMatches(x.File, x.Doc.DeviceId))
            .Select(x => x.Doc!)
            .ToList();

    public void WriteGcMarks(GcMarkDocument document, string deviceShort) =>
        AtomicFile.WriteAllBytes(Path.Combine(Root, "gc", document.DeviceId + ".json"), Json.Serialize(document), deviceShort);

    public IReadOnlyList<RetiredDevice> ReadRetired() =>
        EnumerateJson(Path.Combine(Root, "retired"))
            .Select(f => (File: f, Doc: ReadJson<RetiredDevice>(f)))
            .Where(x => x.Doc is not null && NameMatches(x.File, x.Doc.DeviceId))
            .Select(x => x.Doc!)
            .ToList();

    public void WriteRetired(RetiredDevice document, string deviceShort) =>
        AtomicFile.WriteAllBytes(Path.Combine(Root, "retired", document.DeviceId + ".json"), Json.Serialize(document), deviceShort);

    public VersionScan ScanVersions(string unitId)
    {
        var versions = new List<VersionManifest>();
        var unreadable = 0;
        foreach (var id in ListVersionIds(unitId))
        {
            // A valid name that cannot be parsed or does not match its content may still be arriving.
            var version = ReadVersion(unitId, id);
            if (version is null)
            {
                unreadable++;
            }
            else
            {
                versions.Add(version);
            }
        }

        return new VersionScan(versions, unreadable);
    }

    public int CountRecentTempFiles(TimeSpan maxAge, DateTime utcNow)
    {
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        return Directory.EnumerateFiles(Root, "*" + AtomicFile.TempMarker + "*", options)
            .Count(f => utcNow - File.GetLastWriteTimeUtc(f) < maxAge);
    }

    public IReadOnlyList<BlobInfo> EnumerateBlobs()
    {
        if (!Directory.Exists(BlobsDir))
        {
            return [];
        }

        var result = new List<BlobInfo>();
        foreach (var file in new DirectoryInfo(BlobsDir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.Name.Length == 64 && file.Name.All(Uri.IsHexDigit) && !AtomicFile.IsTempName(file.Name))
            {
                result.Add(new BlobInfo(file.Name.ToLowerInvariant(), file.Length, file.LastWriteTimeUtc));
            }
        }

        return result;
    }

    public bool DeleteBlob(string sha256)
    {
        var path = BlobPath(sha256);
        if (!File.Exists(path))
        {
            return false;
        }

        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
        var folder = Path.GetDirectoryName(path)!;
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            try
            {
                Directory.Delete(folder);
            }
            catch (IOException)
            {
                // Another file arrived at the same time: leave the folder.
            }
        }

        return true;
    }

    private IReadOnlyList<IdSetDocument> ReadIdSets(string dir) =>
        EnumerateJson(dir)
            .Select(f => (File: f, Doc: ReadJson<IdSetDocument>(f)))
            .Where(x => x.Doc is not null && NameMatches(x.File, x.Doc.DeviceId))
            .Select(x => x.Doc!)
            .ToList();

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
