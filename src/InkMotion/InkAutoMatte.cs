using System.Numerics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace InkMotion;

/// <summary>
/// An occlusion matte generated on this device by segmenting a boxed foreground object in each frame. It is drawn on
/// a holdout layer in addition to anything painted there, so lower ink passes behind the object without hand-painting.
/// </summary>
public sealed class InkAutoMatte
{
    public bool Enabled { get; set; } = true;
    public string SourceClipId { get; set; } = "";
    public string SourceSignature { get; set; } = "";
    public double CanvasAspectRatio { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public double? LostSeconds { get; set; }
    public List<InkAutoMatteKey> Keys { get; set; } = [];
}

/// <param name="Polygons">Outlines as flat x, y lists in normalised analysis-viewer coordinates.</param>
public sealed record InkAutoMatteKey(double Seconds, double ClipX, double ClipY, double PictureAspectRatio,
    List<double[]> Polygons);

public sealed record InkAutoMatteResult(IReadOnlyList<InkAutoMatteKey> Keys, double? LostSeconds, string Message);

public static class InkAutoMattes
{
    public const int MaximumPolygons = 32;
    public const int MaximumPointsPerPolygon = 2000;

    public static InkAutoMatte Clone(InkAutoMatte matte) =>
        JsonSerializer.Deserialize<InkAutoMatte>(JsonSerializer.Serialize(matte))!;

    public static bool IsValid(InkAutoMatte matte) =>
        !string.IsNullOrWhiteSpace(matte.SourceClipId) &&
        double.IsFinite(matte.CanvasAspectRatio) && matte.CanvasAspectRatio is >= 0 and <= 100 &&
        double.IsFinite(matte.StartSeconds) && matte.StartSeconds >= 0 &&
        double.IsFinite(matte.EndSeconds) && matte.EndSeconds > matte.StartSeconds &&
        (matte.LostSeconds is null || double.IsFinite(matte.LostSeconds.Value) &&
            matte.LostSeconds >= matte.StartSeconds && matte.LostSeconds <= matte.EndSeconds) &&
        matte.Keys is { Count: > 0 and <= 100000 } &&
        matte.Keys.All(key => key is not null && double.IsFinite(key.Seconds) &&
            key.Seconds >= matte.StartSeconds && key.Seconds <= matte.EndSeconds &&
            double.IsFinite(key.ClipX) && double.IsFinite(key.ClipY) &&
            double.IsFinite(key.PictureAspectRatio) && key.PictureAspectRatio is >= 0 and <= 1000 &&
            key.Polygons is { Count: <= MaximumPolygons } polygons &&
            polygons.All(polygon => polygon is { Length: >= 6 and <= MaximumPointsPerPolygon * 2 } &&
                polygon.Length % 2 == 0 && polygon.All(value => double.IsFinite(value) && value is >= -1 and <= 2))) &&
        matte.Keys.Zip(matte.Keys.Skip(1)).All(pair => pair.First.Seconds < pair.Second.Seconds);

    /// <summary>The key in force at <paramref name="seconds"/>, holding each analysed frame until the next.</summary>
    public static InkAutoMatteKey? Evaluate(InkAutoMatte? matte, IReadOnlyList<TimelineTrackProject> tracks, double seconds)
    {
        if (matte is not { Enabled: true } || matte.Keys.Count == 0 || seconds < matte.StartSeconds ||
            seconds >= matte.EndSeconds || (matte.LostSeconds is double lost && seconds >= lost)) return null;
        if (!string.IsNullOrEmpty(matte.SourceSignature) &&
            !tracks.Where(track => track.Kind == TimelineTrackKind.Video).SelectMany(track => track.Clips)
                .Any(clip => clip.Id == matte.SourceClipId && JsonSerializer.Serialize(clip) == matte.SourceSignature))
            return null;
        int low = 0, high = matte.Keys.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (matte.Keys[middle].Seconds <= seconds + 1e-9) low = middle;
            else high = middle - 1;
        }
        return matte.Keys[low].Seconds <= seconds + 1e-9 ? matte.Keys[low] : null;
    }

    /// <summary>Whether any part of the painted layer can be visible: it has no tracking or its tracking is current.</summary>
    public static bool IsPaintedVisible(InkLayerProject layer, IReadOnlyList<TimelineTrackProject> tracks, double seconds) =>
        InkObjectMotion.IsVisible(layer.ObjectTracking, seconds) && InkObjectMotion.IsSourceCurrent(layer.ObjectTracking, tracks);

    public static bool IsActive(InkLayerProject layer, IReadOnlyList<TimelineTrackProject> tracks, double seconds) =>
        layer.IsHoldout && Evaluate(layer.AutoMatte, tracks, seconds) is not null;

    /// <summary>Maps normalised analysis coordinates to the drawing surface, refitting around the clip as tracking does.</summary>
    public static Matrix3x2 Transform(InkAutoMatte matte, InkAutoMatteKey key, float width, float height)
    {
        if (matte.CanvasAspectRatio > 0 && key.PictureAspectRatio > 0)
        {
            float aspect = (float)matte.CanvasAspectRatio;
            float fit = (float)(Math.Min(width / key.PictureAspectRatio, height) /
                Math.Min(aspect / key.PictureAspectRatio, 1));
            return Matrix3x2.CreateScale(aspect, 1) *
                Matrix3x2.CreateTranslation(-(float)key.ClipX * aspect, -(float)key.ClipY) *
                Matrix3x2.CreateScale(fit) *
                Matrix3x2.CreateTranslation((float)key.ClipX * width, (float)key.ClipY * height);
        }
        return Matrix3x2.CreateScale(width, height);
    }

    public static void Draw(CanvasDrawingSession session, InkAutoMatte? matte, IReadOnlyList<TimelineTrackProject> tracks,
        double seconds, float width, float height, Color colour)
    {
        if (Evaluate(matte, tracks, seconds) is not { Polygons.Count: > 0 } key) return;
        Matrix3x2 transform = Transform(matte!, key, width, height);
        foreach (double[] polygon in key.Polygons)
        {
            var points = new Vector2[polygon.Length / 2];
            for (int i = 0; i < points.Length; i++)
                points[i] = Vector2.Transform(new((float)polygon[i * 2], (float)polygon[i * 2 + 1]), transform);
            using CanvasGeometry geometry = CanvasGeometry.CreatePolygon(session, points);
            session.FillGeometry(geometry, colour);
        }
    }

    /// <summary>
    /// Segments the boxed object through the range with MobileSAM, re-prompting each frame from the previous mask.
    /// </summary>
    public static InkAutoMatteResult Analyse(EditorProject project, string clipId, double start, double end,
        InkTrackingBox box, double aspectRatio, string modelDirectory, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (OnDeviceModels.UnsupportedReason() is string unsupported) throw new InvalidOperationException(unsupported);
        var store = new OnDeviceModelStore(modelDirectory);
        if (!store.AreInstalled(OnDeviceModels.SegmentationEncoder, OnDeviceModels.SegmentationDecoder))
            throw new InvalidOperationException("Install both segmentation model files in AI models first.");
        Func<InkObjectPose, InkObjectPose> geometry = InkObjectTracker.IsolateClip(project, clipId, start, end, box, aspectRatio);
        const int width = 640;
        int height = Math.Clamp((int)Math.Round(width / aspectRatio), 96, width * 2);
        var profile = new VideoExportProfile(ExportResolution.P720, width, height, project.FrameRate);
        using var model = new MobileSamModel(store.GetPath(OnDeviceModels.SegmentationEncoder),
            store.GetPath(OnDeviceModels.SegmentationDecoder));
        using var device = new CanvasDevice();
        using var renderer = new VideoExporter.FrameRenderer(device,
            new(project, profile, VideoExportCodec.H264, "", height), []);
        int first = (int)Math.Round(start * project.FrameRate);
        int last = (int)Math.Ceiling(end * project.FrameRate) - 1;
        Vector2 topLeft = new((float)((box.X - box.Width / 2) * width), (float)((box.Y - box.Height / 2) * height));
        Vector2 bottomRight = new((float)((box.X + box.Width / 2) * width), (float)((box.Y + box.Height / 2) * height));
        float[]? lowResolution = null;
        int previousArea = 0;
        var keys = new List<InkAutoMatteKey>();
        for (int frame = first; frame <= last; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double seconds = frame / project.FrameRate;
            MobileSamModel.Result result = model.Segment(renderer.Render(frame), width, height, topLeft, bottomRight, lowResolution);
            var bounds = MaskContours.Bounds(result.Mask, width, height);
            string? problem = bounds is null ? "the object disappeared"
                : result.Score < 0.5f ? "the outline became uncertain"
                : previousArea > 0 && bounds.Value.Area / (double)previousArea is < 0.5 or > 2 ? "the outline changed too suddenly"
                : null;
            if (problem is not null)
            {
                if (frame == first)
                    throw new InvalidOperationException("No clear object was found in the box. Draw it tightly around one foreground object.");
                return new(keys, seconds, $"Automatic matte stopped at {seconds:0.###} s because {problem}. Seek there, draw a new box and generate again.");
            }
            var (left, top, right, bottom, area) = bounds!.Value;
            List<double[]> polygons = MaskContours.Trace(result.Mask, width, height, maximumPolygons: MaximumPolygons)
                .Select(points => points.Take(MaximumPointsPerPolygon)
                    .SelectMany(point => new[] { point.X / (double)width, point.Y / (double)height }).ToArray())
                .Where(polygon => polygon.Length >= 6).ToList();
            InkObjectPose placement = geometry(new(seconds, 0, 0));
            keys.Add(new(seconds, placement.ClipX, placement.ClipY, placement.PictureAspectRatio, polygons));
            // Prompt the next frame with the current outline's box, enlarged so moderate motion stays inside it.
            float padX = (right - left + 1) * 0.1f + 4, padY = (bottom - top + 1) * 0.1f + 4;
            topLeft = new(Math.Max(0, left - padX), Math.Max(0, top - padY));
            bottomRight = new(Math.Min(width, right + 1 + padX), Math.Min(height, bottom + 1 + padY));
            lowResolution = result.LowResolutionMask;
            previousArea = area;
            progress?.Report((frame - first) / (double)Math.Max(1, last - first));
        }
        string providers = WindowsMlRuntime.Providers.Count == 0 ? "CPU" : string.Join(", ", WindowsMlRuntime.Providers);
        return new(keys, null, $"Automatic matte generated on this device ({providers}). Review its edges before exporting.");
    }
}
