namespace InkMotion;

public readonly record struct InkOnionGuide(int FrameIndex, double Opacity, bool IsPrevious);

public static class InkOnionSkin
{
    public static IReadOnlyList<InkOnionGuide> Plan(int frameCount, int selectedIndex,
        int previousCount, int nextCount, double opacity, double falloff)
    {
        if (frameCount <= 0 || selectedIndex < 0 || selectedIndex >= frameCount ||
            previousCount is < 0 or > 8 || nextCount is < 0 or > 8 ||
            !double.IsFinite(opacity) || opacity is < 0 or > 1 ||
            !double.IsFinite(falloff) || falloff is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedIndex), "Use an existing cel, 0-8 guides per direction, opacity 0-1 and falloff above 0 up to 1.");
        }

        var guides = new List<InkOnionGuide>();
        for (int distance = Math.Max(previousCount, nextCount); distance >= 1; distance--)
        {
            double alpha = opacity * Math.Pow(falloff, distance - 1);
            if (distance <= previousCount && selectedIndex - distance >= 0)
            {
                guides.Add(new(selectedIndex - distance, alpha, true));
            }

            if (distance <= nextCount && distance < frameCount - selectedIndex)
            {
                guides.Add(new(selectedIndex + distance, alpha, false));
            }
        }

        return guides;
    }
}
