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
        CancellationToken cancellationToken, InkObjectTracking? previousTracking = null, bool perspective = false,
        bool deformable = false)
    {
        if (perspective && deformable)
            throw new ArgumentException("Choose either planar or deformable tracking, not both.", nameof(deformable));
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
        // Analysis uses its own aspect-adjusted sampling frame, not a letterboxed export.
        project.CanvasSize = null;
        // Tracking samples 8-bit luminance; HDR export settings do not apply to analysis.
        project.Colour = null;
        track.Clips = [clip];
        track.IsVisible = true;
        project.VideoMulticamGroups.Clear();
        // The analysed picture must not include node-graph colour changes or drawing.
        project.CompositeGraph = null;
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
        if (perspective)
            initialPose = initialPose with { Corners = InitialCorners(box, previousTracking, start) };
        if (deformable)
            initialPose = initialPose with { Mesh = InitialMesh(box, previousTracking, start) };
        const int width = 384;
        int height = Math.Clamp((int)Math.Round(width / aspectRatio), 96, 768);
        var profile = new VideoExportProfile(ExportResolution.P720, width, height, project.FrameRate);
        using var device = new CanvasDevice();
        using var renderer = new VideoExporter.FrameRenderer(device,
            new(project, profile, VideoExportCodec.H264, "", height), []);
        int first = (int)Math.Round(start * project.FrameRate);
        int last = (int)Math.Ceiling(end * project.FrameRate) - 1;
        byte[] previous = Luma(renderer.Render(first));
        if (deformable)
            return TrackDeformable(renderer, previous, first, last, start, width, height, box, initialPose, Geometry,
                project.FrameRate, progress, cancellationToken);
        List<Feature> features = FindFeatures(previous, width, height, box);
        if (features.Count < 6)
            throw new InvalidOperationException("The box needs more visible detail. Enlarge it around a textured, rigid part of the object.");
        var keys = new List<InkObjectPose> { initialPose with { Seconds = start } };
        Vector2 centre = new((float)box.X * width, (float)box.Y * height);
        double scale = initialPose.Scale;
        double rotation = initialPose.Rotation;
        Vector2[]? corners = perspective ? Denormalise(initialPose.Corners!, width, height) : null;
        double initialArea = corners is null ? 1 : Math.Max(1, Math.Abs(InkHomography.QuadArea(corners)));
        double previousAngle = corners is null ? 0 : TopEdgeAngle(corners);
        for (int frame = first + 1; frame <= last; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] current = Luma(renderer.Render(frame));
            List<(Vector2 From, Vector2 To)> MatchFeatures(int searchRadius)
            {
                var matches = new List<(Vector2 From, Vector2 To)>();
                foreach (Feature feature in features)
                {
                    var match = VideoMaskTracking.TrackPoint(feature.Patch, current, width, height,
                        (int)Math.Round(feature.Point.X), (int)Math.Round(feature.Point.Y), searchRadius);
                    if (match.Error <= 36)
                        matches.Add((feature.Point, new(match.X, match.Y)));
                }
                return matches;
            }

            var matches = MatchFeatures(12);
            Matrix3x2 motion = Matrix3x2.Identity;
            InkHomography step = InkHomography.Identity;
            double stepScale = 1, stepRotation = 0;
            bool Fit(List<(Vector2 From, Vector2 To)> candidate, out Matrix3x2 similarity, out InkHomography homography,
                out double fittedScale, out double fittedRotation)
            {
                similarity = Matrix3x2.Identity;
                homography = InkHomography.Identity;
                fittedScale = 1;
                fittedRotation = 0;
                return perspective
                    ? TryFitHomography(candidate, out homography)
                    : TryFitSimilarity(candidate, out similarity, out fittedScale, out fittedRotation);
            }
            bool fitted = Fit(matches, out motion, out step, out stepScale, out stepRotation);
            if (!fitted || matches.Count < Math.Ceiling(features.Count * 0.75))
            {
                List<(Vector2 From, Vector2 To)> widerMatches = MatchFeatures(28);
                bool widerFit = Fit(widerMatches, out Matrix3x2 widerMotion, out InkHomography widerStep,
                    out double widerScale, out double widerRotation);
                if (widerFit && (!fitted || widerMatches.Count > matches.Count))
                {
                    matches = widerMatches;
                    motion = widerMotion;
                    step = widerStep;
                    stepScale = widerScale;
                    stepRotation = widerRotation;
                    fitted = true;
                }
            }
            double seconds = frame / project.FrameRate;
            Vector2[]? nextCorners = null;
            if (fitted && perspective)
            {
                nextCorners = corners!.Select(step.Apply).ToArray();
                double area = Math.Abs(InkHomography.QuadArea(nextCorners));
                double previousArea = Math.Abs(InkHomography.QuadArea(corners!));
                fitted = InkHomography.IsConvexQuad(nextCorners, 4) && area / previousArea is > 0.6 and < 1.6;
            }
            if (!fitted)
                return new(keys, seconds, $"Object lost at {seconds:0.###} s. Seek to the flagged point, reselect the object box and resume.");
            Vector2 Map(Vector2 point) => perspective ? step.Apply(point) : Vector2.Transform(point, motion);
            if (perspective)
            {
                corners = nextCorners!;
                centre = corners.Aggregate(Vector2.Zero, (sum, corner) => sum + corner) / 4;
                scale = initialPose.Scale * Math.Sqrt(Math.Abs(InkHomography.QuadArea(corners)) / initialArea);
                double angle = TopEdgeAngle(corners);
                rotation += Math.IEEERemainder(angle - previousAngle, 360);
                previousAngle = angle;
            }
            else
            {
                centre = Vector2.Transform(centre, motion);
                scale *= stepScale;
                rotation += stepRotation;
            }
            if (scale is < 0.05 or > 20 || centre.X < 0 || centre.X >= width || centre.Y < 0 || centre.Y >= height)
                return new(keys, seconds, $"Object left the usable tracking region at {seconds:0.###} s. Correct the box and resume.");
            keys.Add(Geometry(new(seconds, centre.X / width, centre.Y / height, scale, rotation,
                Corners: perspective ? Normalise(corners!, width, height) : null)));
            features = matches.Where(match => Vector2.Distance(Map(match.From), match.To) <= 2)
                .Where(match => match.To.X >= 5 && match.To.X < width - 5 && match.To.Y >= 5 && match.To.Y < height - 5)
                .Select(match => new Feature(match.To, Patch(current, width, match.To))).ToList();
            if (features.Count < 16)
            {
                InkTrackingBox currentBox = perspective
                    ? ShrunkBounds(corners!, width, height)
                    : new InkTrackingBox(centre.X / width, centre.Y / height,
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

    /// <summary>
    /// RANSAC homography over tracked feature pairs, refined on the inliers. Deterministic so repeated analyses agree.
    /// </summary>
    public static bool TryFitHomography(IReadOnlyList<(Vector2 From, Vector2 To)> matches, out InkHomography homography)
    {
        homography = InkHomography.Identity;
        if (matches.Count < 6) return false;
        var random = new Random(matches.Count * 7919 + 17);
        List<(Vector2 From, Vector2 To)> best = [];
        var sample = new (Vector2 From, Vector2 To)[4];
        for (int iteration = 0; iteration < 250; iteration++)
        {
            for (int i = 0; i < 4; i++) sample[i] = matches[random.Next(matches.Count)];
            if (!InkHomography.IsConvexQuad(sample.Select(pair => pair.From).ToArray(), 40) ||
                !InkHomography.TryFit(sample, out InkHomography candidate)) continue;
            var inliers = matches.Where(match => Vector2.Distance(candidate.Apply(match.From), match.To) <= 2).ToList();
            if (inliers.Count > best.Count) best = inliers;
            if (best.Count == matches.Count) break;
        }
        if (best.Count < 6 || best.Count < matches.Count * 0.6 || !InkHomography.TryFit(best, out homography))
            return false;
        InkHomography refined = homography;
        var refinedInliers = matches.Where(match => Vector2.Distance(refined.Apply(match.From), match.To) <= 2).ToList();
        return refinedInliers.Count >= best.Count * 0.9;
    }

    private static double[] InitialCorners(InkTrackingBox box, InkObjectTracking? previousTracking, double start)
    {
        double left = box.X - box.Width / 2, right = box.X + box.Width / 2;
        double top = box.Y - box.Height / 2, bottom = box.Y + box.Height / 2;
        double[] rectangle = [left, top, right, top, right, bottom, left, bottom];
        if (previousTracking is not { Perspective: true, Keys.Count: > 0 }) return rectangle;
        // When resuming, keep the last reliable perspective shape and fit it into the corrected box.
        double reliable = previousTracking.LostSeconds is double lost ? Math.Min(start, lost) : start;
        if (InkObjectMotion.Evaluate(previousTracking, reliable).Corners is not { Length: 8 } shape) return rectangle;
        return FitShape(shape, box) ?? rectangle;
    }

    private static double[] InitialMesh(InkTrackingBox box, InkObjectTracking? previousTracking, double start)
    {
        double[] grid = InkObjectMotion.ReferenceMesh(box.X, box.Y, box.Width, box.Height);
        if (previousTracking is not { Deformable: true, Keys.Count: > 0 }) return grid;
        // When resuming, keep the last reliable bend and fit it into the corrected box.
        double reliable = previousTracking.LostSeconds is double lost ? Math.Min(start, lost) : start;
        if (InkObjectMotion.Evaluate(previousTracking, reliable).Mesh is not { } shape || shape.Length != grid.Length)
            return grid;
        return FitShape(shape, box) ?? grid;
    }

    private static double[]? FitShape(double[] shape, InkTrackingBox box)
    {
        double left = box.X - box.Width / 2, top = box.Y - box.Height / 2;
        double minX = shape.Where((_, i) => i % 2 == 0).Min(), maxX = shape.Where((_, i) => i % 2 == 0).Max();
        double minY = shape.Where((_, i) => i % 2 == 1).Min(), maxY = shape.Where((_, i) => i % 2 == 1).Max();
        if (maxX - minX < 1e-6 || maxY - minY < 1e-6) return null;
        return shape.Select((value, i) => i % 2 == 0
            ? left + (value - minX) / (maxX - minX) * box.Width
            : top + (value - minY) / (maxY - minY) * box.Height).ToArray();
    }

    /// <summary>
    /// Mesh tracking for a smoothly bending, textured surface. Each feature keeps its original template (refreshed
    /// only when appearance drifts) so integer matching does not accumulate drift; locally inconsistent matches are
    /// rejected; and each mesh node follows a Gaussian-weighted local affine fit of nearby feature motion.
    /// </summary>
    private static InkTrackingResult TrackDeformable(VideoExporter.FrameRenderer renderer, byte[] previous,
        int first, int last, double start, int width, int height, InkTrackingBox box, InkObjectPose initialPose,
        Func<InkObjectPose, InkObjectPose> geometry, double frameRate, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        const int maximumFeatures = 48;
        const float featureSpacing = 8;
        List<Feature> features = FindFeatures(previous, width, height, box, maximumFeatures, featureSpacing);
        if (features.Count < 8)
            throw new InvalidOperationException("The box needs more visible detail. Enlarge it around a textured part of the bending surface.");
        var keys = new List<InkObjectPose> { initialPose with { Seconds = start } };
        Vector2[] nodes = Denormalise(initialPose.Mesh!, width, height);
        int[] triangles = MeshTriangleIndices();
        int[] referenceSigns = TriangleAreas(nodes, triangles).Select(Math.Sign).ToArray();
        if (referenceSigns.Contains(0))
            throw new InvalidOperationException("The resumed mesh is folded. Clear tracking and start a new attachment.");
        double initialHull = Math.Max(1, Math.Abs(HullArea(nodes)));
        double rotation = initialPose.Rotation;
        double previousAngle = MeshTopEdgeAngle(nodes);
        for (int frame = first + 1; frame <= last; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] current = Luma(renderer.Render(frame));
            double seconds = frame / frameRate;
            string lostMessage = $"Surface lost at {seconds:0.###} s. Seek to the flagged point, reselect the surface box and resume.";
            List<(int Index, Vector2 From, Vector2 To, double Error)> Match(int searchRadius)
            {
                var found = new List<(int, Vector2, Vector2, double)>();
                for (int i = 0; i < features.Count; i++)
                {
                    var match = VideoMaskTracking.TrackPoint(features[i].Patch, current, width, height,
                        (int)Math.Round(features[i].Point.X), (int)Math.Round(features[i].Point.Y), searchRadius);
                    if (match.Error <= 36) found.Add((i, features[i].Point, new Vector2(match.X, match.Y), match.Error));
                }
                return found;
            }
            var matches = Match(12);
            if (matches.Count < Math.Max(8, features.Count * 0.6))
            {
                var wider = Match(28);
                if (wider.Count > matches.Count) matches = wider;
            }
            float spacing = MeanMeshSpacing(nodes);
            float neighbourhood = spacing * 1.5f;
            var consistent = matches.Where(match =>
            {
                var near = matches.Where(other => Vector2.Distance(other.From, match.From) <= neighbourhood)
                    .Select(other => other.To - other.From).ToList();
                Vector2 median = new(Median(near.Select(d => d.X)), Median(near.Select(d => d.Y)));
                return Vector2.Distance(match.To - match.From, median) <= 3;
            }).ToList();
            if (consistent.Count < 8) return new(keys, seconds, lostMessage);

            Vector2 meanDisplacement = consistent.Aggregate(Vector2.Zero, (sum, match) => sum + match.To - match.From) /
                consistent.Count;
            float sigma = Math.Max(4, spacing * 0.6f);
            var next = new Vector2[nodes.Length];
            for (int n = 0; n < nodes.Length; n++)
                next[n] = nodes[n] + LocalDisplacement(nodes[n], consistent.Select(match => (match.From, match.To)),
                    meanDisplacement, sigma);
            double[] areas = TriangleAreas(next, triangles);
            double[] previousAreas = TriangleAreas(nodes, triangles);
            double hullRatio = Math.Abs(HullArea(next)) / Math.Max(1, Math.Abs(HullArea(nodes)));
            bool valid = next.All(point => float.IsFinite(point.X) && float.IsFinite(point.Y)) &&
                hullRatio is > 0.6 and < 1.6;
            for (int t = 0; valid && t < areas.Length; t++)
                valid = Math.Sign(areas[t]) == referenceSigns[t] && areas[t] / previousAreas[t] is > 0.4 and < 2.5;
            if (!valid) return new(keys, seconds, lostMessage);
            nodes = next;
            Vector2 centre = nodes.Aggregate(Vector2.Zero, (sum, node) => sum + node) / nodes.Length;
            double scale = initialPose.Scale * Math.Sqrt(Math.Abs(HullArea(nodes)) / initialHull);
            double angle = MeshTopEdgeAngle(nodes);
            rotation += Math.IEEERemainder(angle - previousAngle, 360);
            previousAngle = angle;
            if (scale is < 0.05 or > 20 || centre.X < 0 || centre.X >= width || centre.Y < 0 || centre.Y >= height)
                return new(keys, seconds, $"Surface left the usable tracking region at {seconds:0.###} s. Correct the box and resume.");
            keys.Add(geometry(new(seconds, centre.X / width, centre.Y / height, scale, rotation,
                Mesh: Normalise(nodes, width, height))));

            features = consistent
                .Where(match => match.To.X >= 5 && match.To.X < width - 5 && match.To.Y >= 5 && match.To.Y < height - 5)
                .Select(match => new Feature(match.To,
                    match.Error > 18 ? Patch(current, width, match.To) : features[match.Index].Patch)).ToList();
            if (features.Count < 24)
            {
                foreach (Feature feature in FindFeatures(current, width, height, ShrunkBounds(nodes, width, height),
                    maximumFeatures, featureSpacing))
                {
                    if (features.All(existing => Vector2.Distance(existing.Point, feature.Point) >= featureSpacing))
                        features.Add(feature);
                    if (features.Count >= maximumFeatures) break;
                }
            }
            progress?.Report((frame - first) / (double)Math.Max(1, last - first));
        }
        return new(keys, null, "Surface tracking completed. Review the bend before exporting.");
    }

    /// <summary>
    /// Moving-least-squares displacement at a node: a Gaussian-weighted affine fit of nearby feature motion, with a
    /// weak prior towards the overall motion so sparsely covered nodes stay stable. The affine term lets edge nodes
    /// extrapolate a local bend instead of averaging towards the interior.
    /// </summary>
    private static Vector2 LocalDisplacement(Vector2 node, IEnumerable<(Vector2 From, Vector2 To)> matches,
        Vector2 prior, float sigma)
    {
        const double priorWeight = 0.05;
        double[,] normal = new double[3, 3];
        double[] rightX = new double[3], rightY = new double[3];
        void Add(double dx, double dy, Vector2 displacement, double weight)
        {
            double[] basis = [1, dx, dy];
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++) normal[r, c] += weight * basis[r] * basis[c];
                rightX[r] += weight * basis[r] * displacement.X;
                rightY[r] += weight * basis[r] * displacement.Y;
            }
        }
        double total = 0;
        foreach ((Vector2 from, Vector2 to) in matches)
        {
            Vector2 offset = from - node;
            double weight = Math.Exp(-offset.LengthSquared() / (2.0 * sigma * sigma));
            if (weight < 1e-6) continue;
            total += weight;
            Add(offset.X, offset.Y, to - from, weight);
        }
        Add(0, 0, prior, priorWeight);
        double ridge = 0.01 * (total + priorWeight) * sigma * sigma;
        normal[1, 1] += ridge;
        normal[2, 2] += ridge;
        return Solve3(normal, rightX) is double x && Solve3(normal, rightY) is double y ? new((float)x, (float)y) : prior;
    }

    /// <summary>Solves a 3×3 linear system by Cramer's rule, returning only the first unknown.</summary>
    private static double? Solve3(double[,] m, double[] b)
    {
        double Determinant(double[,] a) =>
            a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) -
            a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0]) +
            a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
        double determinant = Determinant(m);
        if (Math.Abs(determinant) < 1e-12) return null;
        var first = (double[,])m.Clone();
        for (int r = 0; r < 3; r++) first[r, 0] = b[r];
        double value = Determinant(first) / determinant;
        return double.IsFinite(value) ? value : null;
    }

    private static int[] MeshTriangleIndices()
    {
        const int size = InkObjectMotion.MeshSize;
        var indices = new List<int>();
        for (int row = 0; row < size - 1; row++)
        for (int column = 0; column < size - 1; column++)
        {
            int a = row * size + column, b = a + 1, c = a + size + 1, d = a + size;
            indices.AddRange([a, b, c, a, c, d]);
        }
        return indices.ToArray();
    }

    private static double[] TriangleAreas(Vector2[] nodes, int[] indices) =>
        Enumerable.Range(0, indices.Length / 3).Select(t =>
        {
            Vector2 a = nodes[indices[t * 3]], b = nodes[indices[t * 3 + 1]], c = nodes[indices[t * 3 + 2]];
            return 0.5 * ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X));
        }).ToArray();

    private static double HullArea(Vector2[] nodes)
    {
        const int size = InkObjectMotion.MeshSize;
        var boundary = new List<Vector2>();
        for (int c = 0; c < size; c++) boundary.Add(nodes[c]);
        for (int r = 1; r < size; r++) boundary.Add(nodes[r * size + size - 1]);
        for (int c = size - 2; c >= 0; c--) boundary.Add(nodes[(size - 1) * size + c]);
        for (int r = size - 2; r > 0; r--) boundary.Add(nodes[r * size]);
        return InkHomography.QuadArea(boundary.ToArray());
    }

    private static double MeshTopEdgeAngle(Vector2[] nodes)
    {
        Vector2 a = nodes[0], b = nodes[InkObjectMotion.MeshSize - 1];
        return Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
    }

    private static float MeanMeshSpacing(Vector2[] nodes)
    {
        const int size = InkObjectMotion.MeshSize;
        double total = 0;
        int count = 0;
        for (int row = 0; row < size; row++)
        for (int column = 0; column < size; column++)
        {
            int node = row * size + column;
            if (column < size - 1) { total += Vector2.Distance(nodes[node], nodes[node + 1]); count++; }
            if (row < size - 1) { total += Vector2.Distance(nodes[node], nodes[node + size]); count++; }
        }
        return (float)(total / count);
    }

    private static float Median(IEnumerable<float> values)
    {
        float[] sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0) return 0;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static Vector2[] Denormalise(double[] corners, int width, int height) =>
        Enumerable.Range(0, corners.Length / 2).Select(i => new Vector2((float)(corners[i * 2] * width), (float)(corners[i * 2 + 1] * height))).ToArray();

    private static double[] Normalise(Vector2[] corners, int width, int height) =>
        corners.SelectMany(corner => new[] { corner.X / (double)width, corner.Y / (double)height }).ToArray();

    private static double TopEdgeAngle(Vector2[] corners) =>
        Math.Atan2(corners[1].Y - corners[0].Y, corners[1].X - corners[0].X) * 180 / Math.PI;

    private static InkTrackingBox ShrunkBounds(Vector2[] corners, int width, int height)
    {
        double minX = corners.Min(corner => corner.X) / width, maxX = corners.Max(corner => corner.X) / width;
        double minY = corners.Min(corner => corner.Y) / height, maxY = corners.Max(corner => corner.Y) / height;
        return new((minX + maxX) / 2, (minY + maxY) / 2,
            Math.Min(1, (maxX - minX) * 0.85), Math.Min(1, (maxY - minY) * 0.85));
    }

    private static List<Feature> FindFeatures(byte[] luma, int width, int height, InkTrackingBox box,
        int maximum = 24, float spacing = 10)
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
            if (features.All(feature => Vector2.Distance(feature.Point, candidate.Point) >= spacing))
                features.Add(new(candidate.Point, Patch(luma, width, candidate.Point)));
            if (features.Count == maximum) break;
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
