using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.UI;

namespace InkMotion;

public static class VideoVignette
{
    public const double MaximumAmount = 1;

    public static string? Validate(TimelineClipProject clip, TimelineTrackKind kind)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!double.IsFinite(clip.VignetteAmount) || clip.VignetteAmount is < 0 or > MaximumAmount)
        {
            return "Vignette amount must be between 0 and 1.";
        }

        if (clip.VignetteAmount > 0 && kind is not (TimelineTrackKind.Video or TimelineTrackKind.Image))
        {
            return "Vignette is supported only for video and image clips.";
        }

        return null;
    }
}

public sealed class VideoVignetteCanvasEffect : IDisposable
{
    private readonly VignetteEffect _effect = new()
    {
        Curve = 0.5f,
        Color = Color.FromArgb(255, 0, 0, 0),
    };

    public ICanvasImage GetOutput(ICanvasImage source, TimelineClipProject clip)
    {
        _effect.Source = source;
        _effect.Amount = (float)clip.VignetteAmount;
        return _effect;
    }

    public void Dispose() => _effect.Dispose();
}
