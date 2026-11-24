namespace SaveFileSync.Core.Config;

/// <summary>
/// Device-local folders (PLAN.md §5). Everything lives under
/// <c>%LOCALAPPDATA%\SaveFileSync</c>, or under <c>profiles\&lt;name&gt;</c> with the
/// dev-only <c>--profile</c> switch.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string ConfigFile => Path.Combine(Root, "config.json");

    public string DeviceFile => Path.Combine(Root, "device.json");

    public string StateDatabase => Path.Combine(Root, "state.db");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string CacheDirectory => Path.Combine(Root, "cache");

    /// <summary>Encrypted API keys (DPAPI, this Windows account only).</summary>
    public string SecretsFile => Path.Combine(Root, "secrets.dat");

    public string CoversDirectory => Path.Combine(CacheDirectory, "covers");

    public static string DefaultBase =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaveFileSync");

    public static AppPaths ForProfile(string? profile) =>
        new(string.IsNullOrWhiteSpace(profile)
            ? DefaultBase
            : Path.Combine(DefaultBase, "profiles", profile));

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}
