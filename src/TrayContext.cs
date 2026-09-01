using System.Drawing.Drawing2D;

namespace MonitorLayoutSwitcher;

public class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly HotkeyManager _hotkeyManager;
    private readonly FileSystemWatcher _profileWatcher;
    private readonly System.Windows.Forms.Timer _profileReloadTimer;
    private List<DisplayProfile> _profiles;
    private DisplayProfile? _activeProfile;
    private ConfigForm? _configForm;
    private bool _profileChangePending;
    private string _lastHandledProfileSignature = string.Empty;
    private bool _reloadingProfiles;

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

        // The check margin is laid out from ImageScalingSize. Left at its 16x16
        // default it stays put while the DPI-scaled item grows around it, which is
        // what pushed the tick out past the left edge of the menu.
        _contextMenu.Opening += (s, e) =>
        {
            RefreshMenuAndHotkeys();

            int dpi = _contextMenu.DeviceDpi <= 0 ? 96 : _contextMenu.DeviceDpi;
            int glyph = Math.Max(16, (int)Math.Round(16 * dpi / 96.0));
            if (_contextMenu.ImageScalingSize.Width != glyph)
            {
                _contextMenu.ImageScalingSize = new Size(glyph, glyph);
            }
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateMonitorIcon(),
            ContextMenuStrip = _contextMenu,
            Text = "Monitor Layout Switcher",
            Visible = true
        };

        _notifyIcon.DoubleClick += (s, e) => ShowConfigWindow();

        _lastHandledProfileSignature = GetProfileSignature();
        _profileWatcher = new FileSystemWatcher(AppDomain.CurrentDomain.BaseDirectory, "profiles.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _profileWatcher.Changed += ProfileFileChanged;
        _profileWatcher.Created += ProfileFileChanged;
        _profileWatcher.Renamed += ProfileFileChanged;

        _profileReloadTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _profileReloadTimer.Tick += (_, _) =>
        {
            if (!_profileChangePending) return;
            _profileChangePending = false;
            ReloadProfilesFromDisk();
        };
        _profileReloadTimer.Start();

        RefreshMenuAndHotkeys();
        DetectActiveProfile();

        // The layout can change without this app doing it — Windows display settings,
        // a monitor plugged or unplugged, a driver event. Without this the tray shows
        // a checkmark against a profile that is no longer live.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        LayoutSafety.UndoStateChanged += (_, _) =>
        {
            if (_notifyIcon.Visible) BeginInvokeOnUi(RefreshMenuAndHotkeys);
        };
        ShowConfigWindow();
    }

    private void BeginInvokeOnUi(Action action)
    {
        var form = _configForm;
        if (form != null && form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke(action);
        else action();
    }

    private void ProfileFileChanged(object? sender, FileSystemEventArgs e)
    {
        _profileChangePending = true;
    }

    private string GetProfileSignature()
    {
        try
        {
            if (!File.Exists(ProfileManager.ConfigPath)) return string.Empty;
            var info = new FileInfo(ProfileManager.ConfigPath);
            return $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private void MarkProfileFileHandled() =>
        _lastHandledProfileSignature = GetProfileSignature();

    private void ReloadProfilesFromDisk()
    {
        if (_reloadingProfiles) return;
        string signature = GetProfileSignature();
        if (string.IsNullOrEmpty(signature) || signature == _lastHandledProfileSignature) return;

        _reloadingProfiles = true;
        var loaded = ProfileManager.LoadProfiles();
        if (loaded.Count == 0)
        {
            _reloadingProfiles = false;
            return;
        }

        if (_configForm != null && !_configForm.IsDisposed && _configForm.HasUnsavedChanges)
        {
            var answer = MessageBox.Show(
                "profiles.json changed outside the app. Reload it and discard your unsaved editor changes?",
                "Profiles changed on disk",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                _lastHandledProfileSignature = signature;
                _reloadingProfiles = false;
                return;
            }
        }

        _profiles.Clear();
        _profiles.AddRange(loaded);
        _configForm?.ReloadProfilesFromDisk(_profiles);
        _lastHandledProfileSignature = signature;
        DetectActiveProfile();
        RefreshMenuAndHotkeys();
        _reloadingProfiles = false;
    }

    private void RefreshMenuAndHotkeys()
    {
        _hotkeyManager.UnregisterAll();
        _contextMenu.Items.Clear();

        var titleItem = new ToolStripMenuItem("Monitor Layout Switcher")
        {
            Enabled = false,
            // Derived from the menu's own font rather than a fixed point size, so it
            // follows the per-monitor DPI the menu was actually opened on.
            Font = new Font(_contextMenu.Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(232, 189, 99)
        };
        _contextMenu.Items.Add(titleItem);
        _contextMenu.Items.Add(new ToolStripSeparator());

        // Profile items. Checked state must reflect the actual live layout first —
        // _activeProfile (the last profile explicitly switched to) is only a
        // fallback for when the live layout doesn't match any saved profile at all
        // (e.g. it was changed some other way). Using both independently let a
        // stale _activeProfile stay checked alongside whichever profile the
        // display layout had actually already matched, showing two checked items.
        // One display query for the whole menu, not one per profile.
        var liveDisplays = DisplayEngine.GetCurrentDisplays();
        var liveMatches = _profiles.ToDictionary(p => p.Id, p => DisplayEngine.MatchesCurrent(p, liveDisplays));
        bool anyLive = liveMatches.Values.Any(v => v);

        if (_profiles.Count == 0)
        {
            _contextMenu.Items.Add(new ToolStripMenuItem("No layouts saved yet") { Enabled = false });
            _contextMenu.Items.Add(new ToolStripMenuItem("Capture your current layout to get started") { Enabled = false });
        }

        foreach (var profile in _profiles)
        {
            bool isLive = liveMatches[profile.Id];
            string label = string.IsNullOrWhiteSpace(profile.Hotkey)
                ? profile.Name
                : $"{profile.Name}   ({profile.Hotkey})";
            if (isLive) label += "  •";

            var item = new ToolStripMenuItem(label)
            {
                Checked = isLive || (!anyLive && _activeProfile?.Id == profile.Id)
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

        var undoItem = new ToolStripMenuItem(LayoutSafety.CanUndo
            ? $"Undo last layout ({LayoutSafety.RemainingSeconds}s)"
            : "Undo last layout")
        {
            Enabled = LayoutSafety.CanUndo
        };
        undoItem.Click += (s, e) =>
        {
            bool ok = LayoutSafety.Undo(out string msg);
            if (ok)
            {
                DetectActiveProfile();
                _notifyIcon.Text = "Monitor Layout Switcher";
            }
            else
            {
                _notifyIcon.ShowBalloonTip(3000, "Monitor Layout Switcher — Failed", msg, ToolTipIcon.Error);
            }
            RefreshMenuAndHotkeys();
        };
        _contextMenu.Items.Add(undoItem);

        var configItem = new ToolStripMenuItem("Configure Profiles...");
        configItem.Click += (s, e) => ShowConfigWindow();
        _contextMenu.Items.Add(configItem);

        var captureItem = new ToolStripMenuItem("Capture Current Layout as New Profile");
        captureItem.Click += (s, e) =>
        {
            var newP = DisplayEngine.CaptureCurrentLayoutAsProfile($"Layout {_profiles.Count + 1}", string.Empty);
            _profiles.Add(newP);
            if (!ProfileManager.TrySaveProfiles(_profiles, out string saveError))
            {
                _profiles.Remove(newP);
                _notifyIcon.ShowBalloonTip(4000, "Monitor Layout Switcher — Failed", saveError, ToolTipIcon.Error);
                return;
            }
            MarkProfileFileHandled();
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
        var turningOff = DisplayEngine.MonitorsThatWouldDisable(profile);
        if (turningOff.Count > 0)
        {
            var answer = MessageBox.Show(
                "This layout will turn off:\n\n• " + string.Join("\n• ", turningOff) +
                "\n\nUndo is available for 20 seconds after apply.",
                "Apply layout?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }

        bool success = LayoutSafety.Apply(profile, interactive: true, out string error);
        if (success)
        {
            _activeProfile = profile;
            _notifyIcon.Text = $"Monitor Layout: {profile.Name}";
            RefreshMenuAndHotkeys();
        }
        else
        {
            _notifyIcon.ShowBalloonTip(3000, "Monitor Layout Switcher — Failed", error, ToolTipIcon.Error);
        }
    }

    private void DetectActiveProfile()
    {
        _activeProfile = DisplayEngine.FindMatchingProfile(_profiles) ?? _activeProfile;
        if (_activeProfile != null)
        {
            _notifyIcon.Text = $"Monitor Layout: {_activeProfile.Name}";
        }
    }

    private void ShowConfigWindow()
    {
        if (_configForm == null || _configForm.IsDisposed)
        {
            _configForm = new ConfigForm(_profiles, () =>
            {
                DetectActiveProfile();
                RefreshMenuAndHotkeys();
                MarkProfileFileHandled();
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

    /// <summary>
    /// Re-reads the live layout after an external display change. Read-only — it
    /// never applies anything, so it cannot feed back into the change it is
    /// reacting to.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        BeginInvokeOnUi(() =>
        {
            RefreshMenuAndHotkeys();
            DetectActiveProfile();
        });
    }

    protected override void ExitThreadCore()
    {
        // SystemEvents holds a static, process-lifetime subscriber list; leaving this
        // attached keeps the whole TrayContext alive after exit.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        _profileReloadTimer.Stop();
        _profileReloadTimer.Dispose();
        _profileWatcher.Dispose();
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

        /// <summary>
        /// Draws the checkmark as vector art centred in the image margin, instead of
        /// letting the stock renderer blit a fixed-size glyph at a position it
        /// computed before DPI scaling was applied. That mismatch is what made the
        /// tick sit slightly outside the menu at scaling above 100%.
        /// </summary>
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;

            // Square, centred, and inset — never wider than the margin it sits in.
            int size = Math.Max(8, (int)Math.Round(Math.Min(r.Width, r.Height) * 0.72));
            var box = new Rectangle(
                r.X + (r.Width - size) / 2,
                r.Y + (r.Height - size) / 2,
                size,
                size);

            var g = e.Graphics;
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            Color tick = e.Item.Selected ? Color.Black : Color.FromArgb(232, 189, 99);
            using var pen = new Pen(tick, Math.Max(1.6f, size * 0.16f))
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };

            g.DrawLines(pen, new[]
            {
                new PointF(box.Left + size * 0.18f, box.Top + size * 0.52f),
                new PointF(box.Left + size * 0.42f, box.Top + size * 0.76f),
                new PointF(box.Left + size * 0.84f, box.Top + size * 0.24f)
            });

            g.SmoothingMode = oldMode;
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
        if (width <= 0 || height <= 0)
        {
            path.AddRectangle(new Rectangle(x, y, Math.Max(1, width), Math.Max(1, height)));
            return path;
        }

        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(width, height)));
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
