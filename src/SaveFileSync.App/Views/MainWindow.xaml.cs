using System.Windows;
using SaveFileSync.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly AppHost _host;
    private readonly MainViewModel _viewModel;

    public MainWindow(AppHost host)
    {
        _host = host;
        _viewModel = new MainViewModel(host);
        DataContext = _viewModel;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        Closed += (_, _) => _viewModel.Dispose();
    }

    /// <summary>Dev self-test: switches to the grid and back, and puts a generated cover on the first game.</summary>
    public async Task<IReadOnlyList<string>> SelfTestAsync()
    {
        var problems = new List<string>();
        var original = _viewModel.IsGridView;
        _viewModel.IsGridView = !original;
        await Task.Delay(300);

        if (_viewModel.Units.FirstOrDefault() is { } unit)
        {
            var path = Path.Combine(Path.GetTempPath(), "sfs-selftest-cover-" + Guid.NewGuid().ToString("N")[..8] + ".png");
            try
            {
                var pixels = new byte[8 * 12 * 4];
                Array.Fill(pixels, (byte)200);
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(8, 12, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 8 * 4);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                await using (var stream = File.Create(path))
                {
                    encoder.Save(stream);
                }

                await unit.SetCoverAsync(path);
                await Task.Delay(200);
                if (!unit.HasCover)
                {
                    problems.Add("A generated cover was not loaded.");
                }

                await unit.SetCoverAsync(null);
                if (unit.HasCover)
                {
                    problems.Add("Clearing the cover did not work.");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        _viewModel.IsGridView = original;
        await Task.Delay(100);
        return problems;
    }

    private void OnAddGame(object sender, RoutedEventArgs e)
    {
        new AddUnitWindow(_host) { Owner = this }.ShowDialog();
    }

    private void OnAddSteam(object sender, RoutedEventArgs e) =>
        new AddSteamWindow(_host) { Owner = this }.ShowDialog();

    private void OnActivity(object sender, RoutedEventArgs e) =>
        new ActivityWindow(_host) { Owner = this }.Show();

    private void OnDevices(object sender, RoutedEventArgs e) =>
        new DevicesWindow(_host) { Owner = this }.ShowDialog();

    private void OnSettings(object sender, RoutedEventArgs e) =>
        new SettingsWindow(_host) { Owner = this }.ShowDialog();

    private void OnUnitClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string unitId })
        {
            new UnitDetailsWindow(_host, unitId) { Owner = this }.ShowDialog();
        }
    }
}
