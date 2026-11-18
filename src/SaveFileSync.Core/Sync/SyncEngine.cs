using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SaveFileSync.Core.Config;
using SaveFileSync.Core.IO;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.State;

namespace SaveFileSync.Core.Sync;

public sealed record SyncOptions(bool AllowPush, bool AllowApply)
{
    public static readonly SyncOptions Full = new(true, true);
}

public enum SyncResultKind
{
    UpToDate,
    Pushed,
    Pulled,
    /// <summary>A newer remote version exists but applying it is not allowed now (game running).</summary>
    RemoteNewer,
    /// <summary>Local changes exist but pushing is not allowed now (game running).</summary>
    PushDeferred,
    Conflict,
    /// <summary>Files changed while reading them; retry after the quiet period.</summary>
    Unstable,
    /// <summary>A needed blob is not available yet (OneDrive still syncing).</summary>
    Incomplete,
    LocalEmptied,
    LocationMissing,
    RepositoryUnavailable,
}

public sealed record ConflictInfo(ConflictReason Reason, IReadOnlyList<string> CandidateVersionIds, DateTime DetectedUtc);

public sealed record SyncResult(SyncResultKind Kind, string? Message = null, VersionManifest? RemoteVersion = null)
{
    public static SyncResult Of(SyncResultKind kind, string? message = null) => new(kind, message);
}

/// <summary>
/// Executes one sync pass for a unit: scan → plan → push / apply / record conflict
/// (PLAN.md §6). Not thread-safe: the scheduler calls it from a single worker.
/// </summary>
public sealed class SyncEngine
{
    private const int MaxPlanIterations = 4;

    private readonly IRemoteStore _remote;
    private readonly ILocalState _state;
    private readonly DeviceIdentity _device;
    private readonly IClock _clock;
    private readonly ILogger _log;
    private readonly Scanner _scanner;

    public SyncEngine(IRemoteStore remote, ILocalState state, DeviceIdentity device, IClock clock, ILogger? log = null)
    {
        _remote = remote;
        _state = state;
        _device = device;
        _clock = clock;
        _log = log ?? NullLogger.Instance;
        _scanner = new Scanner(state);
    }

    public IRemoteStore Remote => _remote;

    public SyncResult Sync(UnitDefinition unit, IReadOnlyDictionary<string, string> bindings, SyncOptions options)
    {
        if (!_remote.IsAvailable())
        {
            return SyncResult.Of(SyncResultKind.RepositoryUnavailable);
        }

        var recovered = RecoverInterruptedApply(unit, bindings);
        if (recovered is not null)
        {
            return recovered;
        }

        var pushed = false;
        for (var i = 0; i < MaxPlanIterations; i++)
        {
            var scan = _scanner.Scan(unit, bindings);
            if (scan.MissingLocations.Count > 0)
            {
                return SyncResult.Of(SyncResultKind.LocationMissing, string.Join(", ", scan.MissingLocations));
            }

            if (scan.Unstable)
            {
                return SyncResult.Of(SyncResultKind.Unstable);
            }

            var (graph, incomplete) = LoadGraph(unit.UnitId);
            var unitState = _state.GetUnitState(unit.UnitId);
            var plan = Planner.Decide(unitState?.BaseVersionId, scan.Snapshot, graph);
            _log.LogDebug("Unit {Unit}: plan {Plan}", unit.UnitId, plan);

            switch (plan)
            {
                case SyncPlan.NoOp noOp:
                    if (noOp.AdoptBaseId is not null || unitState?.ConflictJson is not null)
                    {
                        SetBase(unit.UnitId, noOp.AdoptBaseId ?? unitState?.BaseVersionId);
                    }

                    if (pushed)
                    {
                        return SyncResult.Of(SyncResultKind.Pushed);
                    }

                    return incomplete > 0
                        ? SyncResult.Of(SyncResultKind.Incomplete, $"{incomplete} version(s) still arriving")
                        : SyncResult.Of(SyncResultKind.UpToDate);

                case SyncPlan.Push push:
                    if (!options.AllowPush)
                    {
                        return SyncResult.Of(SyncResultKind.PushDeferred);
                    }

                    if (!PushVersion(unit.UnitId, scan, push.Parents, push.Kind, note: null))
                    {
                        return SyncResult.Of(SyncResultKind.Unstable);
                    }

                    pushed = true;
                    continue;

                case SyncPlan.Pull pull:
                    var target = graph.Get(pull.TargetVersionId)!;
                    if (!options.AllowApply)
                    {
                        return new SyncResult(SyncResultKind.RemoteNewer, null, target);
                    }

                    return Apply(unit, bindings, scan, target, force: false);

                case SyncPlan.Conflict conflict:
                    if (conflict.PreserveLocalFirst)
                    {
                        if (!options.AllowPush)
                        {
                            return new SyncResult(SyncResultKind.RemoteNewer, null, graph.Get(conflict.CandidateVersionIds[0]));
                        }

                        var kind = conflict.Reason == ConflictReason.InitialSetup ? VersionKind.Initial : VersionKind.Normal;
                        if (!PushVersion(unit.UnitId, scan, conflict.PreserveParents, kind, note: "Local version saved before a conflict"))
                        {
                            return SyncResult.Of(SyncResultKind.Unstable);
                        }

                        pushed = true;
                        continue;
                    }

                    var info = new ConflictInfo(conflict.Reason, conflict.CandidateVersionIds, _clock.UtcNow);
                    _state.SaveUnitState(new UnitStateRecord(
                        unit.UnitId, unitState?.BaseVersionId, unitState?.LastSyncUtc, System.Text.Json.JsonSerializer.Serialize(info, Json.Options)));
                    _log.LogInformation("Unit {Unit}: conflict between {Candidates}", unit.UnitId, string.Join(", ", conflict.CandidateVersionIds));
                    return SyncResult.Of(SyncResultKind.Conflict);

                case SyncPlan.LocalEmptied:
                    return SyncResult.Of(SyncResultKind.LocalEmptied);
            }
        }

        _log.LogWarning("Unit {Unit}: plan did not settle after {N} iterations", unit.UnitId, MaxPlanIterations);
        return SyncResult.Of(SyncResultKind.Unstable);
    }

    /// <summary>The recorded conflict of a unit, if any.</summary>
    public ConflictInfo? GetConflict(string unitId)
    {
        var json = _state.GetUnitState(unitId)?.ConflictJson;
        return json is null ? null : Json.Deserialize<ConflictInfo>(json);
    }

    public string? GetBaseVersionId(string unitId) => _state.GetUnitState(unitId)?.BaseVersionId;

    public DateTime? GetLastSyncUtc(string unitId) => _state.GetUnitState(unitId)?.LastSyncUtc;

    /// <summary>
    /// Resolves a recorded conflict by keeping <paramref name="chosenVersionId"/>: creates a
    /// <see cref="VersionKind.Resolve"/> version with the candidates as parents and applies it.
    /// The caller must make sure the game is not running.
    /// </summary>
    public SyncResult Resolve(UnitDefinition unit, IReadOnlyDictionary<string, string> bindings, string chosenVersionId)
    {
        var conflict = GetConflict(unit.UnitId);
        if (conflict is null)
        {
            return SyncResult.Of(SyncResultKind.UpToDate);
        }

        var scan = _scanner.Scan(unit, bindings);
        if (scan.MissingLocations.Count > 0)
        {
            return SyncResult.Of(SyncResultKind.LocationMissing, string.Join(", ", scan.MissingLocations));
        }

        if (scan.Unstable)
        {
            return SyncResult.Of(SyncResultKind.Unstable);
        }

        var (graph, _) = LoadGraph(unit.UnitId);
        var chosen = graph.Get(chosenVersionId)
            ?? throw new InvalidOperationException($"Version {chosenVersionId} is not available.");
        var candidates = conflict.CandidateVersionIds.Select(graph.Get).OfType<VersionManifest>().ToList();
        var unitState = _state.GetUnitState(unit.UnitId);
        var baseVersion = graph.Get(unitState?.BaseVersionId);

        var localIsKnown = candidates.Any(c => c.Content.ContentEquals(scan.Snapshot))
            || (baseVersion is not null && baseVersion.Content.ContentEquals(scan.Snapshot));
        if (!localIsKnown)
        {
            // Local files changed after the conflict was recorded: re-evaluate from scratch.
            ClearConflict(unit.UnitId);
            return Sync(unit, bindings, SyncOptions.Full);
        }

        var parents = Minimize(graph, candidates.Select(c => c.VersionId)
            .Concat(baseVersion is null ? [] : [baseVersion.VersionId]));
        var resolved = CreateVersion(unit.UnitId, parents, VersionKind.Resolve, chosen.Files, $"Conflict resolved: kept {chosen.VersionId}");
        _state.PutCachedVersion(resolved, complete: true);

        if (scan.Snapshot.ContentEquals(resolved.Content))
        {
            SetBase(unit.UnitId, resolved.VersionId);
            return SyncResult.Of(SyncResultKind.Pushed);
        }

        var applied = Apply(unit, bindings, scan, resolved, force: false);
        return applied.Kind == SyncResultKind.Pulled ? SyncResult.Of(SyncResultKind.Pushed) : applied;
    }

    /// <summary>"Restore from repository" after the local files disappeared: re-applies the base.</summary>
    public SyncResult RestoreBase(UnitDefinition unit, IReadOnlyDictionary<string, string> bindings)
    {
        var (graph, _) = LoadGraph(unit.UnitId);
        var baseVersion = graph.Get(GetBaseVersionId(unit.UnitId));
        if (baseVersion is null)
        {
            return Sync(unit, bindings, SyncOptions.Full);
        }

        var scan = _scanner.Scan(unit, bindings);
        if (scan.MissingLocations.Count > 0)
        {
            return SyncResult.Of(SyncResultKind.LocationMissing, string.Join(", ", scan.MissingLocations));
        }

        return Apply(unit, bindings, scan, baseVersion, force: true);
    }

    /// <summary>"Delete on all PCs": confirms that the local files were removed on purpose.</summary>
    public SyncResult PropagateDeletion(UnitDefinition unit, IReadOnlyDictionary<string, string> bindings)
    {
        var scan = _scanner.Scan(unit, bindings);
        if (!scan.Snapshot.IsEmpty || scan.MissingLocations.Count > 0)
        {
            return Sync(unit, bindings, SyncOptions.Full);
        }

        var baseId = GetBaseVersionId(unit.UnitId);
        PushVersion(unit.UnitId, scan, baseId is null ? [] : [baseId], VersionKind.Normal, "All files deleted");
        return SyncResult.Of(SyncResultKind.Pushed);
    }

    /// <summary>Reads the complete versions of a unit (cached locally once complete).</summary>
    public (VersionGraph Graph, int Incomplete) LoadGraph(string unitId)
    {
        var complete = new List<VersionManifest>();
        var incomplete = 0;
        foreach (var id in _remote.ListVersionIds(unitId))
        {
            var version = _state.GetCachedVersion(unitId, id, out var isComplete);
            if (version is null)
            {
                version = _remote.ReadVersion(unitId, id);
                if (version is null)
                {
                    incomplete++;
                    continue;
                }

                isComplete = false;
            }

            if (!isComplete)
            {
                isComplete = version.Files.All(f => _remote.GetBlobState(f.Sha256, f.Size) is BlobState.Present or BlobState.CloudOnly);
                _state.PutCachedVersion(version, isComplete);
            }

            if (version.DeviceShortId == _device.ShortId)
            {
                _state.EnsureSeqAbove(version.Seq);
            }

            if (isComplete)
            {
                complete.Add(version);
            }
            else
            {
                incomplete++;
            }
        }

        return (new VersionGraph(complete), incomplete);
    }

    private SyncResult? RecoverInterruptedApply(UnitDefinition unit, IReadOnlyDictionary<string, string> bindings)
    {
        var targetId = _state.GetJournal(unit.UnitId);
        if (targetId is null)
        {
            return null;
        }

        _log.LogWarning("Unit {Unit}: resuming interrupted apply of {Version}", unit.UnitId, targetId);
        DeleteApplyTemps(unit, bindings);

        var (graph, _) = LoadGraph(unit.UnitId);
        var target = graph.Get(targetId) ?? graph.Get(GetBaseVersionId(unit.UnitId));
        if (target is null)
        {
            _state.ClearJournal(unit.UnitId);
            return SyncResult.Of(SyncResultKind.Incomplete, "Interrupted apply: version not available");
        }

        var scan = _scanner.Scan(unit, bindings, updateIndex: false);
        if (scan.MissingLocations.Count > 0)
        {
            return SyncResult.Of(SyncResultKind.LocationMissing, string.Join(", ", scan.MissingLocations));
        }

        var result = Apply(unit, bindings, scan, target, force: true);
        return result.Kind == SyncResultKind.Pulled ? null : result;
    }

    private bool PushVersion(string unitId, LocalScan scan, IReadOnlyList<string> parents, VersionKind kind, string? note)
    {
        try
        {
            foreach (var file in scan.Snapshot.Files)
            {
                var path = scan.AbsolutePaths[file.Key];
                _remote.PutBlob(file.Sha256, file.Size, () => Hashing.OpenShared(path), _device.ShortId);
            }
        }
        catch (Exception ex) when (ex is BlobHashMismatchException or FileNotFoundException or DirectoryNotFoundException)
        {
            _log.LogInformation("Unit {Unit}: a file changed during upload ({Message}); will retry", unitId, ex.Message);
            return false;
        }

        if (!StillMatches(scan))
        {
            return false;
        }

        var version = CreateVersion(unitId, parents, kind, scan.Snapshot.Files.ToList(), note);
        _state.PutCachedVersion(version, complete: true);
        SetBase(unitId, version.VersionId);
        _log.LogInformation("Unit {Unit}: pushed {Version} ({Count} files, {Size} bytes)", unitId, version.VersionId, version.Files.Count, version.TotalSize);
        return true;
    }

    private VersionManifest CreateVersion(string unitId, IReadOnlyList<string> parents, VersionKind kind, IReadOnlyList<FileEntry> files, string? note)
    {
        for (var attempt = 0; ; attempt++)
        {
            var seq = _state.NextSeq();
            var version = new VersionManifest
            {
                UnitId = unitId,
                VersionId = $"{_device.ShortId}-{seq:D6}",
                DeviceId = _device.DeviceId,
                Seq = seq,
                Parents = parents.Distinct(StringComparer.Ordinal).ToList(),
                Kind = kind,
                CreatedUtc = _clock.UtcNow,
                Files = files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList(),
                TotalSize = files.Sum(f => f.Size),
                Note = note,
            };

            try
            {
                _remote.WriteVersion(version, _device.ShortId);
                return version;
            }
            catch (IOException) when (attempt < 50 && _remote.ReadVersion(unitId, version.VersionId) is not null)
            {
                // Sequence number already used (e.g. local state was reset): try the next one.
            }
        }
    }

    private SyncResult Apply(UnitDefinition unit, IReadOnlyDictionary<string, string> bindings, LocalScan scan, VersionManifest target, bool force)
    {
        var toWrite = target.Files
            .Where(f => !(scan.Snapshot.TryGet(f.Key, out var local) && string.Equals(local.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var toDelete = scan.Snapshot.Files.Where(f => !target.Content.TryGet(f.Key, out _)).ToList();
        var staged = new List<(string Temp, string Destination, FileEntry File)>();

        try
        {
            foreach (var file in toWrite)
            {
                var destination = ResolvePath(bindings, file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temp = destination + Scanner.ApplyTempSuffix;
                string actual;
                Stream source;
                try
                {
                    source = _remote.OpenBlob(file.Sha256);
                }
                catch (IOException ex) when (ex is not BlobUnavailableException)
                {
                    throw new BlobUnavailableException(file.Sha256, ex);
                }

                using (var blob = source)
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    actual = Hashing.CopyAndHash(blob, output);
                    output.Flush(flushToDisk: true);
                }

                staged.Add((temp, destination, file));
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new BlobHashMismatchException(file.Sha256, actual);
                }
            }

            if (!force && !StillMatches(scan))
            {
                DeleteTemps(staged);
                return SyncResult.Of(SyncResultKind.Unstable);
            }

            _state.SetJournal(unit.UnitId, target.VersionId, _clock.UtcNow);

            foreach (var (temp, destination, file) in staged)
            {
                if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(destination, FileAttributes.Normal);
                }

                File.Move(temp, destination, overwrite: true);
                File.SetLastWriteTimeUtc(destination, file.MtimeUtc);
            }

            foreach (var file in toDelete)
            {
                var path = scan.AbsolutePaths[file.Key];
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }

                PruneEmptyDirectories(Path.GetDirectoryName(path)!, Path.GetFullPath(bindings[file.Loc]));
            }

            var index = target.Files.Select(f =>
            {
                var written = staged.FirstOrDefault(s => s.File.Key.Equals(f.Key, StringComparison.OrdinalIgnoreCase));
                if (written.Destination is not null)
                {
                    var info = new FileInfo(written.Destination);
                    return new IndexEntry(f.Key, info.Length, info.LastWriteTimeUtc.Ticks, f.Sha256);
                }

                scan.Snapshot.TryGet(f.Key, out var existing);
                return new IndexEntry(f.Key, existing.Size, existing.MtimeUtc.Ticks, existing.Sha256);
            });
            _state.ReplaceFileIndex(unit.UnitId, index);
            SetBase(unit.UnitId, target.VersionId);
            _state.ClearJournal(unit.UnitId);
            _log.LogInformation("Unit {Unit}: applied {Version} ({Written} written, {Deleted} deleted)", unit.UnitId, target.VersionId, toWrite.Count, toDelete.Count);
            return new SyncResult(SyncResultKind.Pulled, null, target);
        }
        catch (Exception ex) when (ex is BlobHashMismatchException or BlobUnavailableException)
        {
            DeleteTemps(staged);
            _log.LogInformation("Unit {Unit}: version {Version} not fully available yet ({Message})", unit.UnitId, target.VersionId, ex.Message);
            return SyncResult.Of(SyncResultKind.Incomplete, ex.Message);
        }
        catch
        {
            DeleteTemps(staged);
            throw;
        }
    }

    private void SetBase(string unitId, string? versionId)
    {
        _state.SaveUnitState(new UnitStateRecord(unitId, versionId, _clock.UtcNow, ConflictJson: null));
        if (versionId is not null)
        {
            _remote.WriteHead(unitId, new HeadInfo { DeviceId = _device.DeviceId, VersionId = versionId, UpdatedUtc = _clock.UtcNow }, _device.ShortId);
        }
    }

    private void ClearConflict(string unitId)
    {
        var s = _state.GetUnitState(unitId);
        if (s is not null)
        {
            _state.SaveUnitState(s with { ConflictJson = null });
        }
    }

    private static bool StillMatches(LocalScan scan)
    {
        foreach (var file in scan.Snapshot.Files)
        {
            var info = new FileInfo(scan.AbsolutePaths[file.Key]);
            if (!info.Exists || info.Length != file.Size || info.LastWriteTimeUtc != file.MtimeUtc)
            {
                return false;
            }
        }

        return true;
    }

    private static string ResolvePath(IReadOnlyDictionary<string, string> bindings, FileEntry file)
    {
        var root = Path.GetFullPath(bindings[file.Loc]);
        var full = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path '{file.Path}' escapes its location.");
        }

        return full;
    }

    private static void PruneEmptyDirectories(string directory, string root)
    {
        var current = Path.GetFullPath(directory);
        while (current.Length > root.Length
               && current.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && Directory.Exists(current)
               && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static void DeleteTemps(IEnumerable<(string Temp, string Destination, FileEntry File)> staged)
    {
        foreach (var s in staged)
        {
            AtomicFile.TryDelete(s.Temp);
        }
    }

    private static void DeleteApplyTemps(UnitDefinition unit, IReadOnlyDictionary<string, string> bindings)
    {
        foreach (var loc in unit.Locations)
        {
            if (bindings.TryGetValue(loc.Id, out var root) && Directory.Exists(root))
            {
                foreach (var temp in Directory.EnumerateFiles(root, "*" + Scanner.ApplyTempSuffix, SearchOption.AllDirectories))
                {
                    AtomicFile.TryDelete(temp);
                }
            }
        }
    }

    /// <summary>Removes parents that are ancestors of other parents.</summary>
    private static List<string> Minimize(VersionGraph graph, IEnumerable<string> parents)
    {
        var list = parents.Distinct(StringComparer.Ordinal).ToList();
        return list
            .Where(p => !list.Any(other => other != p && graph.IsAncestorOrSelf(p, other)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
