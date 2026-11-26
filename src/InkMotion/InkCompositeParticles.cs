namespace InkMotion;

public sealed class InkCompositeParticleSettings
{
    public double EmitterX { get; set; } = 0.5;
    public double EmitterY { get; set; } = 0.8;
    public int Rate { get; set; } = 20;
    public double Lifetime { get; set; } = 2;
    public double Direction { get; set; }
    public double Spread { get; set; } = 45;
    public double Speed { get; set; } = 0.25;
    public double Gravity { get; set; } = 0.2;
    public double Size { get; set; } = 0.02;
    public int Seed { get; set; } = 1;
    public uint Colour { get; set; } = 0xFFFFFFFF;

    public InkCompositeParticleSettings Clone() => (InkCompositeParticleSettings)MemberwiseClone();
}

public static class InkCompositeParticles
{
    public static bool IsValid(InkCompositeParticleSettings? settings) =>
        settings is not null &&
        InRange(settings.EmitterX, 0, 1) && InRange(settings.EmitterY, 0, 1) &&
        settings.Rate is >= 1 and <= 200 &&
        InRange(settings.Lifetime, 0.1, 10) &&
        InRange(settings.Direction, -180, 180) && InRange(settings.Spread, 0, 360) &&
        InRange(settings.Speed, 0, 2) && InRange(settings.Gravity, -2, 2) &&
        InRange(settings.Size, 0.002, 0.2) && settings.Seed is >= 0 and <= 10000 &&
        (settings.Colour >> 24) == 255;

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}
