using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using YesVPN.Core.Ipc;

namespace YesVPN.App.Services;

/// <summary>
/// Manages the Windows system tray icon, context menu, and connection state indicator.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly Icon _disconnectedIcon;
    private readonly Icon _connectedIcon;
    private readonly Icon _busyIcon;

    public event Action? OpenRequested;
    public event Action? ToggleConnectRequested;
    public event Action? ExitRequested;

    public TrayIconManager()
    {
        _disconnectedIcon = CreateStatusIcon(Color.FromArgb(100, 116, 139));
        _connectedIcon = CreateStatusIcon(Color.FromArgb(34, 197, 94));
        _busyIcon = CreateStatusIcon(Color.FromArgb(245, 158, 11));

        _statusItem = new ToolStripMenuItem("Disconnected") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Connect", null, (_, _) => ToggleConnectRequested?.Invoke());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open YesVPN", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Text = "YesVPN — Disconnected",
            Icon = _disconnectedIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void UpdateState(TunnelState state, string? activeProxyLabel)
    {
        var label = state switch
        {
            TunnelState.Connected => string.IsNullOrWhiteSpace(activeProxyLabel)
                ? "Connected"
                : $"Connected via {activeProxyLabel}",
            TunnelState.Connecting => "Connecting...",
            TunnelState.Reconnecting => "Reconnecting...",
            TunnelState.Error => "Connection error",
            _ => "Disconnected"
        };

        _statusItem.Text = label;
        _toggleItem.Text = state is TunnelState.Connected or TunnelState.Connecting or TunnelState.Reconnecting
            ? "Disconnect"
            : "Connect";

        _notifyIcon.Icon = state switch
        {
            TunnelState.Connected => _connectedIcon,
            TunnelState.Connecting or TunnelState.Reconnecting => _busyIcon,
            _ => _disconnectedIcon
        };

        var tooltip = $"YesVPN — {label}";
        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon CreateStatusIcon(Color statusColor)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bgBrush = new SolidBrush(Color.FromArgb(15, 23, 42));
        g.FillEllipse(bgBrush, 1, 1, 30, 30);

        using var ringPen = new Pen(Color.FromArgb(59, 130, 246), 2.2f);
        g.DrawEllipse(ringPen, 2, 2, 28, 28);

        // Shield shape
        using var shieldPath = new GraphicsPath();
        shieldPath.AddPolygon([
            new PointF(16f, 6f),
            new PointF(24f, 9f),
            new PointF(23f, 18f),
            new PointF(16f, 25f),
            new PointF(9f, 18f),
            new PointF(8f, 9f)
        ]);
        using var shieldBrush = new SolidBrush(Color.FromArgb(59, 130, 246));
        g.FillPath(shieldBrush, shieldPath);

        // Status dot at bottom-right
        using var dotBorder = new SolidBrush(Color.FromArgb(11, 16, 24));
        g.FillEllipse(dotBorder, 19, 19, 12, 12);
        using var dotBrush = new SolidBrush(statusColor);
        g.FillEllipse(dotBrush, 21, 21, 8, 8);

        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _disconnectedIcon.Dispose();
        _connectedIcon.Dispose();
        _busyIcon.Dispose();
    }
}
