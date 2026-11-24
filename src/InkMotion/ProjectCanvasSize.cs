using Windows.Foundation;

namespace InkMotion;

public sealed record ProjectCanvasSize(int Width, int Height)
{
    public void Validate()
    {
        if (Width is < 1 or > VideoExportProfile.MaximumCustomDimension ||
            Height is < 1 or > VideoExportProfile.MaximumCustomDimension ||
            (long)Width * Height > VideoExportProfile.MaximumCustomPixelCount)
        {
            throw new InvalidDataException(
                "Canvas dimensions must be whole pixels from 1 to 8,192 per side, with no more than 33,554,432 pixels.");
        }
    }

    public Rect Fit(double width, double height)
    {
        Validate();
        return TimelineCompositor.GetFitRect(new Size(Width, Height), width, height, 1, 0.5, 0.5);
    }
}
