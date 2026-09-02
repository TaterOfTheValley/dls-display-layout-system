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

        float scale = DeviceDpi / 96f;
        Size = new Size((int)(500 * scale), (int)(304 * scale));
        Region = RoundedRegion(new Rectangle(Point.Empty, Size), (int)(14 * scale));

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

    private float UiScale => DeviceDpi / 96f;
    private int S(int v) => (int)Math.Round(v * UiScale);
    private float Remaining => Math.Max(0f, (float)(_expiresUtc - DateTime.UtcNow).TotalSeconds);

    private void PlaceOnPrimary()
    {
        // The primary display is the one this layout just made active, so it is the
        // one screen guaranteed to be showing something.
        var area = Screen.PrimaryScreen?.Bounds ?? Screen.AllScreens[0].Bounds;
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
        using (var nameFont = new Font("Segoe UI", 20f * UiScale, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var nameBrush = new SolidBrush(UiTheme.Text))
        {
            g.DrawString(_profileName, nameFont, nameBrush,
                new RectangleF(pad, S(148), Width - pad * 2, S(28)), centre);
        }

        DrawCountdown(g);
        DrawButtons(g, centre);
    }

    private void DrawCountdown(Graphics g)
    {
        int seconds = (int)Math.Ceiling(Remaining);
        float fraction = Math.Clamp(Remaining / _totalSeconds, 0f, 1f);

        using var font = new Font("Segoe UI", 13f * UiScale, FontStyle.Regular, GraphicsUnit.Pixel);
        string text = $"Reverting in {seconds}s unless you keep it";

        // Measured against a fixed-width stand-in, not the live string: the group is
        // centred, and measuring the real text would shift everything sideways the
        // moment the count drops from two digits to one.
        float textWidth = g.MeasureString("Reverting in 00s unless you keep it", font).Width;

        int d = S(20);
        int gap = S(10);
        int y = S(190);
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
            new RectangleF(ring.Right + gap, y, Width - (ring.Right + gap) - S(20), d), left);
    }

    private void DrawButtons(Graphics g, StringFormat centre)
    {
        int pad = S(28);
        int h = S(44);
        int y = Height - h - pad;
        int gap = S(12);
        int revertW = S(150);
        int keepW = Width - pad * 2 - revertW - gap;

        _keepRect = new Rectangle(pad, y, keepW, h);
        _revertRect = new Rectangle(_keepRect.Right + gap, y, revertW, h);

        using (var keepBg = new SolidBrush(_keepHot ? UiTheme.GoldHover : UiTheme.Gold))
            g.FillPath(keepBg, RoundedPath(_keepRect, S(7)));
        using (var keepInk = new SolidBrush(UiTheme.Ink))
        using (var f = new Font("Segoe UI", 14f * UiScale, FontStyle.Bold, GraphicsUnit.Pixel))
            g.DrawString("Keep this layout", f, keepInk, _keepRect, centre);

        using (var revertBg = new SolidBrush(_revertHot ? UiTheme.CardHover : UiTheme.Card))
            g.FillPath(revertBg, RoundedPath(_revertRect, S(7)));
        using (var border = new Pen(UiTheme.Line))
            g.DrawPath(border, RoundedPath(_revertRect, S(7)));
        using (var revertInk = new SolidBrush(UiTheme.Text))
        using (var f = new Font("Segoe UI", 14f * UiScale, FontStyle.Regular, GraphicsUnit.Pixel))
            g.DrawString("Revert now", f, revertInk, _revertRect, centre);
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
