using System.Numerics;

namespace InkMotion;

/// <param name="Line">The segment the user drew at the playhead as x1, y1, x2, y2 in normalised viewer coordinates.</param>
/// <param name="ReferenceBone">The tracking's reference segment when resuming; null for new tracking.</param>
/// <param name="PoseJoints">AI pose only: the COCO joints already being followed when resuming.</param>
/// <param name="Anchor">The artwork anchor in normalised viewer coordinates.</param>
public sealed record InkLimbRequest(InkTrackingArticulation Articulation, double[] Line, Vector2 Anchor,
    double[]? ReferenceBone = null, int[]? PoseJoints = null, string? ModelDirectory = null);

public static partial class InkObjectTracker
{
    /// <summary>How long a hidden joint may be held or bridged before tracking stops for a correction.</summary>
    public const double LimbGapSeconds = 0.5;
    private const float MinimumLengthRatio = 0.4f, MaximumLengthRatio = 2.5f;

    /// <summary>The analysis box around a drawn limb line: its bounds padded and grown to the tracker's minimum size.</summary>
    public static InkTrackingBox LimbBox(double x1, double y1, double x2, double y2)
    {
        double length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        double pad = Math.Max(0.03, length * 0.2);
        static (double Low, double High) Span(double a, double b, double pad)
        {
            double low = Math.Min(a, b) - pad, high = Math.Max(a, b) + pad;
            if (high - low < 0.06) { double middle = (low + high) / 2; low = middle - 0.03; high = middle + 0.03; }
            low = Math.Max(0, low);
            high = Math.Min(1, high);
            return (low, high);
        }
        (double left, double right) = Span(x1, x2, pad);
        (double top, double bottom) = Span(y1, y2, pad);
        return new((left + right) / 2, (top + bottom) / 2, right - left, bottom - top);
    }

    /// <summary>
    /// The similarity that carries the reference segment onto the current one, applied to the artwork anchor.
    /// Points are in analysis pixels; rotation is unwrapped against <paramref name="previousRotation"/> in degrees.
    /// </summary>
    public static InkObjectPose LimbPose(double seconds, Vector2 referenceA, Vector2 referenceB, Vector2 a, Vector2 b,
        Vector2 anchor, double previousRotation, int width, int height)
    {
        Vector2 from = referenceB - referenceA, to = b - a;
        double scale = to.Length() / Math.Max(0.001f, from.Length());
        double angle = (Math.Atan2(to.Y, to.X) - Math.Atan2(from.Y, from.X)) * 180 / Math.PI;
        double rotation = previousRotation + Math.IEEERemainder(angle - previousRotation, 360);
        var similarity = Matrix3x2.CreateScale((float)scale) * Matrix3x2.CreateRotation((float)(rotation * Math.PI / 180));
        Vector2 point = (a + b) / 2 + Vector2.Transform(anchor - (referenceA + referenceB) / 2, similarity);
        return new(seconds, point.X / width, point.Y / height, scale, rotation);
    }

    private static InkTrackingResult TrackLimb(VideoExporter.FrameRenderer renderer, int width, int height, int first,
        int last, double frameRate, Func<InkObjectPose, InkObjectPose> geometry, InkLimbRequest request,
        double rotation, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Vector2 Pixels(double x, double y) => new((float)(x * width), (float)(y * height));
        if (request.Line is not { Length: 4 } line || line.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("Draw a line along the limb.", nameof(request));
        Vector2 a = Pixels(line[0], line[1]), b = Pixels(line[2], line[3]);
        if (Vector2.Distance(a, b) < 12)
            throw new InvalidOperationException("Draw a longer line along the limb, from one joint to the next.");
        Vector2 anchor = Pixels(request.Anchor.X, request.Anchor.Y);
        int maximumGap = Math.Max(1, (int)Math.Round(LimbGapSeconds * frameRate));
        return request.Articulation == InkTrackingArticulation.Pose
            ? TrackPose(renderer, width, height, first, last, frameRate, geometry, request, a, b, anchor, rotation,
                maximumGap, Pixels, progress, cancellationToken)
            : TrackTwoPoint(renderer, width, height, first, last, frameRate, geometry, request, a, b, anchor, rotation,
                maximumGap, Pixels, progress, cancellationToken);
    }

    private static InkTrackingResult TrackTwoPoint(VideoExporter.FrameRenderer renderer, int width, int height,
        int first, int last, double frameRate, Func<InkObjectPose, InkObjectPose> geometry, InkLimbRequest request,
        Vector2 a, Vector2 b, Vector2 anchor, double rotation, int maximumGap, Func<double, double, Vector2> pixels,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        (Vector2 referenceA, Vector2 referenceB) = request.ReferenceBone is { Length: 4 } bone
            ? (pixels(bone[0], bone[1]), pixels(bone[2], bone[3])) : (a, b);
        float radius = Math.Clamp(Vector2.Distance(a, b) * 0.2f, 8, 40);
        List<Feature> EndFeatures(byte[] luma, Vector2 point) => FindFeatures(luma, width, height,
            new(point.X / width, point.Y / height, 2 * radius / width, 2 * radius / height), 8, 5);
        byte[] luma = Luma(renderer.Render(first));
        List<Feature> featuresA = EndFeatures(luma, a), featuresB = EndFeatures(luma, b);
        if (featuresA.Count < 2 || featuresB.Count < 2)
            throw new InvalidOperationException("Both ends of the line need visible detail. Start and finish it on textured points such as a cuff, knee or sleeve edge.");
        double start = first / frameRate;
        var keys = new List<InkObjectPose>
            { geometry(LimbPose(start, referenceA, referenceB, a, b, anchor, rotation, width, height)) };
        rotation = keys[0].Rotation;
        float initialLength = Vector2.Distance(a, b);
        int heldA = 0, heldB = 0;
        for (int frame = first + 1; frame <= last; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double seconds = frame / frameRate;
            byte[] current = Luma(renderer.Render(frame));
            (bool trackedA, Vector2 moveA) = MedianDisplacement(featuresA, current, width, height, heldA > 0 ? 28 : 12);
            (bool trackedB, Vector2 moveB) = MedianDisplacement(featuresB, current, width, height, heldB > 0 ? 28 : 12);
            Vector2 segment = b - a;
            if (trackedA && trackedB) { a += moveA; b += moveB; heldA = heldB = 0; }
            else if (trackedA) { a += moveA; b = a + segment; heldB++; }
            else if (trackedB) { b += moveB; a = b - segment; heldA++; }
            else
                return new(keys, seconds, $"Both ends of the limb were hidden at {seconds:0.###} s. Redraw the line on a later visible frame and resume.",
                    ToBone(referenceA, referenceB, width, height));
            float ratio = Vector2.Distance(a, b) / initialLength;
            if (heldA > maximumGap || heldB > maximumGap || ratio is < MinimumLengthRatio or > MaximumLengthRatio ||
                !Inside(a, width, height) || !Inside(b, width, height))
                return new(keys, seconds, $"The limb was lost at {seconds:0.###} s. Redraw the line on a later visible frame and resume.",
                    ToBone(referenceA, referenceB, width, height));
            keys.Add(geometry(LimbPose(seconds, referenceA, referenceB, a, b, anchor, rotation, width, height)));
            rotation = keys[^1].Rotation;
            // Refresh templates so the ends follow changing appearance; keep shifted old ones if detail vanishes.
            List<Feature> nextA = EndFeatures(current, a), nextB = EndFeatures(current, b);
            featuresA = nextA.Count >= 2 ? nextA : featuresA.Select(f => f with { Point = f.Point + (trackedA ? moveA : Vector2.Zero) }).ToList();
            featuresB = nextB.Count >= 2 ? nextB : featuresB.Select(f => f with { Point = f.Point + (trackedB ? moveB : Vector2.Zero) }).ToList();
            progress?.Report((frame - first) / (double)Math.Max(1, last - first));
        }
        return new(keys, null, "Limb tracking completed. Review the motion before exporting.",
            ToBone(referenceA, referenceB, width, height));
    }

    private static InkTrackingResult TrackPose(VideoExporter.FrameRenderer renderer, int width, int height,
        int first, int last, double frameRate, Func<InkObjectPose, InkObjectPose> geometry, InkLimbRequest request,
        Vector2 a, Vector2 b, Vector2 anchor, double rotation, int maximumGap, Func<double, double, Vector2> pixels,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var store = new OnDeviceModelStore(request.ModelDirectory ?? OnDeviceModels.DefaultDirectory);
        if (!store.IsInstalled(OnDeviceModels.PoseEstimator))
            throw new InvalidOperationException("Install the pose model in AI models first, or choose two-point limb tracking.");
        using var model = new RtmPoseModel(store.GetPath(OnDeviceModels.PoseEstimator));
        static Vector2 Crop(Vector2 a, Vector2 b) { float size = Math.Max(48, Vector2.Distance(a, b) * 3); return new(size, size); }
        PoseKeypoint[] keypoints = model.Estimate(renderer.Render(first), width, height, (a + b) / 2, Crop(a, b));
        (int A, int B) joints = request.PoseJoints is [int first0, int first1]
            ? (first0, first1)
            : RtmPoseModel.NearestBone(keypoints, a, b) ??
              throw new InvalidOperationException("No body segment was found along the line. Draw it from one joint to the next, for example shoulder to elbow.");
        if (keypoints[joints.A].Confidence < RtmPoseModel.ConfidenceThreshold ||
            keypoints[joints.B].Confidence < RtmPoseModel.ConfidenceThreshold)
            throw new InvalidOperationException("That body segment is not clearly visible at the playhead. Choose a frame where both joints can be seen.");
        a = keypoints[joints.A].Point;
        b = keypoints[joints.B].Point;
        (Vector2 referenceA, Vector2 referenceB) = request.ReferenceBone is { Length: 4 } bone
            ? (pixels(bone[0], bone[1]), pixels(bone[2], bone[3])) : (a, b);
        double start = first / frameRate;
        var keys = new List<InkObjectPose>
            { geometry(LimbPose(start, referenceA, referenceB, a, b, anchor, rotation, width, height)) };
        rotation = keys[0].Rotation;
        float initialLength = Vector2.Distance(a, b);
        int held = 0, missing = 0;
        double? firstMissing = null;
        double[] Bone() => ToBone(referenceA, referenceB, width, height);
        for (int frame = first + 1; frame <= last; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double seconds = frame / frameRate;
            keypoints = model.Estimate(renderer.Render(frame), width, height, (a + b) / 2, Crop(a, b));
            bool seenA = keypoints[joints.A].Confidence >= RtmPoseModel.ConfidenceThreshold;
            bool seenB = keypoints[joints.B].Confidence >= RtmPoseModel.ConfidenceThreshold;
            Vector2 segment = b - a, nextA = a, nextB = b;
            if (seenA && seenB) { nextA = keypoints[joints.A].Point; nextB = keypoints[joints.B].Point; }
            else if (seenA) { nextA = keypoints[joints.A].Point; nextB = nextA + segment; }
            else if (seenB) { nextB = keypoints[joints.B].Point; nextA = nextB - segment; }
            float ratio = Vector2.Distance(nextA, nextB) / initialLength;
            bool usable = (seenA || seenB) && ratio is >= MinimumLengthRatio and <= MaximumLengthRatio &&
                Inside(nextA, width, height) && Inside(nextB, width, height);
            if (!usable)
            {
                // Leave a gap: keys either side are interpolated if the limb reappears soon enough.
                firstMissing ??= seconds;
                if (++missing > maximumGap)
                    return new(keys, firstMissing, $"The limb was hidden from {firstMissing:0.###} s. Redraw the line on a later visible frame and resume.",
                        Bone(), [joints.A, joints.B]);
                continue;
            }
            held = seenA && seenB ? 0 : held + 1;
            if (held > maximumGap)
                return new(keys, seconds, $"One joint stayed hidden until {seconds:0.###} s. Redraw the line on a later visible frame and resume.",
                    Bone(), [joints.A, joints.B]);
            missing = 0;
            firstMissing = null;
            a = nextA;
            b = nextB;
            keys.Add(geometry(LimbPose(seconds, referenceA, referenceB, a, b, anchor, rotation, width, height)));
            rotation = keys[^1].Rotation;
            progress?.Report((frame - first) / (double)Math.Max(1, last - first));
        }
        string providers = WindowsMlRuntime.Providers.Count == 0 ? "CPU" : string.Join(", ", WindowsMlRuntime.Providers);
        return new(keys, firstMissing, firstMissing is double lost
                ? $"The limb was hidden at the end of the range from {lost:0.###} s."
                : $"AI limb tracking completed on this device ({providers}). Review the motion before exporting.",
            Bone(), [joints.A, joints.B]);
    }

    private static (bool Tracked, Vector2 Move) MedianDisplacement(List<Feature> features, byte[] luma, int width,
        int height, int radius)
    {
        var dx = new List<float>();
        var dy = new List<float>();
        foreach (Feature feature in features)
        {
            int x = (int)Math.Round(feature.Point.X), y = (int)Math.Round(feature.Point.Y);
            if (x < 5 || y < 5 || x >= width - 5 || y >= height - 5) continue;
            var match = VideoMaskTracking.TrackPoint(feature.Patch, luma, width, height, x, y, radius);
            if (match.Error > 36) continue;
            dx.Add(match.X - feature.Point.X);
            dy.Add(match.Y - feature.Point.Y);
        }
        if (dx.Count < 2) return (false, Vector2.Zero);
        dx.Sort();
        dy.Sort();
        return (true, new(dx[dx.Count / 2], dy[dy.Count / 2]));
    }

    private static bool Inside(Vector2 point, int width, int height) =>
        point.X >= 0 && point.Y >= 0 && point.X < width && point.Y < height;

    private static double[] ToBone(Vector2 a, Vector2 b, int width, int height) =>
        [a.X / width, a.Y / height, b.X / width, b.Y / height];
}
