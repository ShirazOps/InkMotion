namespace InkMotion;

public sealed record VideoHslQualifier
{
    public bool Enabled { get; init; }
    public double Hue { get; init; }
    public double HueWidth { get; init; } = 30;
    public double MinimumSaturation { get; init; }
    public double MaximumSaturation { get; init; } = 1;
    public double MinimumLightness { get; init; }
    public double MaximumLightness { get; init; } = 1;
    public double Softness { get; init; } = 0.1;
    public double HueShift { get; init; }
    public double Saturation { get; init; } = 1;
    public double Lightness { get; init; }

    public bool IsActive => Enabled && (HueShift != 0 || Saturation != 1 || Lightness != 0);

    public bool IsValid =>
        InRange(Hue, 0, 360) && InRange(HueWidth, 0, 180) &&
        InRange(MinimumSaturation, 0, 1) && InRange(MaximumSaturation, MinimumSaturation, 1) &&
        InRange(MinimumLightness, 0, 1) && InRange(MaximumLightness, MinimumLightness, 1) &&
        InRange(Softness, 0, 1) && InRange(HueShift, -180, 180) &&
        InRange(Saturation, 0, 2) && InRange(Lightness, -1, 1);

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    public (double Red, double Green, double Blue) Apply(double red, double green, double blue)
    {
        if (!IsActive) return (red, green, blue);
        double maximum = Math.Max(red, Math.Max(green, blue));
        double minimum = Math.Min(red, Math.Min(green, blue));
        double delta = maximum - minimum;
        double lightness = (maximum + minimum) / 2;
        double saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * lightness - 1));
        double hue = delta == 0 ? 0 : maximum == red ? 60 * (((green - blue) / delta) % 6) :
            maximum == green ? 60 * (((blue - red) / delta) + 2) : 60 * (((red - green) / delta) + 4);
        hue = (hue + 360) % 360;
        double distance = Math.Abs(hue - (Hue % 360));
        distance = Math.Min(distance, 360 - distance);
        double weight = Band(distance, 0, HueWidth, Softness * 180) *
            Band(saturation, MinimumSaturation, MaximumSaturation, Softness) *
            Band(lightness, MinimumLightness, MaximumLightness, Softness);
        // Achromatic pixels have no meaningful hue.
        if (delta == 0 && HueWidth < 180) weight = 0;
        double h = ((hue + HueShift + 360) % 360) / 60;
        double s = Math.Clamp(saturation * Saturation, 0, 1);
        double l = Math.Clamp(lightness + Lightness, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h % 2 - 1));
        (double r, double g, double b) = h switch
        {
            < 1 => (c, x, 0d), < 2 => (x, c, 0d), < 3 => (0d, c, x),
            < 4 => (0d, x, c), < 5 => (x, 0d, c), _ => (c, 0d, x),
        };
        double m = l - c / 2;
        return (red + (r + m - red) * weight, green + (g + m - green) * weight,
            blue + (b + m - blue) * weight);
    }

    private static double Band(double value, double minimum, double maximum, double feather)
    {
        if (value >= minimum && value <= maximum) return 1;
        if (feather == 0) return 0;
        double distance = value < minimum ? minimum - value : value - maximum;
        double t = Math.Clamp(1 - distance / feather, 0, 1);
        return t * t * (3 - 2 * t);
    }

    public VideoColourLutData BuildTable()
    {
        const int size = 33;
        byte[] bytes = new byte[size * size * size * 4];
        for (int r = 0; r < size; r++)
        for (int g = 0; g < size; g++)
        for (int b = 0; b < size; b++)
        {
            (double red, double green, double blue) = Apply(r / 32d, g / 32d, b / 32d);
            int offset = ((r * size + g) * size + b) * 4;
            bytes[offset] = (byte)Math.Round(red * 255);
            bytes[offset + 1] = (byte)Math.Round(green * 255);
            bytes[offset + 2] = (byte)Math.Round(blue * 255);
            bytes[offset + 3] = 255;
        }
        return new(size, bytes);
    }
}
