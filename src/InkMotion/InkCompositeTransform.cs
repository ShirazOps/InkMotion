namespace InkMotion;

public sealed class InkCompositeTransformSettings
{
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public double Rotation { get; set; }

    public InkCompositeTransformSettings Clone() => (InkCompositeTransformSettings)MemberwiseClone();
}

public static class InkCompositeTransforms
{
    public static bool IsValid(InkCompositeTransformSettings? settings) =>
        settings is not null &&
        InRange(settings.OffsetX, -2, 2) && InRange(settings.OffsetY, -2, 2) &&
        InRange(settings.ScaleX, 0.05, 10) && InRange(settings.ScaleY, 0.05, 10) &&
        InRange(settings.Rotation, -180, 180);

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}
