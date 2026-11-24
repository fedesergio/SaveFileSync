using System.Net.Http;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SaveFileSync.Core;
using SaveFileSync.Core.Config;
using SaveFileSync.Core.Model;
using SaveFileSync.Core.Service;
using SaveFileSync.Core.State;
using SaveFileSync.Core.Sync;
using SaveFileSync.Manifest;
using SaveFileSync.Metadata;
using SaveFileSync.Repository;
using SaveFileSync.Windows;
using Serilog;
using Serilog.Extensions.Logging;

namespace SaveFileSync.App;

/// <summary>Composition root: wires paths, config, identity, state, repository and the sync service.</summary>
public sealed class AppHost : IDisposable
{
    private AppHost(string? profile, AppPaths paths, ConfigStore config, ILoggerFactory loggerFactory)
    {
        Profile = profile;
        Paths = paths;
        Config = config;
        LoggerFactory = loggerFactory;
    }

    public string? Profile { get; }

    public AppPaths Paths { get; }

    public ConfigStore Config { get; }

    public ILoggerFactory LoggerFactory { get; }

    public DeviceIdentity? Device { get; private set; }

    public FolderRemoteStore? Remote { get; private set; }

    public SyncService? Service { get; private set; }

    /// <summary>Ludusavi manifest cache (PLAN.md §7.3); the index is only loaded while a window needs it.</summary>
    public ManifestStore Manifest => _manifest ??= new ManifestStore(Paths.CacheDirectory, AppVersion);

    /// <summary>Finds save folders on this PC: known folders, library roots and emulator folders.</summary>
    public LocationResolver Resolver { get; private set; } = null!;

    private ManifestStore? _manifest;

    /// <summary>API keys of this PC, encrypted with DPAPI (never in config.json, git or the repository).</summary>
    public ISecretStore Secrets { get; private set; } = null!;

    /// <summary>Covers and official names (phase 4); works without keys, it just has nothing to do then.</summary>
    public MetadataService Metadata { get; private set; } = null!;

    public HttpClient Http { get; } = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // A generic client name: the version only, nothing about this PC or its user.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SaveFileSync/" + AppVersion);
        return client;
    }

    public static string AppVersion =>
        typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "dev";

    /// <summary>True when running as a published single-file exe (installable by copying one file).</summary>
#pragma warning disable IL3000 // An empty Location is exactly how a single-file app is detected.
    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(AppHost).Assembly.Location);
#pragma warning restore IL3000

    public static AppHost Create(string? profile)
    {
        var paths = AppPaths.ForProfile(profile);
        paths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "savefilesync-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        var factory = new SerilogLoggerFactory(Log.Logger, dispose: true);
        factory.CreateLogger<AppHost>().LogInformation("SaveFileSync {Version} starting (profile: {Profile})", AppVersion, profile ?? "default");

        return new AppHost(profile, paths, new ConfigStore(paths.ConfigFile), factory) { Secrets = new DpapiSecretStore(paths.SecretsFile) };
    }

    /// <summary>Opens the configured repository and starts the background service.</summary>
    public void StartSync()
    {
        var config = Config.Snapshot();
        if (!config.IsConfigured)
        {
            throw new InvalidOperationException("The app is not configured yet.");
        }

        Remote = new FolderRemoteStore(config.RepositoryPath!);
        var devices = Remote.IsAvailable() ? Remote.ReadDevices() : [];
        Device = DeviceIdentity.LoadOrCreate(
            Paths.DeviceFile,
            MachineFingerprint.Get(),
            shortId => devices.Any(d => d.ShortId == shortId));

        if (Remote.IsAvailable() && OneDrive.IsInsideOneDrive(Remote.Root) && !OneDrive.IsPinned(Remote.Root))
        {
            OneDrive.TryPin(Remote.Root);
        }

        var state = new SqliteLocalState(Paths.StateDatabase);
        var engine = new SyncEngine(Remote, state, Device, SystemClock.Instance, LoggerFactory.CreateLogger<SyncEngine>());
        Resolver = new LocationResolver(ShellKnownFolders.Get, Config.Snapshot, new WindowsProcessPaths());
        Service = new SyncService(
            engine,
            Config,
            Device,
            new WindowsProcessMonitor(),
            SystemClock.Instance,
            LoggerFactory.CreateLogger<SyncService>(),
            new SyncServiceOptions { AppVersion = AppVersion },
            Resolver);
        Service.Start();
        _state = state;

        Directory.CreateDirectory(Paths.CoversDirectory);
        Metadata = new MetadataService(
            new ServiceCatalog(Service),
            new SteamGridDbClient(Http, () => Secrets.Get(SecretNames.SteamGridDbKey)),
            new IgdbClient(Http, () => Secrets.Get(SecretNames.IgdbClientId) is { Length: > 0 } id
                && Secrets.Get(SecretNames.IgdbClientSecret) is { Length: > 0 } secret ? (id, secret) : null),
            new CoverCache(Paths.CoversDirectory, Http),
            Path.Combine(Paths.CacheDirectory, "metadata-state.json"),
            SystemClock.Instance,
            LoggerFactory.CreateLogger<MetadataService>());
        var knownUnits = -1;
        Service.StatusChanged += status =>
        {
            if (status.Count != knownUnits)
            {
                knownUnits = status.Count;
                Metadata.Trigger();
            }
        };
        Metadata.Start();
    }

    private SqliteLocalState? _state;

    public void Dispose()
    {
        Metadata?.Dispose();
        Http.Dispose();
        Service?.Dispose();
        _state?.Dispose();
        LoggerFactory.Dispose();
        Log.CloseAndFlush();
    }
}

/// <summary>Gives the metadata service the games of the sync service.</summary>
internal sealed class ServiceCatalog(SyncService service) : IUnitCatalog
{
    public IReadOnlyList<UnitDefinition> GetDefinitions() =>
        service.CurrentStatus.Select(s => service.GetDefinition(s.UnitId)).OfType<UnitDefinition>().ToList();

    public Task<bool> SaveMetadataAsync(string unitId, UnitMetadata metadata) => service.SetMetadataAsync(unitId, metadata);
}
