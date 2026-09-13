using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DLS;

internal sealed class MonitorCanvas : Control
{
    private sealed class CanvasItem
    {
        public DisplayTargetConfig Config { get; init; } = null!;
        public int Number { get; set; }
        public bool Present { get; set; }

        /// <summary>What Windows reports for this monitor right now, when it is
        /// attached. Lets the tile show a real refresh rate and scale even when the
        /// layout itself has no opinion about them.</summary>
        public DisplayInfo? Live { get; set; }
        public Rectangle DrawRect;

        /// <summary>Where this tile is actually painted while a transition is running.
        /// DrawRect stays the true target, so hit-testing and dragging never have to
        /// know an animation is happening.</summary>
        public Rectangle FromRect;
        public bool HasFrom;
        public bool Hovered;
    }

    private sealed class ViewTransform
    {
        public float Scale = 0.08f;
        public float OffsetX;
        public float OffsetY;
    }

    private DisplayProfile? _profile;
    private List<DisplayInfo> _hardware = new();
    private readonly List<CanvasItem> _items = new();
    private CanvasItem? _selected;
    private CanvasItem? _drag;
    private Point _dragOffset;
    private bool _dragFromShelf;
    private bool _viewFrozen;
    private ViewTransform _view = new();
    private readonly List<(Point A, Point B)> _guides = new();
    private bool _overShelf;

    private const int ShelfWidthDesign = 184;
    private const int ShelfCollapsedWidthDesign = 56;

    /// <summary>Vertical space reserved at the top of the rail for the "not in this
    /// layout" caption. Chips used to stack starting right under the top padding,
    /// which put the first one or two directly on top of the wrapped caption text.</summary>
    private const int ShelfHeaderDesign = 92;
    private const int SnapThreshold = 72;

    public event EventHandler? LayoutChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler<string>? StatusMessage;

    public DisplayTargetConfig? SelectedConfig => _selected?.Config;
    public bool SnapEnabled { get; set; } = true;

    /// <summary>
    /// The scale to draw at, set by the owning form so the canvas and the chrome
    /// around it agree. It is normally the monitor's DPI scale, but the editor drops
    /// below that on a screen too small for its design size, and a canvas still
    /// drawing at full scale inside a shrunken window would overflow it.
    /// </summary>
    public float UiScale
    {
        get => _uiScale > 0 ? _uiScale : DeviceDpi / 96f;
        set { _uiScale = value; Invalidate(); }
    }

    private float _uiScale;

    private float DpiScale => UiScale;
    private int S(int val) => (int)Math.Round(val * DpiScale);

    public MonitorCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        BackColor = UiTheme.Input;
        TabStop = true;

        _anim.Tick += (_, _) =>
        {
            if ((DateTime.UtcNow - _animStart).TotalMilliseconds >= AnimMs)
            {
                _animating = false;
                _anim.Stop();
                foreach (var item in _items) item.HasFrom = false;
            }
            Invalidate();
        };
    }

    /// <summary>
    /// True while the rail has something to show — an unused monitor sitting on it, or
    /// one mid-drag out of it. This, not drag state in general, is what the rail's
    /// width should follow: widening for every drag (including one that never touches
    /// the rail, like repositioning a monitor already on the canvas) is what used to
    /// shove the whole view sideways under the user's cursor.
    /// </summary>
    private bool ShelfExpanded => _items.Any(i => !i.Config.Enabled) || _dragFromShelf;

    /// <summary>
    /// A rail down the left edge, present whether or not anything is on it — only its
    /// width changes, and only between drags, never during one, so a drag never sees
    /// the canvas geometry shift under it. Collapsed to a labeled strip when nothing is
    /// unused, so the rail's purpose stays visible without permanently spending a fifth
    /// of the canvas on "empty".
    /// </summary>
    private int ShelfWidth => ShelfExpanded
        ? Math.Max(S(96), S(ShelfWidthDesign))
        : Math.Max(S(40), S(ShelfCollapsedWidthDesign));
    private int Pad => Math.Max(S(16), S(18));

    /// <summary>
    /// Space at the top of the canvas the fitted view must keep clear, for the view
    /// controls that float over it. Set by the owner, because the owner is what puts
    /// them there.
    ///
    /// Without it the fit uses the full height and centres in it, which is fine on a
    /// tall canvas and wrong on a short one: the monitors end up drawn underneath the
    /// Snap/Fit/Identify strip, where the top of the selected tile — its MAIN badge —
    /// cannot be seen.
    /// </summary>
    public int TopReserve
    {
        get => _topReserve;
        set
        {
            if (_topReserve == value) return;
            _topReserve = value;
            RecalcLayout();
            Invalidate();
        }
    }

    private int _topReserve;

    public void Bind(DisplayProfile? profile)
    {
        bool hadItems = _items.Count > 0;
        if (hadItems) CaptureFromRects();
        var previous = _items.ToDictionary(i => i.Config.MonitorDevicePath ?? string.Empty, i => i.FromRect,
            StringComparer.OrdinalIgnoreCase);

        _profile = profile;
        _hardware = DisplayEngine.GetCurrentDisplays();
        MergeHardware();
        RebuildItems();
        _selected = _items.FirstOrDefault(i => i.Config.Enabled && i.Config.IsPrimary)
            ?? _items.FirstOrDefault(i => i.Config.Enabled) ?? _items.FirstOrDefault();
        _viewFrozen = false;
        RecalcLayout();

        // The items are rebuilt on every bind, so carry the old positions across by
        // monitor identity — the same panel keeps moving rather than vanishing and
        // reappearing somewhere else.
        if (hadItems)
        {
            foreach (var item in _items)
            {
                if (previous.TryGetValue(item.Config.MonitorDevicePath ?? string.Empty, out var from))
                {
                    item.FromRect = from;
                    item.HasFrom = true;
                }
            }
            BeginTransition();
        }

        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-lays out after a monitor's resolution was changed from the
    /// inspector. Does not raise LayoutChanged — the caller already recorded the edit,
    /// and re-entering the inspector update from here would loop.</summary>
    public void RefreshLayoutGeometry()
    {
        CaptureFromRects();
        var previous = _items.ToDictionary(i => i.Config.MonitorDevicePath ?? string.Empty, i => i.FromRect,
            StringComparer.OrdinalIgnoreCase);

        RebuildItems();
        RecalcLayout();

        foreach (var item in _items)
        {
            if (previous.TryGetValue(item.Config.MonitorDevicePath ?? string.Empty, out var from))
            {
                item.FromRect = from;
                item.HasFrom = true;
            }
        }

        BeginTransition();
        Invalidate();
    }

    public void FitView()
    {
        _viewFrozen = false;
        RecalcLayout();
        Invalidate();
    }

    public void RefreshHardware()
    {
        string? selectedName = _selected?.Config.DeviceName;
        _hardware = DisplayEngine.GetCurrentDisplays();
        MergeHardware();
        RebuildItems();
        _selected = _items.FirstOrDefault(i =>
                        string.Equals(i.Config.DeviceName, selectedName, StringComparison.OrdinalIgnoreCase))
                    ?? _items.FirstOrDefault(i => i.Config.Enabled)
                    ?? _items.FirstOrDefault();
        RecalcLayout();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool NudgeSelected(int dx, int dy)
    {
        if (_selected?.Config.Enabled != true) return false;
        _selected.Config.X += dx;
        _selected.Config.Y += dy;
        RebaseToPrimary();
        UpdateRelativePositions();
        RecalcLayout();
        Invalidate();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SetEnabled(DisplayTargetConfig cfg, bool enabled)
    {
        var item = _items.FirstOrDefault(i => ReferenceEquals(i.Config, cfg));
        if (item == null) return;
        if (enabled) EnableItem(item, null);
        else DisableItem(item);
    }

    public void SetPrimary(DisplayTargetConfig cfg)
    {
        var item = _items.FirstOrDefault(i => ReferenceEquals(i.Config, cfg));
        if (item == null) return;
        if (!item.Config.Enabled) EnableItem(item, null);
        foreach (var i in _items) i.Config.IsPrimary = false;
        item.Config.IsPrimary = true;
        RebaseToPrimary();
        RecalcLayout();
        Invalidate();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        StatusMessage?.Invoke(this, $"'{ShortName(item)}' is now the main display.");
    }

    private void MergeHardware()
    {
        if (_profile == null) return;

        // Clean out dead / 0x0 entries
        _profile.Displays.RemoveAll(d => d.Width <= 0 || d.Height <= 0);

        // Re-attach anything whose port has moved before deciding what is missing.
        // Without this, a monitor that came back on a different path is not
        // recognised as one this layout already knows, and the code below dutifully
        // adds it a second time — which is how a layout ends up listing the same
        // physical monitor twice, once unusable.
        MonitorIdentity.Rebind(_profile, _hardware);

        // Full CCD capture of every connected monitor, fetched once. A monitor the
        // profile has never seen has to be added with its complete mode detail, not
        // just its geometry — a target without MonitorDevicePath makes the whole
        // profile look pre-CCD (NeedsRecapture), and one without the captured target
        // mode can't be re-enabled at its native mode in a single call.
        var live = DisplayEngine.CaptureTargets();

        foreach (var hw in _hardware)
        {
            var match = FindMatch(hw);
            if (match != null)
            {
                match.DeviceName = hw.DeviceName;
                match.MonitorDevicePath = hw.MonitorDevicePath;
                match.HardwareId = hw.MonitorDevicePath;
                if (string.IsNullOrWhiteSpace(match.MonitorId)) match.MonitorId = hw.MonitorId;
                if (match.Width <= 0) match.Width = hw.Width;
                if (match.Height <= 0) match.Height = hw.Height;
                if (match.RefreshRate <= 0) match.RefreshRate = hw.RefreshRate;
                continue;
            }

            var captured = live.FirstOrDefault(t =>
                DisplayEngine.SameHardwareIdentity(t.MonitorDevicePath, hw.MonitorDevicePath));
            if (captured == null) continue;

            var added = captured.Clone();
            added.RelativePosition = hw.RelativePosition;
            added.Enabled = false;
            added.IsPrimary = false;
            _profile.Displays.Add(added);
        }
    }

    // Matching is a single whole-string compare on the CCD device path. The old
    // DISPLAYn and monitor-description fallbacks are gone: DISPLAYn renumbers on
    // every enable/disable this app performs, and the description is identical for
    // identical monitors, so both could only ever bind a target to the wrong panel.
    private DisplayTargetConfig? FindMatch(DisplayInfo hw) =>
        _profile?.Displays.FirstOrDefault(d =>
            DisplayEngine.SameHardwareIdentity(d.MonitorDevicePath, hw.MonitorDevicePath));

    // ---------------------------------------------------------------- transitions

    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };

    /// <summary>
    /// The transition timer outlives the control unless it is stopped here, and it
    /// calls Invalidate on every tick. That was harmless while the canvas lived as long
    /// as the window did; it stopped being harmless once a scale change started
    /// rebuilding the canvas, which would otherwise leave a timer per rebuild ticking
    /// at a disposed control.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Stop();
            _anim.Dispose();
        }
        base.Dispose(disposing);
    }
    private DateTime _animStart;
    private bool _animating;
    private const int AnimMs = 220;

    /// <summary>
    /// Eases the tiles from where they were to where they now belong.
    ///
    /// This is the cheapest thing in the app that makes it feel considered, and it
    /// also does real work: switching layouts snaps three monitors to new places at
    /// once, and watching them move tells you what changed far better than noticing
    /// that the picture is different.
    /// </summary>
    private void BeginTransition()
    {
        bool any = false;
        foreach (var item in _items)
        {
            if (!item.HasFrom || item.FromRect == item.DrawRect) continue;
            any = true;
            break;
        }

        if (!any)
        {
            _animating = false;
            _anim.Stop();
            return;
        }

        _animStart = DateTime.UtcNow;
        _animating = true;
        _anim.Start();
    }

    private void CaptureFromRects()
    {
        foreach (var item in _items)
        {
            item.FromRect = Painted(item);
            item.HasFrom = true;
        }
    }

    /// <summary>Where a tile is drawn right now, easing between FromRect and DrawRect
    /// while a transition runs.</summary>
    private Rectangle Painted(CanvasItem item)
    {
        if (!_animating || !item.HasFrom) return item.DrawRect;

        float t = (float)((DateTime.UtcNow - _animStart).TotalMilliseconds / AnimMs);
        if (t >= 1f) return item.DrawRect;
        t = Math.Max(0f, t);

        // Ease-out cubic: quick to start, settling gently — motion that reads as
        // physical rather than mechanical.
        float e = 1f - (float)Math.Pow(1 - t, 3);

        static int Lerp(int a, int b, float k) => a + (int)Math.Round((b - a) * k);
        return new Rectangle(
            Lerp(item.FromRect.X, item.DrawRect.X, e),
            Lerp(item.FromRect.Y, item.DrawRect.Y, e),
            Lerp(item.FromRect.Width, item.DrawRect.Width, e),
            Lerp(item.FromRect.Height, item.DrawRect.Height, e));
    }

    private void RebuildItems()
    {
        _items.Clear();
        if (_profile == null) return;

        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int n = 1;
        foreach (var hw in _hardware.OrderBy(h => h.X).ThenBy(h => h.Y))
        {
            numbers[hw.MonitorDevicePath] = n++;
        }

        foreach (var cfg in _profile.Displays)
        {
            EnsureSize(cfg);
        }

        foreach (var cfg in _profile.Displays.Where(c => !numbers.ContainsKey(c.MonitorDevicePath))
                                             .OrderBy(c => c.X).ThenBy(c => c.Y))
        {
            numbers[cfg.MonitorDevicePath] = n++;
        }

        foreach (var cfg in _profile.Displays)
        {
            var live = FindHardware(cfg);
            _items.Add(new CanvasItem
            {
                Config = cfg,
                Number = numbers.TryGetValue(cfg.MonitorDevicePath, out int num) ? num : n++,
                Present = live != null,
                Live = live
            });
        }
    }

    private void EnsureSize(DisplayTargetConfig cfg)
    {
        if (cfg.Width > 0 && cfg.Height > 0) return;
        var hw = FindHardware(cfg);
        cfg.Width = hw?.Width > 0 ? hw.Width : 1920;
        cfg.Height = hw?.Height > 0 ? hw.Height : 1080;
        if (cfg.RefreshRate <= 0) cfg.RefreshRate = hw?.RefreshRate ?? 60;
    }

    private DisplayInfo? FindHardware(DisplayTargetConfig cfg) =>
        _hardware.FirstOrDefault(h =>
            DisplayEngine.SameHardwareIdentity(h.MonitorDevicePath, cfg.MonitorDevicePath));


    private void RecalcLayout()
    {
        if (!_viewFrozen) _view = ComputeView();

        int chipW = Math.Max(S(96), ShelfWidth - Pad * 2);
        int chipH = Math.Max(S(54), S(62));
        int x = Pad;
        int y = S(ShelfHeaderDesign);

        foreach (var item in _items)
        {
            if (item.Config.Enabled)
            {
                item.DrawRect = ScreenToCanvas(new Rectangle(item.Config.X, item.Config.Y, item.Config.Width, item.Config.Height));
            }
            else if (!ReferenceEquals(item, _drag))
            {
                item.DrawRect = new Rectangle(x, y, chipW, chipH);
                y += chipH + Pad / 2;
            }
        }
    }

    private ViewTransform ComputeView()
    {
        var enabled = _items.Where(i => i.Config.Enabled).ToList();
        int shelf = ShelfWidth;
        int top = Pad + TopReserve;
        int availW = Math.Max(S(40), Width - shelf - Pad * 2);
        int availH = Math.Max(S(40), Height - top - Pad);

        if (enabled.Count == 0)
        {
            return new ViewTransform
            {
                Scale = Math.Min(availW / 3840f, availH / 2160f) * 0.7f,
                OffsetX = shelf + (Width - shelf) / 2f,
                OffsetY = top + availH / 2f
            };
        }

        int minX = enabled.Min(i => i.Config.X);
        int minY = enabled.Min(i => i.Config.Y);
        int maxX = enabled.Max(i => i.Config.X + Math.Max(1, i.Config.Width));
        int maxY = enabled.Max(i => i.Config.Y + Math.Max(1, i.Config.Height));
        float bw = Math.Max(1, maxX - minX);
        float bh = Math.Max(1, maxY - minY);
        float scale = Math.Min(availW / bw, availH / bh) * 0.82f;

        float usedW = bw * scale;
        float usedH = bh * scale;
        return new ViewTransform
        {
            Scale = scale,
            OffsetX = shelf + Pad + (availW - usedW) / 2f - minX * scale,
            OffsetY = top + (availH - usedH) / 2f - minY * scale
        };
    }

    private Rectangle ScreenToCanvas(Rectangle r)
    {
        int x = (int)Math.Round(r.X * _view.Scale + _view.OffsetX);
        int y = (int)Math.Round(r.Y * _view.Scale + _view.OffsetY);
        int w = Math.Max(S(56), (int)Math.Round(r.Width * _view.Scale));
        int h = Math.Max(S(36), (int)Math.Round(r.Height * _view.Scale));
        return new Rectangle(x, y, w, h);
    }

    private Point CanvasToScreen(Point canvas)
    {
        float scale = Math.Max(0.0001f, _view.Scale);
        int x = (int)Math.Round((canvas.X - _view.OffsetX) / scale);
        int y = (int)Math.Round((canvas.Y - _view.OffsetY) / scale);
        return new Point(x, y);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_drag == null) RecalcLayout();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(UiTheme.Input);

        int shelfLeft = ShelfWidth;
        DrawGrid(g, shelfLeft);
        DrawEnabled(g);
        DrawGuides(g);
        DrawShelf(g, shelfLeft);
        DrawUnused(g);
        DrawBorder(g);
    }

    private void DrawGrid(Graphics g, int shelfLeft)
    {
        int step = Math.Max(S(16), S(22));
        using var brush = new SolidBrush(Color.FromArgb(40, UiTheme.Gold));
        for (int x = shelfLeft + step / 2; x < Width; x += step)
        {
            for (int y = step / 2; y < Height; y += step)
            {
                g.FillRectangle(brush, x, y, 1, 1);
            }
        }

        if (_items.All(i => !i.Config.Enabled) && _drag == null)
        {
            using var font = new Font("Segoe UI", 10f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            using var muted = new SolidBrush(UiTheme.Muted);
            var text = "Drag a display from the rail to include it in this profile";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, muted, shelfLeft + (Width - shelfLeft - size.Width) / 2f, (Height - size.Height) / 2f);
        }
    }

    private void DrawEnabled(Graphics g)
    {
        foreach (var item in _items.Where(i => i.Config.Enabled).OrderBy(i => ReferenceEquals(i, _selected) ? 1 : 0))
        {
            DrawMonitorCard(g, item, Painted(item), true);
        }
    }

    private void DrawUnused(Graphics g)
    {
        foreach (var item in _items.Where(i => !i.Config.Enabled))
        {
            DrawMonitorCard(g, item, Painted(item), false);
        }
    }

    private void DrawMonitorCard(Graphics g, CanvasItem item, Rectangle r, bool enabled)
    {
        if (r.Width < S(8) || r.Height < S(8)) return;
        bool selected = ReferenceEquals(item, _selected);
        int radius = Math.Max(S(6), S(8));

        Color fill = selected ? UiTheme.CardActive : item.Hovered ? UiTheme.CardHover : UiTheme.Card;

        // A soft halo instead of a thicker border. Weight makes a shape look heavy;
        // light makes it look raised, which is what "selected" should feel like.
        if (selected)
        {
            for (int i = 3; i >= 1; i--)
            {
                using var glow = new Pen(Color.FromArgb(16 * i, UiTheme.Gold), i * 2f);
                g.DrawRoundedRectangle(glow, r.X - i * 2, r.Y - i * 2,
                    r.Width + i * 4 - 1, r.Height + i * 4 - 1, radius + i * 2);
            }
        }

        // A vertical gradient reads as a lit surface; a flat fill reads as a box. The
        // difference is a few argb values and it is most of what makes these look like
        // screens rather than rectangles.
        using (var path = RoundedPath(r, radius))
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                   new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)),
                   Lighten(fill, enabled ? 0.10f : 0.04f),
                   Darken(fill, enabled ? 0.05f : 0.02f),
                   System.Drawing.Drawing2D.LinearGradientMode.Vertical))
        {
            g.FillPath(brush, path);
        }

        // The inside of the top edge catches the light.
        using (var sheen = new Pen(Color.FromArgb(enabled ? 26 : 12, 255, 255, 255)))
        {
            g.DrawLine(sheen, r.X + radius, r.Y + 1, r.Right - radius, r.Y + 1);
        }

        Color border = selected ? UiTheme.Gold : item.Hovered ? UiTheme.GoldDim : UiTheme.Line;
        using (var pen = new Pen(border, selected ? 2f : 1f))
        {
            g.DrawRoundedRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1, radius);
        }

        if (enabled) DrawStand(g, r, selected);

        // --- Content, as one block centred in the panel. Pinned to the top it looked
        // like a caption that had run out of room; centred it looks placed.
        float numSize = Math.Clamp(r.Height * 0.20f, S(15), S(46));
        float nameSize = Math.Clamp(r.Height * 0.085f, S(10), S(17));
        float subSize = Math.Clamp(r.Height * 0.062f, S(9), S(13));

        using var numFont = new Font("Segoe UI", numSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var nameFont = new Font("Segoe UI", nameSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI", subSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var text = new SolidBrush(enabled ? UiTheme.Text : UiTheme.Muted);
        using var muted = new SolidBrush(UiTheme.Muted);

        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        string sub = $"{item.Config.Width} × {item.Config.Height}";
        int hz = item.Config.RefreshRate > 0 ? item.Config.RefreshRate : (item.Live?.RefreshRate ?? 0);
        if (hz > 0) sub += $"  ·  {hz} Hz";

        // Scaling is only readable for an attached monitor, and a layout that says
        // nothing about it still runs at whatever Windows has — so show the live
        // value rather than leaving the card silent about it.
        int scale = item.Config.ScalePercent > 0 ? item.Config.ScalePercent : (item.Live?.ScalePercent ?? 0);
        if (scale > 0) sub += $"  ·  {scale}%";

        float numH = numFont.Height;
        float nameH = nameFont.Height;
        float subH = subFont.Height;

        bool showName = r.Height > numH + nameH + S(10);
        bool showSub = enabled && r.Height > numH + nameH + subH + S(16);

        float blockH = numH + (showName ? nameH + S(2) : 0) + (showSub ? subH + S(3) : 0);
        float y = r.Y + (r.Height - blockH) / 2f;

        string numText = item.Present ? item.Number.ToString() : "—";
        using (var numBrush = new SolidBrush(item.Present
                   ? (enabled ? UiTheme.Gold : UiTheme.GoldDim)
                   : UiTheme.Muted))
        {
            g.DrawString(numText, numFont, numBrush, new RectangleF(r.X, y, r.Width, numH), sf);
        }
        y += numH + S(2);

        // Always the monitor's own name. A "Left"/"Center"/"Right" caption restated
        // what the tile's position on the canvas already shows, while hiding the one
        // thing the canvas can't: which physical monitor this actually is.
        if (showName)
        {
            g.DrawString(ShortName(item), nameFont, text,
                new RectangleF(r.X + S(6), y, r.Width - S(12), nameH), sf);
            y += nameH + S(3);
        }

        if (showSub)
        {
            g.DrawString(sub, subFont, muted, new RectangleF(r.X + S(6), y, r.Width - S(12), subH), sf);
        }

        if (item.Config.IsPrimary && enabled)
        {
            string badge = "MAIN";
            using var badgeFont = new Font("Segoe UI", 7.5f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
            var bSize = g.MeasureString(badge, badgeFont);
            var br = new RectangleF(r.Right - bSize.Width - S(12), r.Y + S(6), bSize.Width + S(8), bSize.Height + S(2));
            using var badgeBg = new SolidBrush(UiTheme.Gold);
            using var badgeInk = new SolidBrush(UiTheme.Ink);
            g.FillRoundedRectangle(badgeBg, (int)br.X, (int)br.Y, (int)br.Width, (int)br.Height, 3);
            g.DrawString(badge, badgeFont, badgeInk, br.X + S(4), br.Y + S(1));
        }

        // Only the absent case is worth drawing, and in a muted tone rather than the
        // danger colour: a monitor currently plugged into the other computer is the
        // situation this app exists for, not a fault. Present monitors say nothing —
        // "Connected" on nearly every tile was pure noise.
        if (!item.Present)
        {
            using var warn = new SolidBrush(UiTheme.Muted);
            using var warnFont = new Font("Segoe UI", 7.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString("Not connected", warnFont, warn, new RectangleF(r.X + S(4), r.Bottom - S(18), r.Width - S(8), S(16)), sf);
        }
    }

    /// <summary>A monitor silhouette: a short neck under the panel and a wider foot.
    /// Two small shapes, and the tiles stop reading as plain rectangles.</summary>
    private void DrawStand(Graphics g, Rectangle r, bool selected)
    {
        int alpha = selected ? 170 : 80;
        int neckW = Math.Max(S(8), r.Width / 10);
        int neckH = Math.Max(S(3), S(5));
        int footW = Math.Max(S(20), r.Width / 4);
        int footH = Math.Max(S(3), S(4));

        using var brush = new SolidBrush(Color.FromArgb(alpha, UiTheme.Gold));
        g.FillRectangle(brush, r.X + (r.Width - neckW) / 2, r.Bottom, neckW, neckH);
        g.FillRectangle(brush, r.X + (r.Width - footW) / 2, r.Bottom + neckH, footW, footH);
    }

    private static Color Lighten(Color c, float amount) => Color.FromArgb(
        c.A,
        (int)Math.Min(255, c.R + 255 * amount),
        (int)Math.Min(255, c.G + 255 * amount),
        (int)Math.Min(255, c.B + 255 * amount));

    private static Color Darken(Color c, float amount) => Color.FromArgb(
        c.A,
        (int)Math.Max(0, c.R - 255 * amount),
        (int)Math.Max(0, c.G - 255 * amount),
        (int)Math.Max(0, c.B - 255 * amount));

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void DrawShelf(Graphics g, int shelfLeft)
    {
        using var fill = new SolidBrush(_overShelf ? Color.FromArgb(36, 22, 20) : UiTheme.Panel);
        g.FillRectangle(fill, 0, 0, shelfLeft, Height);
        using var pen = new Pen(_overShelf ? UiTheme.Danger : UiTheme.Line);
        g.DrawLine(pen, shelfLeft, 0, shelfLeft, Height);

        if (ShelfExpanded) DrawShelfLabelExpanded(g, shelfLeft);
        else DrawShelfLabelCollapsed(g, shelfLeft);
    }

    private void DrawShelfLabelExpanded(Graphics g, int shelfLeft)
    {
        using var font = new Font("Segoe UI", 8f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(_overShelf ? UiTheme.Danger : UiTheme.Muted);
        using var fmt = new StringFormat();
        string label = _overShelf
            ? "DROP TO REMOVE FROM THIS PROFILE"
            : "NOT IN THIS LAYOUT  ·  drag onto the canvas to include";
        g.DrawString(label, font, brush, new RectangleF(Pad * 0.6f, S(10), shelfLeft - Pad, S(ShelfHeaderDesign) - S(16)), fmt);
    }

    /// <summary>
    /// The collapsed rail is too narrow for a horizontal line, so the caption runs
    /// bottom-to-top instead — the one orientation that fits readable text into a strip
    /// that width. It says what the rail is for even with nothing on it, so a first-time
    /// user isn't left wondering what the empty sliver down the left edge does.
    /// </summary>
    private void DrawShelfLabelCollapsed(Graphics g, int shelfLeft)
    {
        string label = _overShelf ? "DROP TO REMOVE" : "UNUSED DISPLAYS";
        using var font = new Font("Segoe UI", 8f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(_overShelf ? UiTheme.Danger : UiTheme.Muted);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        var size = g.MeasureString(label, font);
        var state = g.Save();
        g.TranslateTransform(shelfLeft / 2f, Height / 2f);
        g.RotateTransform(-90);
        g.DrawString(label, font, brush, new RectangleF(-size.Width / 2f, -size.Height / 2f, size.Width, size.Height), fmt);
        g.Restore(state);
    }

    private void DrawGuides(Graphics g)
    {
        if (_guides.Count == 0) return;
        using var pen = new Pen(Color.FromArgb(200, UiTheme.Gold), 1.5f) { DashStyle = DashStyle.Dash };
        foreach (var (a, b) in _guides)
        {
            g.DrawLine(pen, a, b);
        }
    }

    private void DrawBorder(Graphics g)
    {
        using var pen = new Pen(UiTheme.Line);
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private static string ShortName(CanvasItem item)
    {
        string raw = string.IsNullOrWhiteSpace(item.Config.MonitorId) ? item.Config.DeviceName : item.Config.MonitorId;
        int cut = raw.IndexOf(" (\\", StringComparison.Ordinal);
        if (cut > 0) raw = raw[..cut];
        cut = raw.IndexOf(" (HDMI", StringComparison.OrdinalIgnoreCase);
        if (cut > 0) raw = raw[..cut];
        return raw.Length > 28 ? raw[..27] + "…" : raw;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var hit = HitTest(e.Location);
        if (hit == null)
        {
            if (e.Button == MouseButtons.Left)
            {
                _selected = null;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
            return;
        }

        _selected = hit;
        SelectionChanged?.Invoke(this, EventArgs.Empty);

        if (e.Button == MouseButtons.Right)
        {
            ShowItemMenu(hit, e.Location);
            Invalidate();
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            _drag = hit;
            _dragFromShelf = !hit.Config.Enabled;
            _dragOffset = new Point(e.X - hit.DrawRect.X, e.Y - hit.DrawRect.Y);
            _viewFrozen = true;
            Capture = true;
        }

        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag != null)
        {
            _overShelf = e.X < ShelfWidth;
            if (_dragFromShelf)
            {
                MoveShelfDrag(e.Location);
            }
            else
            {
                MoveEnabledDrag(e.Location);
            }
            Invalidate();
            return;
        }

        var hit = HitTest(e.Location);
        bool changed = false;
        foreach (var item in _items)
        {
            bool hover = ReferenceEquals(item, hit);
            if (item.Hovered != hover)
            {
                item.Hovered = hover;
                changed = true;
            }
        }

        Cursor = hit == null ? Cursors.Default : Cursors.SizeAll;
        if (changed) Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == null)
        {
            Capture = false;
            return;
        }

        var dragged = _drag;
        bool fromShelf = _dragFromShelf;
        bool overShelf = e.X < ShelfWidth;
        _drag = null;
        _dragFromShelf = false;
        _overShelf = false;
        _guides.Clear();
        Capture = false;
        _viewFrozen = false;

        if (fromShelf)
        {
            if (!overShelf) EnableItem(dragged, e.Location);
            RecalcLayout();
        }
        else if (overShelf)
        {
            DisableItem(dragged);
        }
        else
        {
            RebaseToPrimary();
            UpdateRelativePositions();
            RecalcLayout();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        foreach (var item in _items) item.Hovered = false;
        if (_drag == null) Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.X < ShelfWidth) return;
        float old = Math.Max(0.0001f, _view.Scale);
        float factor = e.Delta > 0 ? 1.12f : 1f / 1.12f;
        float next = Math.Clamp(old * factor, 0.02f, 0.45f);
        float screenX = (e.X - _view.OffsetX) / old;
        float screenY = (e.Y - _view.OffsetY) / old;
        _view.Scale = next;
        _view.OffsetX = e.X - screenX * next;
        _view.OffsetY = e.Y - screenY * next;
        _viewFrozen = true;
        RecalcLayout();
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int step = e.Shift ? 8 : 48;
        bool moved = e.KeyCode switch
        {
            Keys.Left => NudgeSelected(-step, 0),
            Keys.Right => NudgeSelected(step, 0),
            Keys.Up => NudgeSelected(0, -step),
            Keys.Down => NudgeSelected(0, step),
            _ => false
        };
        if (moved) e.Handled = true;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_selected?.Config.Enabled == true)
        {
            SetPrimary(_selected.Config);
        }
    }

    private void MoveEnabledDrag(Point mouse)
    {
        if (_drag == null) return;
        var topLeftCanvas = new Point(mouse.X - _dragOffset.X, mouse.Y - _dragOffset.Y);
        var screen = CanvasToScreen(topLeftCanvas);
        Snap(_drag, ref screen);
        _drag.Config.X = screen.X;
        _drag.Config.Y = screen.Y;
        _drag.DrawRect = ScreenToCanvas(new Rectangle(_drag.Config.X, _drag.Config.Y, _drag.Config.Width, _drag.Config.Height));
    }

    private void MoveShelfDrag(Point mouse)
    {
        if (_drag == null) return;
        if (mouse.X >= ShelfWidth)
        {
            EnsureSize(_drag.Config);
            var sized = ScreenToCanvas(new Rectangle(0, 0, _drag.Config.Width, _drag.Config.Height));
            var topLeftCanvas = new Point(mouse.X - sized.Width / 2, mouse.Y - sized.Height / 2);
            var screen = CanvasToScreen(topLeftCanvas);
            Snap(_drag, ref screen);
            _drag.Config.X = screen.X;
            _drag.Config.Y = screen.Y;
            _drag.DrawRect = ScreenToCanvas(new Rectangle(_drag.Config.X, _drag.Config.Y, _drag.Config.Width, _drag.Config.Height));
        }
        else
        {
            int chipW = Math.Max(S(96), ShelfWidth - Pad * 2);
            int chipH = Math.Max(S(54), S(62));
            _drag.DrawRect = new Rectangle(mouse.X - chipW / 2, mouse.Y - chipH / 2, chipW, chipH);
            _guides.Clear();
        }
    }

    private void Snap(CanvasItem moving, ref Point screen)
    {
        if (!SnapEnabled)
        {
            _guides.Clear();
            return;
        }
        _guides.Clear();
        int w = Math.Max(1, moving.Config.Width);
        int h = Math.Max(1, moving.Config.Height);
        int x = screen.X;
        int y = screen.Y;
        int bestAbsX = SnapThreshold + 1;
        int bestAbsY = SnapThreshold + 1;
        int snapX = x;
        int snapY = y;

        foreach (var other in _items.Where(i => i.Config.Enabled && !ReferenceEquals(i, moving)))
        {
            int ox = other.Config.X;
            int oy = other.Config.Y;
            int ow = Math.Max(1, other.Config.Width);
            int oh = Math.Max(1, other.Config.Height);

            int[] xs = { ox - w, ox + ow, ox, ox + ow - w };
            foreach (int candidate in xs)
            {
                int d = Math.Abs(x - candidate);
                if (d < bestAbsX && d <= SnapThreshold)
                {
                    bestAbsX = d;
                    snapX = candidate;
                }
            }

            int[] ys = { oy - h, oy + oh, oy, oy + oh - h };
            foreach (int candidate in ys)
            {
                int d = Math.Abs(y - candidate);
                if (d < bestAbsY && d <= SnapThreshold)
                {
                    bestAbsY = d;
                    snapY = candidate;
                }
            }
        }

        if (bestAbsX <= SnapThreshold) x = snapX;
        if (bestAbsY <= SnapThreshold) y = snapY;
        screen = new Point(x, y);

        _guides.Clear();
        if (bestAbsX <= SnapThreshold)
        {
            int cx = (int)Math.Round(x * _view.Scale + _view.OffsetX);
            _guides.Add((new Point(cx, Pad), new Point(cx, Height - Pad)));
        }
        if (bestAbsY <= SnapThreshold)
        {
            int cy = (int)Math.Round(y * _view.Scale + _view.OffsetY);
            _guides.Add((new Point(ShelfWidth + Pad, cy), new Point(Width - Pad, cy)));
        }
    }

    private void EnableItem(CanvasItem item, Point? dropCanvas)
    {
        EnsureSize(item.Config);
        var others = _items.Where(i => i.Config.Enabled && !ReferenceEquals(i, item)).ToList();
        item.Config.Enabled = true;

        if (dropCanvas != null && dropCanvas.Value.X >= ShelfWidth)
        {
            var sized = ScreenToCanvas(new Rectangle(0, 0, item.Config.Width, item.Config.Height));
            var topLeft = new Point(dropCanvas.Value.X - sized.Width / 2, dropCanvas.Value.Y - sized.Height / 2);
            var screen = CanvasToScreen(topLeft);
            Snap(item, ref screen);
            item.Config.X = screen.X;
            item.Config.Y = screen.Y;
        }
        else if (others.Count > 0)
        {
            var right = others.OrderByDescending(i => i.Config.X + i.Config.Width).First();
            item.Config.X = right.Config.X + right.Config.Width;
            item.Config.Y = right.Config.Y;
        }
        else
        {
            item.Config.X = 0;
            item.Config.Y = 0;
        }

        if (!_items.Any(i => i.Config.Enabled && i.Config.IsPrimary))
        {
            item.Config.IsPrimary = true;
        }

        RebaseToPrimary();
        UpdateRelativePositions();
        RecalcLayout();
        Invalidate();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        StatusMessage?.Invoke(this, $"Included '{ShortName(item)}' in this profile.");
    }

    private void DisableItem(CanvasItem item)
    {
        if (_items.Count(i => i.Config.Enabled) <= 1)
        {
            StatusMessage?.Invoke(this, "Keep at least one display in the profile.");
            RecalcLayout();
            Invalidate();
            return;
        }

        item.Config.Enabled = false;
        if (item.Config.IsPrimary)
        {
            item.Config.IsPrimary = false;
            var next = _items.Where(i => i.Config.Enabled).OrderBy(i => i.Config.X).FirstOrDefault();
            if (next != null) next.Config.IsPrimary = true;
        }

        RebaseToPrimary();
        UpdateRelativePositions();
        RecalcLayout();
        Invalidate();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        StatusMessage?.Invoke(this, $"Removed '{ShortName(item)}' from this profile.");
    }

    private void RebaseToPrimary()
    {
        var primary = _items.FirstOrDefault(i => i.Config.Enabled && i.Config.IsPrimary)
                      ?? _items.FirstOrDefault(i => i.Config.Enabled);
        if (primary == null) return;
        primary.Config.IsPrimary = true;
        int dx = -primary.Config.X;
        int dy = -primary.Config.Y;
        if (dx == 0 && dy == 0) return;
        foreach (var item in _items.Where(i => i.Config.Enabled))
        {
            item.Config.X += dx;
            item.Config.Y += dy;
        }
    }

    private void UpdateRelativePositions()
    {
        var enabled = _items.Where(i => i.Config.Enabled).OrderBy(i => i.Config.X).ThenBy(i => i.Config.Y).ToList();
        if (enabled.Count == 1)
        {
            enabled[0].Config.RelativePosition = "Left";
        }
        else if (enabled.Count == 2)
        {
            enabled[0].Config.RelativePosition = "Left";
            enabled[1].Config.RelativePosition = "Right";
        }
        else if (enabled.Count >= 3)
        {
            enabled[0].Config.RelativePosition = "Left";
            for (int i = 1; i < enabled.Count - 1; i++)
            {
                enabled[i].Config.RelativePosition = "Center";
            }
            enabled[^1].Config.RelativePosition = "Right";
        }
    }

    private CanvasItem? HitTest(Point p)
    {
        if (_selected != null && _selected.DrawRect.Contains(p)) return _selected;
        foreach (var item in _items.Where(i => i.Config.Enabled).Reverse())
        {
            if (item.DrawRect.Contains(p)) return item;
        }
        foreach (var item in _items.Where(i => !i.Config.Enabled).Reverse())
        {
            if (item.DrawRect.Contains(p)) return item;
        }
        return null;
    }

    private void ShowItemMenu(CanvasItem item, Point location)
    {
        var menu = new ContextMenuStrip
        {
            BackColor = UiTheme.Panel,
            ForeColor = UiTheme.Text,
            ShowImageMargin = false
        };
        var primary = new ToolStripMenuItem(item.Config.IsPrimary ? "Main display" : "Set as main display")
        {
            Enabled = !item.Config.IsPrimary,
            Font = new Font("Segoe UI", 9.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel)
        };
        primary.Click += (_, _) => SetPrimary(item.Config);

        var toggle = new ToolStripMenuItem(item.Config.Enabled ? "Remove from this profile" : "Include in this profile")
        {
            Font = new Font("Segoe UI", 9.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel)
        };
        toggle.Click += (_, _) =>
        {
            if (item.Config.Enabled) DisableItem(item);
            else EnableItem(item, null);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };

        var identify = new ToolStripMenuItem("Identify on screen")
        {
            Font = new Font("Segoe UI", 9.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel)
        };
        identify.Click += (_, _) => IdentifyOverlays.Show(item.Config.DeviceName);

        menu.Items.Add(primary);
        menu.Items.Add(toggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(identify);
        menu.Show(this, location);
    }
}

internal static class IdentifyOverlays
{
    private static readonly List<Form> Open = new();
    private static System.Windows.Forms.Timer? _timer;

    public static void ShowAll() => Show(null);

    public static void Show(string? deviceName)
    {
        CloseAll();
        var attached = DisplayEngine.GetCurrentDisplays()
            .Where(d => d.IsAttached)
            .OrderBy(d => d.X)
            .ThenBy(d => d.Y)
            .ToList();

        int n = 1;
        foreach (var d in attached)
        {
            int number = n++;
            if (!string.IsNullOrWhiteSpace(deviceName) &&
                !string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var form = new IdentifyForm(number, d);
            Open.Add(form);
            form.Show();
        }

        _timer = new System.Windows.Forms.Timer { Interval = 2200 };
        _timer.Tick += (_, _) => CloseAll();
        _timer.Start();
    }

    private static void CloseAll()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        foreach (var form in Open)
        {
            try
            {
                form.Close();
                form.Dispose();
            }
            catch
            {
            }
        }
        Open.Clear();
    }

    private sealed class IdentifyForm : Form
    {
        private readonly int _number;
        private readonly string _caption;
        private readonly float _dpiScale;

        public IdentifyForm(int number, DisplayInfo display)
        {
            _number = number;
            _caption = string.IsNullOrWhiteSpace(display.MonitorId) ? display.DeviceName : display.MonitorId;
            _dpiScale = display.Width >= 3840 ? 2.0f : (display.Width >= 2560 ? 1.5f : 1.0f);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = UiTheme.Bg;
            Opacity = 0.94;
            int size = (int)Math.Round(180 * _dpiScale);
            Bounds = new Rectangle(
                display.X + (display.Width - size) / 2,
                display.Y + (display.Height - size) / 2,
                size, size);
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(UiTheme.Bg);
            int pad = (int)Math.Round(10 * _dpiScale);
            int radius = (int)Math.Round(18 * _dpiScale);
            using var fill = new SolidBrush(UiTheme.Card);
            using var pen = new Pen(UiTheme.Gold, 2.5f);
            g.FillRoundedRectangle(fill, pad, pad, Width - pad * 2, Height - pad * 2, radius);
            g.DrawRoundedRectangle(pen, pad, pad, Width - pad * 2 - 1, Height - pad * 2 - 1, radius);

            using var numFont = new Font("Segoe UI", 48f * _dpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
            using var capFont = new Font("Segoe UI", 11f * _dpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
            using var gold = new SolidBrush(UiTheme.Gold);
            using var text = new SolidBrush(UiTheme.Text);
            var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(_number.ToString(), numFont, gold, new RectangleF(0, pad + (int)(18 * _dpiScale), Width, (int)(75 * _dpiScale)), sf);
            g.DrawString(_caption, capFont, text, new RectangleF(pad, Height - pad - (int)(32 * _dpiScale), Width - pad * 2, (int)(28 * _dpiScale)), sf);
        }
    }
}
