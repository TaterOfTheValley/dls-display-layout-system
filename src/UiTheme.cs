namespace DLS;

/// <summary>
/// The one piece of DPI arithmetic that is not this app's own.
///
/// WinForms adjusts a control's Font — once, when its handle is first created — by
/// the ratio between the DPI the window actually landed on and the DPI the *process*
/// started at. It does this whatever AutoScaleMode says, and it does not touch the
/// control's Size or Location. That asymmetry is the bug it causes here: this app
/// scales geometry itself, so a window that opens on a monitor scaled differently
/// from the primary ends up with correctly-sized boxes holding wrongly-sized text.
/// On a machine whose primary is at 225%, a window opened on a 100% screen came up
/// with every label more than twice the size of the control it sat in — labels cut
/// off mid-word, buttons showing "Duplica".
///
/// Rather than fight it, feed it: a font assigned at
/// <see cref="ControlFontPx"/> is exactly the size that comes back out the far side
/// of that adjustment.
/// </summary>
internal static class UiScaling
{
    private static int _initialDpi;

    /// <summary>
    /// The DPI every Control believes it is at until its handle says otherwise — the
    /// primary monitor's scale when the process started. A freshly constructed,
    /// never-parented control reports it, and it is fixed for the process lifetime,
    /// so reading it once is enough.
    /// </summary>
    public static int InitialDpi
    {
        get
        {
            if (_initialDpi <= 0)
            {
                using var probe = new Control();
                _initialDpi = probe.DeviceDpi > 0 ? probe.DeviceDpi : 96;
            }
            return _initialDpi;
        }
    }

    /// <summary>
    /// The size to assign to a <see cref="Control"/>'s Font so that it ends up
    /// rendering at <paramref name="designPx"/> pixels times <paramref name="uiScale"/>,
    /// once WinForms has applied its own adjustment.
    ///
    /// Only for fonts that get assigned to a control. A font created inside a Paint
    /// handler is never adjusted by anyone, so those are sized directly and must not
    /// go through here.
    /// </summary>
    public static float ControlFontPx(float designPx, float uiScale, int deviceDpi) =>
        designPx * uiScale * InitialDpi / Math.Max(96, deviceDpi);
}

internal static class UiTheme
{
    public static readonly Color Bg = Color.FromArgb(14, 13, 11);
    public static readonly Color Panel = Color.FromArgb(22, 20, 17);
    public static readonly Color Card = Color.FromArgb(28, 25, 21);
    public static readonly Color CardActive = Color.FromArgb(46, 38, 25);
    public static readonly Color CardHover = Color.FromArgb(38, 33, 27);
    public static readonly Color Input = Color.FromArgb(18, 16, 14);
    public static readonly Color Gold = Color.FromArgb(232, 189, 99);
    public static readonly Color GoldHover = Color.FromArgb(248, 215, 135);
    public static readonly Color GoldDim = Color.FromArgb(141, 109, 50);
    public static readonly Color Muted = Color.FromArgb(170, 160, 140);
    public static readonly Color Line = Color.FromArgb(58, 50, 40);
    public static readonly Color Text = Color.FromArgb(245, 240, 230);
    public static readonly Color Ink = Color.FromArgb(14, 13, 11);
    public static readonly Color Danger = Color.FromArgb(232, 131, 117);
    public static readonly Color Ok = Color.FromArgb(139, 213, 160);

    /// <param name="dpiScale">Scales the button's geometry.</param>
    /// <param name="fontPx">The font size to assign — from
    /// <see cref="UiScaling.ControlFontPx"/>, not from dpiScale, because WinForms
    /// adjusts a control's font behind our back and its geometry not at all.</param>
    public static Button MakeButton(string text, bool primary, float dpiScale, float fontPx)
    {
        var btn = new Button
        {
            Text = text,
            UseMnemonic = false,
            BackColor = primary ? Gold : Card,
            ForeColor = primary ? Ink : Text,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", fontPx, primary ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel),
            Cursor = Cursors.Hand,
            Height = (int)Math.Round(36 * dpiScale)
        };
        btn.FlatAppearance.BorderSize = primary ? 0 : 1;
        btn.FlatAppearance.BorderColor = Line;
        btn.FlatAppearance.MouseOverBackColor = primary ? GoldHover : CardHover;
        btn.EnabledChanged += (_, _) =>
        {
            btn.BackColor = btn.Enabled ? (primary ? Gold : Card) : Card;
            btn.ForeColor = btn.Enabled ? (primary ? Ink : Text) : Muted;
        };
        return btn;
    }

    public static Label MakeEyebrow(string text, float fontPx) => new()
    {
        Text = text,
        ForeColor = Muted,
        Font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel),
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft
    };
}
