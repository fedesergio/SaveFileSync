using System.Text.Json.Serialization;

namespace SaveFileSync.Core.Model;

/// <summary><c>repo.json</c> at the repository root.</summary>
public sealed class RepoInfo
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;

    public required string RepoId { get; init; }

    public DateTime CreatedUtc { get; init; }
}

/// <summary><c>devices/&lt;deviceId&gt;.json</c>; written only by that device.</summary>
public sealed class DeviceInfo
{
    public required string DeviceId { get; init; }

    public required string ShortId { get; init; }

    public required string Name { get; init; }

    public string? AppVersion { get; init; }

    public string? Fingerprint { get; init; }

    public DateTime LastSeenUtc { get; init; }
}

/// <summary><c>units/&lt;unitId&gt;/heads/&lt;deviceId&gt;.json</c>: the device's current base.</summary>
public sealed class HeadInfo
{
    public required string DeviceId { get; init; }

    public required string VersionId { get; init; }

    public DateTime UpdatedUtc { get; init; }
}

/// <summary>A logical save folder of a unit. Its absolute path is bound per device.</summary>
public sealed class LocationDefinition
{
    public required string Id { get; init; }

    public string? Label { get; init; }

    public IReadOnlyList<string> Includes { get; init; } = ["**"];

    public IReadOnlyList<string> Excludes { get; init; } = [];
}

/// <summary>
/// A unit definition revision (<c>units/&lt;unitId&gt;/def/&lt;revision&gt;-&lt;devShort&gt;.json</c>).
/// The highest (revision, device) pair wins.
/// </summary>
public sealed class UnitDefinition
{
    public int Format { get; init; } = 1;

    public required string UnitId { get; init; }

    public required string Name { get; init; }

    public IReadOnlyList<LocationDefinition> Locations { get; init; } = [];

    public IReadOnlyList<string> ProcessNames { get; init; } = [];

    public MergeGranularity Granularity { get; init; } = MergeGranularity.Unit;

    public UnitMode Mode { get; init; } = UnitMode.Sync;

    public long Revision { get; init; } = 1;

    public string? RevisionDevice { get; init; }

    public DateTime UpdatedUtc { get; init; }

    /// <summary>Set when the unit was removed from sync on all PCs.</summary>
    public bool Removed { get; init; }
}

/// <summary>An immutable version manifest (<c>units/&lt;unitId&gt;/versions/&lt;versionId&gt;.json</c>).</summary>
public sealed class VersionManifest
{
    private Snapshot? _snapshot;

    public int Format { get; init; } = 1;

    public required string UnitId { get; init; }

    public required string VersionId { get; init; }

    public required string DeviceId { get; init; }

    public long Seq { get; init; }

    public IReadOnlyList<string> Parents { get; init; } = [];

    public VersionKind Kind { get; init; }

    /// <summary>Clock of the creating device: for display only, never used for ordering.</summary>
    public DateTime CreatedUtc { get; init; }

    public IReadOnlyList<FileEntry> Files { get; init; } = [];

    public long TotalSize { get; init; }

    public string? Note { get; init; }

    [JsonIgnore]
    public Snapshot Content => _snapshot ??= new Snapshot(Files);

    /// <summary>Device short id prefix of the version id ("a1b2c3d4-000042" → "a1b2c3d4").</summary>
    [JsonIgnore]
    public string DeviceShortId => VersionId.Split('-')[0];
}
