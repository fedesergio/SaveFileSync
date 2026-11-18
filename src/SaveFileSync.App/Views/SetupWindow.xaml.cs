using System.Windows;
using Microsoft.Win32;
using SaveFileSync.Repository;
using SaveFileSync.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

/// <summary>Phase 1a first-run dialog: repository folder, device name, autostart (PLAN.md §11).</summary>
public partial class SetupWindow : FluentWindow
{
    private readonly AppHost _host;

    public SetupWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        var oneDrive = OneDrive.GetPersonalRoot();
        RepoPath.Text = oneDrive is null ? "" : System.IO.Path.Combine(oneDrive, "SaveFileSync");
        DeviceName.Text = Environment.MachineName;

        var canInstall = AppHost.IsSingleFile
            && host.Profile is null
            && !Installer.IsRunningFromInstallDirectory(Environment.ProcessPath!);
        Install.IsChecked = canInstall;
        Install.IsEnabled = canInstall;
        if (!canInstall)
        {
            Install.Visibility = Visibility.Collapsed;
        }

        Validate();
    }

    /// <summary>Set when the app was installed and must be restarted from the installed copy.</summary>
    public string? RelaunchPath { get; private set; }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose or create the SaveFileSync folder inside OneDrive",
            InitialDirectory = OneDrive.GetPersonalRoot() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) == true)
        {
            RepoPath.Text = dialog.FolderName;
        }
    }

    private void OnInputChanged(object sender, RoutedEventArgs e) => Validate();

    private void Validate()
    {
        if (RepoInfo is null || ContinueButton is null)
        {
            return;
        }

        var path = RepoPath.Text.Trim();
        var valid = path.Length > 0 && System.IO.Path.IsPathFullyQualified(path) && DeviceName.Text.Trim().Length > 0;
        ContinueButton.IsEnabled = valid;

        if (path.Length == 0 || !System.IO.Path.IsPathFullyQualified(path))
        {
            RepoInfo.IsOpen = false;
            return;
        }

        RepoInfo.IsOpen = true;
        if (new FolderRemoteStore(path).IsAvailable())
        {
            RepoInfo.Severity = InfoBarSeverity.Success;
            RepoInfo.Title = "Existing repository";
            RepoInfo.Message = "This folder is already used by SaveFileSync on another PC: this PC will join it.";
        }
        else if (!OneDrive.IsInsideOneDrive(path))
        {
            RepoInfo.Severity = InfoBarSeverity.Warning;
            RepoInfo.Title = "Not inside OneDrive";
            RepoInfo.Message = "This folder is not synced by OneDrive, so other PCs will not see it. Use it only for testing.";
        }
        else
        {
            RepoInfo.Severity = InfoBarSeverity.Informational;
            RepoInfo.Title = "New repository";
            RepoInfo.Message = "The folder will be created and kept always available offline.";
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        var path = System.IO.Path.GetFullPath(RepoPath.Text.Trim());
        try
        {
            var store = new FolderRemoteStore(path);
            store.Initialize(DateTime.UtcNow);
            if (OneDrive.IsInsideOneDrive(path))
            {
                OneDrive.TryPin(path);
            }

            _host.Config.Update(c =>
            {
                c.RepositoryPath = path;
                c.DeviceName = DeviceName.Text.Trim();
            });

            var exe = Environment.ProcessPath!;
            if (Install.IsChecked == true)
            {
                exe = Installer.Install(exe);
                RelaunchPath = exe;
            }

            if (StartWithWindows.IsChecked == true)
            {
                Autostart.Enable(exe, _host.Profile);
            }
            else
            {
                Autostart.Disable(_host.Profile);
            }

            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
