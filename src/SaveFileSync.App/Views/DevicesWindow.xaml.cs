using System.Windows;
using System.Windows.Controls;
using SaveFileSync.Core.History;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

public sealed class DeviceItem
{
    public required DeviceRow Row { get; init; }

    public string Name => Row.IsThisPc ? $"{Row.Info.Name} (this PC)" : Row.Info.Name;

    public string Status => Row.State switch
    {
        DeviceState.ThisPc => "In use",
        DeviceState.Active => "Active",
        DeviceState.Inactive => "Inactive (90+ days)",
        _ => "Retired",
    };

    public string LastSeen => Row.IsThisPc ? "Now" : Row.Info.LastSeenUtc.ToLocalTime().ToString("g");

    public string AppVersion => Row.Info.AppVersion ?? "";
}

/// <summary>The PCs of the repository, with "Retire" (PLAN.md §8, item 8).</summary>
public partial class DevicesWindow : FluentWindow
{
    private readonly AppHost _host;

    public DevicesWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        Loaded += async (_, _) => await ReloadAsync();
        UpdateButtons();
    }

    private DeviceItem? Selected => DeviceList.SelectedItem as DeviceItem;

    private async Task ReloadAsync()
    {
        try
        {
            var rows = await _host.Service!.GetDevicesAsync();
            DeviceList.ItemsSource = rows.Select(r => new DeviceItem { Row = r }).ToList();
            Message.Text = $"{rows.Count} device(s).";
        }
        catch (Exception ex)
        {
            Message.Text = ex.Message;
        }

        UpdateButtons();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        if (RetireButton is not null)
        {
            RetireButton.IsEnabled = Selected is { Row: { IsThisPc: false, State: DeviceState.Active or DeviceState.Inactive } };
        }
    }

    private async void OnRetire(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Retire {item.Row.Info.Name}?\n\nIts current version stops being protected, so its old versions can be cleaned up. " +
            "No save is deleted from that PC. If you use it again, it comes back by itself.",
            "Retire device", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _host.Service!.RetireDeviceAsync(item.Row.Info.DeviceId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Retire device", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await ReloadAsync();
    }
}
