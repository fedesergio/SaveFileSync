using System.Text.Json;
using SaveFileSync.Core.IO;
using SaveFileSync.Core.Model;

namespace SaveFileSync.Core.Config;

/// <summary>Device-local settings (<c>config.json</c>). Never stored in git or in the repository.</summary>
public sealed class AppConfig
{
    public string? RepositoryPath { get; set; }

    public string? DeviceName { get; set; }

    public int QuietPeriodSeconds { get; set; } = 120;

    public int ProcessExitDelaySeconds { get; set; } = 15;

    public int InPlayCapMinutes { get; set; } = 15;

    public int FullRescanMinutes { get; set; } = 15;

    public int RemotePollSeconds { get; set; } = 120;

    public int ProcessPollSeconds { get; set; } = 5;

    /// <summary>Global pause; <see cref="DateTime.MaxValue"/> means "until resumed".</summary>
    public DateTime? PausedUntilUtc { get; set; }

    public List<string> PausedUnits { get; set; } = [];

    /// <summary>Folders where games are installed on this PC (used by automatic detection, phase 2).</summary>
    public List<string> LibraryRoots { get; set; } = [];

    /// <summary>
    /// Bind games that other PCs already sync as soon as their save folder is found here
    /// (only when it resolves to exactly one existing folder).
    /// </summary>
    public bool AutoSetup { get; set; } = true;

    /// <summary>Emulator id → folder of its exe on this PC (e.g. "rpcs3" → "C:\Emulators\RPCS3").</summary>
    public Dictionary<string, string> EmulatorDirs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>unitId → (locationId → absolute path on this PC).</summary>
    public Dictionary<string, Dictionary<string, string>> Bindings { get; set; } = new(StringComparer.Ordinal);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(RepositoryPath) && !string.IsNullOrWhiteSpace(DeviceName);
}

/// <summary>Thread-safe load/save of <see cref="AppConfig"/>.</summary>
public sealed class ConfigStore
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private AppConfig _current;

    public ConfigStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public event EventHandler? Changed;

    public AppConfig Snapshot()
    {
        lock (_lock)
        {
            // Deep copy through JSON so callers never share mutable state.
            return Json.Deserialize<AppConfig>(Json.Serialize(_current))!;
        }
    }

    public void Update(Action<AppConfig> change)
    {
        lock (_lock)
        {
            var copy = Json.Deserialize<AppConfig>(Json.Serialize(_current))!;
            change(copy);
            AtomicFile.WriteAllBytes(_path, Json.Serialize(copy), "cfg");
            _current = copy;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        try
        {
            var config = Json.Deserialize<AppConfig>(File.ReadAllBytes(path)) ?? new AppConfig();
            config.Bindings = new Dictionary<string, Dictionary<string, string>>(config.Bindings, StringComparer.Ordinal);
            return config;
        }
        catch (JsonException)
        {
            // A corrupt config must not crash the app: keep a copy and start fresh.
            File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
            return new AppConfig();
        }
    }
}
