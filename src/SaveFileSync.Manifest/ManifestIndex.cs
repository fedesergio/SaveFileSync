using System.Text;
using System.Text.Json;

namespace SaveFileSync.Manifest;

/// <summary>In-memory index of the manifest with name search.</summary>
public sealed class ManifestIndex
{
    private readonly string[][] _searchKeys;

    public ManifestIndex(IReadOnlyList<GameRecord> games)
    {
        Games = games;
        _searchKeys = games.Select(g => g.Aliases.Prepend(g.Name).Select(Normalize).ToArray()).ToArray();
    }

    public IReadOnlyList<GameRecord> Games { get; }

    /// <summary>Case- and punctuation-insensitive search: every word must appear; better matches first.</summary>
    public IReadOnlyList<GameRecord> Search(string query, int max = 50)
    {
        var q = Normalize(query);
        if (q.Length == 0)
        {
            return [];
        }

        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(int Score, GameRecord Game)>();
        for (var i = 0; i < Games.Count; i++)
        {
            var best = int.MaxValue;
            foreach (var key in _searchKeys[i])
            {
                best = Math.Min(best, Score(key, q, words));
            }

            if (best != int.MaxValue)
            {
                scored.Add((best, Games[i]));
            }
        }

        return scored
            .OrderBy(s => s.Score)
            .ThenBy(s => s.Game.Name.Length)
            .ThenBy(s => s.Game.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(s => s.Game)
            .ToList();
    }

    public GameRecord? Find(string name) =>
        Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    private static int Score(string key, string query, string[] words)
    {
        if (key == query)
        {
            return 0;
        }

        if (key.StartsWith(query, StringComparison.Ordinal))
        {
            return 1;
        }

        if (!words.All(w => key.Contains(w, StringComparison.Ordinal)))
        {
            return int.MaxValue;
        }

        return key.Contains(query, StringComparison.Ordinal) ? 2 : 3;
    }

    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    // ---------------------------------------------------------------- cache file

    public const int CacheFormat = 1;

    private sealed class CacheFile
    {
        public int Format { get; set; }

        public List<GameRecord> Games { get; set; } = [];
    }

    private static readonly JsonSerializerOptions CacheOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Save(string path)
    {
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new CacheFile { Format = CacheFormat, Games = Games.ToList() }, CacheOptions);
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    /// <summary>Loads a cache written by <see cref="Save"/>; null if missing, unreadable or from another format.</summary>
    public static ManifestIndex? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var file = JsonSerializer.Deserialize<CacheFile>(stream, CacheOptions);
            return file is { Format: CacheFormat } ? new ManifestIndex(file.Games) : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
