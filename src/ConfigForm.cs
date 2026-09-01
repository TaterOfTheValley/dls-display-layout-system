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
    private Button _testApplyBtn = null!;
    private Button _saveBtn = null!;
    private Button _cancelBtn = null!;
    private Button _addProfileBtn = null!;
    private Button _deleteProfileBtn = null!;
    private Button _identifyAllBtn = null!;
    private Button _duplicateBtn = null!;
    private Button _refreshBtn = null!;
    private Button _fitBtn = null!;
    private Button _saveOnlyBtn = null!;
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
    private bool _dirty;
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
            Height = S(72),
            BackColor = UiTheme.Panel,
            Padding = new Padding(S(24), S(12), S(20), S(12))
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
            Location = new Point(S(24), S(10)),
            Size = new Size(S(640), S(26))
        };
        var subtitle = new Label
        {
            Text = "Drag displays to arrange them · Snap edges together · Drop unused monitors off the canvas",
            Font = new Font("Segoe UI", 8.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            ForeColor = UiTheme.Muted,
            AutoSize = false,
            Location = new Point(S(24), S(38)),
            Size = new Size(S(760), S(22))
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
            _identifyAllBtn.Location = new Point(header.Width - _identifyAllBtn.Width - S(20), S(18));
            _refreshBtn.Location = new Point(_identifyAllBtn.Left - S(8) - _refreshBtn.Width, S(18));
        };

        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(_identifyAllBtn);
        header.Controls.Add(_refreshBtn);
        return header;
    }

    private Control BuildSidebar()
    {
        var root = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Panel };

        var heading = UiTheme.MakeEyebrow("PROFILES", DpiScale);
        heading.Dock = DockStyle.Top;
        heading.Height = S(22);

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = S(86), BackColor = UiTheme.Panel };
        _addProfileBtn = UiTheme.MakeButton("+ Add", false, DpiScale);
        _duplicateBtn = UiTheme.MakeButton("Duplicate", false, DpiScale);
        _deleteProfileBtn = UiTheme.MakeButton("Delete", false, DpiScale);
        buttons.Resize += (_, _) =>
        {
            int gap = S(8);
            int w = (buttons.Width - gap) / 2;
            _addProfileBtn.SetBounds(0, S(4), w, S(34));
            _duplicateBtn.SetBounds(w + gap, S(4), buttons.Width - w - gap, S(34));
            _deleteProfileBtn.SetBounds(0, S(44), buttons.Width, S(34));
        };
        _addProfileBtn.Click += AddProfileBtn_Click;
        _duplicateBtn.Click += DuplicateProfileBtn_Click;
        _deleteProfileBtn.Click += DeleteProfileBtn_Click;
        _tips.SetToolTip(_duplicateBtn, "Copy the selected profile");
        buttons.Controls.Add(_addProfileBtn);
        buttons.Controls.Add(_duplicateBtn);
        buttons.Controls.Add(_deleteProfileBtn);

        _profileCardsPanel = new FlowLayoutPanel
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
        var actions = BuildActions();

        _hotkeyHint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = S(22),
            ForeColor = UiTheme.Muted,
            Font = new Font("Segoe UI", 8.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel),
            Text = "Click a monitor to select it  ·  Double-click for main  ·  Arrow keys nudge  ·  Scroll to zoom",
            TextAlign = ContentAlignment.MiddleLeft
        };

        root.Controls.Add(_canvas);
        root.Controls.Add(_hotkeyHint);
        root.Controls.Add(inspector);
        root.Controls.Add(actions);
        root.Controls.Add(toolbar);
        root.Controls.Add(meta);
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
            Height = S(92),
            BackColor = UiTheme.Panel
        };

        var nameLabel = UiTheme.MakeEyebrow("PROFILE NAME", DpiScale);
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
            Height = S(80),
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
            Text = "Include in profile",
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
        return panel;
    }

    private Control BuildActions()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = S(52),
            BackColor = UiTheme.Panel
        };
        _captureCurrentLayoutBtn = UiTheme.MakeButton("Capture current layout", false, DpiScale);
        _testApplyBtn = UiTheme.MakeButton("Apply this profile", false, DpiScale);
        _captureCurrentLayoutBtn.Click += CaptureCurrentLayoutBtn_Click;
        _testApplyBtn.Click += (_, _) => ApplySelected(saveFirst: false);
        _tips.SetToolTip(_captureCurrentLayoutBtn, "Overwrite this profile with the live Windows layout");
        _tips.SetToolTip(_testApplyBtn, "Change Windows display settings to this profile");
        panel.Resize += (_, _) =>
        {
            int gap = S(8);
            int w = (panel.Width - gap) / 2;
            _captureCurrentLayoutBtn.SetBounds(0, S(8), w, S(36));
            _testApplyBtn.SetBounds(w + gap, S(8), panel.Width - w - gap, S(36));
        };
        panel.Controls.Add(_captureCurrentLayoutBtn);
        panel.Controls.Add(_testApplyBtn);
        return panel;
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

        _saveOnlyBtn = UiTheme.MakeButton("Save", false, DpiScale);
        _saveOnlyBtn.Size = new Size(S(90), S(40));
        _saveOnlyBtn.Click += (_, _) => SaveProfilesOnly();
        _tips.SetToolTip(_saveOnlyBtn, "Save profiles to disk without changing Windows");

        _applyBtn = UiTheme.MakeButton("Apply", false, DpiScale);
        _applyBtn.Size = new Size(S(90), S(40));
        _applyBtn.Click += (_, _) => ApplySelected(saveFirst: false);
        _tips.SetToolTip(_applyBtn, "Apply this layout to Windows. Undo is available for 20 seconds.");

        _saveBtn = UiTheme.MakeButton("Save & Apply", true, DpiScale);
        _saveBtn.Size = new Size(S(140), S(40));
        _saveBtn.Click += (_, _) => ApplySelected(saveFirst: true);

        footer.Resize += (_, _) =>
        {
            _saveBtn.Location = new Point(footer.Width - _saveBtn.Width - S(20), (footer.Height - _saveBtn.Height) / 2);
            _applyBtn.Location = new Point(_saveBtn.Left - S(8) - _applyBtn.Width, (footer.Height - _applyBtn.Height) / 2);
            _saveOnlyBtn.Location = new Point(_applyBtn.Left - S(8) - _saveOnlyBtn.Width, (footer.Height - _saveOnlyBtn.Height) / 2);
            _cancelBtn.Location = new Point(_saveOnlyBtn.Left - S(10) - _cancelBtn.Width, (footer.Height - _cancelBtn.Height) / 2);
            _feedbackLabel.SetBounds(S(24), 0, Math.Max(S(80), _cancelBtn.Left - S(36)), footer.Height);
        };

        footer.Controls.Add(_feedbackLabel);
        footer.Controls.Add(_cancelBtn);
        footer.Controls.Add(_saveOnlyBtn);
        footer.Controls.Add(_applyBtn);
        footer.Controls.Add(_saveBtn);
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
            var card = new Panel
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
        }
        else
        {
            _inspectorTitle.Text = string.IsNullOrWhiteSpace(sel.MonitorId) ? sel.DeviceName : sel.MonitorId;
            string hz = sel.RefreshRate > 0 ? $" @ {sel.RefreshRate}Hz" : "";
            var liveDisplays = DisplayEngine.GetCurrentDisplays();
            bool connected = liveDisplays.Any(d =>
                DisplayEngine.SameHardwareIdentity(d.MonitorDevicePath, sel.MonitorDevicePath));
            string link = connected ? "Connected" : "Not connected";
            _inspectorSub.Text = $"{sel.DeviceName}  ·  {sel.Width} × {sel.Height}{hz}  ·  {link}" + (sel.IsPrimary ? "  ·  Main" : "");
            _includeToggle.Enabled = true;
            _includeToggle.Checked = sel.Enabled;
            _primaryBtn.Enabled = sel.Enabled && !sel.IsPrimary;
            _primaryBtn.Text = sel.IsPrimary ? "Main display" : "Set as main";
            _identifyOneBtn.Enabled = true;
        }
        _syncingInspector = false;
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
        if (_selectedProfile != null && _profiles.Count > 1)
        {
            string name = _selectedProfile.Name;
            _profiles.Remove(_selectedProfile);
            _selectedProfile = _profiles[0];
            RebuildProfileCards();
            LoadSelectedProfile();
            MarkDirty();
            _feedbackLabel.ForeColor = UiTheme.Gold;
            _feedbackLabel.Text = $"Deleted '{name}'.";
        }
        else
        {
            _feedbackLabel.ForeColor = UiTheme.Danger;
            _feedbackLabel.Text = "Cannot delete the only profile.";
        }
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

    private void SaveProfilesOnly()
    {
        if (!ProfileManager.TrySaveProfiles(_profiles, out string error))
        {
            _feedbackLabel.ForeColor = UiTheme.Danger;
            _feedbackLabel.Text = error;
            return;
        }

        _onSaveCallback?.Invoke();
        MarkClean();
        _feedbackLabel.ForeColor = UiTheme.Ok;
        _feedbackLabel.Text = "Saved profiles.";
        RefreshActiveBadges();
    }

    private void ApplySelected(bool saveFirst)
    {
        if (_selectedProfile == null) return;

        if (DisplayEngine.MatchesCurrent(_selectedProfile))
        {
            if (saveFirst)
            {
                SaveProfilesOnly();
            }
            else
            {
                _feedbackLabel.ForeColor = UiTheme.Ok;
                _feedbackLabel.Text = $"'{_selectedProfile.Name}' is already active.";
            }
            return;
        }

        var turningOff = DisplayEngine.MonitorsThatWouldDisable(_selectedProfile);
        if (turningOff.Count > 0)
        {
            var answer = MessageBox.Show(
                this,
                "This layout will turn off:\n\n• " + string.Join("\n• ", turningOff) +
                "\n\nUndo is available for 20 seconds after apply.",
                "Apply layout?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }

        if (saveFirst)
        {
            if (!ProfileManager.TrySaveProfiles(_profiles, out string saveError))
            {
                _feedbackLabel.ForeColor = UiTheme.Danger;
                _feedbackLabel.Text = saveError;
                return;
            }

            _onSaveCallback?.Invoke();
            MarkClean();
        }

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
        _canvas.RefreshHardware();
        UpdateInspector();
        RefreshActiveBadges();
        _feedbackLabel.ForeColor = UiTheme.Gold;
        _feedbackLabel.Text = "Rescanned connected monitors.";
    }

    private void MarkDirty()
    {
        if (_loading || _dirty) return;
        _dirty = true;
        UpdateWindowTitle();
    }

    private void MarkClean()
    {
        _dirty = false;
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        string star = _dirty ? " • Unsaved" : "";
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
            view.SubLabel.Text = live?.Id == view.Profile.Id ? $"{hotkey}  ·  LIVE" : hotkey;
            view.CardPanel.Invalidate();
        }
        UpdateWindowTitle();
    }

    // Keeps the canvas honest when the layout changes outside this window — a
    // monitor plugged in, or settings changed in Windows itself.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
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
        if (!_dirty) return;
        var answer = MessageBox.Show(
            this,
            "Save unsaved profile changes?",
            "Unsaved changes",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel)
        {
            e.Cancel = true;
        }
        else if (answer == DialogResult.Yes)
        {
            if (!ProfileManager.TrySaveProfiles(_profiles, out string error))
            {
                MessageBox.Show(this, error, "Could not save profiles", MessageBoxButtons.OK, MessageBoxIcon.Error);
                e.Cancel = true;
                return;
            }

            _onSaveCallback?.Invoke();
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int wmDisplayChange = 0x007E;
        if (m.Msg == wmDisplayChange)
        {
            BeginInvoke(() =>
            {
                _canvas.RefreshHardware();
                RefreshActiveBadges();
                UpdateInspector();
            });
        }
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_nameTextBox.Focused || _hotkeyTextBox.Focused) return base.ProcessCmdKey(ref msg, keyData);

        if (keyData == (Keys.Control | Keys.S))
        {
            SaveProfilesOnly();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Z) && LayoutSafety.CanUndo)
        {
            UndoLayout();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
