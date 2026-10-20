namespace InkMotion;

public readonly record struct InkGuideLine(double X1, double Y1, double X2, double Y2, bool IsCentre);

public static class InkGuideLayout
{
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
