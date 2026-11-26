namespace InkMotion;

public enum InkComposite3DShape
{
    Cube,
    Sphere,
    Plane,
}

public sealed class InkComposite3DObject
{
    public InkComposite3DShape Shape { get; set; }
    public bool Visible { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double RotationX { get; set; }
    public double RotationY { get; set; }
    public double RotationZ { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public double ScaleZ { get; set; } = 1;
    public double SpinX { get; set; }
    public double SpinY { get; set; }
    public double SpinZ { get; set; }
    public uint Colour { get; set; } = 0xFF80BFFF;

    public InkComposite3DObject Clone() => (InkComposite3DObject)MemberwiseClone();
}

public sealed class InkCompositeScene3D
{
    public List<InkComposite3DObject> Objects { get; set; } = [new()];
    public double CameraYaw { get; set; } = 30;
    public double CameraPitch { get; set; } = 20;
    public double CameraDistance { get; set; } = 4;
    public double FieldOfView { get; set; } = 45;
    public double LightYaw { get; set; } = 30;
    public double LightPitch { get; set; } = 45;
    public double AmbientLight { get; set; } = 0.35;

    public InkCompositeScene3D Clone() => new()
    {
        Objects = Objects.Select(item => item.Clone()).ToList(),
        CameraYaw = CameraYaw,
        CameraPitch = CameraPitch,
        CameraDistance = CameraDistance,
        FieldOfView = FieldOfView,
        LightYaw = LightYaw,
        LightPitch = LightPitch,
        AmbientLight = AmbientLight,
    };
}

public static class InkCompositeScenes3D
{
    public const int MaxObjects = 32;

    public static bool IsValid(InkCompositeScene3D? scene) =>
        scene is not null &&
        scene.Objects is not null &&
        scene.Objects.Count <= MaxObjects &&
        scene.Objects.All(IsValidObject) &&
        InRange(scene.CameraYaw, -180, 180) &&
        InRange(scene.CameraPitch, -89, 89) &&
        InRange(scene.CameraDistance, 1, 100) &&
        InRange(scene.FieldOfView, 15, 120) &&
        InRange(scene.LightYaw, -180, 180) &&
        InRange(scene.LightPitch, -90, 90) &&
        InRange(scene.AmbientLight, 0, 1);

    private static bool IsValidObject(InkComposite3DObject? item) =>
        item is not null &&
        Enum.IsDefined(item.Shape) &&
        InRange(item.X, -50, 50) && InRange(item.Y, -50, 50) && InRange(item.Z, -50, 50) &&
        InRange(item.RotationX, -180, 180) && InRange(item.RotationY, -180, 180) &&
        InRange(item.RotationZ, -180, 180) &&
        InRange(item.ScaleX, 0.05, 10) && InRange(item.ScaleY, 0.05, 10) &&
        InRange(item.ScaleZ, 0.05, 10) &&
        InRange(item.SpinX, -360, 360) && InRange(item.SpinY, -360, 360) &&
        InRange(item.SpinZ, -360, 360) &&
        (item.Colour >> 24) == 255;

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}
