namespace DLS;

/// <summary>Finds the nearest edge or center alignment independently on each axis.</summary>
internal static class MonitorAlignment
{
    internal readonly record struct Guide(bool Vertical, double Coordinate, bool Center);
    internal sealed record Result(Point Position, List<Guide> Guides);

    public static Result Calculate(Rectangle moving, IEnumerable<Rectangle> others,
        int edgeThreshold, int centerThreshold, bool snapEnabled)
    {
        var targets = others.ToArray();
        int x = moving.X, y = moving.Y;
        int bestX = int.MaxValue, bestY = int.MaxValue;
        Guide? guideX = null, guideY = null;

        void Consider(int candidate, double coordinate, bool vertical, bool center)
        {
            int distance = Math.Abs((vertical ? moving.X : moving.Y) - candidate);
            int threshold = center ? centerThreshold : edgeThreshold;
            if (!snapEnabled || distance > threshold || distance >= (vertical ? bestX : bestY)) return;
            var guide = new Guide(vertical, coordinate, center);
            if (vertical) { x = candidate; bestX = distance; guideX = guide; }
            else { y = candidate; bestY = distance; guideY = guide; }
        }

        foreach (var other in targets)
        {
            Consider(other.Left - moving.Width, other.Left, true, false);
            Consider(other.Right, other.Right, true, false);
            Consider(other.Left, other.Left, true, false);
            Consider(other.Right - moving.Width, other.Right, true, false);
            Consider(other.Top - moving.Height, other.Top, false, false);
            Consider(other.Bottom, other.Bottom, false, false);
            Consider(other.Top, other.Top, false, false);
            Consider(other.Bottom - moving.Height, other.Bottom, false, false);

            // Desktop positions are integers; different odd/even sizes can only get
            // within half a pixel of a shared center.
            Consider(other.X + (int)Math.Round((other.Width - moving.Width) / 2.0),
                other.X + other.Width / 2.0, true, true);
            Consider(other.Y + (int)Math.Round((other.Height - moving.Height) / 2.0),
                other.Y + other.Height / 2.0, false, true);
        }

        // Exact center indicators also work with snapping off or temporarily bypassed.
        foreach (var other in targets)
        {
            double cx = other.X + other.Width / 2.0;
            double cy = other.Y + other.Height / 2.0;
            if (Math.Abs(x + moving.Width / 2.0 - cx) <= 0.5)
                guideX = new Guide(true, cx, true);
            if (Math.Abs(y + moving.Height / 2.0 - cy) <= 0.5)
                guideY = new Guide(false, cy, true);
        }

        var guides = new List<Guide>();
        if (guideX.HasValue) guides.Add(guideX.Value);
        if (guideY.HasValue) guides.Add(guideY.Value);
        return new Result(new Point(x, y), guides);
    }
}
