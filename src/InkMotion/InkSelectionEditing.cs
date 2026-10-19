namespace InkMotion;

public static class InkSelectionEditing
{
    public static List<List<InkPoint>> Select(List<List<InkPoint>> strokes, IReadOnlyList<InkPoint> polygon)
    {
        if (polygon.Count < 3 || Math.Abs(Enumerable.Range(0, polygon.Count).Sum(index =>
            polygon[index].X * polygon[(index + 1) % polygon.Count].Y -
            polygon[(index + 1) % polygon.Count].X * polygon[index].Y)) < 1e-10)
        {
            return [];
        }

        bool Inside(InkPoint point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                InkPoint a = polygon[i];
                InkPoint b = polygon[j];
                if ((a.Y > point.Y) != (b.Y > point.Y) &&
                    point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        return strokes.Where(stroke => stroke.Any(Inside) ||
            Enumerable.Range(1, Math.Max(0, stroke.Count - 1)).Any(index =>
                Enumerable.Range(0, polygon.Count).Any(edge => Intersects(stroke[index - 1], stroke[index],
                    polygon[edge], polygon[(edge + 1) % polygon.Count])))).ToList();
    }

    private static bool Intersects(InkPoint a, InkPoint b, InkPoint c, InkPoint d)
    {
        static double Cross(InkPoint p, InkPoint q, InkPoint r) =>
            (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        return Math.Max(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) <= Math.Min(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) + 1e-10 &&
            Math.Max(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) <= Math.Min(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) + 1e-10 &&
            Cross(a, b, c) * Cross(a, b, d) <= 1e-10 && Cross(c, d, a) * Cross(c, d, b) <= 1e-10;
    }

    public static bool Transform(
        List<List<InkPoint>> strokes, double offsetX, double offsetY, double scale, double degrees,
        double canvasWidth, double canvasHeight)
    {
        if (new[] { offsetX, offsetY, scale, degrees, canvasWidth, canvasHeight }.Any(value => !double.IsFinite(value)) ||
            scale <= 0 || canvasWidth <= 0 || canvasHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        List<InkPoint> points = strokes.SelectMany(stroke => stroke).ToList();
        if (points.Count == 0)
        {
            return false;
        }

        double cx = (points.Min(point => point.X) + points.Max(point => point.X)) / 2;
        double cy = (points.Min(point => point.Y) + points.Max(point => point.Y)) / 2;
        double sin = Math.Sin((degrees % 360) * Math.PI / 180);
        double cos = Math.Cos((degrees % 360) * Math.PI / 180);
        var positions = points.Select(point =>
        {
            double x = (point.X - cx) * canvasWidth * scale;
            double y = (point.Y - cy) * canvasHeight * scale;
            return (X: cx + offsetX + (cos * x - sin * y) / canvasWidth,
                Y: cy + offsetY + (sin * x + cos * y) / canvasHeight);
        }).ToList();
        if (positions.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.X < -1e-8 || point.X > 1 + 1e-8 || point.Y < -1e-8 || point.Y > 1 + 1e-8))
        {
            return false;
        }

        for (int index = 0; index < points.Count; index++)
        {
            points[index].X = Math.Clamp(positions[index].X, 0, 1);
            points[index].Y = Math.Clamp(positions[index].Y, 0, 1);
        }

        return true;
    }
}
