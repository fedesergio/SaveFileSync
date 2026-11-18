using System.Reflection;
using Microsoft.Extensions.Logging;
using SaveFileSync.Core;
using SaveFileSync.Core.Config;
using SaveFileSync.Core.Service;
using SaveFileSync.Core.State;
using SaveFileSync.Core.Sync;
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

        return new AppHost(profile, paths, new ConfigStore(paths.ConfigFile), factory);
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
        Service = new SyncService(
            engine,
            Config,
            Device,
            new WindowsProcessMonitor(),
            SystemClock.Instance,
            LoggerFactory.CreateLogger<SyncService>(),
            new SyncServiceOptions { AppVersion = AppVersion });
        Service.Start();
        _state = state;
    }

    private SqliteLocalState? _state;

    public void Dispose()
    {
        Service?.Dispose();
        _state?.Dispose();
        LoggerFactory.Dispose();
        Log.CloseAndFlush();
    }
}
