namespace InkMotion;

public sealed record InkBrushPreset(string Name, double Width, double Opacity, double Stabilisation,
    InkBrushTexture Texture = InkBrushTexture.Round)
{
    public uint ApplyColour(uint colour) =>
        ((uint)Math.Round(Opacity * 255) << 24) | (colour & 0x00FFFFFF);
}

public static class InkBrushPresets
{
    public static IReadOnlyList<InkBrushPreset> All { get; } = Array.AsReadOnly(new[]
    {
        new InkBrushPreset("Fine pen", 1.5, 1, 0),
        new InkBrushPreset("Writing pen", 4, 1, 0.2),
        new InkBrushPreset("Broad marker", 10, 1, 0.1),
        new InkBrushPreset("Translucent highlighter", 12, 0.3, 0.1),
        new InkBrushPreset("Grain pencil", 4, 0.8, 0.15, InkBrushTexture.Grain),
        new InkBrushPreset("Grain chalk", 12, 0.6, 0.1, InkBrushTexture.Grain),
    });
}
