using System.Drawing.Drawing2D;

namespace MonitorLayoutSwitcher;

/// <summary>
/// The tray menu, as a drawn popup rather than a ContextMenuStrip.
///
/// ToolStrip's layout engine sizes rows from the item's Text and its own font, so an
/// owner-drawn row that paints something larger simply gets clipped — the menu has no
/// idea what was drawn inside it. Rather than fight that, this measures its own
/// content and sizes the window to fit, which also means DPI scaling is one explicit
/// multiplier instead of an interaction between three layout systems.
///
/// It is the surface this app is actually used through, so it earns the same
/// treatment as the switch HUD: each layout shown as a diagram of the arrangement,
/// because the only question you ever have here is "which one is this?".
/// </summary>
internal sealed class TrayPopup : Form
{
    internal abstract class Entry
    {
        public Action? Invoke;
        public bool Enabled = true;
        public virtual bool Selectable => Enabled && Invoke != null;
    }

    internal sealed class LayoutEntry : Entry
    {
        public required DisplayProfile Profile;
        public bool IsLive;
    }

    internal sealed class CommandEntry : Entry
    {
        public required string Text;
        public string? Detail;
        public bool Emphasis;
    }

    internal sealed class SeparatorEntry : Entry
    {
        public override bool Selectable => false;
    }

    internal sealed class HeadingEntry : Entry
    {
        public required string Text;
        public override bool Selectable => false;
    }

    private static TrayPopup? _open;

    private readonly List<Entry> _entries;
    private readonly float _scale;
    private readonly List<Rectangle> _rows = new();
    private int _hot = -1;

    private const int PadX = 14;
    private const int PadY = 8;
    private const int LayoutRowH = 52;
    private const int CommandRowH = 34;
    private const int HeadingH = 26;
    private const int SeparatorH = 9;
    private const int MinWidth = 300;
    private const int MaxWidth = 460;

    public static void Show(IEnumerable<Entry> entries, Point anchor, float scale)
    {
        Dismiss();
        _open = new TrayPopup(entries.ToList(), scale);
        _open.PlaceNear(anchor);
        _open.Show();

        // Focus is what lets Deactivate close the popup and what makes the arrow
        // keys work. A tray menu that stays open after you click elsewhere is worse
        // than one that never opened.
        _open.Activate();
    }

    public static void Dismiss()
    {
        if (_open is { IsDisposed: false }) _open.Close();
        _open = null;
    }

    /// <summary>Builds the popup without showing it, so its rendering can be captured
    /// by the --screenshot-menu diagnostic.</summary>
    internal static TrayPopup CreateForCapture(IEnumerable<Entry> entries, float scale) =>
        new(entries.ToList(), scale);

    private TrayPopup(List<Entry> entries, float scale)
    {
        _entries = entries;
        _scale = scale <= 0 ? 1f : scale;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(21, 19, 17);
        DoubleBuffered = true;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);

        Size = Measure();
        Region = new Region(RoundedPath(new Rectangle(Point.Empty, Size), S(10)));
    }

    private int S(int v) => (int)Math.Round(v * _scale);

    private Font NameFont() => new("Segoe UI", 15f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font SubFont() => new("Segoe UI", 11.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font HotkeyFont() => new("Consolas", 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font CommandFont() => new("Segoe UI", 13.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font HeadingFont() => new("Segoe UI", 11f * _scale, FontStyle.Bold, GraphicsUnit.Pixel);

    /// <summary>
    /// Measures every row and sizes the window to the widest, so nothing can be
    /// clipped. This is the whole reason for not using a ContextMenuStrip.
    /// </summary>
    private Size Measure()
    {
        using var g = CreateGraphics();
        using var nameFont = NameFont();
        using var subFont = SubFont();
        using var hotkeyFont = HotkeyFont();
        using var commandFont = CommandFont();

        int glyphBlock = S(46) + S(14);   // diagram plus its gutter
        int widest = S(MinWidth);
        int height = S(PadY) * 2;

        foreach (var entry in _entries)
        {
            switch (entry)
            {
                case LayoutEntry layout:
                {
                    height += S(LayoutRowH);
                    int name = (int)Math.Ceiling(g.MeasureString(layout.Profile.Name, nameFont).Width);
                    int sub = (int)Math.Ceiling(g.MeasureString(SubtitleFor(layout.Profile), subFont).Width);
                    int hotkey = string.IsNullOrWhiteSpace(layout.Profile.Hotkey)
                        ? 0
                        : (int)Math.Ceiling(g.MeasureString(layout.Profile.Hotkey, hotkeyFont).Width) + S(18);
                    widest = Math.Max(widest, S(PadX) + glyphBlock + Math.Max(name, sub) + hotkey + S(PadX));
                    break;
                }
                case CommandEntry command:
                {
                    height += S(CommandRowH);
                    int text = (int)Math.Ceiling(g.MeasureString(command.Text, commandFont).Width);
                    int detail = string.IsNullOrWhiteSpace(command.Detail)
                        ? 0
                        : (int)Math.Ceiling(g.MeasureString(command.Detail, subFont).Width) + S(18);
                    widest = Math.Max(widest, S(PadX) + S(6) + text + detail + S(PadX));
                    break;
                }
                case HeadingEntry heading:
                {
                    height += S(HeadingH);
                    using var headingFont = HeadingFont();
                    widest = Math.Max(widest,
                        S(PadX) + S(6) + (int)Math.Ceiling(g.MeasureString(heading.Text, headingFont).Width) + S(PadX));
                    break;
                }
                default:
                    height += S(SeparatorH);
                    break;
            }
        }

        return new Size(Math.Min(widest, S(MaxWidth)), height);
    }

    private void PlaceNear(Point anchor)
    {
        // Anchored to the pointer and clamped to the working area, which puts it in
        // the right place whichever edge the taskbar lives on without special-casing.
        var screen = Screen.FromPoint(anchor).WorkingArea;
        int x = Math.Min(Math.Max(anchor.X - Width / 2, screen.Left + S(8)), screen.Right - Width - S(8));
        int y = anchor.Y - Height - S(12);
        if (y < screen.Top + S(8)) y = Math.Min(anchor.Y + S(12), screen.Bottom - Height - S(8));
        Location = new Point(x, y);
    }

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics);

    /// <summary>Paints the popup. Separated from OnPaint so the screenshot diagnostic
    /// can render it to a bitmap without showing a window.</summary>
    internal void Render(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(Color.FromArgb(21, 19, 17)))
            g.FillRectangle(bg, new Rectangle(Point.Empty, Size));
        using (var edge = new Pen(Color.FromArgb(70, 62, 48)))
            g.DrawPath(edge, RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), S(10)));

        _rows.Clear();
        int y = S(PadY);

        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            int h = entry switch
            {
                LayoutEntry => S(LayoutRowH),
                CommandEntry => S(CommandRowH),
                HeadingEntry => S(HeadingH),
                _ => S(SeparatorH)
            };

            var row = new Rectangle(0, y, Width, h);
            _rows.Add(row);

            switch (entry)
            {
                case LayoutEntry layout: DrawLayoutRow(g, row, layout, i == _hot); break;
                case CommandEntry command: DrawCommandRow(g, row, command, i == _hot); break;
                case HeadingEntry heading: DrawHeading(g, row, heading); break;
                default: DrawSeparator(g, row); break;
            }

            y += h;
        }
    }

    private void DrawLayoutRow(Graphics g, Rectangle row, LayoutEntry entry, bool hot)
    {
        if (hot) FillHot(g, row);

        // A single accent bar marks the live layout. The gold in this app was doing
        // far too many jobs; here it means exactly one thing.
        if (entry.IsLive)
        {
            using var live = new SolidBrush(UiTheme.Gold);
            g.FillRectangle(live, new Rectangle(S(4), row.Y + S(9), S(3), row.Height - S(18)));
        }

        bool on = entry.Enabled;
        LayoutGlyph.Draw(g,
            new RectangleF(S(PadX), row.Y + S(12), S(46), S(28)),
            entry.Profile.Displays,
            onColor: Color.FromArgb(on ? 170 : 70, UiTheme.Text),
            offColor: Color.FromArgb(90, UiTheme.Line),
            primaryColor: on ? UiTheme.GoldDim : Color.FromArgb(120, UiTheme.GoldDim));

        int textLeft = S(PadX) + S(46) + S(14);
        using var trim = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        string hotkey = entry.Profile.Hotkey ?? string.Empty;
        using var hotkeyFont = HotkeyFont();
        int hotkeyWidth = string.IsNullOrWhiteSpace(hotkey)
            ? 0
            : (int)Math.Ceiling(g.MeasureString(hotkey, hotkeyFont).Width) + S(18);

        int textWidth = Math.Max(S(40), Width - textLeft - hotkeyWidth - S(PadX));

        using (var nameFont = NameFont())
        using (var ink = new SolidBrush(on ? UiTheme.Text : UiTheme.Muted))
        {
            g.DrawString(entry.Profile.Name, nameFont, ink,
                new RectangleF(textLeft, row.Y + S(8), textWidth, S(20)), trim);
        }

        bool broken = entry.Profile.NeedsRecapture;
        using (var subFont = SubFont())
        using (var subInk = new SolidBrush(broken ? UiTheme.Danger : UiTheme.Muted))
        {
            g.DrawString(SubtitleFor(entry.Profile), subFont, subInk,
                new RectangleF(textLeft, row.Y + S(28), textWidth, S(18)), trim);
        }

        if (hotkeyWidth > 0)
        {
            using var hotkeyInk = new SolidBrush(Color.FromArgb(on ? 160 : 90, UiTheme.Muted));
            using var right = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(hotkey, hotkeyFont, hotkeyInk,
                new RectangleF(Width - hotkeyWidth, row.Y, hotkeyWidth - S(PadX), row.Height), right);
        }
    }

    private void DrawCommandRow(Graphics g, Rectangle row, CommandEntry entry, bool hot)
    {
        if (hot && entry.Enabled) FillHot(g, row);

        using var font = CommandFont();
        using var ink = new SolidBrush(entry.Enabled
            ? (entry.Emphasis ? UiTheme.Gold : UiTheme.Text)
            : UiTheme.Muted);
        using var left = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        g.DrawString(entry.Text, font, ink,
            new RectangleF(S(PadX) + S(6), row.Y, Width - S(PadX) * 2 - S(6), row.Height), left);

        if (!string.IsNullOrWhiteSpace(entry.Detail))
        {
            using var subFont = SubFont();
            using var subInk = new SolidBrush(UiTheme.Muted);
            using var right = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(entry.Detail, subFont, subInk,
                new RectangleF(0, row.Y, Width - S(PadX), row.Height), right);
        }
    }

    private void DrawHeading(Graphics g, Rectangle row, HeadingEntry entry)
    {
        using var font = HeadingFont();
        using var ink = new SolidBrush(UiTheme.Muted);
        using var left = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(entry.Text, font, ink,
            new RectangleF(S(PadX) + S(6), row.Y, Width - S(PadX) * 2, row.Height), left);
    }

    private void DrawSeparator(Graphics g, Rectangle row)
    {
        using var pen = new Pen(Color.FromArgb(45, 40, 32));
        int y = row.Y + row.Height / 2;
        g.DrawLine(pen, S(PadX), y, Width - S(PadX), y);
    }

    private void FillHot(Graphics g, Rectangle row)
    {
        using var brush = new SolidBrush(UiTheme.CardHover);
        g.FillRectangle(brush, new Rectangle(S(4), row.Y + S(2), Width - S(8), row.Height - S(4)));
    }

    /// <summary>The subtitle actually rendered for a layout — measurement and drawing
    /// must agree, or the row is sized for text it does not show.</summary>
    private static string SubtitleFor(DisplayProfile profile) =>
        profile.NeedsRecapture ? "Needs re-capture" : DescribeLayout(profile);

    private static string DescribeLayout(DisplayProfile profile)
    {
        int on = profile.Displays.Count(d => d.Enabled);
        int total = profile.Displays.Count;
        if (total == 0) return "Empty";
        if (on == total) return on == 1 ? "1 monitor" : $"All {on} monitors";
        return $"{on} of {total} monitors";
    }

    // ------------------------------------------------------------- interaction

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit != _hot)
        {
            _hot = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hot != -1) { _hot = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit >= 0)
        {
            var action = _entries[hit].Invoke;
            Close();
            action?.Invoke();
            return;
        }
        base.OnMouseUp(e);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _rows.Count && i < _entries.Count; i++)
        {
            if (_rows[i].Contains(p) && _entries[i].Selectable) return i;
        }
        return -1;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                Close();
                return true;

            case Keys.Down:
                MoveSelection(1);
                return true;

            case Keys.Up:
                MoveSelection(-1);
                return true;

            case Keys.Enter:
            case Keys.Space:
                if (_hot >= 0 && _hot < _entries.Count && _entries[_hot].Selectable)
                {
                    var action = _entries[_hot].Invoke;
                    Close();
                    action?.Invoke();
                }
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void MoveSelection(int direction)
    {
        if (_entries.Count == 0) return;
        int i = _hot;
        for (int step = 0; step < _entries.Count; step++)
        {
            i = (i + direction + _entries.Count) % _entries.Count;
            if (_entries[i].Selectable) { _hot = i; Invalidate(); return; }
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (ReferenceEquals(_open, this)) _open = null;
        base.OnFormClosed(e);
    }

    private static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
