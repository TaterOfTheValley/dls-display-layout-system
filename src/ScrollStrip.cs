using System.Drawing.Drawing2D;

namespace DLS;

/// <summary>
/// A row of controls that scrolls sideways without a scroll bar.
///
/// A native scroll bar on a strip this short eats a third of its height and shows up
/// as a bright system-coloured bar in an otherwise dark window. What the user needs
/// to know is just "there is more this way", and a carousel says that better: the row
/// fades out at an edge where it continues, and a round arrow button sits in the fade.
/// The wheel and a touchpad's sideways swipe both scroll it, and movement glides
/// rather than jumping, so it is clear which way things went.
///
/// The fade is painted by the children themselves (<see cref="PaintFade"/>) at the
/// end of their own Paint: WinForms has no real transparency between sibling
/// controls, so an overlay could not let the cards show through it.
/// </summary>
internal sealed class ScrollStrip : Panel
{
    private const int WM_MOUSEHWHEEL = 0x020E;

    private readonly Func<int, int> _scale;
    private readonly StripContent _content;
    private readonly ArrowButton _back;
    private readonly ArrowButton _next;
    private readonly System.Windows.Forms.Timer _glide = new() { Interval = 15 };
    private int _offset;
    private int _target;

    /// <param name="scale">Converts a 96-DPI length to device pixels.</param>
    public ScrollStrip(Func<int, int> scale)
    {
        _scale = scale;
        DoubleBuffered = true;

        _content = new StripContent(this)
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
            Location = Point.Empty
        };
        _content.SizeChanged += (_, _) => Settle();
        _content.ControlAdded += (_, e) => e.Control!.MouseWheel += OnChildWheel;
        _content.ControlRemoved += (_, e) => e.Control!.MouseWheel -= OnChildWheel;
        _content.MouseWheel += OnChildWheel;

        _back = new ArrowButton(this, -1) { Visible = false };
        _next = new ArrowButton(this, +1) { Visible = false };

        Controls.Add(_content);
        Controls.Add(_back);
        Controls.Add(_next);
        _back.BringToFront();
        _next.BringToFront();

        _glide.Tick += (_, _) => Step();
    }

    /// <summary>Where the strip's items go.</summary>
    public FlowLayoutPanel Content => _content;

    private int FadeWidth => _scale(84);
    private int ButtonWidth => _scale(36);
    private int MaxOffset => Math.Max(0, _content.Width - ClientSize.Width);

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        _content.BackColor = BackColor;
        _back.BackColor = BackColor;
        _next.BackColor = BackColor;
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Settle();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Wheel(-e.Delta);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEHWHEEL)
        {
            HorizontalWheel(ref m);
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Scrolls so <paramref name="child"/> is fully clear of the edge fades.</summary>
    public void ScrollIntoView(Control child)
    {
        if (MaxOffset == 0) return;

        int inset = FadeWidth;
        int left = child.Left - inset;
        int right = child.Right + inset - ClientSize.Width;
        if (left < _target) GlideTo(left);
        else if (right > _target) GlideTo(right);
    }

    /// <summary>
    /// Fades <paramref name="child"/> into the strip's background where it runs under
    /// an edge that has more beyond it. Call at the end of the child's Paint.
    /// </summary>
    public void PaintFade(Graphics g, Control child)
    {
        int childX = child.Left + _content.Left;
        int fade = FadeWidth;

        if (_offset > 0 && childX < fade)
            FillFade(g, new Rectangle(-childX, 0, fade, child.Height), reverse: false);

        if (_offset < MaxOffset && childX + child.Width > ClientSize.Width - fade)
            FillFade(g, new Rectangle(ClientSize.Width - fade - childX, 0, fade, child.Height), reverse: true);
    }

    private void FillFade(Graphics g, Rectangle area, bool reverse)
    {
        var solid = BackColor;
        var clear = Color.FromArgb(0, solid);

        // GDI+ wraps a gradient's first colour onto its last pixel unless the brush is
        // a little larger than the area it fills.
        var brushArea = Rectangle.Inflate(area, 1, 0);
        using var brush = new LinearGradientBrush(brushArea, solid, clear, LinearGradientMode.Horizontal);

        // Solid under the arrow button, so the button has no visible box, then easing
        // out across the rest.
        float button = (float)ButtonWidth / FadeWidth;
        float ease = button + (1 - button) * 0.35f;
        brush.InterpolationColors = reverse
            ? new ColorBlend
            {
                Colors = new[] { clear, Color.FromArgb(150, solid), solid, solid },
                Positions = new[] { 0f, 1 - ease, 1 - button, 1f }
            }
            : new ColorBlend
            {
                Colors = new[] { solid, solid, Color.FromArgb(150, solid), clear },
                Positions = new[] { 0f, button, ease, 1f }
            };
        g.FillRectangle(brush, area);
    }

    private void OnChildWheel(object? sender, MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        Wheel(-e.Delta);
    }

    /// <summary>Takes a sideways swipe or tilt from anything on the strip.</summary>
    internal void HorizontalWheel(ref Message m)
    {
        int delta = (short)((long)m.WParam >> 16);
        Wheel(delta);
        m.Result = (IntPtr)1;
    }

    /// <summary>A notch moves about one card; a touchpad's small deltas move in proportion.</summary>
    private void Wheel(int delta)
    {
        if (MaxOffset == 0 || delta == 0) return;
        GlideTo(_target + delta * _scale(160) / 120);
    }

    /// <summary>The arrow buttons move most of a view, keeping one card in sight for context.</summary>
    private void Page(int direction) =>
        GlideTo(_target + direction * Math.Max(_scale(120), ClientSize.Width - 2 * FadeWidth));

    public void ScrollAtDragEdge(Point screenPoint)
    {
        var point = PointToClient(screenPoint);
        if (point.Y < 0 || point.Y > Height) return;
        int direction = point.X < ButtonWidth ? -1 : point.X > Width - ButtonWidth ? 1 : 0;
        if (direction == 0) return;
        _glide.Stop();
        _target = Math.Clamp(_offset + direction * _scale(18), 0, MaxOffset);
        Apply(_target);
    }

    private void GlideTo(int offset)
    {
        _target = Math.Clamp(offset, 0, MaxOffset);
        if (_target != _offset) _glide.Start();
    }

    private void Step()
    {
        int gap = _target - _offset;
        if (gap == 0)
        {
            _glide.Stop();
            return;
        }

        // Ease out: cover a fraction of what is left each tick, but never stall short.
        int move = gap * 3 / 10;
        if (move == 0) move = Math.Sign(gap);
        Apply(_offset + move);
    }

    /// <summary>Keeps the offset valid after the strip or its content changes size.</summary>
    private void Settle()
    {
        int h = Math.Max(_content.Height, ClientSize.Height);
        int w = ButtonWidth;
        _back.SetBounds(0, 0, w, h);
        _next.SetBounds(ClientSize.Width - w, 0, w, h);

        _target = Math.Clamp(_target, 0, MaxOffset);
        Apply(Math.Clamp(_offset, 0, MaxOffset), force: true);
    }

    private void Apply(int offset, bool force = false)
    {
        if (offset == _offset && !force) return;
        _offset = offset;
        _content.Left = -_offset;

        _back.Visible = _offset > 0;
        _next.Visible = _offset < MaxOffset;

        // Moving the content copies its pixels across, fades and all; the fades have
        // to be redrawn where the edges now fall.
        _content.Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _glide.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>The row itself. Passes sideways swipes up, since WinForms has no
    /// event for them and the strip is the one that knows what to do.</summary>
    private sealed class StripContent : FlowLayoutPanel
    {
        private readonly ScrollStrip _owner;

        public StripContent(ScrollStrip owner)
        {
            _owner = owner;
            DoubleBuffered = true;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEHWHEEL)
            {
                _owner.HorizontalWheel(ref m);
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>A round "more this way" button that sits in the solid part of a fade.</summary>
    private sealed class ArrowButton : Control
    {
        private readonly ScrollStrip _owner;
        private readonly int _direction;
        private bool _hot;

        public ArrowButton(ScrollStrip owner, int direction)
        {
            _owner = owner;
            _direction = direction;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            AccessibleName = direction < 0 ? "Scroll left" : "Scroll right";
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) _owner.Page(_direction);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _owner.Wheel(-e.Delta);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEHWHEEL)
            {
                _owner.HorizontalWheel(ref m);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int d = _owner._scale(28);
            float cx = _direction < 0 ? d / 2f + 1 : Width - d / 2f - 1;
            float cy = Height / 2f;
            var circle = new RectangleF(cx - d / 2f, cy - d / 2f, d, d);

            using (var fill = new SolidBrush(_hot ? UiTheme.CardHover : UiTheme.Card))
                g.FillEllipse(fill, circle);
            using (var ring = new Pen(_hot ? UiTheme.Gold : UiTheme.Line))
                g.DrawEllipse(ring, circle);

            float arm = d * 0.16f;
            float nudge = _direction * arm * 0.35f;
            using var chevron = new Pen(_hot ? UiTheme.Gold : UiTheme.Muted, Math.Max(1.5f, _owner._scale(2)))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLines(chevron, new[]
            {
                new PointF(cx - _direction * arm + nudge, cy - arm * 1.6f),
                new PointF(cx + _direction * arm + nudge, cy),
                new PointF(cx - _direction * arm + nudge, cy + arm * 1.6f)
            });
        }
    }
}
