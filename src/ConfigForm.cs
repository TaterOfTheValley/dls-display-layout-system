namespace MonitorLayoutSwitcher;

public class ConfigForm : Form
{
    private sealed class ProfileCardView
    {
        public DisplayProfile Profile { get; set; } = null!;
        public Panel CardPanel { get; set; } = null!;
        public Label TitleLabel { get; set; } = null!;
        public Label SubLabel { get; set; } = null!;
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
    private Label _hotkeyHint = null!;
    private SplitContainer _split = null!;
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
        Text = "Monitor Layout Switcher";
        Padding = new Padding(0);

        var header = BuildHeader();
        var footer = BuildFooter();
        var undo = BuildUndoBar();
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterWidth = Math.Max(4, S(6)),
            BackColor = UiTheme.Bg,
            FixedPanel = FixedPanel.Panel1
        };
        _split.Panel1.BackColor = UiTheme.Panel;
        _split.Panel2.BackColor = UiTheme.Panel;
        _split.Panel1.Padding = new Padding(S(16), S(14), S(12), S(14));
        _split.Panel2.Padding = new Padding(S(12), S(14), S(16), S(14));
        _split.Panel1.Controls.Add(BuildSidebar());
        _split.Panel2.Controls.Add(BuildEditor());
        Load += (_, _) => ApplySplitterLayout(_split);

        Controls.Add(_split);
        Controls.Add(undo);
        Controls.Add(footer);
        Controls.Add(header);

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
        ApplySplitterLayout(_split);
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

    private void ApplySplitterLayout(SplitContainer split)
    {
        int width = split.Width;
        if (width < S(200)) return;

        int splitter = Math.Max(1, split.SplitterWidth);
        int panel1Min = Math.Min(S(240), Math.Max(S(160), width / 5));
        int panel2Min = Math.Min(S(480), Math.Max(S(280), width / 2));
        if (panel1Min + panel2Min + splitter > width)
        {
            panel1Min = Math.Max(S(120), width / 4);
            panel2Min = Math.Max(S(160), width - panel1Min - splitter);
        }

        int maxDistance = width - panel2Min - splitter;
        if (maxDistance < panel1Min) return;
        split.SplitterDistance = Math.Clamp(S(290), panel1Min, maxDistance);
        split.Panel1MinSize = panel1Min;
        split.Panel2MinSize = panel2Min;
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = S(52),
            BackColor = UiTheme.Panel,
            Padding = new Padding(S(24), S(8), S(20), S(8))
        };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Line);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var title = new Label
        {
            Text = "MONITOR LAYOUT SWITCHER",
            Font = new Font("Segoe UI", 12f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
            ForeColor = UiTheme.Gold,
            AutoSize = false,
            Location = new Point(S(24), S(14)),
            Size = new Size(S(640), S(26))
        };

        _identifyAllBtn = UiTheme.MakeButton("Identify", false, DpiScale);
        _identifyAllBtn.Size = new Size(S(110), S(34));
        _identifyAllBtn.Click += (_, _) => IdentifyOverlays.ShowAll();
        _tips.SetToolTip(_identifyAllBtn, "Flash numbers on the physical monitors");

        _refreshBtn = UiTheme.MakeButton("Refresh", false, DpiScale);
        _refreshBtn.Size = new Size(S(100), S(34));
        _refreshBtn.Click += (_, _) => RefreshDisplays();
        _tips.SetToolTip(_refreshBtn, "Re-scan connected monitors");

        header.Resize += (_, _) =>
        {
            _identifyAllBtn.Location = new Point(header.Width - _identifyAllBtn.Width - S(20), S(9));
            _refreshBtn.Location = new Point(_identifyAllBtn.Left - S(8) - _refreshBtn.Width, S(9));
        };

        header.Controls.Add(title);
        header.Controls.Add(_identifyAllBtn);
        header.Controls.Add(_refreshBtn);
        return header;
    }

    private Control BuildSidebar()
    {
        var root = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Panel };

        var heading = UiTheme.MakeEyebrow("LAYOUTS", DpiScale);
        heading.Dock = DockStyle.Top;
        heading.Height = S(22);

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = S(86), BackColor = UiTheme.Panel };
        _addProfileBtn = UiTheme.MakeButton("+ Add", false, DpiScale);
        _duplicateBtn = UiTheme.MakeButton("Duplicate", false, DpiScale);
        _deleteProfileBtn = UiTheme.MakeButton("Delete", false, DpiScale);
        // Capturing the live layout is a profile-level action like Add and Duplicate,
        // so it belongs with them. Sitting in the action bar it read as a sibling of
        // Apply, which made a destructive overwrite look like a commit button.
        _captureCurrentLayoutBtn = UiTheme.MakeButton("Capture", false, DpiScale);
        _captureCurrentLayoutBtn.Click += CaptureCurrentLayoutBtn_Click;
        _tips.SetToolTip(_captureCurrentLayoutBtn, "Replace this layout with the monitors as they are arranged right now");
        buttons.Resize += (_, _) =>
        {
            int gap = S(8);
            int w = (buttons.Width - gap) / 2;
            _addProfileBtn.SetBounds(0, S(4), w, S(34));
            _duplicateBtn.SetBounds(w + gap, S(4), buttons.Width - w - gap, S(34));
            _captureCurrentLayoutBtn.SetBounds(0, S(44), w, S(34));
            _deleteProfileBtn.SetBounds(w + gap, S(44), buttons.Width - w - gap, S(34));
        };
        _addProfileBtn.Click += AddProfileBtn_Click;
        _duplicateBtn.Click += DuplicateProfileBtn_Click;
        _deleteProfileBtn.Click += DeleteProfileBtn_Click;
        _tips.SetToolTip(_duplicateBtn, "Copy the selected layout");
        buttons.Controls.Add(_addProfileBtn);
        buttons.Controls.Add(_duplicateBtn);
        buttons.Controls.Add(_deleteProfileBtn);
        buttons.Controls.Add(_captureCurrentLayoutBtn);

        _profileCardsPanel = new BufferedFlowPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Bg,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(S(8), S(8), S(4), S(8)),
            Margin = new Padding(0, S(8), 0, S(8))
        };
        _profileCardsPanel.Resize += (_, _) => SizeProfileCards();

        var spacer = new Panel { Dock = DockStyle.Top, Height = S(8), BackColor = UiTheme.Panel };

        root.Controls.Add(_profileCardsPanel);
        root.Controls.Add(buttons);
        root.Controls.Add(spacer);
        root.Controls.Add(heading);
        return root;
    }

    /// <summary>
    /// First-run state. With no saved layouts the editor is a set of disabled
    /// controls around an empty canvas, which explains nothing. This draws the
    /// monitors as they are right now and offers the single action that matters,
    /// so the app's premise is demonstrated rather than described.
    /// </summary>
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

        var meta = BuildMetaPanel();
        var toolbar = BuildCanvasToolbar();
        var inspector = BuildInspector();

        _hotkeyHint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = S(22),
            ForeColor = UiTheme.Muted,
            Font = new Font("Segoe UI", 8.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            Text = "Click a monitor to select it  ·  Double-click for main  ·  Arrow keys nudge  ·  Scroll to zoom",
            TextAlign = ContentAlignment.MiddleLeft
        };

        root.Controls.Add(BuildEmptyState());
        root.Controls.Add(_canvas);
        root.Controls.Add(_hotkeyHint);
        root.Controls.Add(inspector);
        root.Controls.Add(toolbar);
        root.Controls.Add(meta);

        // Everything that only makes sense once a layout exists.
        _editorChrome = new Control[] { _canvas, _hotkeyHint, inspector, toolbar, meta };
        return root;
    }

    private Control BuildCanvasToolbar()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = S(40),
            BackColor = UiTheme.Panel
        };
        _fitBtn = UiTheme.MakeButton("Fit view", false, DpiScale);
        _fitBtn.Size = new Size(S(90), S(30));
        _fitBtn.Location = new Point(0, S(4));
        _fitBtn.Click += (_, _) => _canvas.FitView();
        _tips.SetToolTip(_fitBtn, "Fit all monitors in the canvas");

        _snapToggle = new CheckBox
        {
            Text = "Snap edges",
            Checked = true,
            ForeColor = UiTheme.Text,
            Font = new Font("Segoe UI", 9f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            AutoSize = true,
            Location = new Point(S(100), S(8)),
            Cursor = Cursors.Hand
        };
        _snapToggle.CheckedChanged += (_, _) => _canvas.SnapEnabled = _snapToggle.Checked;
        _tips.SetToolTip(_snapToggle, "Magnetically align monitor edges while dragging");

        bar.Controls.Add(_fitBtn);
        bar.Controls.Add(_snapToggle);
        return bar;
    }

    private Control BuildMetaPanel()
    {
        var meta = new Panel
        {
            Dock = DockStyle.Top,
            Height = S(62),
            BackColor = UiTheme.Panel
        };

        var nameLabel = UiTheme.MakeEyebrow("LAYOUT NAME", DpiScale);
        nameLabel.Location = new Point(0, 0);
        nameLabel.Size = new Size(S(280), S(18));

        _nameTextBox = new TextBox
        {
            BackColor = UiTheme.Input,
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel)
        };
        _nameTextBox.TextChanged += (_, _) =>
        {
            if (_selectedProfile == null) return;
            _selectedProfile.Name = _nameTextBox.Text;
            UpdateSelectedCardTitle(_nameTextBox.Text);
            MarkDirty();
        };

        var hotkeyLabel = UiTheme.MakeEyebrow("GLOBAL SHORTCUT", DpiScale);
        _hotkeyTextBox = new TextBox
        {
            BackColor = UiTheme.Input,
            ForeColor = UiTheme.Gold,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 10.5f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
            PlaceholderText = "Click and press a shortcut"
        };
        _hotkeyTextBox.GotFocus += (_, _) => StartHotkeyCapture();
        _hotkeyTextBox.KeyDown += HotkeyTextBox_KeyDown;

        _captureHotkeyBtn = UiTheme.MakeButton("Capture", false, DpiScale);
        _captureHotkeyBtn.Click += (_, _) =>
        {
            _hotkeyTextBox.Focus();
            StartHotkeyCapture();
        };

        meta.Resize += (_, _) =>
        {
            int gap = S(16);
            int half = Math.Max(S(200), (meta.Width - gap) / 2);
            nameLabel.SetBounds(0, 0, half - S(8), S(18));
            _nameTextBox.SetBounds(0, S(22), half - S(8), S(32));
            hotkeyLabel.SetBounds(half + S(8), 0, meta.Width - half - S(8), S(18));
            int btnW = S(100);
            _captureHotkeyBtn.SetBounds(meta.Width - btnW, S(22), btnW, S(32));
            _hotkeyTextBox.SetBounds(half + S(8), S(22), meta.Width - half - S(16) - btnW, S(32));
        };

        meta.Controls.Add(nameLabel);
        meta.Controls.Add(_hotkeyTextBox);
        meta.Controls.Add(_captureHotkeyBtn);
        meta.Controls.Add(hotkeyLabel);
        meta.Controls.Add(_nameTextBox);
        return meta;
    }

    private Control BuildInspector()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = S(122),
            BackColor = UiTheme.Card
        };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Line);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };

        _inspectorTitle = new Label
        {
            Font = new Font("Segoe UI", 10.5f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
            ForeColor = UiTheme.Text,
            AutoSize = false,
            Location = new Point(S(14), S(10)),
            Size = new Size(S(400), S(24))
        };
        _inspectorSub = new Label
        {
            Font = new Font("Segoe UI", 8.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            ForeColor = UiTheme.Muted,
            AutoSize = false,
            Location = new Point(S(14), S(38)),
            Size = new Size(S(400), S(20))
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
        resolutionLabel.SetBounds(S(14), S(70), S(120), S(16));

        _resolutionBox = MakeCombo();
        _resolutionBox.SetBounds(S(14), S(88), S(190), S(24));
        _resolutionBox.SelectedIndexChanged += (_, _) => ResolutionChanged();

        var refreshLabel = UiTheme.MakeEyebrow("REFRESH", DpiScale);
        refreshLabel.SetBounds(S(218), S(70), S(120), S(16));

        _refreshBox = MakeCombo();
        _refreshBox.SetBounds(S(218), S(88), S(120), S(24));
        _refreshBox.SelectedIndexChanged += (_, _) => RefreshRateChanged();

        var scaleLabel = UiTheme.MakeEyebrow("SCALE", DpiScale);
        scaleLabel.SetBounds(S(352), S(70), S(120), S(16));

        _scaleBox = MakeCombo();
        _scaleBox.SetBounds(S(352), S(88), S(150), S(24));
        _scaleBox.SelectedIndexChanged += (_, _) => ScaleChanged();

        panel.Resize += (_, _) =>
        {
            int right = panel.Width - S(14);
            _identifyOneBtn.Location = new Point(right - _identifyOneBtn.Width, S(22));
            _primaryBtn.Location = new Point(_identifyOneBtn.Left - S(8) - _primaryBtn.Width, S(22));
            _includeToggle.Location = new Point(_primaryBtn.Left - S(12) - _includeToggle.Width, S(28));
            int textW = Math.Max(S(120), _includeToggle.Left - S(28));
            _inspectorTitle.Width = textW;
            _inspectorSub.Width = textW;
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
            _feedbackLabel.SetBounds(S(24), 0, Math.Max(S(80), _cancelBtn.Left - S(36)), footer.Height);
        };

        footer.Controls.Add(_feedbackLabel);
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

    private void RebuildProfileCards()
    {
        _profileCardsPanel.SuspendLayout();
        _profileCardsPanel.Controls.Clear();
        _cardViews.Clear();

        foreach (var profile in _profiles)
        {
            bool selected = _selectedProfile?.Id == profile.Id;
            var card = new BufferedPanel
            {
                Height = S(74),
                BackColor = selected ? UiTheme.CardActive : UiTheme.Card,
                Margin = new Padding(0, 0, 0, S(8)),
                Cursor = Cursors.Hand,
                Tag = profile
            };

            var p = profile;
            card.Paint += (_, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                bool isCurSelected = (_selectedProfile?.Id == p.Id);
                using var pen = new Pen(isCurSelected ? UiTheme.Gold : UiTheme.Line, isCurSelected ? 2 : 1);
                g.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                DrawPips(g, p, card.Width);
            };

            var title = new Label
            {
                Text = p.Name,
                Font = new Font("Segoe UI", 10f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel),
                ForeColor = selected ? UiTheme.Gold : UiTheme.Text,
                Location = new Point(S(12), S(10)),
                Size = new Size(S(200), S(22)),
                AutoSize = false
            };
            var sub = new Label
            {
                Text = string.IsNullOrWhiteSpace(p.Hotkey) ? "No shortcut" : p.Hotkey,
                Font = new Font("Segoe UI", 8.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
                ForeColor = UiTheme.Muted,
                Location = new Point(S(12), S(34)),
                Size = new Size(S(200), S(18)),
                AutoSize = false
            };

            void SelectCard(object? sender, EventArgs e)
            {
                if (_selectedProfile?.Id == p.Id) return;
                _selectedProfile = p;
                UpdateCardSelectionStyles();
                LoadSelectedProfile();
            }

            card.Click += SelectCard;
            title.Click += SelectCard;
            sub.Click += SelectCard;
            card.Controls.Add(title);
            card.Controls.Add(sub);

            _cardViews.Add(new ProfileCardView
            {
                Profile = p,
                CardPanel = card,
                TitleLabel = title,
                SubLabel = sub
            });

            _profileCardsPanel.Controls.Add(card);
        }

        SizeProfileCards();
        _profileCardsPanel.ResumeLayout();
        RefreshActiveBadges();
        UpdateEmptyState();
    }

    private void UpdateCardSelectionStyles()
    {
        foreach (var view in _cardViews)
        {
            bool isSelected = (_selectedProfile?.Id == view.Profile.Id);
            view.CardPanel.BackColor = isSelected ? UiTheme.CardActive : UiTheme.Card;
            view.TitleLabel.ForeColor = isSelected ? UiTheme.Gold : UiTheme.Text;
            view.CardPanel.Invalidate();
        }
    }

    private void UpdateSelectedCardTitle(string title)
    {
        var view = _cardViews.FirstOrDefault(v => v.Profile.Id == _selectedProfile?.Id);
        if (view != null)
        {
            view.TitleLabel.Text = title;
            view.CardPanel.Invalidate();
        }
    }

    private void UpdateSelectedCardHotkey(string hotkey)
    {
        var view = _cardViews.FirstOrDefault(v => v.Profile.Id == _selectedProfile?.Id);
        if (view != null)
        {
            view.SubLabel.Text = string.IsNullOrWhiteSpace(hotkey) ? "No shortcut" : hotkey;
            view.CardPanel.Invalidate();
        }
    }

    private void InvalidateProfileCards()
    {
        foreach (var view in _cardViews)
        {
            view.CardPanel.Invalidate();
        }
    }

    private void SizeProfileCards()
    {
        int w = Math.Max(S(80), _profileCardsPanel.ClientSize.Width - S(16));
        foreach (var view in _cardViews)
        {
            view.CardPanel.Width = w;
            view.TitleLabel.Width = Math.Max(S(40), w - S(24));
            view.SubLabel.Width = Math.Max(S(40), w - S(24));
        }
    }

    private void DrawPips(Graphics g, DisplayProfile profile, int cardWidth)
    {
        var displays = profile.Displays.Where(d => d.Width > 0 && d.Height > 0).OrderBy(d => d.X).ToList();
        if (displays.Count == 0) return;
        int pipH = S(10);
        int gap = S(3);
        int maxW = S(54);
        int totalW = Math.Min(maxW, displays.Count * S(14));
        int x = cardWidth - totalW - S(12);
        int y = S(52);
        foreach (var d in displays)
        {
            int pw = Math.Max(S(8), totalW / Math.Max(1, displays.Count) - gap);
            using var brush = new SolidBrush(d.Enabled ? UiTheme.Gold : UiTheme.Line);
            g.FillRectangle(brush, x, y, pw, pipH);
            if (d.IsPrimary && d.Enabled)
            {
                using var pen = new Pen(UiTheme.GoldHover);
                g.DrawRectangle(pen, x, y, pw, pipH);
            }
            x += pw + gap;
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
        _captureHotkeyBtn.Text = "Capture";
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
        var live = DisplayEngine.FindMatchingProfile(_profiles);
        string active = live != null ? $"  —  {live.Name} is active" : "";
        Text = "Monitor Layout Switcher" + star + active;
    }

    private void RefreshActiveBadges()
    {
        var live = DisplayEngine.FindMatchingProfile(_profiles);
        foreach (var view in _cardViews)
        {
            string hotkey = string.IsNullOrWhiteSpace(view.Profile.Hotkey) ? "No shortcut" : view.Profile.Hotkey;
            string text = live?.Id == view.Profile.Id ? $"{hotkey}  ·  LIVE" : hotkey;

            // Assigning the same text still raises TextChanged and repaints; skipping
            // the no-op is what stops the card list flickering on every tick.
            if (view.SubLabel.Text == text) continue;
            view.SubLabel.Text = text;
            view.CardPanel.Invalidate();
        }
        UpdateWindowTitle();
    }

    // Keeps the canvas honest when the layout changes outside this window — a
    // monitor plugged in, or settings changed in Windows itself.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        DisplayModes.Invalidate();
        CcdEngine.InvalidateCaches();
        BeginInvoke(() => _canvas.RefreshHardware());
    }

    private void UpdateUndoBar()
    {
        bool show = LayoutSafety.CanUndo;
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
