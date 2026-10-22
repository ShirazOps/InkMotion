namespace InkMotion;

public readonly record struct InkGuideLine(double X1, double Y1, double X2, double Y2, bool IsCentre);
public readonly record struct InkRulerTick(int Pixel, double Position, bool IsMajor);

public static class InkGuideLayout
{
    public static IReadOnlyList<InkRulerTick> CreateRuler(double canvasLength, int pixelLength, double zoom)
    {
        if (!double.IsFinite(canvasLength) || canvasLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasLength));
        }

        if (pixelLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelLength));
        }

        if (!double.IsFinite(zoom) || zoom is < 0.25 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom));
        }

        double intervals = Math.Clamp(Math.Floor(canvasLength * zoom / 80), 1, 100);
        double desiredStep = pixelLength / intervals;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(desiredStep)));
        double scaled = desiredStep / magnitude;
        double step = (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10) * magnitude;
        int majorStep = (int)Math.Min(int.MaxValue, Math.Max(1, Math.Round(step)));
        int minorStep = Math.Max(1, majorStep / 5);
        var ticks = new List<InkRulerTick>();
        for (long pixel = 0; pixel < pixelLength; pixel += minorStep)
        {
            double position = (double)pixel / pixelLength;
            bool major = pixel % majorStep == 0 &&
                (pixel == 0 || (1 - position) * canvasLength * zoom >= 48);
            ticks.Add(new((int)pixel, position, major));
        }

        ticks.Add(new(pixelLength, 1, true));
        return ticks;
    }

    public static IReadOnlyList<InkGuideLine> Create(bool grid, int divisions, bool centre)
    {
        if (divisions is < 2 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(divisions), "Use 2-64 canvas grid divisions.");
        }

        var guides = new List<InkGuideLine>();
        if (grid)
        {
            for (int index = 1; index < divisions; index++)
            {
                double position = (double)index / divisions;
                if (centre && index * 2 == divisions)
                {
                    continue;
                }

                guides.Add(new(position, 0, position, 1, false));
                guides.Add(new(0, position, 1, position, false));
            }
        }

        if (centre)
        {
            guides.Add(new(0.5, 0, 0.5, 1, true));
            guides.Add(new(0, 0.5, 1, 0.5, true));
        }

        return guides;
    }
}
