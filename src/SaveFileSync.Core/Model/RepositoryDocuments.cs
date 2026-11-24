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

/// <summary>
/// A set of version ids written by one device: pins (<c>units/&lt;id&gt;/pins/&lt;deviceId&gt;.json</c>) or
/// archived versions (<c>units/&lt;id&gt;/archive/&lt;deviceId&gt;.json</c>). Each device writes only its
/// own file; readers use the union of all files (PLAN.md §4, "one writer per file").
/// </summary>
public sealed class IdSetDocument
{
    public required string DeviceId { get; init; }

    public IReadOnlyList<string> VersionIds { get; init; } = [];

    public DateTime UpdatedUtc { get; init; }
}

/// <summary>
/// Blobs that a device found unreferenced (<c>gc/&lt;deviceId&gt;.json</c>) and when. A blob is
/// deleted only long after it was first marked and only if it is still unreferenced.
/// </summary>
public sealed class GcMarkDocument
{
    public required string DeviceId { get; init; }

    /// <summary>sha256 → time it was first seen unreferenced by this device.</summary>
    public IReadOnlyDictionary<string, DateTime> Marks { get; init; } = new Dictionary<string, DateTime>();

    public DateTime UpdatedUtc { get; init; }
}

/// <summary>
/// <c>retired/&lt;deviceId&gt;.json</c>, written by the PC that retired it. The device counts as retired
/// until it shows up again (its <c>lastSeenUtc</c> becomes newer than <see cref="RetiredUtc"/>).
/// </summary>
public sealed class RetiredDevice
{
    public required string DeviceId { get; init; }

    public DateTime RetiredUtc { get; init; }

    public string? RetiredByDeviceId { get; init; }
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

    /// <summary>
    /// Where this folder is found on a PC, as templates with tokens such as
    /// <c>&lt;winAppData&gt;/Game</c> or <c>&lt;base&gt;/saves</c> (PLAN.md §7.1). Never contains
    /// absolute paths or user names. Null for manual locations, which are bound by hand.
    /// </summary>
    public IReadOnlyList<string>? Templates { get; init; }
}

/// <summary>How a unit was created and what the app knows about it (PLAN.md §7.3).</summary>
public sealed class UnitSource
{
    public const string ManualKind = "manual";
    public const string ManifestKind = "manifest";
    public const string PresetKind = "preset";

    /// <summary>A Steam game backed up read-only (phase 5).</summary>
    public const string SteamKind = "steam";

    public string Kind { get; init; } = ManualKind;

    /// <summary>Game name in the Ludusavi manifest.</summary>
    public string? ManifestName { get; init; }

    public int? SteamId { get; init; }

    /// <summary>Install folder names from the manifest; <c>&lt;base&gt;</c> = library root + one of these.</summary>
    public IReadOnlyList<string> InstallDirs { get; init; } = [];

    /// <summary>Emulator preset id (e.g. "rpcs3"); the preset knows how to find its folders on each PC.</summary>
    public string? PresetId { get; init; }

    /// <summary>Number of Windows registry keys the game also uses: they are not synced (PLAN.md §7.2).</summary>
    public int RegistryKeys { get; init; }
}

/// <summary>
/// What the metadata sources know about a game (PLAN.md §11, phase 4). Shared by all PCs so that a
/// match corrected once applies everywhere; contains only public identifiers, never keys or paths.
/// </summary>
public sealed class UnitMetadata
{
    public long? SteamGridDbGameId { get; init; }

    /// <summary>Thumbnail of the chosen cover on SteamGridDB (every PC downloads it itself, no key needed).</summary>
    public string? CoverUrl { get; init; }

    /// <summary>Author of the cover, for attribution.</summary>
    public string? CoverAuthor { get; init; }

    public long? IgdbId { get; init; }

    /// <summary>Official name from IGDB.</summary>
    public string? OfficialName { get; init; }

    /// <summary>True once the user picked the match by hand: automatic matching never touches it again.</summary>
    public bool Confirmed { get; init; }
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

    public UnitSource? Source { get; init; }

    /// <summary>How many versions stay restorable; null means <see cref="DefaultKeepVersions"/>. Shared by all PCs.</summary>
    public int? KeepVersions { get; init; }

    /// <summary>Cover and official name (phase 4); null until a metadata source matched the game.</summary>
    public UnitMetadata? Metadata { get; init; }

    public const int DefaultKeepVersions = 10;

    public int EffectiveKeepVersions => Math.Clamp(KeepVersions ?? DefaultKeepVersions, 1, 1000);

    /// <summary>A copy with the next revision number; only the given fields change.</summary>
    public UnitDefinition Revised(
        string deviceShortId,
        DateTime utcNow,
        string? name = null,
        IReadOnlyList<LocationDefinition>? locations = null,
        IReadOnlyList<string>? processNames = null,
        MergeGranularity? granularity = null,
        bool? removed = null,
        UnitSource? source = null,
        int? keepVersions = null,
        UnitMetadata? metadata = null) => new()
    {
        KeepVersions = keepVersions ?? KeepVersions,
        Metadata = metadata ?? Metadata,
        UnitId = UnitId,
        Name = name ?? Name,
        Locations = locations ?? Locations,
        ProcessNames = processNames ?? ProcessNames,
        Granularity = granularity ?? Granularity,
        Mode = Mode,
        Removed = removed ?? Removed,
        Source = source ?? Source,
        Revision = Revision + 1,
        RevisionDevice = deviceShortId,
        UpdatedUtc = utcNow,
    };
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
