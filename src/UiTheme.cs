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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    /// <summary>
    /// The scale factor of the monitor under <paramref name="point"/>, for a window
    /// about to be placed there before it has a handle to ask with.
    ///
    /// The desktop DC is no substitute: in a per-monitor-aware process it reports the
    /// primary monitor's scale as it was when the process started, whichever screen
    /// the question is about.
    /// </summary>
    public static float DpiScaleAt(Point point)
    {
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpi, out _) == 0 && dpi > 0)
        {
            return dpi / 96f;
        }
        return InitialDpi / 96f;
    }

    private static float _textScale;

    /// <summary>
    /// Windows' own "Text size" setting (Settings › Accessibility › Text size), as a
    /// multiplier from 1 to 2.25.
    ///
    /// It is separate from the display scale and applies on top of it: someone at
    /// 100% scaling with text at 150% wants the same boxes with bigger words in them.
    /// Nothing in WinForms reads it — the system fonts it would otherwise inherit are
    /// replaced throughout this app by pixel-sized ones — so it has to be applied here,
    /// to text and to the geometry that has to hold text, and not to anything else.
    /// </summary>
    public static float TextScale
    {
        get
        {
            if (_textScale <= 0) _textScale = ReadTextScale();
            return _textScale;
        }
    }

    /// <summary>Set by the <c>--text-scale</c> command-line switch, so every text size
    /// can be checked without changing the setting for the whole machine.</summary>
    public static float? TextScaleOverride { get; set; }

    /// <summary>
    /// Re-reads the setting. Windows broadcasts a settings change when it moves but
    /// says nothing about what moved, so this runs on any change; each window then
    /// compares <see cref="TextScale"/> with the value it was last laid out at, rather
    /// than trusting a changed/unchanged answer that whichever listener ran first
    /// would already have used up.
    /// </summary>
    public static void RefreshTextScale() => _textScale = ReadTextScale();

    private static float ReadTextScale()
    {
        if (TextScaleOverride is { } forced) return Math.Clamp(forced, 1f, 2.25f);

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            if (key?.GetValue("TextScaleFactor") is int percent) return Math.Clamp(percent, 100, 225) / 100f;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Unreadable is the same as unset: text at its normal size.
        }
        return 1f;
    }
}

/// <summary>
/// The app's type ramp: every text size, in design pixels at 100% scale.
///
/// Anchored to Windows' own sizes rather than chosen per control, so the app reads as
/// part of the system rather than a smaller thing sitting on it. Windows draws body
/// text — title bars, menus, message boxes, Explorer — at 9pt, which is 12px at
/// 100%; captions sit a step below and headings above. Before this ramp the editor's
/// body text was 9.5px, around 7pt, and every window looked a size smaller than
/// everything around it.
/// </summary>
internal static class UiType
{
    /// <summary>Uppercase labels, badges, footnotes.</summary>
    public const float Caption = 11f;

    /// <summary>Anything that is not a caption or a heading. Windows' 9pt.</summary>
    public const float Body = 12f;

    /// <summary>Text the eye should land on first within a group: a monitor's name,
    /// an edited value.</summary>
    public const float BodyLarge = 14f;

    public const float Subtitle = 16f;

    public const float Title = 22f;

    /// <summary>
    /// Segoe UI Variable where Windows 11 has it, since that is what its own UI is set
    /// in; plain Segoe UI everywhere else. Probed rather than assumed: GDI+ silently
    /// substitutes Microsoft Sans Serif for a family it cannot find.
    /// </summary>
    public static readonly string Family = Resolve("Segoe UI Variable Text", "Segoe UI");

    /// <summary>The optical-size cut for headings and big numerals, where the Text cut
    /// looks loose. Chosen by role rather than by pixel size, since a body line at 225%
    /// is as many pixels as a heading at 100%.</summary>
    public static readonly string DisplayFamily = Resolve("Segoe UI Variable Display", Family);

    public const string MonoFamily = "Consolas";

    /// <summary>A UI font at an already-scaled pixel size.</summary>
    public static Font Create(float px, FontStyle style = FontStyle.Regular) =>
        new(Family, Math.Max(1f, px), style, GraphicsUnit.Pixel);

    /// <summary>A heading or display-numeral font at an already-scaled pixel size.</summary>
    public static Font CreateDisplay(float px, FontStyle style = FontStyle.Regular) =>
        new(DisplayFamily, Math.Max(1f, px), style, GraphicsUnit.Pixel);

    public static Font CreateMono(float px, FontStyle style = FontStyle.Regular) =>
        new(MonoFamily, Math.Max(1f, px), style, GraphicsUnit.Pixel);

    /// <summary>
    /// The height one line of text at <paramref name="px"/> needs, for sizing the box
    /// around it. Everything that holds text is at least this tall, so a larger text
    /// setting grows the box instead of cutting off the bottom of the letters.
    /// </summary>
    public static int LineHeight(float px, FontStyle style = FontStyle.Regular)
    {
        // Asked for on every resize, with a handful of distinct sizes per session, so
        // it is worth not creating a GDI font each time.
        var key = ((int)Math.Round(px * 10), style);
        if (!LineHeights.TryGetValue(key, out int height))
        {
            using var font = Create(px, style);
            height = font.Height;
            LineHeights[key] = height;
        }
        return height;
    }

    private static readonly Dictionary<(int, FontStyle), int> LineHeights = new();

    private static string Resolve(string preferred, string fallback)
    {
        try
        {
            using var probe = new Font(preferred, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
            return probe.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase) ? preferred : fallback;
        }
        catch (ArgumentException)
        {
            return fallback;
        }
    }
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

    /// <param name="textScale">Scales the button's default height. A button holds a
    /// label, so it is the text scale — display scale times Windows' Text size — and
    /// not the display scale alone.</param>
    /// <param name="fontPx">The font size to assign — from
    /// <see cref="UiScaling.ControlFontPx"/>, not from textScale, because WinForms
    /// adjusts a control's font behind our back and its geometry not at all.</param>
    public static Button MakeButton(string text, bool primary, float textScale, float fontPx)
    {
        var btn = new Button
        {
            Text = text,
            UseMnemonic = false,
            BackColor = primary ? Gold : Card,
            ForeColor = primary ? Ink : Text,
            FlatStyle = FlatStyle.Flat,
            Font = UiType.Create(fontPx, primary ? FontStyle.Bold : FontStyle.Regular),
            Cursor = Cursors.Hand,
            Height = (int)Math.Round(36 * textScale)
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
        Font = UiType.Create(fontPx, FontStyle.Bold),
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft
    };
}
