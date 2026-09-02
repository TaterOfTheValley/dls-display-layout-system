using System.Drawing.Drawing2D;

namespace DLS;

/// <summary>
/// The application icon, drawn rather than shipped as a resource so it stays a
/// single-file build with no assets beside the exe.
///
/// Shared by the tray and by every window: the config window was showing the default
/// WinForms icon in its title bar, the taskbar and Alt-Tab, which is the kind of
/// detail that makes an otherwise finished app look unfinished.
/// </summary>
internal static class AppIcon
{
    private static Icon? _cached;

    /// <summary>The app icon at 32px. Cached — GetHicon allocates an unmanaged handle
    /// each call, and this is wanted in several places.</summary>
    public static Icon Shared => _cached ??= Create(32);

    public static Icon Create(int size)
    {
        using var bmp = new Bitmap(size, size);
        float k = size / 32f;

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float Sc(float v) => v * k;

            using var pen = new Pen(UiTheme.Gold, Math.Max(1f, Sc(2f)));
            using var body = new SolidBrush(Color.FromArgb(25, 22, 18));

            var panel = new RectangleF(Sc(3), Sc(5), Sc(26), Sc(17));

            // The screen itself gets a gradient, matching how monitors are drawn on
            // the canvas — the icon is a miniature of the thing the app manipulates.
            using (var path = Rounded(panel, Sc(2.5f)))
            using (var screen = new LinearGradientBrush(panel,
                       Color.FromArgb(248, 215, 135), Color.FromArgb(196, 152, 74),
                       LinearGradientMode.Vertical))
            {
                g.FillPath(body, path);
                var inner = RectangleF.Inflate(panel, -Sc(3.5f), -Sc(3.5f));
                using var innerPath = Rounded(inner, Sc(1.5f));
                g.FillPath(screen, innerPath);
                g.DrawPath(pen, path);
            }

            // Neck and foot, the silhouette that says "monitor" at 16px.
            using var stand = new SolidBrush(UiTheme.Gold);
            g.FillRectangle(stand, Sc(14.5f), Sc(22), Sc(3), Sc(4));
            g.FillRectangle(stand, Sc(9.5f), Sc(26), Sc(13), Sc(2.5f));
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
