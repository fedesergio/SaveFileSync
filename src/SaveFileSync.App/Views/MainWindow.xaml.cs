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

    private void OnAddGame(object sender, RoutedEventArgs e)
    {
        new AddUnitWindow(_host) { Owner = this }.ShowDialog();
    }

    private void OnActivity(object sender, RoutedEventArgs e) =>
        new ActivityWindow(_host) { Owner = this }.Show();

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
