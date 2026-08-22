using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OmniDock.Services;

/// <summary>
/// 托盘图标：把占比画成一圈进度环 + 中间的百分数，窗口收起来也能扫一眼。
/// 图标是每次现画的 GDI 位图，所以旧的图标句柄必须显式销毁，否则会泄漏。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int IconSize = 32;

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _autoStartItem;
    private int _lastDrawnPercent = -1;
    private Color _lastAccent = Color.Empty;

    internal TrayIcon()
    {
        _autoStartItem = new ToolStripMenuItem("开机自启") { CheckOnClick = true };
        _autoStartItem.Click += (_, _) =>
        {
            var applied = AutoStart.Set(_autoStartItem.Checked);
            _autoStartItem.Checked = applied;
            AutoStartChanged?.Invoke(applied);
        };

        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(32, 32, 36),
            ForeColor = Color.White,
            ShowImageMargin = false
        };

        var showItem = new ToolStripMenuItem("显示 / 隐藏");
        showItem.Click += (_, _) => ToggleRequested?.Invoke();

        var settingsItem = new ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.AddRange([showItem, settingsItem, _autoStartItem, new ToolStripSeparator(), exitItem]);

        _notifyIcon = new NotifyIcon
        {
            Text = "OmniDock",
            ContextMenuStrip = menu,
            Visible = false
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleRequested?.Invoke();
            }
        };
    }

    internal event Action? ToggleRequested;
    internal event Action? SettingsRequested;
    internal event Action? ExitRequested;
    internal event Action<bool>? AutoStartChanged;

    internal bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    internal void SyncAutoStart(bool enabled) => _autoStartItem.Checked = enabled;

    /// <summary>更新图标与悬浮提示。整数占比没变就不重画，省掉每秒的 GDI 开销。</summary>
    internal void Update(double percent, string tooltip, System.Windows.Media.Color accent)
    {
        var rounded = (int)Math.Round(Math.Clamp(percent, 0d, 100d));
        var color = Color.FromArgb(accent.A, accent.R, accent.G, accent.B);

        // NotifyIcon.Text 上限 63 字符，超了会抛异常
        _notifyIcon.Text = tooltip.Length > 60 ? tooltip[..60] : tooltip;

        if (rounded == _lastDrawnPercent && color == _lastAccent)
        {
            return;
        }

        _lastDrawnPercent = rounded;
        _lastAccent = color;
        Redraw(rounded, color);
    }

    private void Redraw(int percent, Color accent)
    {
        using var bitmap = new Bitmap(IconSize, IconSize);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var box = new Rectangle(3, 3, IconSize - 7, IconSize - 7);

            using var track = new Pen(Color.FromArgb(90, 255, 255, 255), 3.5f);
            g.DrawEllipse(track, box);

            if (percent > 0)
            {
                using var arc = new Pen(accent, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arc, box, -90f, (float)(360d * percent / 100d));
            }

            // 100% 时用两位数字，位数太多画不下
            var label = percent >= 100 ? "!!" : percent.ToString();
            using var font = new Font("Segoe UI", percent >= 10 ? 11f : 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString(label, font);
            g.DrawString(label, font, Brushes.White,
                (IconSize - size.Width) / 2f,
                (IconSize - size.Height) / 2f);
        }

        var handle = bitmap.GetHicon();
        Icon fresh;
        try
        {
            // FromHandle 不接管句柄所有权，所以复制一份自持有的再把原句柄销毁
            using var borrowed = Icon.FromHandle(handle);
            fresh = (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }

        var previous = _notifyIcon.Icon;
        _notifyIcon.Icon = fresh;
        previous?.Dispose();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
