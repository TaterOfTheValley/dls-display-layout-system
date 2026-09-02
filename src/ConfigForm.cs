namespace DLS;

public class ConfigForm : Form
{
    private sealed class ProfileCardView
    {
        public DisplayProfile Profile { get; set; } = null!;
        public Panel CardPanel { get; set; } = null!;

        /// <summary>Whether the desktop currently looks like this layout. Held here
        /// rather than in a label so a repaint is only needed when it actually flips.</summary>
        public bool IsLive { get; set; }
    }

    private readonly List<DisplayProfile> _profiles;
    private readonly Action _onSaveCallback;
    private readonly List<ProfileCardView> _cardViews = new();
    private DisplayProfile? _selectedProfile;
    private bool _syncingInspector;
    private bool _isCapturingHotkey;

    private FlowLayoutPanel _profileCardsPanel = null!;
    private TextBox _nameTextBox = null!;
    private TextBox _hotkeyTextBox = null!;
    private Button _captureHotkeyBtn = null!;
    private MonitorCanvas _canvas = null!;
    private Label _inspectorTitle = null!;
    private Label _inspectorSub = null!;
    private CheckBox _includeToggle = null!;
    private Button _primaryBtn = null!;
    private Button _identifyOneBtn = null!;
    private Button _captureCurrentLayoutBtn = null!;
    private Button _cancelBtn = null!;
    private Button _addProfileBtn = null!;
    private Button _deleteProfileBtn = null!;
    private Button _identifyAllBtn = null!;
    private Button _duplicateBtn = null!;
    private Button _refreshBtn = null!;
    private Button _fitBtn = null!;
    private Button _applyBtn = null!;
    private Button _undoBtn = null!;
    private CheckBox _snapToggle = null!;
    private Panel _undoPanel = null!;
    private Label _undoLabel = null!;
    private Label _feedbackLabel = null!;
    private Label _statusLabel = null!;
    private string _statusTip = string.Empty;
    private Label _hotkeyHint = null!;

    /// <summary>
    /// The live topology, cached. The pending-changes indicator is recomputed on every
    /// edit — including every mouse-move of a drag — and a CCD enumeration per frame
    /// is exactly the stutter the rest of this app goes out of its way to avoid. The
    /// list can only go stale when the displays actually change, and every path that
    /// can do that already ends in <see cref="RefreshActiveBadges"/>, which re-queries.
    /// </summary>
    private List<DisplayInfo> _liveDisplays = new();
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _undoTimer = new() { Interval = 1000 };
    /// <summary>WinForms leaves Panel and FlowLayoutPanel unbuffered, so every child
    /// repaint flashes. These exist only to turn buffering on.</summary>
    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel() => DoubleBuffered = true;
    }

    private sealed class BufferedFlowPanel : FlowLayoutPanel
    {
        public BufferedFlowPanel() => DoubleBuffered = true;
    }

    private DisplayInfo? _live;
    private ComboBox _resolutionBox = null!;
    private ComboBox _refreshBox = null!;
    private ComboBox _scaleBox = null!;
    private Panel? _emptyPanel;
    private Control? _metaBar;
    private Control[]? _editorChrome;
    private List<DisplayInfo> _liveForEmptyState = new();
    private bool _dirty;
    private bool _dirtyNotified;
    private System.Windows.Forms.Timer? _autoSaveTimer;
    private bool _loading;

    public bool HasUnsavedChanges => _dirty;

    private float DpiScale => DeviceDpi / 96f;
    private int S(int val) => (int)Math.Round(val * DpiScale);

    public ConfigForm(List<DisplayProfile> profiles, Action onSaveCallback)
    {
        _profiles = profiles;
        _onSaveCallback = onSaveCallback;

        // Scaling here is fully manual (DpiScale / S(...) below, used throughout every
        // Build* method), so WinForms' own AutoScaleMode.Dpi must stay off — running
        // both at once means two independent scaling systems compound against each
        // other instead of cooperating. DPI changes are instead handled explicitly in
        // OnDpiChanged.
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        MinimumSize = new Size(S(1020), S(680));
        ClientSize = new Size(S(1160), S(750));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UiTheme.Bg;
        ForeColor = UiTheme.Text;
        Font = new Font("Segoe UI", 9.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
        DoubleBuffered = true;
        KeyPreview = true;
        Text = AppInfo.Name;
        Icon = AppIcon.Shared;
        Padding = new Padding(0);

        // Docking runs from the highest control index down, so the add order below is
        // the reverse of the visual order: rail and meta end up at the top, undo and
        // footer at the bottom, and the editor takes everything left over.
        //
        // There is no in-app title bar any more — the window already carries the app
        // name, and that band was 52px of pure repetition. The vertical layout rail is
        // gone too: a list of two or three layouts left most of a 290px column empty
        // while the canvas, the thing you actually manipulate, was squeezed beside it.
        Controls.Add(BuildEditor());
        _metaBar = BuildMetaBar();
        Controls.Add(_metaBar);
        Controls.Add(BuildLayoutRail());
        Controls.Add(BuildUndoBar());
        Controls.Add(BuildFooter());

        if (_profiles.Count > 0) _selectedProfile = _profiles[0];
        RebuildProfileCards();
        LoadSelectedProfile();
        UpdateEmptyState();
        MarkClean();
        FormClosing += ConfigForm_FormClosing;
        _undoTimer.Tick += (_, _) => UpdateUndoBar();
        _undoTimer.Start();
        LayoutSafety.UndoStateChanged += OnUndoStateChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        UpdateUndoBar();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _undoTimer.Stop();
            _undoTimer.Dispose();
            LayoutSafety.UndoStateChanged -= OnUndoStateChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnUndoStateChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(UpdateUndoBar);
    }

    // Every Font and every fixed pixel size/position in this form is computed once,
    // at construction time, from DpiScale (DeviceDpi / 96f) — but DeviceDpi is only
    // meaningless-vs-actually-correct once the window handle exists and Windows has
    // decided which monitor it lives on. Because this form is a PerMonitorV2 window
    // (ApplicationHighDpiMode in the .csproj), Windows sends WM_DPICHANGED whenever
    // that monitor's scale factor changes, or the window moves to a monitor with a
    // different one — this is the only reliable point to redo that one-time math.
    /// <summary>
    /// Keyboard control for the editor. A layout switcher is a keyboard tool — the
    /// whole point is not reaching for the mouse — so the window it is configured in
    /// should not force you to either.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+1..9 jumps straight to a layout, mirroring the global shortcuts.
        if ((keyData & Keys.Control) == Keys.Control)
        {
            var key = keyData & Keys.KeyCode;
            if (key >= Keys.D1 && key <= Keys.D9)
            {
                int index = key - Keys.D1;
                if (index < _profiles.Count) SelectProfile(_profiles[index]);
                return true;
            }

            switch (key)
            {
                case Keys.Z when LayoutSafety.CanUndo:
                    UndoLayout();
                    return true;

                case Keys.Return:
                    ApplySelected();
                    return true;

                case Keys.N:
                    AddProfileBtn_Click(this, EventArgs.Empty);
                    return true;
            }
        }

        // Escape closes, unless a shortcut is being recorded — there it means "stop
        // listening", which is what the user will expect first.
        if (keyData == Keys.Escape)
        {
            if (_isCapturingHotkey)
            {
                EndHotkeyCapture();
                return true;
            }

            Close();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (e.DeviceDpiOld <= 0 || e.DeviceDpiNew == e.DeviceDpiOld) return;

        float ratio = e.DeviceDpiNew / (float)e.DeviceDpiOld;

        Font = ScaleFont(Font, ratio);
        MinimumSize = ScaleSize(MinimumSize, ratio);
        foreach (Control child in Controls)
        {
            RescaleControlForDpiChange(child, ratio);
        }

        // Resizing the form last (after every descendant's Font/explicit Size has
        // already been rescaled) is what makes the existing Resize-handler layout
        // code in the Build* methods above reposition everything correctly — those
        // handlers compute positions from the *current* sibling sizes and the
        // *current* DpiScale, so they only produce the right answer once both of
        // those are already up to date.
        Bounds = e.SuggestedRectangle;
        PerformLayout();
        Invalidate(true);
    }

    private static void RescaleControlForDpiChange(Control control, float ratio)
    {
        control.Font = ScaleFont(control.Font, ratio);
        control.Padding = ScalePadding(control.Padding, ratio);
        control.Margin = ScalePadding(control.Margin, ratio);

        switch (control.Dock)
        {
            case DockStyle.None:
                control.Bounds = new Rectangle(
                    (int)Math.Round(control.Left * ratio),
                    (int)Math.Round(control.Top * ratio),
                    (int)Math.Round(control.Width * ratio),
                    (int)Math.Round(control.Height * ratio));
                break;
            case DockStyle.Top:
            case DockStyle.Bottom:
                control.Height = (int)Math.Round(control.Height * ratio);
                break;
            case DockStyle.Left:
            case DockStyle.Right:
                control.Width = (int)Math.Round(control.Width * ratio);
                break;
            // DockStyle.Fill: size is fully owned by the parent's layout, nothing to do.
        }

        if (control is SplitContainer nestedSplit)
        {
            nestedSplit.SplitterWidth = Math.Max(1, (int)Math.Round(nestedSplit.SplitterWidth * ratio));
        }

        foreach (Control grandchild in control.Controls)
        {
            RescaleControlForDpiChange(grandchild, ratio);
        }
    }

    private static Font ScaleFont(Font font, float ratio) =>
        new(font.FontFamily, font.Size * ratio, font.Style, font.Unit);

    private static Size ScaleSize(Size size, float ratio) =>
        new((int)Math.Round(size.Width * ratio), (int)Math.Round(size.Height * ratio));

    private static Padding ScalePadding(Padding padding, float ratio) => new(
        (int)Math.Round(padding.Left * ratio),
        (int)Math.Round(padding.Top * ratio),
        (int)Math.Round(padding.Right * ratio),
        (int)Math.Round(padding.Bottom * ratio));

    private Control BuildEmptyState()
    {
        _emptyPanel = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Panel, Visible = false };

        var capture = UiTheme.MakeButton("Save this as my first layout", true, DpiScale);
        capture.Size = new Size(S(260), S(40));
        capture.Click += (_, _) => CaptureFirstLayout();
        _emptyPanel.Controls.Add(capture);

        _emptyPanel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int w = Math.Min(S(520), _emptyPanel.Width - S(48));
            int cx = _emptyPanel.Width / 2;
            int top = Math.Max(S(24), _emptyPanel.Height / 2 - S(150));

            var glyph = new RectangleF(cx - w / 2f, top, w, S(130));
            LayoutGlyph.Draw(g, glyph, _liveForEmptyState,
                onColor: Color.FromArgb(190, UiTheme.Text),
                offColor: Color.FromArgb(110, UiTheme.Line),
                primaryColor: _liveForEmptyState.Count > 1 ? UiTheme.GoldDim : Color.FromArgb(190, UiTheme.Text),
                cornerRadius: 4f);

            using var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var head = new Font("Segoe UI", 19f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            using var headBrush = new SolidBrush(UiTheme.Text);
            g.DrawString(_liveForEmptyState.Count == 1
                    ? "This is your monitor, right now."
                    : $"These are your {_liveForEmptyState.Count} monitors, right now.",
                head, headBrush, new RectangleF(cx - w / 2f, top + S(150), w, S(30)), centre);

            using var sub = new Font("Segoe UI", 12f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            using var subBrush = new SolidBrush(UiTheme.Muted);
            g.DrawString(_liveForEmptyState.Count == 1
                    ? "Save it, then add more layouts as you connect more monitors."
                    : "Save this arrangement, then make a second layout with some of them turned off.",
                sub, subBrush, new RectangleF(cx - w / 2f, top + S(184), w, S(24)), centre);
        };

        _emptyPanel.Resize += (_, _) =>
        {
            int top = Math.Max(S(24), _emptyPanel.Height / 2 - S(150));
            capture.Location = new Point((_emptyPanel.Width - capture.Width) / 2, top + S(212));
            _emptyPanel.Invalidate();
        };

        return _emptyPanel;
    }

    private void CaptureFirstLayout()
    {
        var created = DisplayEngine.CaptureCurrentLayoutAsProfile("My layout", string.Empty);
        _profiles.Add(created);
        _selectedProfile = created;
        MarkDirty();
        FlushAutoSave();
        RebuildProfileCards();
        LoadSelectedProfile();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (_emptyPanel == null || _editorChrome == null) return;

        bool empty = _profiles.Count == 0;
        if (empty)
        {
            _liveForEmptyState = DisplayEngine.GetCurrentDisplays().Where(d => d.IsAttached).ToList();
        }

        foreach (var c in _editorChrome) c.Visible = !empty;
        if (_metaBar != null) _metaBar.Visible = !empty;
        _emptyPanel.Visible = empty;
        if (empty)
        {
            _emptyPanel.BringToFront();
            _emptyPanel.Invalidate();
        }

        // The footer's primary action has nothing to act on yet.
        _applyBtn.Enabled = !empty;
    }

    private Control BuildEditor()
    {
        var root = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Panel };

        _canvas = new MonitorCanvas { Dock = DockStyle.Fill };
        _canvas.LayoutChanged += (_, _) =>
        {
            UpdateInspector();
            InvalidateProfileCards();
            MarkDirty();
        };
        _canvas.SelectionChanged += (_, _) => UpdateInspector();
        _canvas.StatusMessage += (_, msg) =>
        {
            _feedbackLabel.ForeColor = UiTheme.Gold;
            _feedbackLabel.Text = msg;
        };

        var inspector = BuildInspector();

        _hotkeyHint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = S(22),
            ForeColor = UiTheme.Muted,
            Font = new Font("Segoe UI", 8.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            Text = CanvasHintText,
            TextAlign = ContentAlignment.MiddleLeft
        };

        root.Controls.Add(BuildEmptyState());
        root.Controls.Add(_canvas);
        root.Controls.Add(_hotkeyHint);
        root.Controls.Add(inspector);

        var tools = BuildCanvasTools();

        // Everything that only makes sense once a layout exists.
        _editorChrome = new Control[] { _canvas, _hotkeyHint, inspector, tools };
        return root;
    }

    /// <summary>
    /// View controls, floated over the canvas instead of occupying a band above it.
    /// They act on the canvas, so they belong on it — and the canvas gets the whole
    /// window rather than sharing it with a toolbar that is used once a session.
    /// </summary>
    private Control BuildCanvasTools()
    {
        var tools = new BufferedPanel { BackColor = UiTheme.Card, Height = S(38) };
        tools.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Line);
            e.Graphics.DrawRectangle(pen, 0, 0, tools.Width - 1, tools.Height - 1);
        };

        _fitBtn = UiTheme.MakeButton("Fit", false, DpiScale);
        _fitBtn.Size = new Size(S(58), S(28));
        _fitBtn.Click += (_, _) => _canvas.FitView();
        _tips.SetToolTip(_fitBtn, "Fit all monitors in view");

        _identifyAllBtn = UiTheme.MakeButton("Identify", false, DpiScale);
        _identifyAllBtn.Size = new Size(S(84), S(28));
        _identifyAllBtn.Click += (_, _) => IdentifyOverlays.ShowAll();
        _tips.SetToolTip(_identifyAllBtn, "Flash numbers on the physical monitors");

        _refreshBtn = UiTheme.MakeButton("Rescan", false, DpiScale);
        _refreshBtn.Size = new Size(S(78), S(28));
        _refreshBtn.Click += (_, _) => RefreshDisplays();
        _tips.SetToolTip(_refreshBtn, "Re-scan connected monitors");

        _snapToggle = new CheckBox
        {
            Text = "Snap",
            Checked = true,
            ForeColor = UiTheme.Text,
            BackColor = UiTheme.Card,
            Font = new Font("Segoe UI", 11f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            AutoSize = true,
            Cursor = Cursors.Hand
        };
        _snapToggle.CheckedChanged += (_, _) => _canvas.SnapEnabled = _snapToggle.Checked;
        _tips.SetToolTip(_snapToggle, "Magnetically align monitor edges while dragging");

        tools.Controls.AddRange(new Control[] { _snapToggle, _fitBtn, _identifyAllBtn, _refreshBtn });

        void Place()
        {
            int pad = S(5);
            int gap = S(6);
            int x = pad;
            _snapToggle.Location = new Point(x, (tools.Height - Math.Max(S(16), _snapToggle.Height)) / 2);
            x += Math.Max(S(52), _snapToggle.Width) + gap + S(4);
            foreach (var b in new[] { _fitBtn, _identifyAllBtn, _refreshBtn })
            {
                b.Location = new Point(x, pad);
                x += b.Width + gap;
            }
            tools.Width = x - gap + pad;
            tools.Location = new Point(Math.Max(0, _canvas.ClientSize.Width - tools.Width - S(16)), S(14));
            tools.BringToFront();
        }

        _canvas.Resize += (_, _) => Place();
        _canvas.Controls.Add(tools);
        Place();
        return tools;
    }

    private Control BuildInspector()
    {
        var panel = new BufferedPanel
        {
            Dock = DockStyle.Bottom,
            Height = S(118),
            BackColor = UiTheme.Card,
            Padding = new Padding(S(18), 0, S(18), 0)
        };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Line);
            e.Graphics.DrawLine(pen, 0, 0, panel.Width, 0);
        };

        _inspectorTitle = new Label
        {
            Font = new Font("Segoe UI", 10.5f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
            ForeColor = UiTheme.Text,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _inspectorSub = new Label
        {
            Font = new Font("Segoe UI", 8.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            ForeColor = UiTheme.Muted,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _includeToggle = new CheckBox
        {
            Text = "Include in layout",
            ForeColor = UiTheme.Text,
            Font = new Font("Segoe UI", 9f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            AutoSize = true,
            Cursor = Cursors.Hand
        };
        _includeToggle.CheckedChanged += (_, _) =>
        {
            if (_syncingInspector || _canvas.SelectedConfig == null) return;
            _canvas.SetEnabled(_canvas.SelectedConfig, _includeToggle.Checked);
            UpdateInspector();
        };

        _primaryBtn = UiTheme.MakeButton("Set as main", false, DpiScale);
        _primaryBtn.Size = new Size(S(135), S(34));
        _primaryBtn.Click += (_, _) =>
        {
            if (_canvas.SelectedConfig != null) _canvas.SetPrimary(_canvas.SelectedConfig);
            UpdateInspector();
        };

        _identifyOneBtn = UiTheme.MakeButton("Identify", false, DpiScale);
        _identifyOneBtn.Size = new Size(S(105), S(34));
        _identifyOneBtn.Click += (_, _) =>
        {
            if (_canvas.SelectedConfig != null) IdentifyOverlays.Show(_canvas.SelectedConfig.DeviceName);
        };

        var resolutionLabel = UiTheme.MakeEyebrow("RESOLUTION", DpiScale);
        var refreshLabel = UiTheme.MakeEyebrow("REFRESH", DpiScale);
        var scaleLabel = UiTheme.MakeEyebrow("SCALE", DpiScale);

        _resolutionBox = MakeCombo();
        _resolutionBox.SelectedIndexChanged += (_, _) => ResolutionChanged();
        _refreshBox = MakeCombo();
        _refreshBox.SelectedIndexChanged += (_, _) => RefreshRateChanged();
        _scaleBox = MakeCombo();
        _scaleBox.SelectedIndexChanged += (_, _) => ScaleChanged();

        panel.Resize += (_, _) =>
        {
            int pad = S(18);
            int right = panel.Width - pad;
            int rowY = S(58);
            int rowH = S(30);

            // Actions, right to left.
            _identifyOneBtn.SetBounds(right - _identifyOneBtn.Width, rowY, _identifyOneBtn.Width, rowH);
            _primaryBtn.SetBounds(_identifyOneBtn.Left - S(8) - _primaryBtn.Width, rowY, _primaryBtn.Width, rowH);
            _includeToggle.Location = new Point(_primaryBtn.Left - S(16) - _includeToggle.Width, rowY + (rowH - _includeToggle.Height) / 2);

            // Mode controls, left to right, filling what is left.
            int available = Math.Max(S(300), _includeToggle.Left - pad - S(24));
            int gap = S(10);
            int boxW = Math.Min(S(210), (available - gap * 2) / 3);

            int x = pad;
            foreach (var (lbl, box) in new (Control, Control)[]
                     { (resolutionLabel, _resolutionBox), (refreshLabel, _refreshBox), (scaleLabel, _scaleBox) })
            {
                lbl.SetBounds(x, S(38), boxW, S(16));
                box.SetBounds(x, rowY, boxW, rowH);
                x += boxW + gap;
            }

            _inspectorTitle.SetBounds(pad, S(10), Math.Max(S(160), panel.Width / 3), S(22));
            _inspectorSub.SetBounds(_inspectorTitle.Right + S(14), S(12), Math.Max(S(120), panel.Width - _inspectorTitle.Right - S(32)), S(20));
        };

        panel.Controls.Add(_inspectorTitle);
        panel.Controls.Add(_inspectorSub);
        panel.Controls.Add(_includeToggle);
        panel.Controls.Add(_primaryBtn);
        panel.Controls.Add(_identifyOneBtn);
        panel.Controls.Add(resolutionLabel);
        panel.Controls.Add(_resolutionBox);
        panel.Controls.Add(refreshLabel);
        panel.Controls.Add(_refreshBox);
        panel.Controls.Add(scaleLabel);
        panel.Controls.Add(_scaleBox);
        return panel;
    }

    private ComboBox MakeCombo() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        BackColor = UiTheme.Input,
        ForeColor = UiTheme.Text,
        Font = new Font("Segoe UI", 9.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel)
    };

    /// <summary>The mode list for the selected monitor, cached for the current selection.</summary>
    private List<DisplayModes.Mode> ModesFor(DisplayTargetConfig cfg)
    {
        string? gdi = DisplayEngine.GetCurrentDisplays()
            .FirstOrDefault(d => DisplayEngine.SameHardwareIdentity(d.MonitorDevicePath, cfg.MonitorDevicePath) && d.IsAttached)
            ?.DeviceName;
        return DisplayModes.For(cfg, gdi);
    }

    private void ResolutionChanged()
    {
        if (_syncingInspector) return;
        var cfg = _canvas.SelectedConfig;
        if (cfg == null || _resolutionBox.SelectedItem is not ResolutionChoice choice) return;
        if (cfg.Width == choice.W && cfg.Height == choice.H) return;

        cfg.Width = choice.W;
        cfg.Height = choice.H;

        // The saved timings describe the old mode, so they cannot be reused — a target
        // mode contradicting the new size is worse than none at all. Clearing them
        // makes the apply request a mode by size and refresh rate instead, which is
        // the documented way to ask for one.
        cfg.HasTargetMode = false;

        // Take the best rate the monitor can do at the new size rather than dropping
        // to whatever Windows picks — going from 4K144 to 1440p and silently landing
        // on 60Hz is a bad surprise.
        int best = DisplayModes.BestRateFor(ModesFor(cfg), choice.W, choice.H);
        SetRefresh(cfg, best);

        _canvas.RefreshLayoutGeometry();
        MarkDirty();
        UpdateInspector();
    }

    private void RefreshRateChanged()
    {
        if (_syncingInspector) return;
        var cfg = _canvas.SelectedConfig;
        if (cfg == null || _refreshBox.SelectedItem is not RefreshChoice choice) return;
        if (cfg.RefreshRate == choice.Hz) return;

        SetRefresh(cfg, choice.Hz);

        // The captured timings are for the old rate; asking by rate is what we want now.
        cfg.HasTargetMode = false;
        InvalidateProfileCards();
        MarkDirty();
        UpdateInspector();
    }

    /// <summary>
    /// Records a refresh rate as the exact rational CCD wants. A whole number is
    /// correct here even though real modes are often 143.998Hz: with no target mode
    /// supplied, this rate is a request that Windows matches against the modes the
    /// hardware genuinely has, rather than a timing we are asserting.
    /// </summary>
    private static void SetRefresh(DisplayTargetConfig cfg, int hz)
    {
        cfg.RefreshRate = hz;
        cfg.RefreshNumerator = hz > 0 ? (uint)hz : 0;
        cfg.RefreshDenominator = hz > 0 ? 1u : 0;
    }

    private void ScaleChanged()
    {
        if (_syncingInspector) return;
        var cfg = _canvas.SelectedConfig;
        if (cfg == null || _scaleBox.SelectedItem is not ScaleChoice choice) return;
        if (cfg.ScalePercent == choice.Percent) return;

        cfg.ScalePercent = choice.Percent;
        InvalidateProfileCards();
        MarkDirty();
        UpdateInspector();
    }

    private sealed record ResolutionChoice(int W, int H, bool Native)
    {
        public override string ToString() => Native ? $"{W} × {H}  (native)" : $"{W} × {H}";
    }

    private sealed record RefreshChoice(int Hz, int Current = 0)
    {
        public override string ToString() => Hz == Current ? $"{Hz} Hz  (now)" : $"{Hz} Hz";
    }

    private sealed record ScaleChoice(int Percent, int Current = 0, int Recommended = 0)
    {
        public override string ToString()
        {
            if (Percent == 0)
            {
                // Naming the value it leaves alone turns a vague option into a fact.
                return Current > 0 ? $"Leave unchanged  ({Current}% now)" : "Leave unchanged";
            }

            string note = Percent == Recommended ? "  (recommended)" : string.Empty;
            return $"{Percent}%{note}";
        }
    }

    private Control BuildFooter()
    {
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = S(70),
            BackColor = UiTheme.Panel,
            Padding = new Padding(S(24), 0, S(20), 0)
        };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Line);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        _feedbackLabel = new Label
        {
            ForeColor = UiTheme.Gold,
            Font = new Font("Segoe UI", 9f * DpiScale, FontStyle.Italic, GraphicsUnit.Pixel),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };

        // Reading of the selected layout against the desktop as it is now. With
        // autosave there is no unsaved state to warn about any more, so the question
        // that matters before pressing Apply is no longer "have I saved this?" but
        // "is this layout what my screens are actually doing?" — and nothing on screen
        // answered it. It sits against the Apply button because that is where the
        // answer is needed.
        _statusLabel = new Label
        {
            ForeColor = UiTheme.Muted,
            Font = new Font("Segoe UI", 9.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleRight
        };

        _cancelBtn = UiTheme.MakeButton("Close", false, DpiScale);
        _cancelBtn.Size = new Size(S(100), S(40));
        _cancelBtn.Click += (_, _) => Close();

        // One commit action. Edits persist on their own (see MarkDirty), so there is
        // nothing left for a Save button to do, and "apply without saving" was a
        // distinction with no meaning once saving is automatic.
        _applyBtn = UiTheme.MakeButton("Apply layout", true, DpiScale);
        _applyBtn.Size = new Size(S(150), S(40));
        _applyBtn.Click += (_, _) => ApplySelected();
        _tips.SetToolTip(_applyBtn, "Switch Windows to this layout. It reverts by itself unless you confirm.");

        footer.Resize += (_, _) =>
        {
            _applyBtn.Location = new Point(footer.Width - _applyBtn.Width - S(20), (footer.Height - _applyBtn.Height) / 2);
            _cancelBtn.Location = new Point(_applyBtn.Left - S(10) - _cancelBtn.Width, (footer.Height - _cancelBtn.Height) / 2);

            int statusW = Math.Min(S(320), Math.Max(S(120), _cancelBtn.Left - S(200)));
            _statusLabel.SetBounds(_cancelBtn.Left - S(18) - statusW, 0, statusW, footer.Height);
            _feedbackLabel.SetBounds(S(24), 0, Math.Max(S(80), _statusLabel.Left - S(36)), footer.Height);
        };

        footer.Controls.Add(_feedbackLabel);
        footer.Controls.Add(_statusLabel);
        footer.Controls.Add(_cancelBtn);
        footer.Controls.Add(_applyBtn);
        return footer;
    }

    private Control BuildUndoBar()
    {
        _undoPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = S(42),
            BackColor = UiTheme.CardActive,
            Visible = false
        };
        _undoLabel = new Label
        {
            ForeColor = UiTheme.Gold,
            Font = new Font("Segoe UI", 9f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _undoBtn = UiTheme.MakeButton("Undo layout", true, DpiScale);
        _undoBtn.Size = new Size(S(130), S(30));
        _undoBtn.Click += (_, _) => UndoLayout();
        _undoPanel.Resize += (_, _) =>
        {
            _undoBtn.Location = new Point(_undoPanel.Width - _undoBtn.Width - S(16), S(6));
            _undoLabel.SetBounds(S(16), 0, Math.Max(S(80), _undoBtn.Left - S(24)), _undoPanel.Height);
        };
        _undoPanel.Controls.Add(_undoLabel);
        _undoPanel.Controls.Add(_undoBtn);
        return _undoPanel;
    }

    private const string CanvasHintText =
        "Click a monitor to select it  ·  Double-click for main  ·  Arrow keys nudge  ·  Scroll to zoom  ·  Ctrl+1…9 switch layout  ·  Ctrl+Enter apply";

    private const int RailCardW = 196;
    private const int RailCardH = 84;

    /// <summary>
    /// The layout picker, as a horizontal strip of thumbnails.
    ///
    /// A layout is a picture — you recognise "the one with just the left screen"
    /// from its shape long before you read its name — so the card leads with the
    /// arrangement and the name comes second. Horizontal because layouts are few and
    /// wide space is cheap, where the old vertical rail spent a fifth of the window
    /// on two entries and a lot of nothing.
    /// </summary>
    private Control BuildLayoutRail()
    {
        var rail = new Panel
        {
            Dock = DockStyle.Top,
            Height = S(RailCardH) + S(30),
            BackColor = UiTheme.Panel,
            Padding = new Padding(S(18), S(10), S(18), S(10))
        };
        rail.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Line);
            e.Graphics.DrawLine(pen, 0, rail.Height - 1, rail.Width, rail.Height - 1);
        };

        _profileCardsPanel = new BufferedFlowPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Panel,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0)
        };

        rail.Controls.Add(_profileCardsPanel);
        return rail;
    }

    /// <summary>
    /// Name, shortcut, and the actions that operate on the selected layout. These used
    /// to be spread across a sidebar button block and a separate two-column form; one
    /// row keeps everything about "this layout" in a single place.
    /// </summary>
    private Control BuildMetaBar()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = S(76),
            BackColor = UiTheme.Bg,
            Padding = new Padding(S(18), S(10), S(18), S(10))
        };

        var nameLabel = UiTheme.MakeEyebrow("LAYOUT NAME", DpiScale);
        var shortcutLabel = UiTheme.MakeEyebrow("GLOBAL SHORTCUT", DpiScale);

        _nameTextBox = new TextBox
        {
            BackColor = UiTheme.Input,
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 12f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            PlaceholderText = "Layout name"
        };
        _nameTextBox.TextChanged += (_, _) =>
        {
            if (_selectedProfile == null) return;
            _selectedProfile.Name = _nameTextBox.Text;
            InvalidateProfileCards();
            MarkDirty();
        };

        _hotkeyTextBox = new TextBox
        {
            BackColor = UiTheme.Input,
            ForeColor = UiTheme.Gold,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 12f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
            PlaceholderText = "Click, then press a shortcut",
            TextAlign = HorizontalAlignment.Center
        };
        _hotkeyTextBox.GotFocus += (_, _) => StartHotkeyCapture();
        _hotkeyTextBox.KeyDown += HotkeyTextBox_KeyDown;
        _captureHotkeyBtn = UiTheme.MakeButton("Record", false, DpiScale);
        _captureHotkeyBtn.Click += (_, _) => { _hotkeyTextBox.Focus(); StartHotkeyCapture(); };
        _tips.SetToolTip(_hotkeyTextBox, "Global shortcut that switches to this layout");

        _captureCurrentLayoutBtn = UiTheme.MakeButton("Capture", false, DpiScale);
        _captureCurrentLayoutBtn.Click += CaptureCurrentLayoutBtn_Click;
        _tips.SetToolTip(_captureCurrentLayoutBtn, "Replace this layout with the monitors as they are arranged right now");

        _duplicateBtn = UiTheme.MakeButton("Duplicate", false, DpiScale);
        _duplicateBtn.Click += DuplicateProfileBtn_Click;
        _tips.SetToolTip(_duplicateBtn, "Copy the selected layout");

        _deleteProfileBtn = UiTheme.MakeButton("Delete", false, DpiScale);
        _deleteProfileBtn.Click += DeleteProfileBtn_Click;

        // Kept for the empty state, which calls it directly.
        _addProfileBtn = UiTheme.MakeButton("+ Add", false, DpiScale);
        _addProfileBtn.Click += AddProfileBtn_Click;
        _addProfileBtn.Visible = false;

        bar.Resize += (_, _) =>
        {
            int y = S(30);
            int h = S(34);
            int gap = S(8);
            int right = bar.Width - S(18);

            foreach (var b in new[] { _deleteProfileBtn, _duplicateBtn, _captureCurrentLayoutBtn })
            {
                int w = S(96);
                right -= w;
                b.SetBounds(right, y, w, h);
                right -= gap;
            }

            int recordW = S(84);
            int hotkeyW = S(220);
            right -= S(14);
            _captureHotkeyBtn.SetBounds(right - recordW, y, recordW, h);
            _hotkeyTextBox.SetBounds(right - recordW - gap - hotkeyW, y, hotkeyW, h);

            int nameW = Math.Min(S(360), Math.Max(S(160), _hotkeyTextBox.Left - S(38)));
            _nameTextBox.SetBounds(S(18), y, nameW, h);

            nameLabel.SetBounds(S(18), S(10), nameW, S(16));
            shortcutLabel.SetBounds(_hotkeyTextBox.Left, S(10), _hotkeyTextBox.Width, S(16));
        };

        bar.Controls.AddRange(new Control[]
        {
            nameLabel, shortcutLabel,
            _nameTextBox, _hotkeyTextBox, _captureHotkeyBtn,
            _captureCurrentLayoutBtn, _duplicateBtn, _deleteProfileBtn, _addProfileBtn
        });
        return bar;
    }

    /// <summary>Rebuilds the layout thumbnails.</summary>
    private void RebuildProfileCards()
    {
        _profileCardsPanel.SuspendLayout();
        _profileCardsPanel.Controls.Clear();
        _cardViews.Clear();

        foreach (var profile in _profiles)
        {
            var p = profile;
            var card = new BufferedPanel
            {
                Width = S(RailCardW),
                Height = S(RailCardH),
                BackColor = UiTheme.Panel,
                Margin = new Padding(0, 0, S(10), 0),
                Cursor = Cursors.Hand,
                Tag = p
            };

            var view = new ProfileCardView { Profile = p, CardPanel = card };

            card.Paint += (_, e) => PaintLayoutCard(e.Graphics, card, view);
            card.Click += (_, _) => SelectProfile(p);
            card.MouseEnter += (_, _) => card.Invalidate();
            card.MouseLeave += (_, _) => card.Invalidate();

            _cardViews.Add(view);
            _profileCardsPanel.Controls.Add(card);
        }

        var addTile = new BufferedPanel
        {
            Width = S(132),
            Height = S(RailCardH),
            BackColor = UiTheme.Panel,
            Margin = new Padding(0),
            Cursor = Cursors.Hand
        };
        addTile.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            bool hot = addTile.ClientRectangle.Contains(addTile.PointToClient(Cursor.Position));

            using var pen = new Pen(hot ? UiTheme.Gold : UiTheme.Line) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawRectangle(pen, 1, 1, addTile.Width - 3, addTile.Height - 3);

            using var font = new Font("Segoe UI", 12f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(hot ? UiTheme.Gold : UiTheme.Muted);
            using var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("+  New layout", font, brush, addTile.ClientRectangle, centre);
        };
        addTile.Click += AddProfileBtn_Click;
        addTile.MouseEnter += (_, _) => addTile.Invalidate();
        addTile.MouseLeave += (_, _) => addTile.Invalidate();
        _profileCardsPanel.Controls.Add(addTile);

        _profileCardsPanel.ResumeLayout();
        RefreshActiveBadges();
        UpdateEmptyState();
    }

    private void PaintLayoutCard(Graphics g, Control card, ProfileCardView view)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        bool selected = _selectedProfile?.Id == view.Profile.Id;
        bool hot = card.ClientRectangle.Contains(card.PointToClient(Cursor.Position));

        var body = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
        using (var bg = new SolidBrush(selected ? UiTheme.CardActive : hot ? UiTheme.CardHover : UiTheme.Card))
            g.FillRoundedRectangle(bg, body.X, body.Y, body.Width, body.Height, S(6));
        using (var pen = new Pen(selected ? UiTheme.Gold : UiTheme.Line, selected ? 2f : 1f))
            g.DrawRoundedRectangle(pen, body.X, body.Y, body.Width, body.Height, S(6));

        // The arrangement, which is what actually distinguishes one layout from another.
        LayoutGlyph.Draw(g,
            new RectangleF(S(12), S(12), S(56), S(34)),
            view.Profile.Displays,
            onColor: Color.FromArgb(selected ? 200 : 150, UiTheme.Text),
            offColor: Color.FromArgb(90, UiTheme.Line),
            primaryColor: selected ? UiTheme.Gold : UiTheme.GoldDim);

        using var trim = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        using (var nameFont = new Font("Segoe UI", 13f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(selected ? UiTheme.Gold : UiTheme.Text))
        {
            g.DrawString(view.Profile.Name, nameFont, ink,
                new RectangleF(S(78), S(12), card.Width - S(90), S(20)), trim);
        }

        string sub = view.Profile.NeedsRecapture
            ? "Needs re-capture"
            : string.IsNullOrWhiteSpace(view.Profile.Hotkey) ? "No shortcut" : view.Profile.Hotkey;
        using (var subFont = new Font("Segoe UI", 11f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var subInk = new SolidBrush(view.Profile.NeedsRecapture ? UiTheme.Danger : UiTheme.Muted))
        {
            g.DrawString(sub, subFont, subInk,
                new RectangleF(S(78), S(33), card.Width - S(90), S(18)), trim);
        }

        if (view.IsLive)
        {
            using var liveFont = new Font("Segoe UI", 10f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
            using var liveInk = new SolidBrush(UiTheme.Gold);
            g.DrawString("LIVE", liveFont, liveInk, new PointF(S(78), card.Height - S(24)));
        }
    }

    private void SelectProfile(DisplayProfile p)
    {
        if (_selectedProfile?.Id == p.Id) return;
        _selectedProfile = p;
        InvalidateProfileCards();
        LoadSelectedProfile();
    }
    private void UpdateSelectedCardHotkey(string hotkey)
    {
        var view = _cardViews.FirstOrDefault(v => v.Profile.Id == _selectedProfile?.Id);
        view?.CardPanel.Invalidate();
    }

    private void InvalidateProfileCards()
    {
        foreach (var view in _cardViews)
        {
            view.CardPanel.Invalidate();
        }
    }

    private void LoadSelectedProfile()
    {
        if (_selectedProfile == null) return;
        _loading = true;
        _nameTextBox.Text = _selectedProfile.Name;
        _hotkeyTextBox.Text = _selectedProfile.Hotkey;
        _canvas.Bind(_selectedProfile);
        UpdateInspector();
        RefreshActiveBadges();
        _loading = false;
    }

    public void ReloadProfilesFromDisk(IEnumerable<DisplayProfile> profiles)
    {
        _loading = true;
        if (!ReferenceEquals(_profiles, profiles))
        {
            var replacement = profiles.ToList();
            _profiles.Clear();
            _profiles.AddRange(replacement);
        }
        _selectedProfile = _profiles.FirstOrDefault();
        RebuildProfileCards();
        LoadSelectedProfile();
        MarkClean();
        _loading = false;
    }

    private void UpdateInspector()
    {
        var sel = _canvas.SelectedConfig;
        _syncingInspector = true;
        if (sel == null)
        {
            _inspectorTitle.Text = "No display selected";
            _inspectorSub.Text = "Click a monitor on the canvas, then drag it into place.";
            _includeToggle.Enabled = false;
            _includeToggle.Checked = false;
            _primaryBtn.Enabled = false;
            _identifyOneBtn.Enabled = false;
            _live = null;
            _resolutionBox.Enabled = false;
            _refreshBox.Enabled = false;
            _scaleBox.Enabled = false;
            _resolutionBox.Items.Clear();
            _refreshBox.Items.Clear();
            _scaleBox.Items.Clear();
        }
        else
        {
            _inspectorTitle.Text = string.IsNullOrWhiteSpace(sel.MonitorId) ? sel.DeviceName : sel.MonitorId;

            var liveDisplays = DisplayEngine.GetCurrentDisplays();
            _live = liveDisplays.FirstOrDefault(d =>
                DisplayEngine.SameHardwareIdentity(d.MonitorDevicePath, sel.MonitorDevicePath));

            // Prefer what the layout specifies; fall back to what Windows reports, so
            // a value we can actually read is never hidden just because the layout has
            // no opinion about it.
            int hz = sel.RefreshRate > 0 ? sel.RefreshRate : (_live?.RefreshRate ?? 0);
            int scale = sel.ScalePercent > 0 ? sel.ScalePercent : (_live?.ScalePercent ?? 0);

            var parts = new List<string> { $"{sel.Width} × {sel.Height}" };
            if (hz > 0) parts.Add($"{hz} Hz");
            if (scale > 0) parts.Add($"{scale}% scale");
            if (sel.NativeWidth > 0) parts.Add($"native {sel.NativeWidth} × {sel.NativeHeight}");
            parts.Add(_live != null ? "Connected" : "Not connected");
            if (sel.IsPrimary) parts.Add("Main");

            _inspectorSub.Text = string.Join("  ·  ", parts);
            _includeToggle.Enabled = true;
            _includeToggle.Checked = sel.Enabled;
            _primaryBtn.Enabled = sel.Enabled && !sel.IsPrimary;
            _primaryBtn.Text = sel.IsPrimary ? "Main display" : "Set as main";
            _identifyOneBtn.Enabled = true;
            PopulateModes(sel);
            PopulateScale(sel);
        }
        _syncingInspector = false;
    }

    private void PopulateModes(DisplayTargetConfig sel)
    {
        var modes = ModesFor(sel);

        _resolutionBox.Enabled = true;
        _resolutionBox.Items.Clear();
        foreach (var (w, h) in DisplayModes.Resolutions(modes))
        {
            _resolutionBox.Items.Add(new ResolutionChoice(w, h, w == sel.NativeWidth && h == sel.NativeHeight));
        }
        Select(_resolutionBox, i => i is ResolutionChoice c && c.W == sel.Width && c.H == sel.Height);

        _refreshBox.Enabled = true;
        _refreshBox.Items.Clear();
        int liveHz = _live?.RefreshRate ?? 0;
        foreach (int hz in DisplayModes.RatesFor(modes, sel.Width, sel.Height))
        {
            _refreshBox.Items.Add(new RefreshChoice(hz, liveHz));
        }
        Select(_refreshBox, i => i is RefreshChoice c && c.Hz == sel.RefreshRate);

        static void Select(ComboBox box, Func<object?, bool> match)
        {
            for (int i = 0; i < box.Items.Count; i++)
            {
                if (match(box.Items[i])) { box.SelectedIndex = i; return; }
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }
    }

    private void PopulateScale(DisplayTargetConfig sel)
    {
        _scaleBox.Enabled = true;
        _scaleBox.Items.Clear();

        // "Leave unchanged" is the default and is not the same as any percentage:
        // it means the layout has no opinion, so switching to it will not touch
        // whatever scaling the monitor already has.
        int current = _live?.ScalePercent ?? 0;
        int recommended = _live?.RecommendedScalePercent ?? 0;
        _scaleBox.Items.Add(new ScaleChoice(0, current, recommended));
        foreach (int percent in Ccd.DpiScaleSteps)
        {
            _scaleBox.Items.Add(new ScaleChoice(percent, current, recommended));
        }

        for (int i = 0; i < _scaleBox.Items.Count; i++)
        {
            if (_scaleBox.Items[i] is ScaleChoice c && c.Percent == sel.ScalePercent)
            {
                _scaleBox.SelectedIndex = i;
                return;
            }
        }

        _scaleBox.SelectedIndex = 0;
    }

    /// <summary>Stops listening for a shortcut without recording one.</summary>
    private void EndHotkeyCapture()
    {
        if (!_isCapturingHotkey) return;
        _isCapturingHotkey = false;
        _captureHotkeyBtn.Text = "Record";
        _hotkeyHint.ForeColor = UiTheme.Muted;
        _hotkeyHint.Text = CanvasHintText;
        ActiveControl = null;
    }

    private void StartHotkeyCapture()
    {
        _isCapturingHotkey = true;
        _hotkeyHint.Text = "Listening… press a key combination (Ctrl, Alt, Shift, Win + key)";
        _hotkeyHint.ForeColor = UiTheme.Gold;
        _captureHotkeyBtn.Text = "Listening";
    }

    private void HotkeyTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey) return;
        if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin) return;

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");
        if (e.KeyCode is Keys.LWin or Keys.RWin) parts.Add("Win");
        parts.Add(e.KeyCode.ToString());

        string captured = string.Join(" + ", parts);
        _hotkeyTextBox.Text = captured;
        if (_selectedProfile != null)
        {
            _selectedProfile.Hotkey = captured;
            UpdateSelectedCardHotkey(captured);
            MarkDirty();
        }

        _isCapturingHotkey = false;
        _hotkeyHint.Text = $"Captured shortcut: {captured}";
        _hotkeyHint.ForeColor = UiTheme.Ok;
        _captureHotkeyBtn.Text = "Record";
        e.SuppressKeyPress = true;
    }

    private void AddProfileBtn_Click(object? sender, EventArgs e)
    {
        var created = DisplayEngine.CaptureCurrentLayoutAsProfile($"Profile {_profiles.Count + 1}", string.Empty);
        _profiles.Add(created);
        _selectedProfile = created;
        RebuildProfileCards();
        LoadSelectedProfile();
        MarkDirty();
        _feedbackLabel.ForeColor = UiTheme.Gold;
        _feedbackLabel.Text = $"Created '{created.Name}' from the current layout.";
    }

    private void DeleteProfileBtn_Click(object? sender, EventArgs e)
    {
        if (_selectedProfile == null) return;

        // Deleting the last layout is allowed. The app starts with none and has a
        // first-run state for exactly that, so refusing to let the user get back
        // there was the editor disagreeing with the rest of the app.
        string name = _selectedProfile.Name;
        _profiles.Remove(_selectedProfile);
        _selectedProfile = _profiles.FirstOrDefault();

        RebuildProfileCards();
        LoadSelectedProfile();
        MarkDirty();
        _feedbackLabel.ForeColor = UiTheme.Gold;
        _feedbackLabel.Text = $"Deleted '{name}'.";
    }

    private void CaptureCurrentLayoutBtn_Click(object? sender, EventArgs e)
    {
        if (_selectedProfile == null) return;
        var captured = DisplayEngine.CaptureCurrentLayoutAsProfile(_selectedProfile.Name, _selectedProfile.Hotkey);
        _selectedProfile.Displays = captured.Displays;
        LoadSelectedProfile();
        InvalidateProfileCards();
        MarkDirty();
        _feedbackLabel.ForeColor = UiTheme.Gold;
        _feedbackLabel.Text = $"Captured the current screen layout into '{_selectedProfile.Name}'.";
    }

    private void DuplicateProfileBtn_Click(object? sender, EventArgs e)
    {
        if (_selectedProfile == null) return;
        var copy = _selectedProfile.Clone();
        copy.Name = UniqueName(_selectedProfile.Name + " copy");
        copy.Hotkey = string.Empty;
        _profiles.Add(copy);
        _selectedProfile = copy;
        RebuildProfileCards();
        LoadSelectedProfile();
        MarkDirty();
        _feedbackLabel.ForeColor = UiTheme.Gold;
        _feedbackLabel.Text = $"Duplicated as '{copy.Name}'.";
    }

    private string UniqueName(string baseName)
    {
        string name = baseName;
        int n = 2;
        while (_profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} {n++}";
        }
        return name;
    }

    private void ScheduleAutoSave()
    {
        _autoSaveTimer ??= CreateAutoSaveTimer();
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private System.Windows.Forms.Timer CreateAutoSaveTimer()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 600 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            FlushAutoSave();
        };
        return timer;
    }

    /// <summary>Writes any pending edits to disk immediately.</summary>
    public void FlushPendingEdits() => FlushAutoSave();

    /// <summary>Writes pending edits immediately. Called on close so nothing is lost
    /// inside the debounce window.</summary>
    private void FlushAutoSave()
    {
        _autoSaveTimer?.Stop();
        if (!_dirty) return;

        if (!ProfileManager.TrySaveProfiles(_profiles, out string error))
        {
            _feedbackLabel.ForeColor = UiTheme.Danger;
            _feedbackLabel.Text = error;
            return;
        }

        _onSaveCallback?.Invoke();
        MarkClean();
    }

    private void ApplySelected()
    {
        if (_selectedProfile == null) return;

        if (DisplayEngine.MatchesCurrent(_selectedProfile))
        {
            _feedbackLabel.ForeColor = UiTheme.Ok;
            _feedbackLabel.Text = $"'{_selectedProfile.Name}' is already active.";
            return;
        }

        // Pending edits go to disk before the switch, so what gets applied and what
        // is stored can never disagree.
        FlushAutoSave();

        // No "this will turn off X" modal any more. The apply itself now reverts
        // unless confirmed (KeepLayoutDialog), so a blocking warning beforehand asked
        // the user to predict a consequence they are about to be shown directly —
        // two confirmations for one action.
        bool ok = LayoutSafety.Apply(_selectedProfile, interactive: true, out string msg);
        _feedbackLabel.Text = msg;
        _feedbackLabel.ForeColor = ok ? UiTheme.Ok : UiTheme.Danger;
        UpdateUndoBar();
        RefreshActiveBadges();
        _canvas.RefreshHardware();
        UpdateInspector();
    }

    private void UndoLayout()
    {
        bool ok = LayoutSafety.Undo(out string msg);
        _feedbackLabel.Text = msg;
        _feedbackLabel.ForeColor = ok ? UiTheme.Ok : UiTheme.Danger;
        UpdateUndoBar();
        RefreshActiveBadges();
        _canvas.RefreshHardware();
        UpdateInspector();
    }

    private void RefreshDisplays()
    {
        DisplayModes.Invalidate();
        CcdEngine.InvalidateCaches();
        _canvas.RefreshHardware();
        UpdateInspector();
        RefreshActiveBadges();
        _feedbackLabel.ForeColor = UiTheme.Gold;
        _feedbackLabel.Text = "Rescanned connected monitors.";
    }

    /// <summary>
    /// Records an edit and schedules it to disk. Saving is automatic: there is no
    /// Save button, so an edit the user can see must already be an edit that
    /// survives closing the window.
    ///
    /// Debounced rather than immediate — dragging a monitor raises this on every
    /// mouse move, and rewriting profiles.json per frame would be pointless churn
    /// (and would wake the file watcher in TrayContext each time).
    /// </summary>
    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        ScheduleAutoSave();

        // The edit is now saved but not applied, which is precisely the state the
        // footer has to keep visible.
        UpdateLayoutStatus();

        if (_dirtyNotified) return;
        _dirtyNotified = true;
        UpdateWindowTitle();
    }

    private void MarkClean()
    {
        _dirty = false;
        _dirtyNotified = false;
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        string star = "";
        var live = LiveProfile();
        string active = live != null ? $"  —  {live.Name} is active" : "";
        Text = AppInfo.Name + star + active;
    }

    /// <summary>The saved layout the desktop currently matches, from the cached
    /// topology — see <see cref="_liveDisplays"/>.</summary>
    private DisplayProfile? LiveProfile() =>
        _profiles.FirstOrDefault(p => DisplayEngine.MatchesCurrent(p, _liveDisplays));

    /// <summary>
    /// Re-reads the live topology and updates everything derived from it. This is the
    /// one place that queries, so every caller that changes the displays — apply,
    /// undo, rescan, a change made outside this app — ends up here.
    /// </summary>
    private void RefreshActiveBadges()
    {
        _liveDisplays = DisplayEngine.GetCurrentDisplays();

        var live = LiveProfile();
        foreach (var view in _cardViews)
        {
            bool isLive = live?.Id == view.Profile.Id;

            // Repaint only on an actual change; this runs from a timer, and repainting
            // every card each tick is what made the list flicker.
            if (view.IsLive == isLive) continue;
            view.IsLive = isLive;
            view.CardPanel.Invalidate();
        }
        UpdateWindowTitle();
        UpdateLayoutStatus();
    }

    /// <summary>
    /// Says whether the selected layout is what the screens are doing, and if not, how
    /// many things applying it would change. Cheap — it compares against the cached
    /// topology — so it can run on every edit, which is the point: an edit that
    /// autosaves itself should visibly become something still waiting to be applied.
    /// </summary>
    private void UpdateLayoutStatus()
    {
        if (_statusLabel is null) return;

        if (_selectedProfile == null || _profiles.Count == 0)
        {
            _statusLabel.Text = string.Empty;
            _statusTip = string.Empty;
            _tips.SetToolTip(_statusLabel, string.Empty);
            return;
        }

        var differences = DisplayEngine.Compare(_selectedProfile, _liveDisplays);
        bool pending = differences.Count > 0;

        string text = pending
            ? $"●  {differences.Count} pending change{(differences.Count == 1 ? "" : "s")} — not applied"
            : "●  In use — matches your displays";
        var colour = pending ? UiTheme.Gold : UiTheme.Ok;

        // The count answers "is anything pending?"; the list behind it is there for
        // when the answer is surprising. Tracked separately from the label text
        // because dragging one monitor back as another goes astray keeps the count
        // identical while the reasons change completely.
        string tip = pending
            ? "Applying this layout would:\n  " + string.Join("\n  ", differences)
            : "This layout is what Windows is using right now.";

        // Only touch either when it actually changed: this runs on every mouse-move of
        // a canvas drag, and reassigning Text repaints the footer.
        if (_statusLabel.Text != text) _statusLabel.Text = text;
        if (_statusLabel.ForeColor != colour) _statusLabel.ForeColor = colour;
        if (_statusTip != tip)
        {
            _statusTip = tip;
            _tips.SetToolTip(_statusLabel, tip);
        }
    }

    // Keeps the canvas honest when the layout changes outside this window — a
    // monitor plugged in, or settings changed in Windows itself.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        DisplayModes.Invalidate();
        CcdEngine.InvalidateCaches();
        BeginInvoke(() =>
        {
            _canvas.RefreshHardware();

            // A layout that stopped matching because the displays changed underneath
            // it — Windows settings, a monitor unplugged, an auto-revert — is pending
            // just as much as one the user edited.
            RefreshActiveBadges();
        });
    }

    private void UpdateUndoBar()
    {
        // The overlay already asks "keep or revert?" for every interactive apply, with
        // its own countdown — showing this bar at the same time was the same question
        // twice. It only needs to appear for a non-interactive apply (the CLI, a hotkey
        // with no message loop), where the overlay never opened in the first place.
        bool show = LayoutSafety.CanUndo && !KeepLayoutDialog.IsOpen;
        _undoPanel.Visible = show;
        if (show)
        {
            _undoLabel.Text = $"Reverting in {LayoutSafety.RemainingSeconds}s unless you keep this layout.";
        }
    }

    private void ConfigForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        // Edits are already saved; just make sure nothing is still sitting in the
        // debounce window. No "save your changes?" prompt — with autosave there is
        // no unsaved state to ask about.
        FlushAutoSave();
    }
}
