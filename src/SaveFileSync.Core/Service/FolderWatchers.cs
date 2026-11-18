using System.Collections.Concurrent;
using SaveFileSync.Core.IO;
using SaveFileSync.Core.Sync;

namespace SaveFileSync.Core.Service;

/// <summary>
/// FileSystemWatcher wrappers. Watchers are only triggers: they enqueue "something
/// changed" signals; periodic full scans cover dropped events (PLAN.md §6.5).
/// </summary>
internal sealed class FolderWatchers : IDisposable
{
    private readonly ConcurrentQueue<string> _localSignals = new();
    private readonly Dictionary<string, List<FileSystemWatcher>> _local = new(StringComparer.Ordinal);
    private FileSystemWatcher? _remote;
    private int _remoteSignal;

    /// <summary>Unit ids whose folders changed since the last call.</summary>
    public HashSet<string> DrainLocalSignals()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (_localSignals.TryDequeue(out var unitId))
        {
            result.Add(unitId);
        }

        return result;
    }

    public bool DrainRemoteSignal() => Interlocked.Exchange(ref _remoteSignal, 0) != 0;

    public void WatchUnit(string unitId, IEnumerable<string> folders)
    {
        UnwatchUnit(unitId);
        var list = new List<FileSystemWatcher>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                FileSystemEventHandler handler = (_, e) =>
                {
                    if (!e.FullPath.EndsWith(Scanner.ApplyTempSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        _localSignals.Enqueue(unitId);
                    }
                };
                watcher.Changed += handler;
                watcher.Created += handler;
                watcher.Deleted += handler;
                watcher.Renamed += (_, _) => _localSignals.Enqueue(unitId);
                watcher.Error += (_, _) => _localSignals.Enqueue(unitId);
                watcher.EnableRaisingEvents = true;
                list.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // Periodic scans still cover this folder.
            }
        }

        _local[unitId] = list;
    }

    public void UnwatchUnit(string unitId)
    {
        if (_local.Remove(unitId, out var list))
        {
            foreach (var w in list)
            {
                w.Dispose();
            }
        }
    }

    public void WatchRepository(string root)
    {
        _remote?.Dispose();
        _remote = null;
        var units = Path.Combine(root, "units");
        if (!Directory.Exists(units))
        {
            return;
        }

        try
        {
            _remote = new FileSystemWatcher(units)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler handler = (_, e) =>
            {
                if (!AtomicFile.IsTempName(Path.GetFileName(e.FullPath)))
                {
                    Interlocked.Exchange(ref _remoteSignal, 1);
                }
            };
            _remote.Created += handler;
            _remote.Changed += handler;
            _remote.Renamed += (_, _) => Interlocked.Exchange(ref _remoteSignal, 1);
            _remote.Error += (_, _) => Interlocked.Exchange(ref _remoteSignal, 1);
            _remote.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _remote = null;
        }
    }

    public void Dispose()
    {
        foreach (var id in _local.Keys.ToList())
        {
            UnwatchUnit(id);
        }

        _remote?.Dispose();
    }
}
