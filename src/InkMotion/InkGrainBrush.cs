using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.UI;

namespace InkMotion;

internal static class InkGrainBrush
{
    public static void Draw(CanvasDrawingSession session, IReadOnlyList<InkPoint> stroke,
    Color colour, float width, float height, float widthScale)
    {
        uint stamp = 0;
        InkBrushStamping.Draw(stroke, width, height, widthScale, 0.2, DrawStamp);

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
