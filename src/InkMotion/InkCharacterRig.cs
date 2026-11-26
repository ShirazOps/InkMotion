using System.Numerics;

namespace InkMotion;

public sealed class InkCharacterRig
{
    public bool Enabled { get; set; } = true;
    public List<InkRigBone> Bones { get; set; } = [];
}

public sealed class InkRigBone
{
    public string Id { get; set; } = $"bone-{Guid.NewGuid():N}";
    public string Name { get; set; } = "Bone";
    public string? ParentId { get; set; }
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    public double EndX { get; set; } = 0.5;
    public double EndY { get; set; } = 0.7;
    public List<InkRigPose> Keys { get; set; } = [];
}

public sealed record InkRigPose(double Seconds, double Rotation = 0, double X = 0, double Y = 0,
    double Stretch = 1, InkInterpolationMode Interpolation = InkInterpolationMode.Linear);

public enum InkRigBindingMode { CutOut, Weighted }

public sealed class InkRigBinding
{
    public InkRigBindingMode Mode { get; set; }
    public string BoneId { get; set; } = "";
    /// <summary>Weighted mode: positive influence multipliers, normalised per point by distance to rest bones.</summary>
    public Dictionary<string, double> Influences { get; set; } = new(StringComparer.Ordinal);
}

public static class InkCharacterRigs
{
    public static InkCharacterRig Clone(InkCharacterRig rig) => new()
    {
        Enabled = rig.Enabled,
        Bones = rig.Bones.Select(bone => new InkRigBone
        {
            Id = bone.Id, Name = bone.Name, ParentId = bone.ParentId,
            X = bone.X, Y = bone.Y, EndX = bone.EndX, EndY = bone.EndY, Keys = bone.Keys.ToList(),
        }).ToList(),
    };

    public static InkRigBinding Clone(InkRigBinding binding) => new()
    {
        Mode = binding.Mode, BoneId = binding.BoneId,
        Influences = new(binding.Influences, StringComparer.Ordinal),
    };

    public static string? Validate(InkCharacterRig? rig, IReadOnlyList<InkLayerProject> layers)
    {
        if (rig is not null)
        {
            if (rig.Bones is not { Count: <= 64 } ||
                rig.Bones.Any(bone => bone is null || string.IsNullOrWhiteSpace(bone.Id) ||
                    string.IsNullOrWhiteSpace(bone.Name) || bone.Name.Length > 80 ||
                    !Coordinate(bone.X) || !Coordinate(bone.Y) || !Coordinate(bone.EndX) || !Coordinate(bone.EndY) ||
                    Math.Abs(bone.X - bone.EndX) + Math.Abs(bone.Y - bone.EndY) < 0.0001 ||
                    bone.Keys is not { Count: <= 10000 } ||
                    bone.Keys.Any(key => key is null || !double.IsFinite(key.Seconds) || key.Seconds is < 0 or > 86400 ||
                        !double.IsFinite(key.Rotation) || key.Rotation is < -3600 or > 3600 ||
                        !double.IsFinite(key.X) || key.X is < -10 or > 10 ||
                        !double.IsFinite(key.Y) || key.Y is < -10 or > 10 ||
                        !double.IsFinite(key.Stretch) || key.Stretch is < 0.01 or > 100 ||
                        !Enum.IsDefined(key.Interpolation)) ||
                    !bone.Keys.Zip(bone.Keys.Skip(1)).All(pair => pair.First.Seconds < pair.Second.Seconds)) ||
                rig.Bones.Select(bone => bone.Id).Distinct(StringComparer.Ordinal).Count() != rig.Bones.Count)
                return "A rig supports at most 64 named, non-zero bones with finite rest coordinates and ordered pose keys.";

            var bones = rig.Bones.ToDictionary(bone => bone.Id, StringComparer.Ordinal);
            foreach (InkRigBone bone in rig.Bones)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal) { bone.Id };
                string? parent = bone.ParentId;
                while (parent is not null)
                {
                    if (!visited.Add(parent) || !bones.TryGetValue(parent, out InkRigBone? ancestor))
                        return "Bone parents must exist and cannot form a cycle.";
                    parent = ancestor.ParentId;
                }
            }
        }
        foreach (InkRigBinding binding in layers.Select(layer => layer.RigBinding).OfType<InkRigBinding>())
        {
            if (rig is null || !Enum.IsDefined(binding.Mode) || binding.Influences is null ||
                !rig.Bones.Any(bone => bone.Id == binding.BoneId) ||
                binding.Influences.Any(pair => !rig.Bones.Any(bone => bone.Id == pair.Key) ||
                    !double.IsFinite(pair.Value) || pair.Value is <= 0 or > 100) ||
                (binding.Mode == InkRigBindingMode.Weighted && binding.Influences.Count == 0))
                return "Bind artwork to existing bones; weighted bindings need at least one positive influence.";
        }
        return null;
    }

    private static bool Coordinate(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    public static InkRigPose Evaluate(InkRigBone bone, double seconds)
    {
        if (bone.Keys.Count == 0) return new(seconds);
        InkRigPose left = bone.Keys[0];
        if (seconds <= left.Seconds) return left;
        foreach (InkRigPose right in bone.Keys.Skip(1))
        {
            if (seconds <= right.Seconds)
            {
                double t = (seconds - left.Seconds) / (right.Seconds - left.Seconds);
                t = left.Interpolation switch
                {
                    InkInterpolationMode.EaseIn => t * t,
                    InkInterpolationMode.EaseOut => 1 - (1 - t) * (1 - t),
                    InkInterpolationMode.EaseInOut => t * t * (3 - 2 * t),
                    _ => t,
                };
                return new(seconds, Mix(left.Rotation, right.Rotation, t), Mix(left.X, right.X, t),
                    Mix(left.Y, right.Y, t), Mix(left.Stretch, right.Stretch, t), left.Interpolation);
            }
            left = right;
        }
        return left;
    }

    public static void SetPose(InkRigBone bone, InkRigPose pose)
    {
        bone.Keys.RemoveAll(key => Math.Abs(key.Seconds - pose.Seconds) < 0.000001);
        bone.Keys.Add(pose);
        bone.Keys.Sort((left, right) => left.Seconds.CompareTo(right.Seconds));
    }

    private static double Mix(double a, double b, double t) => a + (b - a) * t;

    public static Dictionary<string, Matrix3x2> Transforms(InkCharacterRig rig, double seconds, float width, float height)
    {
        var result = new Dictionary<string, Matrix3x2>(StringComparer.Ordinal);
        var bones = rig.Bones.ToDictionary(bone => bone.Id, StringComparer.Ordinal);
        Matrix3x2 Resolve(InkRigBone bone)
        {
            if (result.TryGetValue(bone.Id, out Matrix3x2 cached)) return cached;
            InkRigPose pose = Evaluate(bone, seconds);
            var start = new Vector2((float)bone.X * width, (float)bone.Y * height);
            float heading = MathF.Atan2((float)(bone.EndY - bone.Y) * height, (float)(bone.EndX - bone.X) * width);
            Matrix3x2 transform = Matrix3x2.CreateTranslation(-start) *
                Matrix3x2.CreateRotation(-heading) * Matrix3x2.CreateScale((float)pose.Stretch, 1) *
                Matrix3x2.CreateRotation(heading + (float)(pose.Rotation * Math.PI / 180)) *
                Matrix3x2.CreateTranslation(start + new Vector2((float)pose.X * width, (float)pose.Y * height));
            if (bone.ParentId is string parent) transform *= Resolve(bones[parent]);
            result.Add(bone.Id, transform);
            return transform;
        }
        foreach (InkRigBone bone in rig.Bones) Resolve(bone);
        return result;
    }

    public static IReadOnlyDictionary<string, double> Weights(InkCharacterRig rig, InkRigBinding binding,
        double x, double y, float width, float height)
    {
        var point = new Vector2((float)x * width, (float)y * height);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        double total = 0;
        foreach (InkRigBone bone in rig.Bones)
        {
            if (!binding.Influences.TryGetValue(bone.Id, out double influence)) continue;
            var start = new Vector2((float)bone.X * width, (float)bone.Y * height);
            var end = new Vector2((float)bone.EndX * width, (float)bone.EndY * height);
            Vector2 segment = end - start;
            float t = Math.Clamp(Vector2.Dot(point - start, segment) / segment.LengthSquared(), 0, 1);
            double distance = Vector2.DistanceSquared(point, start + segment * t);
            double weight = influence / Math.Max(distance, Math.Pow(Math.Min(width, height) * 0.005, 2));
            weights.Add(bone.Id, weight);
            total += weight;
        }
        foreach (string id in weights.Keys.ToArray()) weights[id] /= total;
        return weights;
    }

    public static IReadOnlyList<List<InkPoint>> Deform(IReadOnlyList<List<InkPoint>> strokes,
        InkCharacterRig? rig, InkRigBinding? binding, double seconds, float width, float height)
    {
        if (rig is not { Enabled: true } || binding is null) return strokes;
        Dictionary<string, Matrix3x2> transforms = Transforms(rig, seconds, width, height);
        var result = new List<List<InkPoint>>(strokes.Count);
        foreach (List<InkPoint> stroke in strokes)
        {
            var output = new List<InkPoint>();
            void Append(InkPoint source)
            {
                var point = new Vector2((float)source.X * width, (float)source.Y * height);
                Vector2 moved;
                if (binding.Mode == InkRigBindingMode.CutOut)
                    moved = Vector2.Transform(point, transforms[binding.BoneId]);
                else
                {
                    moved = Vector2.Zero;
                    foreach ((string id, double weight) in Weights(rig, binding, source.X, source.Y, width, height))
                        moved += Vector2.Transform(point, transforms[id]) * (float)weight;
                }
                InkPoint copy = InkStrokeEditing.Interpolate(source, source, 0);
                copy.X = moved.X / width;
                copy.Y = moved.Y / height;
                output.Add(copy);
            }
            bool closeOutline = binding.Mode == InkRigBindingMode.Weighted && stroke.Count >= 3 &&
                stroke[0].IsFilled && (stroke[0].X != stroke[^1].X || stroke[0].Y != stroke[^1].Y);
            for (int index = 0; index < stroke.Count + (closeOutline ? 1 : 0); index++)
            {
                InkPoint source = stroke[index % stroke.Count];
                if (index > 0 && binding.Mode == InkRigBindingMode.Weighted)
                {
                    InkPoint a = stroke[index - 1], b = source;
                    // Subdivide vector edges before skinning so long lines and filled outlines can bend.
                    int steps = Math.Clamp((int)Math.Ceiling(Math.Sqrt(Math.Pow(a.X - b.X, 2) +
                        Math.Pow(a.Y - b.Y, 2)) * 64), 1, 128);
                    for (int step = 1; step < steps; step++)
                    {
                        double t = (double)step / steps;
                        InkPoint point = InkStrokeEditing.Interpolate(a, a, 0);
                        point.X = Mix(a.X, b.X, t); point.Y = Mix(a.Y, b.Y, t);
                        point.Pressure = Mix(a.Pressure, b.Pressure, t);
                        point.BrushWidth = Mix(a.BrushWidth, b.BrushWidth, t);
                        Append(point);
                    }
                }
                Append(source);
            }
            result.Add(output);
        }
        return result;
    }

    public static IReadOnlyList<List<InkPoint>> Strokes(EditorProject project, int frameIndex,
        InkLayerProject layer, double seconds, float width, float height)
    {
        IReadOnlyList<List<InkPoint>> strokes = InkObjectMotion.Strokes(project.Frames, frameIndex, layer, project.FrameRate);
        if (layer.LipSync is { Enabled: true } sync && InkLipSyncs.IsSourceCurrent(sync, project))
            strokes = sync.Mouths[InkLipSyncs.Evaluate(sync, seconds)];
        if (project.CharacterRig is { Enabled: true } && layer.RigBinding is not null && strokes.Count == 0)
        {
            for (int previous = frameIndex - 1; previous >= 0 && strokes.Count == 0; previous--)
                strokes = InkAnimation.GetDisplayedStrokes(project.Frames, previous,
                    frame => ExportPlanning.GetLayerStrokes(frame, layer.Id));
        }
        return Deform(strokes, project.CharacterRig, layer.RigBinding, seconds, width, height);
    }
}
