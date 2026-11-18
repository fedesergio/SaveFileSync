using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using SaveFileSync.Core.Service;
using Forms = System.Windows.Forms;

namespace SaveFileSync.App;

public enum TrayState
{
    Ok,
    Syncing,
    Attention,
    Paused,
}

/// <summary>Notification-area icon with menu and balloons (PLAN.md §8, item 10).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Dictionary<TrayState, Icon> _icons = new();
    private readonly Forms.ToolStripMenuItem _pauseHour;
    private readonly Forms.ToolStripMenuItem _pauseIndefinitely;
    private readonly Forms.ToolStripMenuItem _resume;

    public TrayIcon()
    {
        foreach (var state in Enum.GetValues<TrayState>())
        {
            _icons[state] = Draw(state);
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open SaveFileSync", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Sync all now", null, (_, _) => SyncAllRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _pauseHour = new Forms.ToolStripMenuItem("Pause for 1 hour", null, (_, _) => PauseRequested?.Invoke(TimeSpan.FromHours(1)));
        _pauseIndefinitely = new Forms.ToolStripMenuItem("Pause until resumed", null, (_, _) => PauseRequested?.Invoke(null));
        _resume = new Forms.ToolStripMenuItem("Resume sync", null, (_, _) => ResumeRequested?.Invoke()) { Visible = false };
        menu.Items.Add(_pauseHour);
        menu.Items.Add(_pauseIndefinitely);
        menu.Items.Add(_resume);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Icon = _icons[TrayState.Ok],
            Text = "SaveFileSync",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    public event Action? OpenRequested;

    public event Action? SyncAllRequested;

    /// <summary>Duration, or <c>null</c> for "until resumed".</summary>
    public event Action<TimeSpan?>? PauseRequested;

    public event Action? ResumeRequested;

    public event Action? ExitRequested;

    public void Update(IReadOnlyList<UnitStatus> units, bool paused)
    {
        var attention = units.Count(u => u.State is UnitState.Conflict or UnitState.Attention or UnitState.Error
            or UnitState.Stale or UnitState.LocationMissing or UnitState.RepositoryUnavailable);
        var syncing = units.Any(u => u.State is UnitState.Syncing);
        var state = paused ? TrayState.Paused
            : attention > 0 ? TrayState.Attention
            : syncing ? TrayState.Syncing
            : TrayState.Ok;

        var text = paused ? "SaveFileSync — paused"
            : attention > 0 ? $"SaveFileSync — {attention} game(s) need attention"
            : syncing ? "SaveFileSync — syncing"
            : $"SaveFileSync — {units.Count(u => u.IsBound)} game(s) in sync";

        _icon.Icon = _icons[state];
        _icon.Text = text.Length > 120 ? text[..120] : text;
        _pauseHour.Visible = !paused;
        _pauseIndefinitely.Visible = !paused;
        _resume.Visible = paused;
    }

    public void ShowBalloon(SyncNotification notification)
    {
        var icon = notification.Level switch
        {
            NotificationLevel.Error => Forms.ToolTipIcon.Error,
            NotificationLevel.Warning => Forms.ToolTipIcon.Warning,
            _ => Forms.ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(10_000, notification.Title, notification.Message, icon);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    /// <summary>Draws the state icon at runtime: no binary assets in the repository.</summary>
    private static Icon Draw(TrayState state)
    {
        var color = state switch
        {
            TrayState.Syncing => Color.FromArgb(0x2F, 0x7D, 0xD1),
            TrayState.Attention => Color.FromArgb(0xD9, 0x82, 0x1E),
            TrayState.Paused => Color.FromArgb(0x80, 0x80, 0x80),
            _ => Color.FromArgb(0x2E, 0x9E, 0x5B),
        };

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(state == TrayState.Paused ? "II" : "S", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }

        var handle = bitmap.GetHicon();
        using var temp = Icon.FromHandle(handle);
        var icon = (Icon)temp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
