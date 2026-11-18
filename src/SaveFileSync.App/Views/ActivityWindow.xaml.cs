using SaveFileSync.Core.Service;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SaveFileSync.App.Views;

/// <summary>Recent sync operations and errors (PLAN.md §8, item 9).</summary>
public partial class ActivityWindow : FluentWindow
{
    private readonly SyncService _service;

    public ActivityWindow(AppHost host)
    {
        _service = host.Service!;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        Refresh();
        _service.ActivityAdded += OnActivityAdded;
        Closed += (_, _) => _service.ActivityAdded -= OnActivityAdded;
    }

    private void OnActivityAdded() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        Entries.ItemsSource = _service.RecentActivity.Select(a => new
        {
            Time = a.TimeUtc.ToLocalTime().ToString("g"),
            Game = a.UnitName,
            Kind = a.Kind switch
            {
                ActivityKind.Uploaded => "Uploaded",
                ActivityKind.Downloaded => "Downloaded",
                ActivityKind.Merged => "Merged",
                ActivityKind.Conflict => "Conflict",
                ActivityKind.Resolved => "Resolved",
                ActivityKind.Warning => "Warning",
                ActivityKind.Error => "Error",
                _ => "Info",
            },
            a.Message,
        }).ToList();
    }
}
