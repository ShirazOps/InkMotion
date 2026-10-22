namespace InkMotion;

public sealed record InkStrokePartition(
    List<InkPoint> Original,
    List<List<InkPoint>> Pieces,
    List<List<InkPoint>> Selected);

public static class InkSelectionEditing
{
    internal static IEnumerable<(InkPoint A, InkPoint B)> BoundarySegments(IReadOnlyList<InkPoint> polygon)
    {
        if (polygon.Count == 0 || !polygon[0].IsFilled)
        {
            return Enumerable.Range(0, polygon.Count)
                .Select(index => (polygon[index], polygon[(index + 1) % polygon.Count]));
        }

        var segments = new Dictionary<(double, double, double, double), (InkPoint A, InkPoint B)>();
        for (int index = 0; index < polygon.Count; index++)
        {
            InkPoint a = polygon[index];
            InkPoint b = polygon[(index + 1) % polygon.Count];
            if (a.X == b.X && a.Y == b.Y)
            {
                continue;
            }
            bool forward = a.X < b.X || (a.X == b.X && a.Y < b.Y);
            var key = forward ? (a.X, a.Y, b.X, b.Y) : (b.X, b.Y, a.X, a.Y);
            if (!segments.Remove(key))
            {
                segments.Add(key, (a, b));
            }
        }
        return segments.Values;
    }

    public static bool ContainsPoint(IReadOnlyList<InkPoint> polygon, double x, double y)
    {
        bool inside = false;
        foreach ((InkPoint a, InkPoint b) in BoundarySegments(polygon))
        {
            double cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            if (Math.Abs(cross) < 1e-12 &&
                x >= Math.Min(a.X, b.X) && x <= Math.Max(a.X, b.X) &&
                y >= Math.Min(a.Y, b.Y) && y <= Math.Max(a.Y, b.Y))
            {
                return true;
            }

            if ((a.Y > y) != (b.Y > y) &&
                x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    public static List<InkStrokePartition> SelectPartial(
        List<List<InkPoint>> strokes, IReadOnlyList<InkPoint> polygon)
    {
        if (polygon.Count < 3 || polygon.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)) ||
            Math.Abs(Enumerable.Range(0, polygon.Count).Sum(index =>
                polygon[index].X * polygon[(index + 1) % polygon.Count].Y -
                polygon[(index + 1) % polygon.Count].X * polygon[index].Y)) < 1e-10)
        {
            return [];
        }

        bool Inside(InkPoint point) => ContainsPoint(polygon, point.X, point.Y);

        var partitions = new List<InkStrokePartition>();
        foreach (List<InkPoint> stroke in strokes)
        {
            if (stroke.Count >= 3 && stroke[0].IsFilled)
            {
                if (Select([stroke], polygon).Count > 0)
                {
                    List<InkPoint> shape = InkEditing.CloneStrokes([stroke])[0];
                    partitions.Add(new(stroke, [shape], [shape]));
                }
                continue;
            }

            if (stroke.Count == 1)
            {
                if (Inside(stroke[0]))
                {
                    List<InkPoint> dot = InkEditing.CloneStrokes([stroke])[0];
                    partitions.Add(new(stroke, [dot], [dot]));
                }
                continue;
            }

            var pieces = new List<List<InkPoint>>();
            var selected = new List<List<InkPoint>>();
            List<InkPoint>? piece = null;
            bool? previousInside = null;
            for (int index = 1; index < stroke.Count; index++)
            {
                InkPoint a = stroke[index - 1];
                InkPoint b = stroke[index];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                var cuts = new List<double> { 0, 1 };
                for (int edge = 0; edge < polygon.Count; edge++)
                {
                    InkPoint c = polygon[edge];
                    InkPoint d = polygon[(edge + 1) % polygon.Count];
                    double ex = d.X - c.X;
                    double ey = d.Y - c.Y;
                    double denominator = dx * ey - dy * ex;
                    if (Math.Abs(denominator) > 1e-12)
                    {
                        double t = ((c.X - a.X) * ey - (c.Y - a.Y) * ex) / denominator;
                        double u = ((c.X - a.X) * dy - (c.Y - a.Y) * dx) / denominator;
                        if (t > 0 && t < 1 && u >= 0 && u <= 1)
                        {
                            cuts.Add(t);
                        }
                    }
                    else if (Math.Abs((c.X - a.X) * dy - (c.Y - a.Y) * dx) < 1e-12 &&
                        dx * dx + dy * dy > 1e-20)
                    {
                        foreach (InkPoint end in new[] { c, d })
                        {
                            double t = ((end.X - a.X) * dx + (end.Y - a.Y) * dy) / (dx * dx + dy * dy);
                            if (t > 0 && t < 1)
                            {
                                cuts.Add(t);
                            }
                        }
                    }
                }

                cuts.Sort();
                for (int cut = 1; cut < cuts.Count; cut++)
                {
                    double start = cuts[cut - 1];
                    double end = cuts[cut];
                    if (end - start < 1e-12)
                    {
                        continue;
                    }

                    bool inside = Inside(InkStrokeEditing.Interpolate(a, b, (start + end) / 2));
                    if (piece is null || inside != previousInside)
                    {
                        piece = [InkStrokeEditing.Interpolate(a, b, start)];
                        pieces.Add(piece);
                        if (inside)
                        {
                            selected.Add(piece);
                        }
                    }

                    piece.Add(InkStrokeEditing.Interpolate(a, b, end));
                    previousInside = inside;
                }
            }

            if (selected.Count > 0)
            {
                partitions.Add(new(stroke, pieces, selected));
            }
        }

        return partitions;
    }

    public static void ApplyPartitions(List<List<InkPoint>> strokes, IReadOnlyList<InkStrokePartition> partitions)
    {
        if (partitions.Any(partition => !strokes.Contains(partition.Original)))
        {
            throw new InvalidOperationException("The drawing changed after the partial selection. Select it again.");
        }

        foreach (InkStrokePartition partition in partitions)
        {
            int index = strokes.IndexOf(partition.Original);
            strokes.RemoveAt(index);
            strokes.InsertRange(index, partition.Pieces);
        }
    }

    public static List<List<InkPoint>> Select(List<List<InkPoint>> strokes, IReadOnlyList<InkPoint> polygon)
    {
        if (polygon.Count < 3 || Math.Abs(Enumerable.Range(0, polygon.Count).Sum(index =>
            polygon[index].X * polygon[(index + 1) % polygon.Count].Y -
            polygon[(index + 1) % polygon.Count].X * polygon[index].Y)) < 1e-10)
        {
            return [];
        }

        return strokes.Where(stroke => stroke.Any(point => ContainsPoint(polygon, point.X, point.Y)) ||
            (stroke.Count >= 3 && stroke[0].IsFilled && ContainsPoint(stroke, polygon[0].X, polygon[0].Y)) ||
            (stroke.Count >= 3 && stroke[0].IsFilled
                ? BoundarySegments(stroke)
                : Enumerable.Range(1, Math.Max(0, stroke.Count - 1))
                    .Select(index => (A: stroke[index - 1], B: stroke[index])))
            .Any(segment =>
                Enumerable.Range(0, polygon.Count).Any(edge => Intersects(segment.A, segment.B,
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
