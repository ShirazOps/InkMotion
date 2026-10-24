using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace InkMotion;

public static class VideoSharpen
{
    public const double MaximumAmount = 10;

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!double.IsFinite(clip.SharpenAmount) || clip.SharpenAmount is < 0 or > MaximumAmount)
        {
            return "Sharpen amount must be between 0 and 10.";
        }

        if (clip.SharpenAmount > 0 && kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return "Sharpen is supported only for video and image clips.";
        }

        return null;
    }
}

public sealed class VideoSharpenCanvasEffect : IDisposable
{
    private readonly SharpenEffect _effect = new();

    public ICanvasImage GetOutput(ICanvasImage source, TimelineClipProject clip, double? animatedAmount = null)
    {
        _effect.Source = source;
        _effect.Amount = (float)(animatedAmount ?? clip.SharpenAmount);
        return _effect;
    }

    public void Dispose() => _effect.Dispose();
}
