using System.Numerics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;

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
    /// <summary>Planar (corner-pin) tracking: each key stores four tracked corners and artwork is warped in perspective.</summary>
    public bool Perspective { get; set; }
    /// <summary>
    /// Deformable (mesh) tracking: each key stores a grid of tracked mesh nodes and artwork is warped piece by piece,
    /// so it can bend with a flexing surface. Mutually exclusive with <see cref="Perspective"/>.
    /// </summary>
    public bool Deformable { get; set; }
    /// <summary>
    /// Articulated (limb) tracking: artwork follows one body segment, rotating and stretching with it, so it stays on
    /// a bending arm or leg. Keys carry position, scale and rotation only.
    /// </summary>
    public InkTrackingArticulation Articulation { get; set; }
    /// <summary>The followed segment at the reference time as x1, y1, x2, y2 in normalised analysis coordinates.</summary>
    public double[]? Bone { get; set; }
    /// <summary>AI pose only: the two COCO keypoint indices of the followed segment.</summary>
    public int[]? PoseJoints { get; set; }
    public List<InkObjectPose> Keys { get; set; } = [];
}

public enum InkTrackingArticulation
{
    None,
    /// <summary>Classical feature tracking at both ends of a segment, holding the last direction when one end is hidden.</summary>
    TwoPoint,
    /// <summary>On-device RTMPose body-pose estimation through Windows ML.</summary>
    Pose,
}

/// <param name="Corners">Planar tracking only: top-left, top-right, bottom-right and bottom-left corners as
/// x, y pairs in normalised analysis-viewer coordinates.</param>
/// <param name="Mesh">Deformable tracking only: <see cref="InkObjectMotion.MeshSize"/> × MeshSize nodes in row-major
/// order as x, y pairs in normalised analysis-viewer coordinates.</param>
public sealed record InkObjectPose(double Seconds, double X, double Y, double Scale = 1, double Rotation = 0,
    double ClipX = 0.5, double ClipY = 0.5, double PictureAspectRatio = 0, double[]? Corners = null,
    double[]? Mesh = null);

public static class InkObjectMotion
{
    /// <summary>Number of mesh nodes along each side of the deformable tracking box.</summary>
    public const int MeshSize = 4;
    private const int MeshValues = MeshSize * MeshSize * 2;
    /// <summary>How far (in normalised viewer units) the rigidly following outer ring extends beyond the mesh.</summary>
    private const double MeshMargin = 2;
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
            double.IsFinite(key.PictureAspectRatio) && key.PictureAspectRatio is >= 0 and <= 1000 &&
            (tracking.Perspective
                ? key.Corners is { Length: 8 } corners && corners.All(value => double.IsFinite(value) && value is >= -10 and <= 11)
                : key.Corners is null) &&
            (tracking.Deformable
                ? key.Mesh is { Length: MeshValues } mesh && mesh.All(value => double.IsFinite(value) && value is >= -10 and <= 11)
                : key.Mesh is null)) &&
        !(tracking.Perspective && tracking.Deformable) &&
        Enum.IsDefined(tracking.Articulation) &&
        (tracking.Articulation == InkTrackingArticulation.None
            ? tracking.Bone is null && tracking.PoseJoints is null
            : !tracking.Perspective && !tracking.Deformable &&
              tracking.Bone is { Length: 4 } bone && bone.All(value => double.IsFinite(value) && value is >= -1 and <= 2) &&
              (tracking.Articulation == InkTrackingArticulation.Pose
                  ? tracking.PoseJoints is { Length: 2 } joints && joints.All(j => j is >= 0 and < RtmPoseModel.KeypointCount) && joints[0] != joints[1]
                  : tracking.PoseJoints is null)) &&
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
                    previous.PictureAspectRatio + (next.PictureAspectRatio - previous.PictureAspectRatio) * amount,
                    previous.Corners is { Length: 8 } from && next.Corners is { Length: 8 } to
                        ? from.Zip(to, (a, b) => a + (b - a) * amount).ToArray()
                        : previous.Corners,
                    previous.Mesh is { Length: MeshValues } fromMesh && next.Mesh is { Length: MeshValues } toMesh
                        ? fromMesh.Zip(toMesh, (a, b) => a + (b - a) * amount).ToArray()
                        : previous.Mesh);
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

    /// <summary>
    /// The perspective warp for planar tracking, or null when the tracking is rigid. The reference box maps to the
    /// tracked corners using the same viewer-to-export refit as <see cref="Transform"/>.
    /// </summary>
    public static Matrix4x4? Projection(InkObjectTracking? tracking, double seconds, float width, float height)
    {
        if (tracking is not { Enabled: true, Perspective: true }) return null;
        InkObjectPose pose = Evaluate(tracking, seconds);
        if (pose.Corners is not { Length: 8 } corners) return null;
        bool refit = tracking.CanvasAspectRatio > 0 && pose.PictureAspectRatio > 0;
        float aspect = (float)tracking.CanvasAspectRatio;
        float spaceX = refit ? aspect : width, spaceY = refit ? 1 : height;
        double left = tracking.AnchorX - tracking.BoxWidth / 2, right = tracking.AnchorX + tracking.BoxWidth / 2;
        double top = tracking.AnchorY - tracking.BoxHeight / 2, bottom = tracking.AnchorY + tracking.BoxHeight / 2;
        double[] reference = [left, top, right, top, right, bottom, left, bottom];
        var pairs = new (Vector2 From, Vector2 To)[4];
        for (int i = 0; i < 4; i++)
            pairs[i] = (new((float)reference[i * 2] * spaceX, (float)reference[i * 2 + 1] * spaceY),
                new((float)corners[i * 2] * spaceX, (float)corners[i * 2 + 1] * spaceY));
        if (!InkHomography.IsConvexQuad(pairs.Select(pair => pair.To).ToArray(), 1e-6) ||
            !InkHomography.TryFit(pairs, out InkHomography homography)) return null;
        if (!refit) return homography.ToMatrix4x4();
        float fit = (float)(Math.Min(width / pose.PictureAspectRatio, height) /
            Math.Min(aspect / pose.PictureAspectRatio, 1));
        var input = new Matrix4x4(Matrix3x2.CreateScale(aspect / width, 1 / height));
        var output = new Matrix4x4(Matrix3x2.CreateTranslation(-(float)pose.ClipX * aspect, -(float)pose.ClipY) *
            Matrix3x2.CreateScale(fit) *
            Matrix3x2.CreateTranslation((float)pose.ClipX * width, (float)pose.ClipY * height));
        return input * homography.ToMatrix4x4() * output;
    }

    /// <summary>The untracked mesh: a regular grid of nodes spanning the given box, in row-major x, y pairs.</summary>
    public static double[] ReferenceMesh(double anchorX, double anchorY, double boxWidth, double boxHeight)
    {
        var mesh = new double[MeshValues];
        for (int row = 0; row < MeshSize; row++)
        for (int column = 0; column < MeshSize; column++)
        {
            int index = (row * MeshSize + column) * 2;
            mesh[index] = anchorX - boxWidth / 2 + boxWidth * column / (MeshSize - 1);
            mesh[index + 1] = anchorY - boxHeight / 2 + boxHeight * row / (MeshSize - 1);
        }
        return mesh;
    }

    /// <summary>
    /// The piecewise-affine warp for deformable tracking as canvas-space triangles, or null when the tracking is not
    /// deformable. Inner triangles follow the tracked mesh; an outer ring follows the mesh's overall (similarity)
    /// motion so artwork drawn outside the box stays attached and continuous with the bent region.
    /// </summary>
    public static IReadOnlyList<(Vector2[] Source, Vector2[] Target)>? MeshTriangles(InkObjectTracking? tracking,
        double seconds, float width, float height)
    {
        if (tracking is not { Enabled: true, Deformable: true }) return null;
        InkObjectPose pose = Evaluate(tracking, seconds);
        if (pose.Mesh is not { Length: MeshValues } mesh) return null;
        bool refit = tracking.CanvasAspectRatio > 0 && pose.PictureAspectRatio > 0;
        float aspect = (float)tracking.CanvasAspectRatio;
        float spaceX = refit ? aspect : width, spaceY = refit ? 1 : height;
        Matrix3x2 output = Matrix3x2.Identity;
        if (refit)
        {
            float fit = (float)(Math.Min(width / pose.PictureAspectRatio, height) /
                Math.Min(aspect / pose.PictureAspectRatio, 1));
            output = Matrix3x2.CreateTranslation(-(float)pose.ClipX * aspect, -(float)pose.ClipY) *
                Matrix3x2.CreateScale(fit) *
                Matrix3x2.CreateTranslation((float)pose.ClipX * width, (float)pose.ClipY * height);
        }
        Vector2 ToCanvas(double x, double y) => refit
            ? Vector2.Transform(new((float)(x * aspect), (float)y), output)
            : new((float)(x * width), (float)(y * height));

        double[] reference = ReferenceMesh(tracking.AnchorX, tracking.AnchorY, tracking.BoxWidth, tracking.BoxHeight);
        var pairs = new (Vector2 From, Vector2 To)[MeshSize * MeshSize];
        for (int i = 0; i < pairs.Length; i++)
            pairs[i] = (new((float)(reference[i * 2] * spaceX), (float)(reference[i * 2 + 1] * spaceY)),
                new((float)(mesh[i * 2] * spaceX), (float)(mesh[i * 2 + 1] * spaceY)));
        if (!TryFitSimilarity(pairs, out Matrix3x2 overall)) return null;

        const int size = MeshSize + 2;
        double left = tracking.AnchorX - tracking.BoxWidth / 2, top = tracking.AnchorY - tracking.BoxHeight / 2;
        double Line(int index, double start, double extent) => index == 0 ? start - MeshMargin :
            index == size - 1 ? start + extent + MeshMargin : start + extent * (index - 1) / (MeshSize - 1);
        var source = new Vector2[size * size];
        var target = new Vector2[size * size];
        for (int row = 0; row < size; row++)
        for (int column = 0; column < size; column++)
        {
            double x = Line(column, left, tracking.BoxWidth), y = Line(row, top, tracking.BoxHeight);
            int node = row * size + column;
            source[node] = new((float)(x * width), (float)(y * height));
            if (row is > 0 and < size - 1 && column is > 0 and < size - 1)
            {
                int index = ((row - 1) * MeshSize + column - 1) * 2;
                target[node] = ToCanvas(mesh[index], mesh[index + 1]);
            }
            else
            {
                Vector2 moved = Vector2.Transform(new((float)(x * spaceX), (float)(y * spaceY)), overall);
                target[node] = ToCanvas(moved.X / spaceX, moved.Y / spaceY);
            }
        }
        if (target.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y))) return null;
        var triangles = new List<(Vector2[] Source, Vector2[] Target)>((size - 1) * (size - 1) * 2);
        for (int row = 0; row < size - 1; row++)
        for (int column = 0; column < size - 1; column++)
        {
            int a = row * size + column, b = a + 1, c = a + size + 1, d = a + size;
            triangles.Add(([source[a], source[b], source[c]], [target[a], target[b], target[c]]));
            triangles.Add(([source[a], source[c], source[d]], [target[a], target[c], target[d]]));
        }
        return triangles;
    }

    /// <summary>Least-squares similarity (translation, rotation, uniform scale) between point pairs.</summary>
    public static bool TryFitSimilarity(IReadOnlyList<(Vector2 From, Vector2 To)> pairs, out Matrix3x2 similarity)
    {
        similarity = Matrix3x2.Identity;
        if (pairs.Count < 2) return false;
        Vector2 sourceCentre = Vector2.Zero, targetCentre = Vector2.Zero;
        foreach (var pair in pairs) { sourceCentre += pair.From; targetCentre += pair.To; }
        sourceCentre /= pairs.Count;
        targetCentre /= pairs.Count;
        double dot = 0, cross = 0, denominator = 0;
        foreach (var pair in pairs)
        {
            Vector2 from = pair.From - sourceCentre, to = pair.To - targetCentre;
            dot += Vector2.Dot(from, to);
            cross += from.X * to.Y - from.Y * to.X;
            denominator += from.LengthSquared();
        }
        if (denominator < 1e-12) return false;
        float a = (float)(dot / denominator), b = (float)(cross / denominator);
        similarity = Matrix3x2.CreateTranslation(-sourceCentre) * new Matrix3x2(a, b, -b, a, 0, 0) *
            Matrix3x2.CreateTranslation(targetCentre);
        return float.IsFinite(a) && float.IsFinite(b) && a * a + b * b > 1e-8;
    }

    /// <summary>The affine map taking the source triangle's vertices onto the target triangle's.</summary>
    public static bool TryMapTriangle(Vector2[] source, Vector2[] target, out Matrix3x2 map)
    {
        map = Matrix3x2.Identity;
        Vector2 e1 = source[1] - source[0], e2 = source[2] - source[0];
        Vector2 f1 = target[1] - target[0], f2 = target[2] - target[0];
        var fromBasis = new Matrix3x2(e1.X, e1.Y, e2.X, e2.Y, source[0].X, source[0].Y);
        var toBasis = new Matrix3x2(f1.X, f1.Y, f2.X, f2.Y, target[0].X, target[0].Y);
        if (Math.Abs(e1.X * e2.Y - e1.Y * e2.X) < 1e-6 || Math.Abs(f1.X * f2.Y - f1.Y * f2.X) < 1e-6 ||
            !Matrix3x2.Invert(fromBasis, out Matrix3x2 inverse)) return false;
        map = inverse * toBasis;
        return true;
    }

    /// <summary>
    /// Draws object-bound content. Rigid tracking uses the session transform; planar tracking records the content
    /// as resolution-independent vector commands and warps it in perspective; deformable tracking replays those
    /// commands once per mesh triangle with that triangle's affine map, clipped without anti-aliasing so neighbouring
    /// triangles meet without seams.
    /// </summary>
    public static void Draw(CanvasDrawingSession session, InkObjectTracking? tracking, double seconds,
        float width, float height, Action<CanvasDrawingSession> draw)
    {
        Matrix3x2 original = session.Transform;
        if (tracking is { Enabled: true, Deformable: true })
        {
            if (MeshTriangles(tracking, seconds, width, height) is not { } triangles) return;
            using var recorded = new CanvasCommandList(session);
            using (CanvasDrawingSession recording = recorded.CreateDrawingSession()) draw(recording);
            CanvasAntialiasing antialiasing = session.Antialiasing;
            try
            {
                foreach ((Vector2[] source, Vector2[] target) in triangles)
                {
                    if (!TryMapTriangle(source, target, out Matrix3x2 map)) continue;
                    session.Transform = original;
                    session.Antialiasing = CanvasAntialiasing.Aliased;
                    using CanvasGeometry clip = CanvasGeometry.CreatePolygon(session, target);
                    using (session.CreateLayer(1, clip))
                    {
                        session.Antialiasing = antialiasing;
                        session.Transform = map * original;
                        session.DrawImage(recorded);
                    }
                }
            }
            finally
            {
                session.Transform = original;
                session.Antialiasing = antialiasing;
            }
            return;
        }
        if (tracking is { Enabled: true, Perspective: true })
        {
            if (Projection(tracking, seconds, width, height) is not Matrix4x4 projection) return;
            using var commands = new CanvasCommandList(session);
            using (CanvasDrawingSession recording = commands.CreateDrawingSession()) draw(recording);
            using var warp = new Transform3DEffect
            {
                Source = commands,
                TransformMatrix = projection,
                InterpolationMode = CanvasImageInterpolation.Anisotropic,
            };
            session.DrawImage(warp);
            return;
        }
        session.Transform = Transform(tracking, seconds, width, height) * original;
        try { draw(session); }
        finally { session.Transform = original; }
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
