namespace SaveFileSync.Core.Paths;

/// <summary>
/// Windows known folders of the current user, resolved per PC (PLAN.md §7.1).
/// Implemented with the shell known-folder API so that a moved Documents folder works.
/// </summary>
public sealed class KnownFolderSet
{
    public required string Home { get; init; }

    public required string AppData { get; init; }

    public required string LocalAppData { get; init; }

    public required string LocalAppDataLow { get; init; }

    public required string Documents { get; init; }

    public required string Public { get; init; }

    public required string ProgramData { get; init; }

    public required string WinDir { get; init; }

    public required string UserName { get; init; }

    /// <summary>Fallback that uses .NET's folder lookup (it also follows redirected folders).</summary>
    public static KnownFolderSet FromEnvironment()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new KnownFolderSet
        {
            Home = home,
            AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            LocalAppData = local,
            LocalAppDataLow = Path.Combine(Path.GetDirectoryName(local) ?? home, "LocalLow"),
            Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Public = Environment.GetEnvironmentVariable("PUBLIC") ?? Path.Combine(Path.GetDirectoryName(home) ?? home, "Public"),
            ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            UserName = Environment.UserName,
        };
    }
}

/// <summary>Everything needed to turn a template into paths on this PC.</summary>
public sealed class ResolveContext
{
    public required KnownFolderSet Folders { get; init; }

    /// <summary>Folders where games are installed (<c>&lt;root&gt;</c>).</summary>
    public IReadOnlyList<string> LibraryRoots { get; init; } = [];

    /// <summary>Install folder names of the game (<c>&lt;game&gt;</c>, and <c>&lt;base&gt;</c> = root + name).</summary>
    public IReadOnlyList<string> InstallDirs { get; init; } = [];
}

/// <summary>
/// Path templates from the Ludusavi manifest and from our presets: tokens such as
/// <c>&lt;winAppData&gt;</c>, forward slashes, and glob wildcards (<c>*</c>, <c>?</c>, <c>**</c>).
/// </summary>
public static class PathTemplate
{
    public const string StoreUserId = "<storeUserId>";
    public const string StoreGameId = "<storeGameId>";

    /// <summary>True for tokens that only exist on other platforms (Linux/macOS).</summary>
    public static bool IsForeignToken(string template) =>
        template.Contains("<xdg", StringComparison.Ordinal);

    /// <summary>Normalizes separators and trailing slashes.</summary>
    public static string Normalize(string template) =>
        template.Replace('\\', '/').Trim().TrimEnd('/');

    /// <summary>
    /// Expands tokens. A template may give several paths (one per library root / install
    /// folder), may contain wildcards, and gives none if it needs something this PC lacks.
    /// <c>&lt;base&gt;</c> only expands to folders that exist.
    /// </summary>
    public static IReadOnlyList<string> Expand(string template, ResolveContext ctx)
    {
        var t = Normalize(template);
        if (IsForeignToken(t))
        {
            return [];
        }

        var f = ctx.Folders;
        t = t.Replace("<home>", Slash(f.Home), StringComparison.Ordinal)
            .Replace("<winAppData>", Slash(f.AppData), StringComparison.Ordinal)
            .Replace("<winLocalAppData>", Slash(f.LocalAppData), StringComparison.Ordinal)
            .Replace("<winLocalAppDataLow>", Slash(f.LocalAppDataLow), StringComparison.Ordinal)
            .Replace("<winDocuments>", Slash(f.Documents), StringComparison.Ordinal)
            .Replace("<winPublic>", Slash(f.Public), StringComparison.Ordinal)
            .Replace("<winProgramData>", Slash(f.ProgramData), StringComparison.Ordinal)
            .Replace("<winDir>", Slash(f.WinDir), StringComparison.Ordinal)
            .Replace("<osUserName>", f.UserName, StringComparison.Ordinal)
            .Replace(StoreUserId, "*", StringComparison.Ordinal)
            .Replace(StoreGameId, "*", StringComparison.Ordinal);

        var results = new List<string> { t };
        if (t.Contains("<base>", StringComparison.Ordinal))
        {
            results = ExpandWith(results, "<base>", BaseFolders(ctx));
        }

        if (t.Contains("<root>", StringComparison.Ordinal))
        {
            results = ExpandWith(results, "<root>", ctx.LibraryRoots.Select(Slash));
        }

        if (t.Contains("<game>", StringComparison.Ordinal))
        {
            results = ExpandWith(results, "<game>", ctx.InstallDirs);
        }

        return results.Where(r => !r.Contains('<', StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Splits a template at its first wildcard segment: the part before it is a folder
    /// (the "root"), the rest is a relative glob. <c>&lt;storeUserId&gt;</c> counts as a wildcard.
    /// </summary>
    public static (string Root, string? Relative) SplitAtWildcard(string template)
    {
        var segments = Normalize(template).Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (IsWildcardSegment(segments[i]))
            {
                return (string.Join('/', segments.Take(i)), string.Join('/', segments.Skip(i)).Replace(StoreUserId, "*").Replace(StoreGameId, "*"));
            }
        }

        return (string.Join('/', segments), null);
    }

    public static bool IsWildcardSegment(string segment) =>
        segment.Contains('*') || segment.Contains('?')
        || segment.Contains(StoreUserId, StringComparison.Ordinal) || segment.Contains(StoreGameId, StringComparison.Ordinal);

    /// <summary>Parent of a template ("a/b/c" → "a/b"), or null when there is none.</summary>
    public static string? Parent(string template)
    {
        var t = Normalize(template);
        var i = t.LastIndexOf('/');
        return i > 0 ? t[..i] : null;
    }

    public static string LastSegment(string template)
    {
        var t = Normalize(template);
        return t[(t.LastIndexOf('/') + 1)..];
    }

    private static IEnumerable<string> BaseFolders(ResolveContext ctx)
    {
        foreach (var root in ctx.LibraryRoots)
        {
            foreach (var dir in ctx.InstallDirs)
            {
                var candidate = Path.Combine(root, dir);
                if (Directory.Exists(candidate))
                {
                    yield return Slash(candidate);
                }
            }
        }
    }

    private static List<string> ExpandWith(List<string> current, string token, IEnumerable<string> values)
    {
        var list = values.ToList();
        return current.SelectMany(c => list.Select(v => c.Replace(token, v, StringComparison.Ordinal))).ToList();
    }

    private static string Slash(string path) => path.Replace('\\', '/').TrimEnd('/');
}
