namespace InkMotion;

public static class InkStrokeEditing
{
    public static bool Erase(
        List<List<InkPoint>> strokes, double x, double y, double width, double height,
        double radius, bool wholeStroke)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0 ||
            !double.IsFinite(radius) || radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Erasing requires finite coordinates, positive canvas dimensions and radius.");
        }

        bool changed = false;
        var remaining = new List<List<InkPoint>>();
        foreach (List<InkPoint> stroke in strokes)
        {
            var pieces = new List<List<InkPoint>>();
            List<InkPoint>? piece = null;
            bool hit = false;
            // Include the round stroke caps so the remaining ends do not bridge the erased area.
            double hitRadius = radius + stroke.Select(TimelineCompositor.GetInkWidth).DefaultIfEmpty(0).Max() / 2;
            bool Inside(InkPoint point) =>
                Math.Pow(point.X * width - x, 2) + Math.Pow(point.Y * height - y, 2) < hitRadius * hitRadius;

            if (stroke.Count == 1)
            {
                hit = Inside(stroke[0]);
            }

            for (int index = 1; index < stroke.Count; index++)
            {
                InkPoint a = stroke[index - 1];
                InkPoint b = stroke[index];
                double dx = (b.X - a.X) * width;
                double dy = (b.Y - a.Y) * height;
                double ax = a.X * width - x;
                double ay = a.Y * height - y;
                double lengthSquared = dx * dx + dy * dy;
                var cuts = new List<double> { 0, 1 };
                if (lengthSquared > 1e-12)
                {
                    double dot = ax * dx + ay * dy;
                    double discriminant = dot * dot - lengthSquared * (ax * ax + ay * ay - hitRadius * hitRadius);
                    if (discriminant > 0)
                    {
                        double root = Math.Sqrt(discriminant);
                        foreach (double t in new[] { (-dot - root) / lengthSquared, (-dot + root) / lengthSquared })
                        {
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
                    if (Inside(Interpolate(a, b, (start + end) / 2)))
                    {
                        hit = true;
                        piece = null;
                        continue;
                    }

                    if (piece is null)
                    {
                        piece = [Interpolate(a, b, start)];
                        pieces.Add(piece);
                    }

                    piece.Add(Interpolate(a, b, end));
                }
            }

            if (!hit)
            {
                remaining.Add(stroke);
            }
            else
            {
                changed = true;
                if (!wholeStroke)
                {
                    remaining.AddRange(pieces);
                }
            }
        }

        if (changed)
        {
            strokes.Clear();
            strokes.AddRange(remaining);
        }

        return changed;
    }

    private static InkPoint Interpolate(InkPoint a, InkPoint b, double t) => new()
    {
        X = a.X + (b.X - a.X) * t,
        Y = a.Y + (b.Y - a.Y) * t,
        Pressure = a.Pressure + (b.Pressure - a.Pressure) * t,
        BrushWidth = a.BrushWidth + (b.BrushWidth - a.BrushWidth) * t,
        BrushColour = t < 1 ? a.BrushColour : b.BrushColour,
    };

    public static InkPoint Stabilise(InkPoint input, InkPoint? previous, double amount)
    {
        if (!double.IsFinite(amount) || amount < 0 || amount > 0.9)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        return new InkPoint
        {
            X = previous is null ? input.X : input.X + (previous.X - input.X) * amount,
            Y = previous is null ? input.Y : input.Y + (previous.Y - input.Y) * amount,
            Pressure = input.Pressure,
            BrushWidth = input.BrushWidth,
            BrushColour = input.BrushColour,
        };
    }
}
