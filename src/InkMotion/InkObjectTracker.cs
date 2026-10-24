using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace InkMotion;

public sealed record InkTrackingBox(double X, double Y, double Width, double Height);
public sealed record InkTrackingResult(IReadOnlyList<InkObjectPose> Keys, double? LostSeconds, string Message);

public static class InkObjectTracker
{
    private sealed record Feature(Vector2 Point, byte[] Patch);

    public static InkTrackingResult Analyse(EditorProject project, string clipId, double start, double end,
        InkTrackingBox box, InkObjectPose initialPose, double aspectRatio, IProgress<double>? progress,
        CancellationToken cancellationToken, InkObjectTracking? previousTracking = null)
    {
        TimelineTrackProject? track = project.VideoTracks.FirstOrDefault(track =>
            track.Kind == TimelineTrackKind.Video && track.Clips.Any(clip => clip.Id == clipId));
        TimelineClipProject? clip = track?.Clips.FirstOrDefault(clip => clip.Id == clipId);
        if (clip is null || track is null) throw new InvalidOperationException("Select a visible video clip beneath the ink.");
        double clipStart = VideoTimelineEditing.GetStartSeconds(clip, project.FrameRate);
        double clipEnd = clipStart + VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < clipStart ||
            end > clipEnd + 0.0001 || end - start < 1 / project.FrameRate ||
            (end - start) * project.FrameRate > 100000 ||
            !double.IsFinite(aspectRatio) || aspectRatio <= 0 ||
            !double.IsFinite(box.X) || !double.IsFinite(box.Y) ||
            !double.IsFinite(box.Width) || !double.IsFinite(box.Height) ||
            box.Width < 0.04 || box.Height < 0.04 || box.X - box.Width / 2 < 0 ||
            box.Y - box.Height / 2 < 0 || box.X + box.Width / 2 > 1 || box.Y + box.Height / 2 > 1)
            throw new InvalidOperationException("Choose a box inside the viewer and a range within one video clip (at least two frames, at most 100,000 frames).");

        // Analyse the selected clip's displayed picture, including its crop, transform and effects,
        // but never the drawing being attached or other video tracks.
        project.VideoTracks = [track];
        track.Clips = [clip];
        track.IsVisible = true;
        project.VideoMulticamGroups.Clear();
        foreach (InkLayerProject layer in project.InkLayers) layer.IsVisible = false;
        foreach (EditorFrame frame in project.Frames) frame.TextOverlays.Clear();
        using var source = new TimelineVideoFrameSource(VideoProxy.GetRenderPath(clip));
        (int _, int _, int sourceWidth, int sourceHeight) = source.Aperture;
        InkObjectPose Geometry(InkObjectPose pose)
        {
            VideoTransformValue transform = VideoTransformKeyframes.GetValueAtTime(clip, pose.Seconds, project.FrameRate);
            Rect crop = VideoCrop.GetSourceRect(new Rect(0, 0, sourceWidth, sourceHeight),
                VideoCropKeyframes.GetValueAtTime(clip, pose.Seconds, project.FrameRate));
            return pose with { ClipX = transform.X, ClipY = transform.Y, PictureAspectRatio = crop.Width / crop.Height };
        }
        initialPose = Geometry(initialPose);
        if (previousTracking is { CanvasAspectRatio: > 0 })
        {
            double referenceAspect = previousTracking.CanvasAspectRatio;
            double factor = Math.Min(referenceAspect / initialPose.PictureAspectRatio, 1) /
                Math.Min(aspectRatio / initialPose.PictureAspectRatio, 1);
            box = new(initialPose.ClipX + (box.X - initialPose.ClipX) * aspectRatio * factor / referenceAspect,
                initialPose.ClipY + (box.Y - initialPose.ClipY) * factor,
                box.Width * aspectRatio * factor / referenceAspect, box.Height * factor);
            initialPose = initialPose with { X = box.X, Y = box.Y,
                Scale = Math.Sqrt(box.Width * box.Height / (previousTracking.BoxWidth * previousTracking.BoxHeight)) };
            aspectRatio = referenceAspect;
        }
        if (!double.IsFinite(initialPose.Scale) || initialPose.Scale is < 0.05 or > 20 ||
            !double.IsFinite(initialPose.Rotation))
            throw new InvalidOperationException("The corrected box changes scale too far. Clear tracking and start a new attachment.");
        const int width = 384;
        int height = Math.Clamp((int)Math.Round(width / aspectRatio), 96, 768);
        var profile = new VideoExportProfile(ExportResolution.P720, width, height, project.FrameRate);
        using var device = new CanvasDevice();
        using var renderer = new VideoExporter.FrameRenderer(device,
            new(project, profile, VideoExportCodec.H264, "", height), []);
        int first = (int)Math.Round(start * project.FrameRate);
        int last = (int)Math.Ceiling(end * project.FrameRate) - 1;
        byte[] previous = Luma(renderer.Render(first));
        List<Feature> features = FindFeatures(previous, width, height, box);
        if (features.Count < 6)
            throw new InvalidOperationException("The box needs more visible detail. Enlarge it around a textured, rigid part of the object.");
        var keys = new List<InkObjectPose> { initialPose with { Seconds = start } };
        Vector2 centre = new((float)box.X * width, (float)box.Y * height);
        double scale = initialPose.Scale;
        double rotation = initialPose.Rotation;
        for (int frame = first + 1; frame <= last; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] current = Luma(renderer.Render(frame));
            var matches = new List<(Vector2 From, Vector2 To)>();
            foreach (Feature feature in features)
            {
                var match = VideoMaskTracking.TrackPoint(feature.Patch, current, width, height,
                    (int)Math.Round(feature.Point.X), (int)Math.Round(feature.Point.Y), 12);
                if (match.Error <= 36)
                    matches.Add((feature.Point, new(match.X, match.Y)));
            }
            double seconds = frame / project.FrameRate;
            if (!TryFitSimilarity(matches, out Matrix3x2 motion, out double stepScale, out double stepRotation))
                return new(keys, seconds, $"Object lost at {seconds:0.###} s. Seek to the flagged point, reselect the object box and resume.");
            centre = Vector2.Transform(centre, motion);
            scale *= stepScale;
            rotation += stepRotation;
            if (scale is < 0.05 or > 20 || centre.X < 0 || centre.X >= width || centre.Y < 0 || centre.Y >= height)
                return new(keys, seconds, $"Object left the usable tracking region at {seconds:0.###} s. Correct the box and resume.");
            keys.Add(Geometry(new(seconds, centre.X / width, centre.Y / height, scale, rotation)));
            features = matches.Where(match => Vector2.Distance(Vector2.Transform(match.From, motion), match.To) <= 2)
                .Where(match => match.To.X >= 5 && match.To.X < width - 5 && match.To.Y >= 5 && match.To.Y < height - 5)
                .Select(match => new Feature(match.To, Patch(current, width, match.To))).ToList();
            if (features.Count < 16)
            {
                var currentBox = new InkTrackingBox(centre.X / width, centre.Y / height,
                    Math.Min(1, box.Width * scale / initialPose.Scale * 0.85),
                    Math.Min(1, box.Height * scale / initialPose.Scale * 0.85));
                foreach (Feature feature in FindFeatures(current, width, height, currentBox))
                {
                    if (features.All(existing => Vector2.Distance(existing.Point, feature.Point) >= 10))
                        features.Add(feature);
                    if (features.Count >= 24) break;
                }
            }
            progress?.Report((frame - first) / (double)Math.Max(1, last - first));
        }
        return new(keys, null, "Object tracking completed. Review the motion before exporting.");
    }

    public static bool TryFitSimilarity(IReadOnlyList<(Vector2 From, Vector2 To)> matches,
        out Matrix3x2 motion, out double scale, out double rotation)
    {
        motion = Matrix3x2.Identity;
        scale = 1;
        rotation = 0;
        if (matches.Count < 4) return false;
        List<(Vector2 From, Vector2 To)> best = [];
        for (int a = 0; a < matches.Count; a++)
        for (int b = a + 1; b < matches.Count; b++)
        {
            Vector2 from = matches[b].From - matches[a].From;
            Vector2 to = matches[b].To - matches[a].To;
            if (from.LengthSquared() < 100 || to.LengthSquared() < 64) continue;
            double s = to.Length() / from.Length();
            double angle = Math.Atan2(to.Y, to.X) - Math.Atan2(from.Y, from.X);
            var candidate = Matrix3x2.CreateTranslation(-matches[a].From) *
                Matrix3x2.CreateScale((float)s) * Matrix3x2.CreateRotation((float)angle) *
                Matrix3x2.CreateTranslation(matches[a].To);
            var inliers = matches.Where(match => Vector2.Distance(Vector2.Transform(match.From, candidate), match.To) <= 2).ToList();
            if (inliers.Count > best.Count) best = inliers;
        }
        if (best.Count < 4 || best.Count < matches.Count * 0.6) return false;
        Vector2 sourceCentre = best.Aggregate(Vector2.Zero, (sum, match) => sum + match.From) / best.Count;
        Vector2 targetCentre = best.Aggregate(Vector2.Zero, (sum, match) => sum + match.To) / best.Count;
        double dot = 0, cross = 0, denominator = 0;
        foreach (var match in best)
        {
            Vector2 from = match.From - sourceCentre, to = match.To - targetCentre;
            dot += Vector2.Dot(from, to);
            cross += from.X * to.Y - from.Y * to.X;
            denominator += from.LengthSquared();
        }
        if (denominator < 100) return false;
        scale = Math.Sqrt(dot * dot + cross * cross) / denominator;
        rotation = Math.Atan2(cross, dot) * 180 / Math.PI;
        if (scale is < 0.8 or > 1.25 || Math.Abs(rotation) > 20) return false;
        motion = Matrix3x2.CreateTranslation(-sourceCentre) * Matrix3x2.CreateScale((float)scale) *
            Matrix3x2.CreateRotation((float)(rotation * Math.PI / 180)) * Matrix3x2.CreateTranslation(targetCentre);
        return true;
    }

    private static List<Feature> FindFeatures(byte[] luma, int width, int height, InkTrackingBox box)
    {
        var candidates = new List<(Vector2 Point, double Variance)>();
        int left = Math.Max(5, (int)((box.X - box.Width / 2) * width));
        int right = Math.Min(width - 6, (int)((box.X + box.Width / 2) * width));
        int top = Math.Max(5, (int)((box.Y - box.Height / 2) * height));
        int bottom = Math.Min(height - 6, (int)((box.Y + box.Height / 2) * height));
        for (int y = top; y <= bottom; y += 4)
        for (int x = left; x <= right; x += 4)
        {
            byte[] patch = Patch(luma, width, new(x, y));
            double mean = patch.Average(value => (double)value);
            double variance = patch.Average(value => Math.Pow(value - mean, 2));
            if (variance > 100) candidates.Add((new(x, y), variance));
        }
        var features = new List<Feature>();
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Variance))
        {
            if (features.All(feature => Vector2.Distance(feature.Point, candidate.Point) >= 10))
                features.Add(new(candidate.Point, Patch(luma, width, candidate.Point)));
            if (features.Count == 24) break;
        }
        return features;
    }

    private static byte[] Patch(byte[] luma, int width, Vector2 point)
    {
        var patch = new byte[81];
        int x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y);
        for (int row = 0; row < 9; row++)
            Array.Copy(luma, (y + row - 4) * width + x - 4, patch, row * 9, 9);
        return patch;
    }

    private static byte[] Luma(byte[] pixels)
    {
        var luma = new byte[pixels.Length / 4];
        for (int i = 0; i < luma.Length; i++)
            luma[i] = (byte)((pixels[i * 4] * 29 + pixels[i * 4 + 1] * 150 + pixels[i * 4 + 2] * 77) >> 8);
        return luma;
    }
}
