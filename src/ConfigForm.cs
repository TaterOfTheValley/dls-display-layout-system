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

    /// <summary>The fixed-height bands around the canvas. Held so <see
    /// cref="ApplyDensity"/> can hand their space back when the window is short.</summary>
    private Control? _rail;
    private Control? _addTile;
    private Control? _inspector;
    private Control? _footer;
    private Control[]? _editorChrome;
    private List<DisplayInfo> _liveForEmptyState = new();
    private bool _dirty;
    private bool _dirtyNotified;
    private System.Windows.Forms.Timer? _autoSaveTimer;
    private bool _loading;

    public bool HasUnsavedChanges => _dirty;

    /// <summary>
    /// The window's design size at 100% scale, and the smallest it is allowed to get.
    ///
    /// The minimum is deliberately far below the comfortable size, because it is not a
    /// statement about how big the editor wants to be — it is the point past which the
    /// user can no longer make the window smaller. Set too high it is invisible on a
    /// large screen and intolerable on a small one: at 1920×1080 with Windows at 150%,
    /// a 680-tall minimum is 1020 real pixels out of 1032 usable, so the window could
    /// never be anything but full height. Reaching this minimum instead needs the rows
    /// below to reflow, which is what <see cref="ApplyDensity"/> and the Resize
    /// handlers in each Build* method do.
    /// </summary>
    private const int DesignMinW = 760, DesignMinH = 480, DesignW = 1160, DesignH = 750;

    /// <summary>Below this design height the fixed bands give up space to the canvas.</summary>
    private const int CompactBelowH = 620;

    /// <summary>Below this design width the inspector's actions move up beside the
    /// monitor name, so the mode controls keep a usable width.</summary>
    private const int NarrowBelowW = 960;

    /// <summary>
    /// The single number every size and font in this form is derived from.
    ///
    /// Normally it is just the monitor's scale factor. It drops below that when the
    /// screen is too small to hold the design minimum — a 1024×768 desktop, or the
    /// 800×600 Windows falls back to while a graphics driver is installing. Shrinking
    /// the whole interface keeps it usable there; the alternative is a window whose
    /// Apply button is off the bottom of the screen, with no way to reach it.
    /// </summary>
    private float _uiScale = 1f;

    /// <summary>Guards against a rebuild being re-entered from something the rebuild
    /// itself triggers.</summary>
    private bool _rescaling;

    private float DpiScale => _uiScale;

    /// <summary>Scales a design pixel length. For geometry.</summary>
    private int S(int val) => (int)Math.Round(val * DpiScale);

    /// <summary>
    /// Scales a design font size, for a font that will be assigned to a control.
    /// Not interchangeable with <see cref="S"/> — see <see cref="UiScaling"/> for why
    /// the two do not come out the same. Fonts drawn in a Paint handler use DpiScale
    /// directly; only fonts that become a <see cref="Control.Font"/> come through here.
    /// </summary>
    private float F(float designPx) => UiScaling.ControlFontPx(designPx, _uiScale, DeviceDpi);

    /// <summary>
    /// One control's width in a row that has to fit: its preferred size while there is
    /// room, never below <paramref name="floor"/>, and never more than
    /// <paramref name="share"/> of the row in between. The share is what makes the
    /// parts shrink together instead of the last one in the row absorbing all of it.
    /// </summary>
    private static int Fit(int room, int preferred, int floor, float share) =>
        Math.Max(floor, Math.Min(preferred, (int)Math.Round(room * share)));

    /// <summary>The client area in design pixels — the units every layout decision in
    /// this form is made in, so those decisions do not change with the scale factor.</summary>
    private int DesignClientW => _uiScale > 0.01f ? (int)Math.Round(ClientSize.Width / _uiScale) : ClientSize.Width;
    private int DesignClientH => _uiScale > 0.01f ? (int)Math.Round(ClientSize.Height / _uiScale) : ClientSize.Height;

    /// <summary>Short enough that the fixed bands have to give space back to the canvas.</summary>
    private bool Compact => DesignClientH < CompactBelowH;

    /// <summary>Narrow enough that a row of controls has to be split across two.</summary>
    private bool Narrow => DesignClientW < NarrowBelowW;

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
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UiTheme.Bg;
        ForeColor = UiTheme.Text;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = AppInfo.Name;
        Icon = AppIcon.Shared;
        Padding = new Padding(0);

        _uiScale = ComputeUiScale(AvailableWorkArea());

        // Whatever the desktop is actually showing right now should be what's
        // selected when the window opens, not just the first saved layout.
        _liveDisplays = DisplayEngine.GetCurrentDisplays();
        if (_profiles.Count > 0) _selectedProfile = LiveProfile() ?? _profiles[0];

        BuildUi();
        ApplySizeConstraints(resize: true);
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

    /// <summary>
    /// Builds — or rebuilds — every control in the window at the current
    /// <see cref="_uiScale"/>.
    ///
    /// Rebuilding wholesale is what makes a scale change correct rather than
    /// approximately correct. Each Build* method below derives its fonts, paddings and
    /// sizes from <see cref="S"/>, so re-running them at a new scale produces exactly
    /// the window the user would have got by opening the app at that scale.
    ///
    /// The alternative — walking the live control tree multiplying everything by the
    /// ratio — is what used to happen here, and it could not be made to work. A
    /// control with no font of its own returns its parent's, so assigning a scaled
    /// copy back scaled the parent's font a second time, and a third for the
    /// generation below that: fonts landed at ratio-cubed while the geometry around
    /// them moved by ratio. That is why the window came back from a scale change with
    /// its buttons' labels clipped and its rows overlapping.
    ///
    /// Docking runs from the highest control index down, so the add order here is the
    /// reverse of the visual order: rail and meta end up at the top, undo and footer
    /// at the bottom, and the editor takes everything left over.
    /// </summary>
    private void BuildUi()
    {
        SuspendLayout();

        var stale = Controls.Cast<Control>().ToArray();
        Controls.Clear();
        _tips.RemoveAll();          // its entries point at controls about to be disposed
        _cardViews.Clear();
        _addTile = null;            // ApplyDensity runs before the rail is rebuilt
        foreach (var control in stale) control.Dispose();

        Font = new Font("Segoe UI", F(9.5f), FontStyle.Regular, GraphicsUnit.Pixel);

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

        ResumeLayout(performLayout: true);
        ApplyDensity();

        RebuildProfileCards();
        LoadSelectedProfile();
        UpdateEmptyState();
    }

    /// <summary>
    /// Hands the canvas back as much of the window as the rest of the editor can spare.
    ///
    /// Five bands of fixed height sit around the canvas — layouts, name and actions,
    /// the keyboard hint, the inspector, the footer. Together they are 400 design
    /// pixels, which is fine in a 750-tall window and absurd in a 480-tall one, where
    /// it would leave the canvas 80 pixels to draw monitors in. Each one gives up what
    /// it can when the window is short: the rail shows smaller cards, the hint line —
    /// the only band that is purely informational — goes away entirely, and the
    /// inspector and footer tighten up.
    ///
    /// Done here, from the form's own resize, rather than inside each band's Resize
    /// handler: a docked band that changes its own height re-enters layout, and the
    /// order the bands settle in then decides how much room the canvas ends up with.
    /// </summary>
    private void ApplyDensity()
    {
        if (_rail == null || _metaBar == null || _inspector == null || _footer == null) return;

        bool compact = Compact;

        _rail.Height = RailCardHeight + S(compact ? 20 : 30);
        _metaBar.Height = S(compact ? 68 : 76);
        _inspector.Height = S(compact ? 104 : 118);
        _footer.Height = S(compact ? 56 : 70);

        // The cards are resized in place rather than rebuilt: PaintLayoutCard reads the
        // card's own height to decide how many lines it has room for, and rebuilding
        // would drag a full display enumeration along behind every drag of the window
        // edge that crossed the threshold.
        var cardSize = new Size(RailCardWidth, RailCardHeight);
        foreach (var view in _cardViews)
        {
            if (view.CardPanel.Size == cardSize) continue;
            view.CardPanel.Size = cardSize;
            view.CardPanel.Invalidate();
        }
        if (_addTile != null) _addTile.Size = new Size(S(compact ? 108 : 132), RailCardHeight);

        UpdateHintVisibility();
    }

    /// <summary>
    /// The keyboard hint is the one band that only says things you would also find out
    /// by trying them, so it is the first thing to go when the window is short and the
    /// last to come back. It is also part of the editor chrome, which disappears
    /// wholesale when there are no layouts yet — hence one place deciding, rather than
    /// two that would take turns overriding each other.
    /// </summary>
    private void UpdateHintVisibility()
    {
        if (_hotkeyHint == null) return;
        _hotkeyHint.Height = S(22);
        _hotkeyHint.Visible = _profiles.Count > 0 && DesignClientH >= 560;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyDensity();
    }

    /// <summary>
    /// The scale to draw at: the monitor's own, unless the screen is too small to hold
    /// the window's design minimum, in which case enough less that it fits. The floor
    /// stops a very small screen from producing text nobody can read — past that point
    /// clipping is the better failure of the two.
    /// </summary>
    private float ComputeUiScale(Rectangle work)
    {
        float dpi = DeviceDpi / 96f;
        if (work.Width <= 0 || work.Height <= 0) return dpi;

        float fits = Math.Min(work.Width / (float)DesignMinW, work.Height / (float)DesignMinH);
        return Math.Clamp(Math.Min(dpi, fits), Math.Min(0.6f, dpi), dpi);
    }

    /// <summary>The usable area of the screen this window is on, or the primary one
    /// before it has a handle to be "on" anything.</summary>
    private Rectangle AvailableWorkArea()
    {
        var screen = IsHandleCreated ? Screen.FromControl(this) : Screen.PrimaryScreen;
        return screen?.WorkingArea ?? Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
    }

    /// <summary>
    /// Keeps the window inside the screen it is on.
    ///
    /// A minimum size larger than the desktop is the one setting a user cannot drag
    /// their way out of: the window cannot be made small enough to see all of. On a
    /// low resolution — the state a machine sits in while a display driver installs,
    /// which is exactly when someone reaches for a display tool — that puts Apply off
    /// the bottom of the screen with no way to reach it.
    /// </summary>
    private void ApplySizeConstraints(bool resize)
    {
        var work = AvailableWorkArea();

        var minimum = new Size(S(DesignMinW), S(DesignMinH));
        if (work.Width > 0 && work.Height > 0)
        {
            minimum = new Size(Math.Min(minimum.Width, work.Width), Math.Min(minimum.Height, work.Height));
        }
        MinimumSize = minimum;

        if (!resize || WindowState != FormWindowState.Normal) return;

        // Sizing has to be done on the whole window, not the client area: the caption
        // and border are another ~80px at 225%, and a client area sized exactly to the
        // work area puts that much of the window off the bottom of the screen.
        var frame = Size - ClientSize;
        var wanted = new Size(S(DesignW) + frame.Width, S(DesignH) + frame.Height);
        if (work.Width > 0 && work.Height > 0)
        {
            wanted = new Size(Math.Min(wanted.Width, work.Width), Math.Min(wanted.Height, work.Height));
        }
        Size = wanted;
    }

    /// <summary>
    /// The first point at which this window knows where it actually is.
    ///
    /// Until now DeviceDpi was a guess — the primary monitor's scale — and everything
    /// the constructor built was sized from that guess. If the window opened anywhere
    /// else, WinForms has just quietly re-sized every font to the real DPI without
    /// moving or resizing a single control around them. Rebuilding here is what puts
    /// text and boxes back on one scale; it is also the one place a window that opens
    /// on a screen too small for it can find that out.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        bool sameScale = Math.Abs(_uiScale - ComputeUiScale(AvailableWorkArea())) < 0.001f;
        if (sameScale && DeviceDpi == UiScaling.InitialDpi)
        {
            // Opened on the monitor it was built for: no font was adjusted, so there
            // is nothing to redo. The size is still worth re-running — the caption and
            // border are only measurable now that the window exists.
            ApplySizeConstraints(resize: true);
            return;
        }

        ForceRescaleUi(resize: true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // The standing minimum is in pixels at the OLD scale. Left in place it stops
        // the window ever getting smaller, so moving from a 200% monitor to a 100% one
        // left a window twice the size it should be — and one that could not be
        // shrunk back.
        MinimumSize = Size.Empty;

        // WinForms handles the rest of the message itself: it adopts Windows' suggested
        // rectangle, updates DeviceDpi, and adjusts every visible control's font by the
        // DPI ratio. It does not move or resize any of those controls — that asymmetry
        // is exactly what this form has to undo.
        base.OnDpiChanged(e);

        // Forced rather than conditional: the scale this form draws at can be pinned by
        // a small screen and so come out unchanged, and the fonts would still have been
        // adjusted out from under it.
        ForceRescaleUi(resize: false);
    }

    /// <summary>
    /// Redraws the whole window at whatever scale is right for where it is now. Cheap
    /// enough to be worth its simplicity: a full rebuild only happens when the scale
    /// genuinely moves, which is a handful of times a session at most.
    /// </summary>
    private void RescaleUi(bool resize)
    {
        if (_rescaling) return;

        if (Math.Abs(ComputeUiScale(AvailableWorkArea()) - _uiScale) < 0.001f)
        {
            ApplySizeConstraints(resize: false);
            return;
        }

        ForceRescaleUi(resize);
    }

    /// <summary>
    /// Rebuilds even when the scale itself has not moved. Needed the first time the
    /// window gets a handle: the scale can be unchanged and the controls still wrong,
    /// because WinForms adjusted their fonts on the way in.
    /// </summary>
    private void ForceRescaleUi(bool resize)
    {
        if (_rescaling) return;

        _uiScale = ComputeUiScale(AvailableWorkArea());
        _rescaling = true;
        try
        {
            // Autosave keys off edits, not off redrawing the window, so the rebuild
            // must not read as one: LoadSelectedProfile repopulates the name box, and
            // that alone would otherwise mark the layout dirty.
            bool dirty = _dirty, notified = _dirtyNotified;
            BuildUi();
            _dirty = dirty;
            _dirtyNotified = notified;

            ApplySizeConstraints(resize);
            UpdateUndoBar();
            Invalidate(true);
        }
        finally
        {
            _rescaling = false;
        }
    }

    private Control BuildEmptyState()
    {
        _emptyPanel = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Panel, Visible = false };

        var capture = UiTheme.MakeButton("Save this as my first layout", true, DpiScale, F(9.5f));
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
        UpdateHintVisibility();
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

        _canvas = new MonitorCanvas { Dock = DockStyle.Fill, UiScale = DpiScale };
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
            Font = new Font("Segoe UI", F(8.5f), FontStyle.Regular, GraphicsUnit.Pixel),
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

        _fitBtn = UiTheme.MakeButton("Fit", false, DpiScale, F(9.5f));
        _fitBtn.Size = new Size(S(58), S(28));
        _fitBtn.Click += (_, _) => _canvas.FitView();
        _tips.SetToolTip(_fitBtn, "Fit all monitors in view");

        _identifyAllBtn = UiTheme.MakeButton("Identify", false, DpiScale, F(9.5f));
        _identifyAllBtn.Size = new Size(S(84), S(28));
        _identifyAllBtn.Click += (_, _) => IdentifyOverlays.ShowAll();
        _tips.SetToolTip(_identifyAllBtn, "Flash numbers on the physical monitors");

        _refreshBtn = UiTheme.MakeButton("Rescan", false, DpiScale, F(9.5f));
        _refreshBtn.Size = new Size(S(78), S(28));
        _refreshBtn.Click += (_, _) => RefreshDisplays();
        _tips.SetToolTip(_refreshBtn, "Re-scan connected monitors");

        _snapToggle = new CheckBox
        {
            Text = "Snap",
            Checked = true,
            ForeColor = UiTheme.Text,
            BackColor = UiTheme.Card,
            Font = new Font("Segoe UI", F(11f), FontStyle.Regular, GraphicsUnit.Pixel),
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

            // Tell the canvas how much of its top these controls are standing on, so
            // the fitted arrangement is placed below them rather than behind them.
            _canvas.TopReserve = tools.Bottom + S(8);
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
            Height = S(Compact ? 104 : 118),
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
            Font = new Font("Segoe UI", F(10.5f), FontStyle.Bold, GraphicsUnit.Pixel),
            ForeColor = UiTheme.Text,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _inspectorSub = new Label
        {
            Font = new Font("Segoe UI", F(8.5f), FontStyle.Regular, GraphicsUnit.Pixel),
            ForeColor = UiTheme.Muted,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _includeToggle = new CheckBox
        {
            Text = "Include in layout",
            ForeColor = UiTheme.Text,
            Font = new Font("Segoe UI", F(9f), FontStyle.Regular, GraphicsUnit.Pixel),
            AutoSize = true,
            Cursor = Cursors.Hand
        };
        _includeToggle.CheckedChanged += (_, _) =>
        {
            if (_syncingInspector || _canvas.SelectedConfig == null) return;
            _canvas.SetEnabled(_canvas.SelectedConfig, _includeToggle.Checked);
            UpdateInspector();
        };

        _primaryBtn = UiTheme.MakeButton("Set as main", false, DpiScale, F(9.5f));
        _primaryBtn.Size = new Size(S(135), S(34));
        _primaryBtn.Click += (_, _) =>
        {
            if (_canvas.SelectedConfig != null) _canvas.SetPrimary(_canvas.SelectedConfig);
            UpdateInspector();
        };

        _identifyOneBtn = UiTheme.MakeButton("Identify", false, DpiScale, F(9.5f));
        _identifyOneBtn.Size = new Size(S(105), S(34));
        _identifyOneBtn.Click += (_, _) =>
        {
            if (_canvas.SelectedConfig != null) IdentifyOverlays.Show(_canvas.SelectedConfig.DeviceName);
        };

        var resolutionLabel = UiTheme.MakeEyebrow("RESOLUTION", F(8.5f));
        var refreshLabel = UiTheme.MakeEyebrow("REFRESH", F(8.5f));
        var scaleLabel = UiTheme.MakeEyebrow("SCALE", F(8.5f));

        _resolutionBox = MakeCombo();
        _resolutionBox.SelectedIndexChanged += (_, _) => ResolutionChanged();
        _refreshBox = MakeCombo();
        _refreshBox.SelectedIndexChanged += (_, _) => RefreshRateChanged();
        _scaleBox = MakeCombo();
        _scaleBox.SelectedIndexChanged += (_, _) => ScaleChanged();

        panel.Resize += (_, _) =>
        {
            bool compact = Compact;
            int pad = S(18);
            int gap = S(10);
            int rowH = S(30);
            int titleY = S(compact ? 6 : 10);

            // Three mode controls and three actions do not both fit on one row in a
            // narrow window — they used to be laid out as though they did, which put
            // "Include in layout" underneath the scale box. So when the row is too
            // tight, the actions move up beside the monitor's name, where there is
            // room going spare, and the mode controls get the whole width.
            bool stacked = Narrow;
            int actionsY = stacked ? titleY - S(4) : S(compact ? 48 : 58);
            int actionsH = stacked ? S(26) : rowH;

            int right = panel.Width - pad;
            int identifyW = Fit(panel.Width, S(105), S(78), 0.14f);
            int primaryW = Fit(panel.Width, S(135), S(96), 0.18f);

            _identifyOneBtn.SetBounds(right - identifyW, actionsY, identifyW, actionsH);
            _primaryBtn.SetBounds(_identifyOneBtn.Left - S(8) - primaryW, actionsY, primaryW, actionsH);
            _includeToggle.Location = new Point(
                _primaryBtn.Left - S(14) - _includeToggle.Width,
                actionsY + (actionsH - _includeToggle.Height) / 2);

            // Mode controls, left to right, sharing whatever the row has.
            int boxesRight = stacked ? panel.Width - pad : _includeToggle.Left - S(20);
            int available = Math.Max(S(210), boxesRight - pad);
            int boxW = Math.Min(S(210), (available - gap * 2) / 3);

            int boxY = S(compact ? 48 : 58);
            int x = pad;
            foreach (var (lbl, box) in new (Control, Control)[]
                     { (resolutionLabel, _resolutionBox), (refreshLabel, _refreshBox), (scaleLabel, _scaleBox) })
            {
                lbl.SetBounds(x, S(compact ? 30 : 38), boxW, S(16));
                box.SetBounds(x, boxY, boxW, rowH);
                x += boxW + gap;
            }

            // The name always gets its space; the read-out beside it is the first
            // thing to go, because everything in it is also shown on the monitor's
            // own tile on the canvas.
            int titleW = stacked
                ? Math.Max(S(120), _includeToggle.Left - pad - S(12))
                : Math.Max(S(160), panel.Width / 3);
            _inspectorTitle.SetBounds(pad, titleY, titleW, S(22));

            // Positioned before the visibility decision, and never gated on reading
            // Visible back: Control.Visible reports whether the control is actually on
            // screen, so while the window is still being built it answers false however
            // it was just set, and a SetBounds behind that test never runs at all.
            int subLeft = _inspectorTitle.Right + S(14);
            int subW = Math.Max(S(20), panel.Width - subLeft - pad);
            _inspectorSub.SetBounds(subLeft, titleY + S(2), subW, S(20));
            _inspectorSub.Visible = !stacked && subW > S(140);
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
        _inspector = panel;
        return panel;
    }

    private ComboBox MakeCombo() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        BackColor = UiTheme.Input,
        ForeColor = UiTheme.Text,
        Font = new Font("Segoe UI", F(9.5f), FontStyle.Regular, GraphicsUnit.Pixel)
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
            Height = S(Compact ? 56 : 70),
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
            Font = new Font("Segoe UI", F(9f), FontStyle.Italic, GraphicsUnit.Pixel),
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
            Font = new Font("Segoe UI", F(9.5f), FontStyle.Regular, GraphicsUnit.Pixel),
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleRight
        };

        _cancelBtn = UiTheme.MakeButton("Close", false, DpiScale, F(9.5f));
        _cancelBtn.Size = new Size(S(100), S(40));
        _cancelBtn.Click += (_, _) => Close();

        // One commit action. Edits persist on their own (see MarkDirty), so there is
        // nothing left for a Save button to do, and "apply without saving" was a
        // distinction with no meaning once saving is automatic.
        _applyBtn = UiTheme.MakeButton("Apply layout", true, DpiScale, F(9.5f));
        _applyBtn.Size = new Size(S(150), S(40));
        _applyBtn.Click += (_, _) => ApplySelected();
        _tips.SetToolTip(_applyBtn, "Switch Windows to this layout. It reverts by itself unless you confirm.");

        footer.Resize += (_, _) =>
        {
            // Apply is the one control in this window that must never be squeezed out
            // or pushed off the edge, so it is placed first and everything else takes
            // what is left.
            int applyW = Fit(footer.Width, S(150), S(104), 0.20f);
            int cancelW = Fit(footer.Width, S(100), S(72), 0.13f);
            int btnH = Math.Min(S(40), footer.Height - S(14));

            _applyBtn.SetBounds(footer.Width - applyW - S(20), (footer.Height - btnH) / 2, applyW, btnH);
            _cancelBtn.SetBounds(_applyBtn.Left - S(10) - cancelW, (footer.Height - btnH) / 2, cancelW, btnH);

            int statusW = Math.Min(S(320), Math.Max(S(120), _cancelBtn.Left - S(200)));
            _statusLabel.SetBounds(_cancelBtn.Left - S(18) - statusW, 0, statusW, footer.Height);

            // The transient "what just happened" line shares the row with the standing
            // status reading. On a narrow footer there is only room for one, and the
            // status — which answers "is this layout what my screens are doing?" right
            // where Apply is — is the one worth keeping.
            int feedbackW = _statusLabel.Left - S(36);
            _feedbackLabel.SetBounds(S(24), 0, Math.Max(S(20), feedbackW), footer.Height);
            _feedbackLabel.Visible = feedbackW >= S(80);
        };

        footer.Controls.Add(_feedbackLabel);
        footer.Controls.Add(_statusLabel);
        footer.Controls.Add(_cancelBtn);
        footer.Controls.Add(_applyBtn);
        _footer = footer;
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
            Font = new Font("Segoe UI", F(9f), FontStyle.Regular, GraphicsUnit.Pixel),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _undoBtn = UiTheme.MakeButton("Undo layout", true, DpiScale, F(9.5f));
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

    /// <summary>Card size for the current density. The compact card drops the second
    /// text line rather than squeezing two lines into the space for one.</summary>
    private int RailCardWidth => S(Compact ? 158 : RailCardW);
    private int RailCardHeight => S(Compact ? 58 : RailCardH);

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
            Height = RailCardHeight + S(30),
            BackColor = UiTheme.Panel,
            Padding = new Padding(S(18), S(10), S(18), S(10))
        };
        _rail = rail;
        rail.Resize += (_, _) =>
            rail.Padding = new Padding(S(18), S(Compact ? 6 : 10), S(18), S(Compact ? 6 : 10));
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

        var nameLabel = UiTheme.MakeEyebrow("LAYOUT NAME", F(8.5f));
        var shortcutLabel = UiTheme.MakeEyebrow("GLOBAL SHORTCUT", F(8.5f));

        _nameTextBox = new TextBox
        {
            BackColor = UiTheme.Input,
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", F(12f), FontStyle.Regular, GraphicsUnit.Pixel),
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
            Font = new Font("Consolas", F(12f), FontStyle.Bold, GraphicsUnit.Pixel),
            PlaceholderText = "Click, then press a shortcut",
            TextAlign = HorizontalAlignment.Center
        };
        _hotkeyTextBox.GotFocus += (_, _) => StartHotkeyCapture();
        _hotkeyTextBox.KeyDown += HotkeyTextBox_KeyDown;
        _captureHotkeyBtn = UiTheme.MakeButton("Record", false, DpiScale, F(9.5f));
        _captureHotkeyBtn.Click += (_, _) => { _hotkeyTextBox.Focus(); StartHotkeyCapture(); };
        _tips.SetToolTip(_hotkeyTextBox, "Global shortcut that switches to this layout");

        _captureCurrentLayoutBtn = UiTheme.MakeButton("Capture", false, DpiScale, F(9.5f));
        _captureCurrentLayoutBtn.Click += CaptureCurrentLayoutBtn_Click;
        _tips.SetToolTip(_captureCurrentLayoutBtn, "Replace this layout with the monitors as they are arranged right now");

        _duplicateBtn = UiTheme.MakeButton("Duplicate", false, DpiScale, F(9.5f));
        _duplicateBtn.Click += DuplicateProfileBtn_Click;
        _tips.SetToolTip(_duplicateBtn, "Copy the selected layout");

        _deleteProfileBtn = UiTheme.MakeButton("Delete", false, DpiScale, F(9.5f));
        _deleteProfileBtn.Click += DeleteProfileBtn_Click;

        // Kept for the empty state, which calls it directly.
        _addProfileBtn = UiTheme.MakeButton("+ Add", false, DpiScale, F(9.5f));
        _addProfileBtn.Click += AddProfileBtn_Click;
        _addProfileBtn.Visible = false;

        // Every width in this row is negotiable, and the row is laid out by handing
        // each part its share of what is actually there rather than by adding up
        // constants and hoping. The old version added them up: at anything under about
        // 900 design pixels the name box hit its floor, the shortcut box was pushed on
        // top of it, and below 700 the name box was pushed off the left edge of the
        // window altogether.
        bar.Resize += (_, _) =>
        {
            bool compact = Compact;
            int pad = S(18);
            int gap = S(8);
            int y = S(compact ? 26 : 30);
            int h = S(compact ? 30 : 34);
            int labelY = S(compact ? 8 : 10);

            int room = bar.Width - pad * 2;

            // The three layout actions shrink before anything else does: their labels
            // are short, so they stay readable a long way down.
            int actionW = Fit(room, S(96), S(74), 0.20f);
            int recordW = Fit(room, S(84), S(64), 0.11f);
            int hotkeyW = Fit(room, S(220), S(150), 0.26f);

            int right = bar.Width - pad;
            foreach (var b in new[] { _deleteProfileBtn, _duplicateBtn, _captureCurrentLayoutBtn })
            {
                right -= actionW;
                b.SetBounds(right, y, actionW, h);
                right -= gap;
            }

            right -= S(compact ? 8 : 14);
            _captureHotkeyBtn.SetBounds(right - recordW, y, recordW, h);
            _hotkeyTextBox.SetBounds(right - recordW - gap - hotkeyW, y, hotkeyW, h);

            // Whatever is left is the name's, down to a floor that still shows a name
            // rather than one word of it.
            int nameW = Math.Min(S(360), Math.Max(S(120), _hotkeyTextBox.Left - pad - S(20)));
            _nameTextBox.SetBounds(pad, y, nameW, h);

            nameLabel.SetBounds(pad, labelY, nameW, S(16));
            shortcutLabel.SetBounds(_hotkeyTextBox.Left, labelY, _hotkeyTextBox.Width, S(16));
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

        // Controls.Clear detaches without disposing, and this runs on every layout
        // added, deleted or renamed as well as on every rebuild.
        var previous = _profileCardsPanel.Controls.Cast<Control>().ToArray();
        _profileCardsPanel.Controls.Clear();
        foreach (var card in previous) card.Dispose();
        _cardViews.Clear();

        foreach (var profile in _profiles)
        {
            var p = profile;
            var card = new BufferedPanel
            {
                Width = RailCardWidth,
                Height = RailCardHeight,
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
            Width = S(Compact ? 108 : 132),
            Height = RailCardHeight,
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
        _addTile = addTile;
        addTile.Click += AddProfileBtn_Click;
        addTile.MouseEnter += (_, _) => addTile.Invalidate();
        addTile.MouseLeave += (_, _) => addTile.Invalidate();
        _profileCardsPanel.Controls.Add(addTile);

        _profileCardsPanel.ResumeLayout();
        RefreshActiveBadges();
        UpdateEmptyState();
        ApplyDensity();
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

        // On a short window the card loses its third line rather than crushing three
        // lines into the room for two. The arrangement and the name are what let you
        // pick a layout out of the row; the shortcut is a reminder, and "LIVE" has
        // somewhere else to be said — the window title.
        bool compact = card.Height < S(RailCardH);
        int glyphW = compact ? S(44) : S(56);
        int textX = compact ? S(64) : S(78);
        int textW = card.Width - textX - S(12);

        // The arrangement, which is what actually distinguishes one layout from another.
        LayoutGlyph.Draw(g,
            new RectangleF(S(12), compact ? S(10) : S(12), glyphW, compact ? S(26) : S(34)),
            view.Profile.Displays,
            onColor: Color.FromArgb(selected ? 200 : 150, UiTheme.Text),
            offColor: Color.FromArgb(90, UiTheme.Line),
            primaryColor: selected ? UiTheme.Gold : UiTheme.GoldDim);

        using var trim = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        using (var nameFont = new Font("Segoe UI", (compact ? 12f : 13f) * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(selected ? UiTheme.Gold : UiTheme.Text))
        {
            g.DrawString(view.Profile.Name, nameFont, ink,
                new RectangleF(textX, compact ? S(8) : S(12), textW, S(20)), trim);
        }

        string sub = view.Profile.NeedsRecapture
            ? "Needs re-capture"
            : compact && view.IsLive ? "LIVE"
            : string.IsNullOrWhiteSpace(view.Profile.Hotkey) ? "No shortcut" : view.Profile.Hotkey;
        using (var subFont = new Font("Segoe UI", (compact ? 10f : 11f) * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var subInk = new SolidBrush(
                   view.Profile.NeedsRecapture ? UiTheme.Danger
                   : compact && view.IsLive ? UiTheme.Gold
                   : UiTheme.Muted))
        {
            g.DrawString(sub, subFont, subInk,
                new RectangleF(textX, compact ? S(28) : S(33), textW, S(18)), trim);
        }

        if (view.IsLive && !compact)
        {
            using var liveFont = new Font("Segoe UI", 10f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
            using var liveInk = new SolidBrush(UiTheme.Gold);
            g.DrawString("LIVE", liveFont, liveInk, new PointF(textX, card.Height - S(24)));
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
    /// mouse move, and rewriting .dls per frame would be pointless churn
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
            // A resolution change does not always come with a scale change, so
            // WM_DPICHANGED cannot be relied on to notice that the screen this window
            // is on just got much smaller — or much bigger. Checking here is what
            // makes the window recover on its own when a display driver finishes
            // installing and the desktop jumps from 1024×768 to its real size.
            RescaleUi(resize: false);

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
