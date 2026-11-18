using SaveFileSync.Core.IO;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Sync;

namespace SaveFileSync.Core.Recovery;

public sealed record RecoveryVersion(string VersionId, string DeviceName, DateTime CreatedUtc, long TotalSize, int FileCount, VersionKind Kind, bool IsTip, bool IsComplete);

public sealed record RecoveryUnit(string UnitId, string Name, bool Removed, IReadOnlyList<RecoveryVersion> Versions);

public sealed record ExportReport(IReadOnlyList<string> ExportedFolders, int FilesWritten, IReadOnlyList<string> Problems);

/// <summary>
/// Read-only access to a repository for the <c>list</c> and <c>export</c> CLI commands
/// (PLAN.md §4.1). Needs no local config or state, and never writes to the repository.
/// </summary>
public sealed class RecoveryService
{
    private readonly IRemoteStore _remote;

    public RecoveryService(IRemoteStore remote)
    {
        _remote = remote;
    }

    public IReadOnlyList<RecoveryUnit> ListUnits()
    {
        var devices = _remote.ReadDevices().ToDictionary(d => d.DeviceId, d => d.Name, StringComparer.Ordinal);
        var result = new List<RecoveryUnit>();
        foreach (var unitId in _remote.ListUnitIds())
        {
            var definition = _remote.ReadUnitDefinition(unitId);
            var (all, graph) = Load(unitId);
            var tips = graph.Tips.Select(t => t.VersionId).ToHashSet(StringComparer.Ordinal);
            var versions = all
                .OrderBy(v => v.CreatedUtc)
                .Select(v => new RecoveryVersion(
                    v.VersionId,
                    devices.GetValueOrDefault(v.DeviceId, v.DeviceShortId),
                    v.CreatedUtc,
                    v.TotalSize,
                    v.Files.Count,
                    v.Kind,
                    tips.Contains(v.VersionId),
                    graph.Contains(v.VersionId)))
                .ToList();
            result.Add(new RecoveryUnit(unitId, definition?.Name ?? unitId, definition?.Removed ?? false, versions));
        }

        return result;
    }

    /// <summary>Finds a unit by exact id, then by name (case-insensitive).</summary>
    public string? FindUnit(string nameOrId)
    {
        var ids = _remote.ListUnitIds();
        if (ids.Contains(nameOrId, StringComparer.Ordinal))
        {
            return nameOrId;
        }

        var matches = ids
            .Where(id => string.Equals(_remote.ReadUnitDefinition(id)?.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Writes the files of a version to <c>&lt;target&gt;\&lt;location&gt;\&lt;relative path&gt;</c>.
    /// Without <paramref name="versionId"/> the latest tip is exported; several tips are
    /// exported to one subfolder each.
    /// </summary>
    public ExportReport Export(string unitId, string targetFolder, string? versionId = null)
    {
        var devices = _remote.ReadDevices().ToDictionary(d => d.DeviceId, d => d.Name, StringComparer.Ordinal);
        var (all, graph) = Load(unitId);
        List<VersionManifest> selected;
        if (versionId is not null)
        {
            var v = all.FirstOrDefault(x => x.VersionId == versionId)
                ?? throw new ArgumentException($"Version '{versionId}' not found in unit '{unitId}'.");
            selected = [v];
        }
        else
        {
            selected = graph.Tips.ToList();
            if (selected.Count == 0)
            {
                throw new InvalidOperationException($"Unit '{unitId}' has no complete version to export.");
            }
        }

        var folders = new List<string>();
        var problems = new List<string>();
        var written = 0;
        foreach (var version in selected)
        {
            var folder = selected.Count == 1
                ? targetFolder
                : Path.Combine(targetFolder, Sanitize($"{devices.GetValueOrDefault(version.DeviceId, version.DeviceShortId)}-{version.VersionId}"));
            folders.Add(folder);
            foreach (var file in version.Files)
            {
                var destination = Path.GetFullPath(Path.Combine(folder, Sanitize(file.Loc), file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{file.Loc}/{file.Path}: path outside the target folder, skipped");
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    string actual;
                    using (var blob = _remote.OpenBlob(file.Sha256))
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        actual = Hashing.CopyAndHash(blob, output);
                    }

                    File.SetLastWriteTimeUtc(destination, file.MtimeUtc);
                    if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{file.Loc}/{file.Path}: content hash mismatch (blob corrupt or still syncing)");
                    }
                    else
                    {
                        written++;
                    }
                }
                catch (IOException ex)
                {
                    problems.Add($"{file.Loc}/{file.Path}: {ex.Message}");
                }
            }
        }

        return new ExportReport(folders, written, problems);
    }

    private (List<VersionManifest> All, VersionGraph Complete) Load(string unitId)
    {
        var all = _remote.ListVersionIds(unitId)
            .Select(id => _remote.ReadVersion(unitId, id))
            .OfType<VersionManifest>()
            .ToList();
        var complete = all.Where(v => v.Files.All(f => _remote.GetBlobState(f.Sha256, f.Size) is BlobState.Present or BlobState.CloudOnly));
        return (all, new VersionGraph(complete));
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
