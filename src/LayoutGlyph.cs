using System.Drawing.Drawing2D;

namespace DLS;

/// <summary>
/// Draws a monitor arrangement as a small diagram.
///
/// This is the app's most useful visual idea, and it was previously stuck on the
/// profile cards in a window you open twice and then never again. A layout is
/// fundamentally a picture — you recognise "the one with only the left screen"
/// instantly from its shape and never from its name — so the same drawing now
/// backs the tray menu and the switch HUD, which is where you actually live.
/// </summary>
internal static class LayoutGlyph
{
    /// <summary>
    /// Renders <paramref name="displays"/> scaled to fit <paramref name="bounds"/>,
    /// preserving the real aspect ratio and relative positions.
    ///
    /// Monitors that are off are drawn as faint outlines rather than omitted: the
    /// gap they leave is the whole point of a layout like "left only", and dropping
    /// them makes a one-monitor layout look identical to a one-monitor desk.
    /// </summary>
    public static void Draw(
        Graphics g,
        RectangleF bounds,
        IReadOnlyList<DisplayTargetConfig> displays,
        Color onColor,
        Color offColor,
        Color primaryColor,
        float cornerRadius = 2f)
    {
        if (displays.Count == 0 || bounds.Width <= 2 || bounds.Height <= 2) return;

        // The frame covers every monitor, on or off, so enabling one doesn't rescale
        // and reposition the whole diagram.
        float minX = displays.Min(d => (float)d.X);
        float minY = displays.Min(d => (float)d.Y);
        float maxX = displays.Max(d => (float)(d.X + Math.Max(1, d.Width)));
        float maxY = displays.Max(d => (float)(d.Y + Math.Max(1, d.Height)));

        float spanX = Math.Max(1, maxX - minX);
        float spanY = Math.Max(1, maxY - minY);

        float scale = Math.Min(bounds.Width / spanX, bounds.Height / spanY);
        float drawnW = spanX * scale;
        float drawnH = spanY * scale;
        float originX = bounds.X + (bounds.Width - drawnW) / 2f;
        float originY = bounds.Y + (bounds.Height - drawnH) / 2f;

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Off first, so an enabled monitor overlapping a disabled slot wins.
        foreach (var d in displays.OrderBy(d => d.Enabled))
        {
            var r = new RectangleF(
                originX + (d.X - minX) * scale,
                originY + (d.Y - minY) * scale,
                Math.Max(2f, Math.Max(1, d.Width) * scale),
                Math.Max(2f, Math.Max(1, d.Height) * scale));

            // A hairline gap so adjacent monitors read as separate panels.
            r.Inflate(-0.75f, -0.75f);
            if (r.Width < 1.5f || r.Height < 1.5f) continue;

            using var path = Rounded(r, Math.Min(cornerRadius, Math.Min(r.Width, r.Height) / 3f));

            if (d.Enabled)
            {
                using var fill = new SolidBrush(d.IsPrimary ? primaryColor : onColor);
                g.FillPath(fill, path);
            }
            else
            {
                using var pen = new Pen(offColor, 1f);
                g.DrawPath(pen, path);
            }
        }

        g.SmoothingMode = oldMode;
    }

    /// <summary>Convenience overload for live hardware rather than a saved layout.</summary>
    public static void Draw(
        Graphics g,
        RectangleF bounds,
        IReadOnlyList<DisplayInfo> displays,
        Color onColor,
        Color offColor,
        Color primaryColor,
        float cornerRadius = 2f)
    {
        var mapped = displays.Select(d => new DisplayTargetConfig
        {
            X = d.X,
            Y = d.Y,
            Width = d.Width,
            Height = d.Height,
            Enabled = d.IsAttached,
            IsPrimary = d.IsPrimary
        }).ToList();

        Draw(g, bounds, mapped, onColor, offColor, primaryColor, cornerRadius);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }

        float d = radius * 2f;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
