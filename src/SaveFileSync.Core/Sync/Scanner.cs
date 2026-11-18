using SaveFileSync.Core.IO;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.State;

namespace SaveFileSync.Core.Sync;

/// <summary>Result of scanning the bound locations of a unit on this PC.</summary>
public sealed class LocalScan
{
    public required Snapshot Snapshot { get; init; }

    /// <summary>A file changed while it was being read: the game is probably still writing.</summary>
    public bool Unstable { get; init; }

    /// <summary>Locations without a binding or whose folder does not exist.</summary>
    public IReadOnlyList<string> MissingLocations { get; init; } = [];

    /// <summary>File key → absolute path on this PC.</summary>
    public IReadOnlyDictionary<string, string> AbsolutePaths { get; init; } = new Dictionary<string, string>();
}

public sealed class Scanner
{
    public const string ApplyTempSuffix = ".sfs-tmp";

    private readonly ILocalState _state;

    public Scanner(ILocalState state)
    {
        _state = state;
    }

    public LocalScan Scan(UnitDefinition unit, IReadOnlyDictionary<string, string> bindings, bool updateIndex = true)
    {
        var missing = new List<string>();
        foreach (var loc in unit.Locations)
        {
            if (!bindings.TryGetValue(loc.Id, out var root) || !Directory.Exists(root))
            {
                missing.Add(loc.Id);
            }
        }

        if (missing.Count > 0)
        {
            return new LocalScan { Snapshot = Snapshot.Empty, MissingLocations = missing };
        }

        var index = _state.GetFileIndex(unit.UnitId);
        var entries = new List<FileEntry>();
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unstable = false;

        foreach (var loc in unit.Locations)
        {
            var root = Path.GetFullPath(bindings[loc.Id]);
            var filter = new PathFilter(loc.Includes, loc.Excludes);

            foreach (var file in EnumerateFiles(root))
            {
                if (file.Name.EndsWith(ApplyTempSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                if (!filter.IsIncluded(relative))
                {
                    continue;
                }

                var key = FileEntry.MakeKey(loc.Id, relative);
                long size;
                DateTime mtime;
                string sha;
                try
                {
                    size = file.Length;
                    mtime = file.LastWriteTimeUtc;
                    if (index.TryGetValue(key, out var cached) && cached.Size == size && cached.MtimeTicks == mtime.Ticks)
                    {
                        sha = cached.Sha256;
                    }
                    else
                    {
                        sha = Hashing.HashFile(file.FullName);
                        file.Refresh();
                        if (!file.Exists || file.Length != size || file.LastWriteTimeUtc != mtime)
                        {
                            unstable = true;
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    unstable = true;
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    unstable = true;
                    continue;
                }

                entries.Add(new FileEntry(loc.Id, relative, size, sha, mtime));
                paths[key] = file.FullName;
            }
        }

        if (updateIndex && !unstable)
        {
            _state.ReplaceFileIndex(unit.UnitId, entries.Select(e => new IndexEntry(e.Key, e.Size, e.MtimeUtc.Ticks, e.Sha256)));
        }

        return new LocalScan { Snapshot = new Snapshot(entries), Unstable = unstable, AbsolutePaths = paths };
    }

    /// <summary>
    /// Recursive enumeration that does not follow directory junctions/symlinks.
    /// Access errors are not swallowed: silently skipping a folder would look like a
    /// deletion and could be propagated to every PC.
    /// </summary>
    private static IEnumerable<FileInfo> EnumerateFiles(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
            {
                if (entry is DirectoryInfo sub)
                {
                    // Folders inside OneDrive are reparse points too (cloud files), so only
                    // real links (symlinks/junctions, which have a LinkTarget) are skipped.
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0 || sub.LinkTarget is null)
                    {
                        pending.Push(sub);
                    }
                }
                else if (entry is FileInfo file)
                {
                    yield return file;
                }
            }
        }
    }
}
