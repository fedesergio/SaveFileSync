namespace SaveFileSync.Manifest;

/// <summary>
/// One path of a game from the Ludusavi manifest that applies to Windows.
/// </summary>
/// <param name="Template">Path template with tokens (e.g. <c>&lt;winAppData&gt;/Game/saves</c>).</param>
/// <param name="Save">Tagged "save" in the manifest.</param>
/// <param name="Config">Tagged "config" (settings, not progress).</param>
/// <param name="SteamOnly">Lives in Steam's folders (<c>userdata</c>, <c>steamapps</c>): handled by the Steam phase, not here.</param>
public sealed record PathRule(string Template, bool Save, bool Config, bool SteamOnly);

/// <summary>A game of the manifest, reduced to what this app needs on Windows.</summary>
public sealed class GameRecord
{
    public required string Name { get; init; }

    public int? SteamId { get; init; }

    public int? GogId { get; init; }

    /// <summary>Folder names under a library root where the game is installed.</summary>
    public IReadOnlyList<string> InstallDirs { get; init; } = [];

    /// <summary>Windows executable file names from the manifest's launch entries.</summary>
    public IReadOnlyList<string> Executables { get; init; } = [];

    public IReadOnlyList<PathRule> Paths { get; init; } = [];

    /// <summary>Windows registry keys that hold game data (not synced by this app).</summary>
    public IReadOnlyList<string> RegistryKeys { get; init; } = [];

    /// <summary>Other names of the same game (the manifest's "alias" entries).</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}
