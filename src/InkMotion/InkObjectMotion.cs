using System.Numerics;
using System.Text.Json;

namespace InkMotion;

public sealed class InkObjectTracking
{
    public bool Enabled { get; set; } = true;
    public string SourceClipId { get; set; } = "";
    public string SourceSignature { get; set; } = "";
    public double CanvasAspectRatio { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public double ReferenceSeconds { get; set; }
    public double AnchorX { get; set; }
    public double AnchorY { get; set; }
    public double BoxWidth { get; set; }
    public double BoxHeight { get; set; }
    public double? LostSeconds { get; set; }
    public List<InkObjectPose> Keys { get; set; } = [];
}

public sealed record InkObjectPose(double Seconds, double X, double Y, double Scale = 1, double Rotation = 0,
    double ClipX = 0.5, double ClipY = 0.5, double PictureAspectRatio = 0);

public static class InkObjectMotion
{
    public static InkObjectTracking Clone(InkObjectTracking tracking) =>
        JsonSerializer.Deserialize<InkObjectTracking>(JsonSerializer.Serialize(tracking))!;

    public static bool IsValid(InkObjectTracking tracking) =>
        !string.IsNullOrWhiteSpace(tracking.SourceClipId) &&
        double.IsFinite(tracking.CanvasAspectRatio) && tracking.CanvasAspectRatio is >= 0 and <= 100 &&
        double.IsFinite(tracking.StartSeconds) && tracking.StartSeconds >= 0 &&
        double.IsFinite(tracking.EndSeconds) && tracking.EndSeconds > tracking.StartSeconds &&
        double.IsFinite(tracking.ReferenceSeconds) && tracking.ReferenceSeconds >= 0 &&
        double.IsFinite(tracking.AnchorX) && tracking.AnchorX is >= 0 and <= 1 &&
        double.IsFinite(tracking.AnchorY) && tracking.AnchorY is >= 0 and <= 1 &&
        double.IsFinite(tracking.BoxWidth) && tracking.BoxWidth is > 0 and <= 1 &&
        double.IsFinite(tracking.BoxHeight) && tracking.BoxHeight is > 0 and <= 1 &&
        (tracking.LostSeconds is null || double.IsFinite(tracking.LostSeconds.Value) &&
            tracking.LostSeconds >= tracking.StartSeconds && tracking.LostSeconds <= tracking.EndSeconds) &&
        tracking.Keys is { Count: > 0 and <= 100000 } &&
        tracking.Keys.All(key => key is not null && double.IsFinite(key.Seconds) &&
            key.Seconds >= tracking.StartSeconds && key.Seconds <= tracking.EndSeconds &&
            double.IsFinite(key.X) && double.IsFinite(key.Y) &&
            double.IsFinite(key.Scale) && key.Scale is >= 0.01 and <= 100 &&
            double.IsFinite(key.Rotation) && double.IsFinite(key.ClipX) && double.IsFinite(key.ClipY) &&
            double.IsFinite(key.PictureAspectRatio) && key.PictureAspectRatio is >= 0 and <= 1000) &&
        tracking.Keys.Zip(tracking.Keys.Skip(1)).All(pair => pair.First.Seconds < pair.Second.Seconds);

    public static bool IsVisible(InkObjectTracking? tracking, double seconds) =>
        tracking is not { Enabled: true } ||
        seconds >= tracking.StartSeconds && seconds < tracking.EndSeconds &&
        (tracking.LostSeconds is null || seconds < tracking.LostSeconds);

    public static bool IsSourceCurrent(InkObjectTracking? tracking, IReadOnlyList<TimelineTrackProject> tracks) =>
        tracking is not { Enabled: true } || string.IsNullOrEmpty(tracking.SourceSignature) ||
        tracks.Where(track => track.Kind == TimelineTrackKind.Video).SelectMany(track => track.Clips)
            .Any(clip => clip.Id == tracking.SourceClipId && JsonSerializer.Serialize(clip) == tracking.SourceSignature);

    public static InkObjectPose Evaluate(InkObjectTracking tracking, double seconds)
    {
        InkObjectPose previous = tracking.Keys[0];
        foreach (InkObjectPose next in tracking.Keys.Skip(1))
        {
            if (next.Seconds >= seconds)
            {
                double amount = Math.Clamp((seconds - previous.Seconds) / (next.Seconds - previous.Seconds), 0, 1);
                return new(seconds, previous.X + (next.X - previous.X) * amount,
                    previous.Y + (next.Y - previous.Y) * amount,
                    previous.Scale + (next.Scale - previous.Scale) * amount,
                    previous.Rotation + (next.Rotation - previous.Rotation) * amount,
                    previous.ClipX + (next.ClipX - previous.ClipX) * amount,
                    previous.ClipY + (next.ClipY - previous.ClipY) * amount,
                    previous.PictureAspectRatio + (next.PictureAspectRatio - previous.PictureAspectRatio) * amount);
            }
            previous = next;
        }
        return previous;
    }

    public static Matrix3x2 Transform(InkObjectTracking? tracking, double seconds, float width, float height)
    {
        if (tracking is not { Enabled: true }) return Matrix3x2.Identity;
        InkObjectPose pose = Evaluate(tracking, seconds);
        if (tracking.CanvasAspectRatio > 0 && pose.PictureAspectRatio > 0)
        {
            float aspect = (float)tracking.CanvasAspectRatio;
            float fit = (float)(Math.Min(width / pose.PictureAspectRatio, height) /
                Math.Min(aspect / pose.PictureAspectRatio, 1));
            // Work in the analysis viewport, then refit around the clip's centre. This keeps
            // object-bound ink aligned when the viewer and export have different aspect ratios.
            return Matrix3x2.CreateScale(aspect / width, 1 / height) *
                Matrix3x2.CreateTranslation(-(float)tracking.AnchorX * aspect, -(float)tracking.AnchorY) *
                Matrix3x2.CreateScale((float)pose.Scale) *
                Matrix3x2.CreateRotation((float)(pose.Rotation * Math.PI / 180)) *
                Matrix3x2.CreateTranslation((float)(pose.X - pose.ClipX) * aspect, (float)(pose.Y - pose.ClipY)) *
                Matrix3x2.CreateScale(fit) *
                Matrix3x2.CreateTranslation((float)pose.ClipX * width, (float)pose.ClipY * height);
        }
        return Matrix3x2.CreateTranslation(-(float)tracking.AnchorX * width, -(float)tracking.AnchorY * height) *
            Matrix3x2.CreateScale((float)pose.Scale) *
            Matrix3x2.CreateRotation((float)(pose.Rotation * Math.PI / 180)) *
            Matrix3x2.CreateTranslation((float)pose.X * width, (float)pose.Y * height);
    }

    public static IReadOnlyList<List<InkPoint>> Strokes(IReadOnlyList<EditorFrame> frames, int frameIndex,
        InkLayerProject layer, double frameRate)
    {
        IReadOnlyList<List<InkPoint>> strokes = InkAnimation.GetDisplayedStrokes(frames, frameIndex,
            frame => ExportPlanning.GetLayerStrokes(frame, layer.Id));
        if (layer.ObjectTracking is { Enabled: true } tracking && strokes.Count == 0)
        {
            int reference = InkTimeline.GetFrameIndexAtPosition(frames,
                (int)Math.Floor(tracking.ReferenceSeconds * frameRate));
            return InkAnimation.GetDisplayedStrokes(frames, reference, frame => ExportPlanning.GetLayerStrokes(frame, layer.Id));
        }
        return strokes;
    }
}
