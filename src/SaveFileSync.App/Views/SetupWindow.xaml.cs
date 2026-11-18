using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using SaveFileSync.Repository;
using SaveFileSync.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

/// <summary>First-run wizard (PLAN.md §8, item 1): welcome, repository, this PC, startup, summary.</summary>
public partial class SetupWindow : FluentWindow
{
    private readonly AppHost _host;
    private readonly FrameworkElement[] _steps;
    private readonly ObservableCollection<string> _roots = [];
    private int _step;

    public SetupWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        _steps = [StepWelcome, StepRepository, StepDevice, StepStartup, StepSummary];

        var oneDrive = OneDrive.GetPersonalRoot();
        RepoPath.Text = oneDrive is null ? "" : Path.Combine(oneDrive, "SaveFileSync");
        DeviceName.Text = Environment.MachineName;
        Roots.ItemsSource = _roots;
        foreach (var candidate in new[] { @"C:\Games", @"D:\Games", @"E:\Games" }.Where(Directory.Exists))
        {
            _roots.Add(candidate);
        }

        var canInstall = AppHost.IsSingleFile
            && host.Profile is null
            && !Installer.IsRunningFromInstallDirectory(Environment.ProcessPath!);
        Install.IsChecked = canInstall;
        if (!canInstall)
        {
            Install.Visibility = Visibility.Collapsed;
            InstallHint.Visibility = Visibility.Collapsed;
        }

        ShowStep(0);
    }

    /// <summary>Set when the app was installed and must be restarted from the installed copy.</summary>
    public string? RelaunchPath { get; private set; }

    private void ShowStep(int step)
    {
        _step = step;
        for (var i = 0; i < _steps.Length; i++)
        {
            _steps[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        }

        StepCounter.Text = $"Step {step + 1} of {_steps.Length}";
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step == _steps.Length - 1 ? "Finish" : "Next";
        if (step == _steps.Length - 1)
        {
            Summary.Text =
                $"Shared folder: {RepoPath.Text.Trim()}\n" +
                $"This PC: {DeviceName.Text.Trim()}\n" +
                $"Library folders: {(_roots.Count == 0 ? "none" : string.Join(", ", _roots))}\n" +
                $"Start with Windows: {(StartWithWindows.IsChecked == true ? "yes" : "no")}" +
                (Install.Visibility == Visibility.Visible ? $"\nInstall for this user: {(Install.IsChecked == true ? "yes" : "no")}" : "");
        }

        Validate();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step < _steps.Length - 1)
        {
            ShowStep(_step + 1);
        }
        else
        {
            Finish();
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowStep(Math.Max(0, _step - 1));

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

    private void OnAddRoot(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder where games are installed" };
        if (dialog.ShowDialog(this) == true && !_roots.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            _roots.Add(dialog.FolderName);
        }
    }

    private void OnRemoveRoot(object sender, RoutedEventArgs e)
    {
        if (Roots.SelectedItem is string selected)
        {
            _roots.Remove(selected);
        }
    }

    private void OnInputChanged(object sender, RoutedEventArgs e) => Validate();

    private void Validate()
    {
        if (NextButton is null || RepoInfo is null)
        {
            return;
        }

        var path = RepoPath.Text.Trim();
        var repoValid = path.Length > 0 && Path.IsPathFullyQualified(path);
        NextButton.IsEnabled = _step switch
        {
            1 => repoValid,
            2 => DeviceName.Text.Trim().Length > 0,
            _ => true,
        };

        if (!repoValid)
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

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Finish()
    {
        var path = Path.GetFullPath(RepoPath.Text.Trim());
        try
        {
            new FolderRemoteStore(path).Initialize(DateTime.UtcNow);
            if (OneDrive.IsInsideOneDrive(path))
            {
                OneDrive.TryPin(path);
            }

            _host.Config.Update(c =>
            {
                c.RepositoryPath = path;
                c.DeviceName = DeviceName.Text.Trim();
                c.LibraryRoots = _roots.ToList();
            });

            var exe = Environment.ProcessPath!;
            if (Install.Visibility == Visibility.Visible && Install.IsChecked == true)
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
            MessageBox.Show(this, ex.Message, "SaveFileSync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
