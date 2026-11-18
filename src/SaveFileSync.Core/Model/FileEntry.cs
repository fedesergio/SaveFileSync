using System.Text.Json.Serialization;

namespace SaveFileSync.Core.Model;

/// <summary>
/// One file inside a sync unit. <see cref="Path"/> is relative to the location root
/// and always uses '/' as separator. Paths are compared case-insensitively (Windows).
/// </summary>
public sealed record FileEntry(string Loc, string Path, long Size, string Sha256, DateTime MtimeUtc)
{
    [JsonIgnore]
    public string Key => MakeKey(Loc, Path);

    public static string MakeKey(string loc, string path) => loc + "/" + path;
}
