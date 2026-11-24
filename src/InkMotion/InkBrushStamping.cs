namespace InkMotion;

internal static class InkBrushStamping
{
    public static void Draw(IReadOnlyList<InkPoint> stroke, float width, float height, float widthScale,
        double spacingFactor, Action<InkPoint> stamp)
    {
        if (!float.IsFinite(widthScale) || widthScale <= 0 ||
            !float.IsFinite(width) || width <= 0 || !float.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthScale),
                "Textured ink requires positive finite canvas dimensions and brush scale.");
        }

        if (stroke.Count == 0) return;
        double spacing = Math.Max(0.25, stroke[0].BrushWidth * spacingFactor) * widthScale;
        double distanceToNext = spacing;
        stamp(stroke[0]);
        for (int index = 1; index < stroke.Count; index++)
        {
            InkPoint a = stroke[index - 1];
            InkPoint b = stroke[index];
            double dx = (b.X - a.X) * width;
            double dy = (b.Y - a.Y) * height;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-10) continue;
            while (distanceToNext <= length)
            {
                stamp(InkStrokeEditing.Interpolate(a, b, distanceToNext / length));
                distanceToNext += spacing;
            }
            distanceToNext -= length;
        }
    }
}
