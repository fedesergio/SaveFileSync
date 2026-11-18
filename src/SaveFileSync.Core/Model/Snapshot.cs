using System.Security.Cryptography;
using System.Text;

namespace SaveFileSync.Core.Model;

/// <summary>
/// An immutable set of files (local scan or remote version). Two snapshots have the
/// same content when they contain the same keys (case-insensitive) with the same hashes;
/// sizes follow from the hashes and mtimes are ignored.
/// </summary>
public sealed class Snapshot
{
    public static readonly Snapshot Empty = new([]);

    private readonly Dictionary<string, FileEntry> _files;

    public Snapshot(IEnumerable<FileEntry> files)
    {
        _files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            _files[f.Key] = f;
        }

        ContentId = ComputeContentId(_files.Values);
        TotalSize = _files.Values.Sum(f => f.Size);
    }

    public IReadOnlyCollection<FileEntry> Files => _files.Values;

    public int Count => _files.Count;

    public long TotalSize { get; }

    public bool IsEmpty => _files.Count == 0;

    /// <summary>Hash of the sorted (key, sha256) pairs: equal content ⇔ equal id.</summary>
    public string ContentId { get; }

    public bool ContentEquals(Snapshot other) => string.Equals(ContentId, other.ContentId, StringComparison.Ordinal);

    public bool TryGet(string key, out FileEntry entry) => _files.TryGetValue(key, out entry!);

    private static string ComputeContentId(IEnumerable<FileEntry> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(f => f.Key.ToUpperInvariant(), StringComparer.Ordinal))
        {
            sb.Append(f.Key.ToUpperInvariant()).Append('\0').Append(f.Sha256.ToLowerInvariant()).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
