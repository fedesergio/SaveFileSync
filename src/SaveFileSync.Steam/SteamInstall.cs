namespace SaveFileSync.Steam;

public sealed record SteamApp(int AppId, string Name, string InstallDir, string LibraryPath)
{
    public string InstallPath => Path.Combine(LibraryPath, "steamapps", "common", InstallDir);
}

/// <summary>A Steam account that logged in on this PC: <c>userdata\&lt;id&gt;</c>.</summary>
public sealed record SteamAccount(string Id, string UserdataDir);

/// <summary>
/// What this PC's Steam installation looks like (PLAN.md §11, phase 5): library folders, installed games
/// and accounts. Read-only: nothing here ever writes into Steam's folders.
/// </summary>
public sealed class SteamInstall
{
    private SteamInstall(string root, IReadOnlyList<string> libraries, IReadOnlyList<SteamApp> apps, IReadOnlyList<SteamAccount> accounts)
    {
        Root = root;
        Libraries = libraries;
        Apps = apps;
        Accounts = accounts;
    }

    public string Root { get; }

    /// <summary>Library folders (each contains <c>steamapps</c>), the Steam folder itself first.</summary>
    public IReadOnlyList<string> Libraries { get; }

    public IReadOnlyList<SteamApp> Apps { get; }

    public IReadOnlyList<SteamAccount> Accounts { get; }

    /// <summary><c>steamapps\common</c> of every library: where games are installed (used for <c>&lt;base&gt;</c>).</summary>
    public IEnumerable<string> CommonDirs => Libraries.Select(l => Path.Combine(l, "steamapps", "common")).Where(Directory.Exists);

    public IEnumerable<string> UserdataDirs => Accounts.Select(a => a.UserdataDir);

    /// <summary>Null when <paramref name="steamPath"/> is not a Steam folder.</summary>
    public static SteamInstall? Discover(string? steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            return null;
        }

        var root = Path.GetFullPath(steamPath.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(Path.Combine(root, "steamapps")) && !Directory.Exists(Path.Combine(root, "userdata")))
        {
            return null;
        }

        var libraries = new List<string> { root };
        var folders = Vdf.TryParseFile(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
        if (folders is not null)
        {
            // Newer files wrap everything in "libraryfolders"; each child has a "path".
            var container = folders["libraryfolders"] ?? folders;
            foreach (var child in container.Children.Values)
            {
                if (child.String("path") is { Length: > 0 } path)
                {
                    var full = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
                    if (!libraries.Contains(full, StringComparer.OrdinalIgnoreCase) && Directory.Exists(full))
                    {
                        libraries.Add(full);
                    }
                }
            }
        }

        var apps = new List<SteamApp>();
        foreach (var library in libraries)
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                var state = Vdf.TryParseFile(file)?["AppState"];
                if (state is not null
                    && int.TryParse(state.String("appid"), out var appId)
                    && state.String("installdir") is { Length: > 0 } installDir)
                {
                    apps.Add(new SteamApp(appId, state.String("name") ?? $"Steam app {appId}", installDir, library));
                }
            }
        }

        var accounts = new List<SteamAccount>();
        var userdata = Path.Combine(root, "userdata");
        if (Directory.Exists(userdata))
        {
            foreach (var dir in Directory.EnumerateDirectories(userdata))
            {
                var id = Path.GetFileName(dir);
                // "0" is Steam's placeholder; real account folders are numeric ids.
                if (id.All(char.IsAsciiDigit) && id != "0")
                {
                    accounts.Add(new SteamAccount(id, dir));
                }
            }
        }

        return new SteamInstall(root, libraries, apps.DistinctBy(a => a.AppId).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(), accounts);
    }
}
