using System.Drawing.Drawing2D;

namespace DLS;

/// <summary>
/// The overlay shown immediately after a layout switch.
///
/// Modelled on the volume and brightness OSDs rather than on a dialog box: centred,
/// borderless, showing the arrangement you just got as a diagram. One surface doing
/// two jobs — it tells you what happened, and it is the confirmation.
///
/// Doing nothing reverts. That inversion is the whole point: the way a display
/// switch fails is that the monitor you would click on is now black, so recovery
/// has to be the path that needs no input. The countdown ring makes the deadline
/// legible without a sentence of explanation.
/// </summary>
internal sealed class KeepLayoutDialog : Form
{
    private static KeepLayoutDialog? _open;

    /// <summary>Whether the overlay is currently up. Lets other surfaces (the editor's
    /// undo bar) stay quiet while this is the one place already asking the confirm/undo
    /// question — otherwise both prompt for the same decision at once.</summary>
    internal static bool IsOpen => _open is { IsDisposed: false };

    private readonly System.Windows.Forms.Timer _tick = new();
    private readonly DateTime _expiresUtc;
    private readonly int _totalSeconds;
    private readonly string _profileName;
    private readonly List<DisplayTargetConfig> _displays;
    private readonly Action _onKeep;
    private readonly Action _onRevert;
    private bool _settled;

    private Rectangle _keepRect;
    private Rectangle _revertRect;
    private bool _keepHot;
    private bool _revertHot;

    public static void Show(string profileName, IReadOnlyList<DisplayTargetConfig> displays,
                            int seconds, Action onKeep, Action onRevert)
    {
        Dismiss();
        _open = new KeepLayoutDialog(profileName, displays, seconds, onKeep, onRevert);
        _open.Show();
        _open.BringToFront();
    }

    /// <summary>Closes any open overlay without reverting. Named Dismiss so it cannot
    /// be confused with the inherited Form.Close.</summary>
    public static void Dismiss()
    {
        if (_open is { IsDisposed: false })
        {
            _open._settled = true;
            _open.Close();
        }
        _open = null;
    }

    private KeepLayoutDialog(string profileName, IReadOnlyList<DisplayTargetConfig> displays,
                             int seconds, Action onKeep, Action onRevert)
    {
        _profileName = profileName;
        _displays = displays.Select(d => d.Clone()).ToList();
        _totalSeconds = Math.Max(1, seconds);
        _expiresUtc = DateTime.UtcNow.AddSeconds(seconds);
        _onKeep = onKeep;
        _onRevert = onRevert;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(16, 15, 13);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);

        ApplyScale();

        _tick.Interval = 100;
        _tick.Tick += (_, _) =>
        {
            Invalidate();
            if (DateTime.UtcNow < _expiresUtc || _settled) return;

            // Timed out unanswered. Revert: the user may well be looking at a monitor
            // this layout switched off, unable to reach either button.
            _settled = true;
            _tick.Stop();
            _onRevert();
            Dismiss();
        };
        _tick.Start();

        PlaceOnPrimary();
    }

    /// <summary>The overlay's design size at 100% scale, with text at its normal size.
    /// Larger text makes it taller and, if the text needs it, wider.</summary>
    private const int DesignW = 500;

    // Fonts on the shared ramp. The profile name is a heading; the rest is body text,
    // a step up from the editor's because this is read at a glance from across a desk.
    private const float NamePx = UiType.Title;
    private const float CountdownPx = UiType.BodyLarge;
    private const float ButtonPx = UiType.BodyLarge;

    private const string KeepText = "Keep this layout";
    private const string RevertText = "Revert now";

    /// <summary>Measured against a fixed-width stand-in, not the live string: the group
    /// is centred, and measuring the real text would shift everything sideways the
    /// moment the count drops from two digits to one.</summary>
    private const string CountdownSample = "Reverting in 00s unless you keep it";

    private float _scale;

    private float UiScale => _scale > 0 ? _scale : DeviceDpi / 96f;
    private float TextScale => UiScale * UiScaling.TextScale;
    private int S(int v) => (int)Math.Round(v * UiScale);

    /// <summary>Where each part of the overlay goes, worked out for one scale.</summary>
    private readonly record struct OverlayLayout(
        int NameY, int NameH, int CountY, int CountH, int ButtonY, int ButtonH, int RevertW, Size Size);

    private OverlayLayout _layout;

    /// <summary>The countdown ring: its design size, or three-quarters of the line it
    /// sits beside once larger text would otherwise dwarf it.</summary>
    private static int RingSize(float scale, float textScale) =>
        Math.Max((int)Math.Round(20 * scale), UiType.LineHeight(CountdownPx * textScale) * 3 / 4);

    /// <summary>
    /// Lays the overlay out from the text it holds. The diagram at the top is a picture
    /// and keeps its size; below it the name, the countdown and the buttons each take
    /// the height their text needs at the Text size setting, and the overlay is as wide
    /// as the widest of them. At normal text this is exactly the 500×304 design.
    /// </summary>
    private static OverlayLayout Measure(float scale)
    {
        float text = scale * UiScaling.TextScale;
        int S(int v) => (int)Math.Round(v * scale);
        int Hold(int box, float px) => S(box) + UiType.LineHeight(px * text) - UiType.LineHeight(px * scale);

        int nameY = S(148);
        int nameH = Hold(28, NamePx);
        int countY = nameY + nameH + S(14);
        int countH = Hold(20, CountdownPx);
        int buttonY = countY + countH + S(22);
        int buttonH = Hold(44, ButtonPx);
        int height = buttonY + buttonH + S(28);

        using var g = Graphics.FromHwnd(IntPtr.Zero);
        using var countFont = UiType.Create(CountdownPx * text);
        using var buttonFont = UiType.Create(ButtonPx * text, FontStyle.Bold);
        int countW = (int)Math.Ceiling(g.MeasureString(CountdownSample, countFont).Width) + RingSize(scale, text) + S(10) + S(40);
        int keepW = (int)Math.Ceiling(g.MeasureString(KeepText, buttonFont).Width) + S(48);
        int revertW = Math.Max(S(150), (int)Math.Ceiling(g.MeasureString(RevertText, buttonFont).Width) + S(48));
        int width = Math.Max(S(DesignW), Math.Max(countW, keepW + revertW + S(12) + S(28) * 2));

        return new OverlayLayout(nameY, nameH, countY, countH, buttonY, buttonH, revertW, new Size(width, height));
    }

    /// <summary>
    /// Sizes the overlay for the screen it is about to appear on.
    ///
    /// Every one of its contents is painted from <see cref="UiScale"/>, so the window
    /// has to be re-sized whenever that changes or the drawing spills out of it. It
    /// changes more often than it looks: this overlay is created in the moments right
    /// after a layout switch, so the monitor it lands on — and that monitor's scale
    /// factor — is frequently not the one the process started on. Getting this wrong
    /// is what produced an overlay with its buttons painted off its own edge.
    ///
    /// The scale is also capped to what the screen can show. A 500×304 overlay at 225%
    /// is 1125×684, which does not fit on a display that is still at 1024×768 because
    /// its driver has not finished installing — and this is the one window that must
    /// be readable then, because it holds the way back. Large text makes it bigger
    /// still, so the cap is taken from the measured size rather than the design one.
    /// </summary>
    private void ApplyScale()
    {
        float scale = DeviceDpi / 96f;
        var layout = Measure(scale);

        var area = WindowFollow.PrimaryBounds();
        if (area.Width > 0 && area.Height > 0)
        {
            float fits = Math.Min(area.Width / (float)layout.Size.Width, area.Height / (float)layout.Size.Height);
            if (fits < 1f)
            {
                scale = Math.Max(scale * fits, Math.Min(0.6f, scale));
                layout = Measure(scale);
            }
        }

        if (_scale > 0 && Math.Abs(scale - _scale) < 0.001f && Size == layout.Size) return;

        _scale = scale;
        _layout = layout;
        Size = layout.Size;
        Region = RoundedRegion(new Rectangle(Point.Empty, Size), (int)Math.Round(14 * scale));
        PlaceOnPrimary();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // DeviceDpi only became meaningful just now, when Windows decided which
        // monitor this window is on.
        ApplyScale();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyScale();
    }
    private float Remaining => Math.Max(0f, (float)(_expiresUtc - DateTime.UtcNow).TotalSeconds);

    /// <summary>
    /// Centres the overlay on the primary display — the one this layout just made
    /// active, and so the one screen guaranteed to be showing something. Internal
    /// because <see cref="WindowFollow"/> calls it again once the desktop has settled:
    /// this runs immediately after the switch, when Windows may not have finished
    /// moving the primary yet.
    /// </summary>
    internal void PlaceOnPrimary()
    {
        var area = WindowFollow.PrimaryBounds();
        if (area.Width <= 0 || area.Height <= 0) return;

        Location = new Point(
            area.X + (area.Width - Width) / 2,
            area.Y + (int)(area.Height * 0.58) - Height / 2);
    }

    internal static KeepLayoutDialog CreateForCapture(string profileName,
                                                     IReadOnlyList<DisplayTargetConfig> displays, int seconds) =>
        new(profileName, displays, seconds, () => { }, () => { });

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics);

    /// <summary>Paints the overlay. Separated from OnPaint so the --screenshot-hud
    /// diagnostic can render it without showing a window.</summary>
    internal void Render(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var full = new Rectangle(Point.Empty, Size);
        using (var bg = new SolidBrush(Color.FromArgb(16, 15, 13))) g.FillRectangle(bg, full);
        using (var edge = new Pen(Color.FromArgb(70, 62, 48)))
            g.DrawPath(edge, RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), S(12)));

        int pad = S(28);

        // The arrangement, given the most space — it is the actual answer to
        // "what just happened".
        var glyph = new RectangleF(pad, S(28), Width - pad * 2, S(104));
        LayoutGlyph.Draw(g, glyph, _displays,
            onColor: Color.FromArgb(210, UiTheme.Text),
            offColor: Color.FromArgb(120, UiTheme.Line),
            primaryColor: UiTheme.Gold,
            cornerRadius: 3f);

        using var centre = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        // Pixel-sized, like every other font in this app. Points are already
        // DPI-relative, so multiplying a point size by the DPI scale sizes text twice
        // — which is what overflowed the countdown row at anything above 100%.
        using (var nameFont = UiType.CreateDisplay(NamePx * TextScale))
        using (var nameBrush = new SolidBrush(UiTheme.Text))
        {
            g.DrawString(_profileName, nameFont, nameBrush,
                new RectangleF(pad, _layout.NameY, Width - pad * 2, _layout.NameH), centre);
        }

        DrawCountdown(g);
        DrawButtons(g, centre);
    }

    private void DrawCountdown(Graphics g)
    {
        int seconds = (int)Math.Ceiling(Remaining);
        float fraction = Math.Clamp(Remaining / _totalSeconds, 0f, 1f);

        using var font = UiType.Create(CountdownPx * TextScale);
        string text = $"Reverting in {seconds}s unless you keep it";
        float textWidth = g.MeasureString(CountdownSample, font).Width;

        int d = RingSize(UiScale, TextScale);
        int gap = S(10);
        int y = _layout.CountY + (_layout.CountH - d) / 2;
        float groupWidth = d + gap + textWidth;
        float x = Math.Max(S(20), (Width - groupWidth) / 2f);

        var ring = new RectangleF(x, y, d, d);

        // A ring rather than a sentence: the deadline is a quantity, and an arc
        // shrinking to nothing says it without asking anyone to read.
        using (var track = new Pen(Color.FromArgb(45, 40, 32), S(3)))
            g.DrawEllipse(track, ring);
        using (var arc = new Pen(UiTheme.GoldDim, S(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(arc, ring, -90f, -360f * fraction);

        using var brush = new SolidBrush(UiTheme.Muted);
        using var left = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(text, font, brush,
            new RectangleF(ring.Right + gap, _layout.CountY, Width - (ring.Right + gap) - S(20), _layout.CountH), left);
    }

    private void DrawButtons(Graphics g, StringFormat centre)
    {
        int pad = S(28);
        int h = _layout.ButtonH;
        int y = _layout.ButtonY;
        int gap = S(12);
        int revertW = _layout.RevertW;
        int keepW = Width - pad * 2 - revertW - gap;

        _keepRect = new Rectangle(pad, y, keepW, h);
        _revertRect = new Rectangle(_keepRect.Right + gap, y, revertW, h);

        using (var keepBg = new SolidBrush(_keepHot ? UiTheme.GoldHover : UiTheme.Gold))
            g.FillPath(keepBg, RoundedPath(_keepRect, S(7)));
        using (var keepInk = new SolidBrush(UiTheme.Ink))
        using (var f = UiType.Create(ButtonPx * TextScale, FontStyle.Bold))
            g.DrawString(KeepText, f, keepInk, _keepRect, centre);

        using (var revertBg = new SolidBrush(_revertHot ? UiTheme.CardHover : UiTheme.Card))
            g.FillPath(revertBg, RoundedPath(_revertRect, S(7)));
        using (var border = new Pen(UiTheme.Line))
            g.DrawPath(border, RoundedPath(_revertRect, S(7)));
        using (var revertInk = new SolidBrush(UiTheme.Text))
        using (var f = UiType.Create(ButtonPx * TextScale))
            g.DrawString(RevertText, f, revertInk, _revertRect, centre);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool keep = _keepRect.Contains(e.Location);
        bool revert = _revertRect.Contains(e.Location);
        if (keep != _keepHot || revert != _revertHot)
        {
            _keepHot = keep;
            _revertHot = revert;
            Cursor = keep || revert ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_settled) return;

        if (_keepRect.Contains(e.Location))
        {
            _settled = true;
            _onKeep();
            Dismiss();
        }
        else if (_revertRect.Contains(e.Location))
        {
            _settled = true;
            _onRevert();
            Dismiss();
        }
        base.OnMouseDown(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_settled && (keyData == Keys.Enter || keyData == Keys.Space))
        {
            _settled = true;
            _onKeep();
            Dismiss();
            return true;
        }

        if (!_settled && keyData == Keys.Escape)
        {
            _settled = true;
            _onRevert();
            Dismiss();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tick.Stop();
        _tick.Dispose();
        if (ReferenceEquals(_open, this)) _open = null;
        base.OnFormClosed(e);
    }

    // Never steal focus from whatever the user is doing.
    protected override bool ShowWithoutActivation => true;

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

    private static Region RoundedRegion(Rectangle r, int radius) => new(RoundedPath(r, radius));
}
