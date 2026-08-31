using System.Drawing.Drawing2D;

namespace MonitorLayoutSwitcher;

public class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly HotkeyManager _hotkeyManager;
    private List<DisplayProfile> _profiles;
    private DisplayProfile? _activeProfile;
    private ConfigForm? _configForm;

    public TrayContext()
    {
        _profiles = ProfileManager.LoadProfiles();
        _hotkeyManager = new HotkeyManager();

        _contextMenu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(21, 19, 17),
            ForeColor = Color.FromArgb(248, 240, 221),
            ShowImageMargin = true,
            Renderer = new DarkMenuRenderer()
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateMonitorIcon(),
            ContextMenuStrip = _contextMenu,
            Text = "Monitor Layout Switcher",
            Visible = true
        };

        _notifyIcon.DoubleClick += (s, e) => ShowConfigWindow();

        RefreshMenuAndHotkeys();
    }

    private void RefreshMenuAndHotkeys()
    {
        _hotkeyManager.UnregisterAll();
        _contextMenu.Items.Clear();

        var titleItem = new ToolStripMenuItem("Monitor Layout Switcher")
        {
            Enabled = false,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(232, 189, 99)
        };
        _contextMenu.Items.Add(titleItem);
        _contextMenu.Items.Add(new ToolStripSeparator());

        // Profile items
        foreach (var profile in _profiles)
        {
            string label = string.IsNullOrWhiteSpace(profile.Hotkey)
                ? profile.Name
                : $"{profile.Name}   ({profile.Hotkey})";

            var item = new ToolStripMenuItem(label)
            {
                Checked = (_activeProfile?.Id == profile.Id),
                Font = new Font("Segoe UI", 9f)
            };

            var p = profile;
            item.Click += (s, e) => SwitchToProfile(p);
            _contextMenu.Items.Add(item);

            // Register Hotkey
            if (!string.IsNullOrWhiteSpace(p.Hotkey))
            {
                _hotkeyManager.Register(p.Hotkey, () =>
                {
                    SwitchToProfile(p);
                });
            }
        }

        _contextMenu.Items.Add(new ToolStripSeparator());

        var configItem = new ToolStripMenuItem("Configure Profiles...");
        configItem.Click += (s, e) => ShowConfigWindow();
        _contextMenu.Items.Add(configItem);

        var captureItem = new ToolStripMenuItem("Capture Current Layout as New Profile");
        captureItem.Click += (s, e) =>
        {
            var newP = DisplayEngine.CaptureCurrentLayoutAsProfile($"Layout {_profiles.Count + 1}", string.Empty);
            _profiles.Add(newP);
            ProfileManager.SaveProfiles(_profiles);
            RefreshMenuAndHotkeys();
            _notifyIcon.ShowBalloonTip(2000, "Monitor Layout Switcher", $"Captured {newP.Name}", ToolTipIcon.Info);
        };
        _contextMenu.Items.Add(captureItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (s, e) => ExitThread();
        _contextMenu.Items.Add(exitItem);
    }

    private void SwitchToProfile(DisplayProfile profile)
    {
        bool success = DisplayEngine.ApplyProfile(profile, out string error);
        if (success)
        {
            _activeProfile = profile;
            _notifyIcon.Text = $"Monitor Layout: {profile.Name}";
            _notifyIcon.ShowBalloonTip(1500, "Monitor Layout Switcher", $"Switched to: {profile.Name}", ToolTipIcon.Info);
            RefreshMenuAndHotkeys();
        }
        else
        {
            _notifyIcon.ShowBalloonTip(3000, "Monitor Layout Switcher — Failed", error, ToolTipIcon.Error);
        }
    }

    private void ShowConfigWindow()
    {
        if (_configForm == null || _configForm.IsDisposed)
        {
            _configForm = new ConfigForm(_profiles, () =>
            {
                _profiles = ProfileManager.LoadProfiles();
                RefreshMenuAndHotkeys();
            });
        }

        _configForm.Show();
        _configForm.BringToFront();
    }

    private Icon CreateMonitorIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Gold monitor frame
            using var pen = new Pen(Color.FromArgb(232, 189, 99), 2f);
            using var brush = new SolidBrush(Color.FromArgb(25, 22, 18));
            using var screenBrush = new SolidBrush(Color.FromArgb(232, 189, 99));

            g.FillRoundedRectangle(brush, 4, 6, 24, 16, 2);
            g.DrawRoundedRectangle(pen, 4, 6, 24, 16, 2);

            // Screen glow dot
            g.FillRectangle(screenBrush, 8, 10, 16, 8);

            // Stand
            g.DrawLine(pen, 16, 22, 16, 26);
            g.DrawLine(pen, 10, 26, 22, 26);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _hotkeyManager.Dispose();
        base.ExitThreadCore();
    }

    private class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.Black : Color.FromArgb(248, 240, 221);
            base.OnRenderItemText(e);
        }
    }

    private class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(21, 19, 17);
        public override Color ImageMarginGradientBegin => Color.FromArgb(21, 19, 17);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(21, 19, 17);
        public override Color ImageMarginGradientEnd => Color.FromArgb(21, 19, 17);
        public override Color MenuBorder => Color.FromArgb(60, 52, 40);
        public override Color MenuItemBorder => Color.FromArgb(232, 189, 99);
        public override Color MenuItemSelected => Color.FromArgb(232, 189, 99);
        public override Color MenuStripGradientBegin => Color.FromArgb(21, 19, 17);
        public override Color MenuStripGradientEnd => Color.FromArgb(21, 19, 17);
        public override Color CheckBackground => Color.FromArgb(60, 52, 40);
        public override Color CheckSelectedBackground => Color.FromArgb(232, 189, 99);
        public override Color SeparatorDark => Color.FromArgb(45, 40, 32);
        public override Color SeparatorLight => Color.FromArgb(45, 40, 32);
    }
}

public static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, int x, int y, int width, int height, int radius)
    {
        using var path = GetRoundedRectPath(x, y, width, height, radius);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics g, Pen pen, int x, int y, int width, int height, int radius)
    {
        using var path = GetRoundedRectPath(x, y, width, height, radius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath GetRoundedRectPath(int x, int y, int width, int height, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
