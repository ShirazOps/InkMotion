using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;

namespace InkMotion;

public static class VideoBlurEffect
{
    public const double MaximumAmount = 20;

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!double.IsFinite(clip.BlurAmount) || clip.BlurAmount is < 0 or > MaximumAmount)
        {
            return "Blur amount must be between 0 and 20.";
        }

        if (clip.BlurAmount > 0 && kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return "Blur is supported only for video and image clips.";
        }

        return null;
    }
}

public sealed class VideoBlurCanvasEffect : IDisposable
{
    private readonly GaussianBlurEffect _effect = new()
    {
        BorderMode = EffectBorderMode.Hard,
        Optimization = EffectOptimization.Balanced,
    };

    public ICanvasImage GetOutput(ICanvasImage source, TimelineClipProject clip, double? animatedAmount = null)
    {
        _effect.Source = source;
        _effect.BlurAmount = (float)(animatedAmount ?? clip.BlurAmount);
        return _effect;
    }

    public void Dispose() => _effect.Dispose();
}
