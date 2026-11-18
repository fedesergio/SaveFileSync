using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SaveFileSync.App.Views;
using SaveFileSync.Core.Service;
using Wpf.Ui.Appearance;

namespace SaveFileSync.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private AppHost? _host;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private bool _exiting;

    public static new App Current => (App)Application.Current;

    public AppHost Host => _host ?? throw new InvalidOperationException("Not started.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var cmd = CommandLine.Parse(e.Args);

        if (cmd.IsCli)
        {
            Shutdown(Cli.Run(cmd));
            return;
        }

        var instanceName = "SaveFileSync-" + (cmd.Profile ?? "default");
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\" + instanceName, out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + instanceName + "-show");
        if (!isFirst)
        {
            // Already running: ask that instance to show its window.
            _showSignal.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        ApplicationThemeManager.ApplySystemTheme();

        _host = AppHost.Create(cmd.Profile);

        if (!_host.Config.Snapshot().IsConfigured)
        {
            var setup = new SetupWindow(_host);
            if (setup.ShowDialog() != true)
            {
                ExitApp();
                return;
            }

            if (setup.RelaunchPath is { } relaunch)
            {
                // Installed to %LOCALAPPDATA%\Programs: continue from there.
                ReleaseInstance();
                Process.Start(new ProcessStartInfo(relaunch, cmd.Profile is null ? "" : $"--profile \"{cmd.Profile}\"") { UseShellExecute = false });
                ExitApp();
                return;
            }
        }

        _host.StartSync();
        var service = _host.Service!;

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowMainWindow;
        _tray.SyncAllRequested += service.RequestSyncAll;
        _tray.PauseRequested += duration => service.PauseAll(duration is { } d ? DateTime.UtcNow + d : DateTime.MaxValue);
        _tray.ResumeRequested += service.ResumeAll;
        _tray.ExitRequested += ExitApp;

        service.StatusChanged += status => Dispatcher.BeginInvoke(() => _tray?.Update(status, IsPaused()));
        service.Notification += n => Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(n));
        _host.Config.Changed += (_, _) => Dispatcher.BeginInvoke(() => _tray?.Update(service.CurrentStatus, IsPaused()));

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (cmd.SelfTestUi)
        {
            Dispatcher.BeginInvoke(async () => ExitWith(await UiSelfTest.RunAsync(_host)));
            return;
        }

        var listener = new Thread(ListenForShowSignal) { IsBackground = true, Name = "Show signal" };
        listener.Start();

        if (!cmd.Background)
        {
            ShowMainWindow();
        }
    }

    public bool IsPaused() =>
        _host?.Config.Snapshot().PausedUntilUtc is { } until && until > DateTime.UtcNow;

    public void ShowMainWindow()
    {
        if (_host?.Service is null)
        {
            return;
        }

        if (_main is null)
        {
            _main = new MainWindow(_host);
            // Closing the window only hides the app to the tray; the window is released to save memory.
            _main.Closed += (_, _) => _main = null;
        }

        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
    }

    private void ExitWith(int code)
    {
        _exiting = true;
        _tray?.Dispose();
        _host?.Dispose();
        ReleaseInstance();
        Shutdown(code);
    }

    public void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _main?.Close();
        _tray?.Dispose();
        _host?.Dispose();
        ReleaseInstance();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting)
        {
            _tray?.Dispose();
            _host?.Dispose();
            ReleaseInstance();
        }

        base.OnExit(e);
    }

    private void ReleaseInstance()
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }

    private void ListenForShowSignal()
    {
        while (!_exiting && _showSignal is not null)
        {
            if (_showSignal.WaitOne(TimeSpan.FromSeconds(1)))
            {
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _host?.Service?.OnResume();
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Serilog.Log.Error(e.Exception, "Unhandled UI exception");
        System.Windows.MessageBox.Show(e.Exception.Message, "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>Human-readable name for a device id ("This PC" for the local one).</summary>
    public string DeviceName(string deviceId) => _host?.Service?.DeviceName(deviceId) ?? deviceId;

    internal static UnitStatus? FindStatus(string unitId) =>
        Current._host?.Service?.CurrentStatus.FirstOrDefault(s => s.UnitId == unitId);
}
