using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace InkMotion;

public sealed partial class InkCompositeGraphWindow
{
    private void ShowParticleProperties(InkCompositeNode node)
    {
        InkCompositeParticleSettings particles = node.Particles ??
            throw new InvalidOperationException("The particle emitter settings are missing.");
        Hint("A deterministic 2D emitter. Particles start at the beginning of the timeline and fade over their lifetime.");
        AddSlider("Emitter X (%)", 0, 100, 1, particles.EmitterX * 100, value => particles.EmitterX = value / 100);
        AddSlider("Emitter Y (%)", 0, 100, 1, particles.EmitterY * 100, value => particles.EmitterY = value / 100);
        AddSlider("Particles per second", 1, 200, 1, particles.Rate, value => particles.Rate = (int)Math.Round(value));
        AddSlider("Lifetime (seconds)", 0.1, 10, 0.1, particles.Lifetime, value => particles.Lifetime = value);
        AddSlider("Direction (degrees from up)", -180, 180, 1, particles.Direction, value => particles.Direction = value);
        AddSlider("Spread (degrees)", 0, 360, 1, particles.Spread, value => particles.Spread = value);
        AddSlider("Speed (% of height per second)", 0, 200, 1, particles.Speed * 100, value => particles.Speed = value / 100);
        AddSlider("Gravity (% of height per second squared)", -200, 200, 1, particles.Gravity * 100, value => particles.Gravity = value / 100);
        AddSlider("Size (% of height)", 0.2, 20, 0.2, particles.Size * 100, value => particles.Size = value / 100);
        AddSlider("Random seed", 0, 10000, 1, particles.Seed, value => particles.Seed = (int)Math.Round(value));
        var picker = new ColorPicker
        {
            Color = TimelineCompositor.ToColour(particles.Colour), IsAlphaEnabled = false, IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false, IsHexInputVisible = true, ColorSpectrumShape = ColorSpectrumShape.Box,
        };
        picker.ColorChanged += (_, args) =>
        {
            if (_loading) return;
            Color colour = args.NewColor;
            particles.Colour = 0xFF000000u | ((uint)colour.R << 16) | ((uint)colour.G << 8) | colour.B;
            Commit("Particle colour changed.", commit: false);
        };
        picker.LostFocus += (_, _) => _commitPending();
        _properties.Children.Add(new TextBlock { Text = "Particle colour" });
        _properties.Children.Add(picker);
    }
}
