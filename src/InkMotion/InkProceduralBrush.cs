using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.UI;

namespace InkMotion;

internal static class InkProceduralBrush
{
    public static void Draw(CanvasDrawingSession session, IReadOnlyList<InkPoint> stroke,
        Color colour, float width, float height, float widthScale)
    {
        InkBrushTexture texture = stroke[0].BrushTexture;
        double spacing = texture == InkBrushTexture.DryBristle ? 0.05 : 0.12;
        InkBrushStamping.Draw(stroke, width, height, widthScale, spacing, point =>
        {
            float radius = (float)(TimelineCompositor.GetInkWidth(point) * widthScale / 2);
            Vector2 centre = new((float)(point.X * width), (float)(point.Y * height));
            switch (texture)
            {
                case InkBrushTexture.Calligraphy:
                    Matrix3x2 previous = session.Transform;
                    session.Transform = Matrix3x2.CreateRotation(MathF.PI / 4, centre) * previous;
                    try { session.FillEllipse(centre, radius, radius * 0.22f, colour); }
                    finally { session.Transform = previous; }
                    break;
                case InkBrushTexture.Airbrush:
                    for (int ring = 10; ring >= 1; ring--)
                    {
                        Color softColour = TimelineCompositor.WithOpacity(colour, 0.025 + 0.008 * (10 - ring));
                        session.FillCircle(centre, radius * ring / 10, softColour);
                    }
                    break;
                case InkBrushTexture.DryBristle:
                    for (int bristle = 0; bristle < 9; bristle++)
                    {
                        float offset = (bristle - 4) * radius * 0.21f;
                        session.FillCircle(centre + new Vector2(offset * 0.3f, offset),
                            radius * (bristle % 3 == 0 ? 0.06f : 0.035f), colour);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(stroke), "Select a supported procedural brush.");
            }
        });
    }
}
