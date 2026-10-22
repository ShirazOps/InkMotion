using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace InkMotion;

public static class VideoColorGrading
{
    public const double MinimumExposure = -2;
    public const double MaximumExposure = 2;
    public const double MinimumContrast = -1;
    public const double MaximumContrast = 1;
    public const double MinimumTemperature = -1;
    public const double MaximumTemperature = 1;
    public const double MinimumTint = -1;
    public const double MaximumTint = 1;
    public const double MinimumSaturation = 0;
    public const double MaximumSaturation = 2;

    public static bool IsNeutral(TimelineClipProject clip) =>
        clip.Exposure == 0 &&
        clip.Contrast == 0 &&
        clip.WhiteBalanceTemperature == 0 &&
        clip.WhiteBalanceTint == 0 &&
        clip.Saturation == 1;

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind)
    {
        bool valid = double.IsFinite(clip.Exposure) && clip.Exposure is >= MinimumExposure and <= MaximumExposure &&
            double.IsFinite(clip.Contrast) && clip.Contrast is >= MinimumContrast and <= MaximumContrast &&
            double.IsFinite(clip.WhiteBalanceTemperature) &&
            clip.WhiteBalanceTemperature is >= MinimumTemperature and <= MaximumTemperature &&
            double.IsFinite(clip.WhiteBalanceTint) && clip.WhiteBalanceTint is >= MinimumTint and <= MaximumTint &&
            double.IsFinite(clip.Saturation) && clip.Saturation is >= MinimumSaturation and <= MaximumSaturation;
        if (!valid)
        {
            return "Clip colour settings are outside their supported ranges.";
        }

        if (kind == TimelineTrackKind.Text && !IsNeutral(clip))
        {
            return "Colour grading is supported only for video and image clips.";
        }

        return null;
    }
}

public sealed class VideoColorGradingEffect : IDisposable
{
    private readonly ExposureEffect _exposure = new();
    private readonly ContrastEffect _contrast = new();
    private readonly TemperatureAndTintEffect _temperatureAndTint = new();
    private readonly SaturationEffect _saturation = new();

    public ICanvasImage GetOutput(CanvasBitmap source, TimelineClipProject clip)
    {
        _exposure.Source = source;
        _exposure.Exposure = (float)clip.Exposure;
        _contrast.Source = _exposure;
        _contrast.Contrast = (float)clip.Contrast;
        _temperatureAndTint.Source = _contrast;
        _temperatureAndTint.Temperature = (float)clip.WhiteBalanceTemperature;
        _temperatureAndTint.Tint = (float)clip.WhiteBalanceTint;
        _saturation.Source = _temperatureAndTint;
        _saturation.Saturation = (float)clip.Saturation;
        return _saturation;
    }

    public void Dispose()
    {
        _saturation.Dispose();
        _temperatureAndTint.Dispose();
        _contrast.Dispose();
        _exposure.Dispose();
    }
}
