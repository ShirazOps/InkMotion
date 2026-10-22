using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.UI;

namespace InkMotion;

internal static class InkGrainBrush
{
    public static void Draw(CanvasDrawingSession session, IReadOnlyList<InkPoint> stroke,
    Color colour, float width, float height, float widthScale)
    {
    if (!float.IsFinite(widthScale) || widthScale <= 0 ||
        !float.IsFinite(width) || width <= 0 || !float.IsFinite(height) || height <= 0)
    {
        throw new ArgumentOutOfRangeException(nameof(widthScale), "Textured ink requires positive finite canvas dimensions and brush scale.");
    }

        double spacing = Math.Max(0.25, stroke[0].BrushWidth * 0.2) * widthScale;
        double distanceToNext = spacing;
        uint stamp = 0;
        DrawStamp(stroke[0]);
        for (int index = 1; index < stroke.Count; index++)
        {
            InkPoint a = stroke[index - 1];
            InkPoint b = stroke[index];
            double dx = (b.X - a.X) * width;
            double dy = (b.Y - a.Y) * height;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-10)
            {
                continue;
            }

            while (distanceToNext <= length)
            {
                DrawStamp(InkStrokeEditing.Interpolate(a, b, distanceToNext / length));
                distanceToNext += spacing;
            }

            distanceToNext -= length;
        }

        void DrawStamp(InkPoint point)
        {
            double radius = TimelineCompositor.GetInkWidth(point) * widthScale / 2;
            uint state = unchecked(++stamp * 747796405u + 2891336453u);
            for (int grain = 0; grain < 12; grain++)
            {
                double angle = Next(ref state) * Math.PI * 2;
                double offset = Math.Sqrt(Next(ref state)) * radius * 0.85;
                float grainRadius = (float)(radius * (0.07 + 0.09 * Next(ref state)));
                session.FillCircle(new Vector2(
                    (float)(point.X * width + Math.Cos(angle) * offset),
                    (float)(point.Y * height + Math.Sin(angle) * offset)), grainRadius, colour);
            }
        }
    }

    private static double Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state / ((double)uint.MaxValue + 1);
    }
}
