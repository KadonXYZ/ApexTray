using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ApexTray.Core;
using Forms = System.Windows.Forms;

namespace ApexTray;

// Notification-area icon that shows the battery level as a number.
public sealed partial class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _open, _sync, _startup, _exit;
    private readonly Icon _logo = AppIcon.ForTray(Forms.SystemInformation.SmallIconSize.Width);
    private DeviceSnapshot _state = new();
    private nint _hIcon;
    private string? _lastKey;

    public TrayIcon(Action open, Action syncClock, Action exit)
    {
        _open = new Forms.ToolStripMenuItem("", null, (_, _) => open());
        _sync = new Forms.ToolStripMenuItem("", null, (_, _) => syncClock());
        _startup = new Forms.ToolStripMenuItem("") { CheckOnClick = true, Checked = StartupRegistration.IsEnabled };
        _startup.CheckedChanged += (_, _) => StartupRegistration.Set(_startup.Checked);
        _exit = new Forms.ToolStripMenuItem("", null, (_, _) => exit());

        var menu = new Forms.ContextMenuStrip();
        _open.Font = new Font(menu.Font, System.Drawing.FontStyle.Bold);
        menu.Items.AddRange([_open, _sync, _startup, new Forms.ToolStripSeparator(), _exit]);
        menu.Opening += (_, _) => _startup.Checked = StartupRegistration.IsEnabled;

        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
        Loc.Changed += Relabel;
        Relabel();
    }

    public void Update(DeviceSnapshot s)
    {
        _state = s;
        var grey = Color.FromArgb(0x86, 0x8E, 0x96);
        var (text, color, tip) = s switch
        {
            { DriverReady: false } => ("...", grey, Loc.T("starting")),
            { DongleConnected: false } => ("✕", grey, Loc.T("dock not connected")),
            { MouseOnline: false } => ("z", grey, Loc.T("asleep")),
            { Battery: { } b } => (b.ToString(), LevelColor(b), $"{b}%"),
            _ => ("", Color.Empty, Loc.T("connected (battery not reported)")),
        };
        // NotifyIcon.Text is capped at 127 characters.
        var full = "AJ179 APEX · " + tip;
        _icon.Text = full.Length > 127 ? full[..127] : full;

        var key = text + color.ToArgb();
        if (key == _lastKey) return;
        _lastKey = key;

        var old = _hIcon;
        if (text.Length == 0)
        {
            _hIcon = 0;
            _icon.Icon = _logo;
        }
        else
        {
            _hIcon = Render(text, color);
            _icon.Icon = Icon.FromHandle(_hIcon);
        }
        if (old != 0) DestroyIcon(old);
    }

    private void Relabel()
    {
        _open.Text = Loc.T("Open ApexTray");
        _sync.Text = Loc.T("Sync dock clock");
        _startup.Text = Loc.T("Start with Windows");
        _exit.Text = Loc.T("Exit");
        _lastKey = null;
        Update(_state);
    }

    public void Notify(string title, string message) => _icon.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        Loc.Changed -= Relabel;
        _icon.Visible = false;
        _icon.Dispose();
        _logo.Dispose();
        if (_hIcon != 0) DestroyIcon(_hIcon);
        _hIcon = 0;
    }

    private static Color LevelColor(int battery) => battery switch
    {
        <= 15 => Color.FromArgb(0xE0, 0x31, 0x31),
        <= 40 => Color.FromArgb(0xE8, 0x59, 0x0C),
        _ => Color.FromArgb(0x2F, 0x9E, 0x44),
    };

    private static nint Render(string text, Color color)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var path = RoundedRect(new RectangleF(0, 2, size, size - 4), 7);
            using var fill = new SolidBrush(color);
            g.FillPath(fill, path);

            var fontSize = text.Length switch { >= 3 => 13f, 2 => 17f, _ => 19f };
            using var font = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 1, size, size), format);
        }
        return bmp.GetHicon();
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint handle);
}
