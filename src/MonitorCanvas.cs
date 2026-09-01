using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MonitorLayoutSwitcher;

internal sealed class MonitorCanvas : Control
{
    private sealed class CanvasItem
    {
        public DisplayTargetConfig Config { get; init; } = null!;
        public int Number { get; set; }
        public bool Present { get; set; }
        public Rectangle DrawRect;
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

    private const int ShelfHeightDesign = 108;
    private const int SnapThreshold = 72;

    public event EventHandler? LayoutChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler<string>? StatusMessage;

    public DisplayTargetConfig? SelectedConfig => _selected?.Config;
    public bool SnapEnabled { get; set; } = true;

    private float DpiScale => DeviceDpi / 96f;
    private int S(int val) => (int)Math.Round(val * DpiScale);

    public MonitorCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        BackColor = UiTheme.Input;
        TabStop = true;
    }

    private int ShelfHeight => Math.Max(S(96), S(ShelfHeightDesign));
    private int Pad => Math.Max(S(16), S(18));

    public void Bind(DisplayProfile? profile)
    {
        _profile = profile;
        _hardware = DisplayEngine.GetCurrentDisplays();
        MergeHardware();
        RebuildItems();
        _selected = _items.FirstOrDefault(i => i.Config.Enabled) ?? _items.FirstOrDefault();
        _viewFrozen = false;
        RecalcLayout();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
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

        foreach (var hw in _hardware)
        {
            var match = FindMatch(hw);
            if (match != null)
            {
                match.DeviceName = hw.DeviceName;
                if (string.IsNullOrWhiteSpace(match.MonitorId)) match.MonitorId = hw.MonitorId;
                if (string.IsNullOrWhiteSpace(match.HardwareId)) match.HardwareId = hw.HardwareId;
                if (match.Width <= 0) match.Width = hw.Width;
                if (match.Height <= 0) match.Height = hw.Height;
                if (match.RefreshRate <= 0) match.RefreshRate = hw.RefreshRate;
                continue;
            }

            _profile.Displays.Add(new DisplayTargetConfig
            {
                DeviceName = hw.DeviceName,
                MonitorId = hw.MonitorId,
                HardwareId = hw.HardwareId,
                RelativePosition = hw.RelativePosition,
                Enabled = false,
                X = hw.X,
                Y = hw.Y,
                Width = hw.Width,
                Height = hw.Height,
                RefreshRate = hw.RefreshRate,
                IsPrimary = false
            });
        }
    }

    private DisplayTargetConfig? FindMatch(DisplayInfo hw)
    {
        if (_profile == null) return null;

        var stable = _profile.Displays.FirstOrDefault(d =>
            !string.IsNullOrWhiteSpace(d.HardwareId) &&
            DisplayEngine.SameHardwareIdentity(d.HardwareId, hw.HardwareId));
        if (stable != null) return stable;

        var byName = _profile.Displays.FirstOrDefault(d =>
            string.Equals(d.DeviceName, hw.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (byName != null &&
            (string.IsNullOrWhiteSpace(byName.MonitorId) ||
             string.IsNullOrWhiteSpace(hw.MonitorId) ||
             DisplayEngine.SameMonitorDescription(byName.MonitorId, hw.MonitorId)))
        {
            return byName;
        }

        if (!string.IsNullOrWhiteSpace(hw.MonitorId))
        {
            var descriptionMatches = _profile.Displays.Where(d =>
                DisplayEngine.SameMonitorDescription(d.MonitorId, hw.MonitorId)).ToList();
            if (descriptionMatches.Count == 1) return descriptionMatches[0];
        }

        return null;
    }

    private void RebuildItems()
    {
        _items.Clear();
        if (_profile == null) return;

        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int n = 1;
        foreach (var hw in _hardware.OrderBy(h => h.X).ThenBy(h => h.Y))
        {
            numbers[hw.DeviceName] = n++;
        }

        foreach (var cfg in _profile.Displays)
        {
            EnsureSize(cfg);
            var hw = FindHardware(cfg);
            if (!numbers.TryGetValue(cfg.DeviceName, out int num))
            {
                num = n++;
            }

            _items.Add(new CanvasItem
            {
                Config = cfg,
                Number = num,
                Present = hw != null
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

    private DisplayInfo? FindHardware(DisplayTargetConfig cfg)
    {
        var stable = !string.IsNullOrWhiteSpace(cfg.HardwareId)
            ? _hardware.FirstOrDefault(h => DisplayEngine.SameHardwareIdentity(h.HardwareId, cfg.HardwareId))
            : null;
        if (stable != null) return stable;

        var byName = _hardware.FirstOrDefault(h => string.Equals(h.DeviceName, cfg.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (byName != null &&
            (string.IsNullOrWhiteSpace(cfg.MonitorId) ||
             string.IsNullOrWhiteSpace(byName.MonitorId) ||
             DisplayEngine.SameMonitorDescription(cfg.MonitorId, byName.MonitorId)))
        {
            return byName;
        }

        if (!string.IsNullOrWhiteSpace(cfg.MonitorId))
        {
            var descriptionMatches = _hardware.Where(h =>
                DisplayEngine.SameMonitorDescription(h.MonitorId, cfg.MonitorId)).ToList();
            if (descriptionMatches.Count == 1) return descriptionMatches[0];
        }

        return null;
    }


    private void RecalcLayout()
    {
        if (!_viewFrozen) _view = ComputeView();

        int shelf = ShelfHeight;
        int chipW = Math.Max(S(120), S(135));
        int chipH = Math.Max(S(54), S(62));
        int x = Pad;
        int y = Height - shelf + S(32);

        foreach (var item in _items)
        {
            if (item.Config.Enabled)
            {
                item.DrawRect = ScreenToCanvas(new Rectangle(item.Config.X, item.Config.Y, item.Config.Width, item.Config.Height));
            }
            else if (!ReferenceEquals(item, _drag))
            {
                item.DrawRect = new Rectangle(x, y, chipW, chipH);
                x += chipW + Pad / 2;
            }
        }
    }

    private ViewTransform ComputeView()
    {
        var enabled = _items.Where(i => i.Config.Enabled).ToList();
        int shelf = ShelfHeight;
        int availW = Math.Max(S(40), Width - Pad * 2);
        int availH = Math.Max(S(40), Height - shelf - Pad * 2);

        if (enabled.Count == 0)
        {
            return new ViewTransform
            {
                Scale = Math.Min(availW / 3840f, availH / 2160f) * 0.7f,
                OffsetX = Width / 2f,
                OffsetY = (Height - shelf) / 2f
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
            OffsetX = Pad + (availW - usedW) / 2f - minX * scale,
            OffsetY = Pad + (availH - usedH) / 2f - minY * scale
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

        int shelfTop = Height - ShelfHeight;
        DrawGrid(g, shelfTop);
        DrawEnabled(g);
        DrawGuides(g);
        DrawShelf(g, shelfTop);
        DrawUnused(g);
        DrawBorder(g);
    }

    private void DrawGrid(Graphics g, int shelfTop)
    {
        int step = Math.Max(S(16), S(22));
        using var brush = new SolidBrush(Color.FromArgb(28, UiTheme.Gold));
        for (int x = step / 2; x < Width; x += step)
        {
            for (int y = step / 2; y < shelfTop; y += step)
            {
                g.FillRectangle(brush, x, y, 1, 1);
            }
        }

        if (_items.All(i => !i.Config.Enabled) && _drag == null)
        {
            using var font = new Font("Segoe UI", 10f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
            using var muted = new SolidBrush(UiTheme.Muted);
            var text = "Drag a display up here to include it in this profile";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, muted, (Width - size.Width) / 2f, (shelfTop - size.Height) / 2f);
        }
    }

    private void DrawEnabled(Graphics g)
    {
        foreach (var item in _items.Where(i => i.Config.Enabled).OrderBy(i => ReferenceEquals(i, _selected) ? 1 : 0))
        {
            DrawMonitorCard(g, item, item.DrawRect, true);
        }
    }

    private void DrawUnused(Graphics g)
    {
        foreach (var item in _items.Where(i => !i.Config.Enabled))
        {
            DrawMonitorCard(g, item, item.DrawRect, false);
        }
    }

    private void DrawMonitorCard(Graphics g, CanvasItem item, Rectangle r, bool enabled)
    {
        if (r.Width < S(8) || r.Height < S(8)) return;
        bool selected = ReferenceEquals(item, _selected);
        int radius = Math.Max(S(6), S(8));

        Color fill = selected ? UiTheme.CardActive : item.Hovered ? UiTheme.CardHover : UiTheme.Card;
        if (!enabled) fill = Color.FromArgb(enabled ? 255 : 200, fill);

        using (var brush = new SolidBrush(fill))
        {
            g.FillRoundedRectangle(brush, r.X, r.Y, r.Width, r.Height, radius);
        }

        Color border = selected ? UiTheme.Gold : item.Hovered ? UiTheme.GoldDim : UiTheme.Line;
        using (var pen = new Pen(border, selected ? 2.2f : 1f))
        {
            g.DrawRoundedRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1, radius);
        }

        if (enabled)
        {
            int standW = Math.Max(S(16), r.Width / 3);
            int standH = Math.Max(S(3), S(4));
            using var stand = new SolidBrush(Color.FromArgb(selected ? 180 : 90, UiTheme.Gold));
            g.FillRectangle(stand, r.X + (r.Width - standW) / 2, r.Bottom - 1, standW, standH);
        }

        using var gold = new SolidBrush(enabled ? UiTheme.Gold : UiTheme.Muted);
        using var text = new SolidBrush(enabled ? UiTheme.Text : UiTheme.Muted);
        using var muted = new SolidBrush(UiTheme.Muted);
        using var numFont = new Font("Segoe UI", (enabled && r.Height > S(70) ? 22f : 14f) * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var nameFont = new Font("Segoe UI", 9.5f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI", 8f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);

        var sf = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        float numY = r.Y + Math.Max(S(4), r.Height * 0.08f);
        g.DrawString(item.Number.ToString(), numFont, gold, new RectangleF(r.X, numY, r.Width, numFont.Height + S(2)), sf);

        string title = enabled
            ? (string.IsNullOrWhiteSpace(item.Config.RelativePosition) ? ShortName(item) : item.Config.RelativePosition)
            : ShortName(item);
        float titleY = numY + numFont.Height;
        if (titleY + S(16) < r.Bottom - S(4))
        {
            g.DrawString(title, nameFont, text, new RectangleF(r.X + S(6), titleY, r.Width - S(12), S(18)), sf);
        }

        string sub = $"{item.Config.Width} × {item.Config.Height}";
        float subY = titleY + S(18);
        if (enabled && subY + S(14) < r.Bottom - S(4))
        {
            g.DrawString(sub, subFont, muted, new RectangleF(r.X + S(6), subY, r.Width - S(12), S(16)), sf);
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

        if (item.Present)
        {
            using var ok = new SolidBrush(Color.FromArgb(160, UiTheme.Ok));
            using var okFont = new Font("Segoe UI", 7f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("Connected", okFont, ok, new RectangleF(r.X + S(4), r.Bottom - S(16), r.Width - S(8), S(14)), sf);
        }
        else
        {
            using var warn = new SolidBrush(UiTheme.Danger);
            using var warnFont = new Font("Segoe UI", 7.5f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("Not connected", warnFont, warn, new RectangleF(r.X + S(4), r.Bottom - S(18), r.Width - S(8), S(16)), sf);
        }
    }

    private void DrawShelf(Graphics g, int shelfTop)
    {
        using var fill = new SolidBrush(_overShelf ? Color.FromArgb(36, 22, 20) : UiTheme.Panel);
        g.FillRectangle(fill, 0, shelfTop, Width, ShelfHeight);
        using var pen = new Pen(_overShelf ? UiTheme.Danger : UiTheme.Line);
        g.DrawLine(pen, 0, shelfTop, Width, shelfTop);

        using var font = new Font("Segoe UI", 8.5f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(_overShelf ? UiTheme.Danger : UiTheme.Muted);
        string label = _overShelf
            ? "DROP TO REMOVE FROM THIS PROFILE"
            : "UNUSED IN THIS PROFILE  ·  drag onto the canvas to include";
        g.DrawString(label, font, brush, Pad, shelfTop + S(8));
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
            _overShelf = e.Y >= Height - ShelfHeight;
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
        bool overShelf = e.Y >= Height - ShelfHeight;
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
        if (e.Y >= Height - ShelfHeight) return;
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
        if (mouse.Y < Height - ShelfHeight)
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
            int chipW = Math.Max(S(120), S(135));
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
            _guides.Add((new Point(cx, Pad), new Point(cx, Height - ShelfHeight - Pad)));
        }
        if (bestAbsY <= SnapThreshold)
        {
            int cy = (int)Math.Round(y * _view.Scale + _view.OffsetY);
            _guides.Add((new Point(Pad, cy), new Point(Width - Pad, cy)));
        }
    }

    private void EnableItem(CanvasItem item, Point? dropCanvas)
    {
        EnsureSize(item.Config);
        var others = _items.Where(i => i.Config.Enabled && !ReferenceEquals(i, item)).ToList();
        item.Config.Enabled = true;

        if (dropCanvas != null && dropCanvas.Value.Y < Height - ShelfHeight)
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
            _caption = string.IsNullOrWhiteSpace(display.RelativePosition) ? display.DeviceName : display.RelativePosition;
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
