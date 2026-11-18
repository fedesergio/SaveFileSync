using System.Security.Cryptography;
using System.Text;
using SaveFileSync.Core.Model;

namespace SaveFileSync.Core.Tests;

/// <summary>Small DSL to build synthetic snapshots and versions (no real save data).</summary>
internal static class TestData
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static Snapshot Snap(params (string Path, string Content)[] files) =>
        new(files.Select(f => new FileEntry("saves", f.Path, f.Content.Length, Sha(f.Content), Epoch)));

    public static VersionManifest V(string id, Snapshot content, params string[] parents) => new()
    {
        UnitId = "unit",
        VersionId = id,
        DeviceId = id.Split('-')[0] + "-device",
        Seq = long.Parse(id.Split('-')[1]),
        Parents = parents,
        Kind = VersionKind.Normal,
        CreatedUtc = Epoch,
        Files = content.Files.ToList(),
        TotalSize = content.TotalSize,
    };

    public static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
