using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        AddOpaqueColourPicker("Particle colour", particles.Colour, value => particles.Colour = value, "Particle colour changed.");
        Hint("Optionally interact in 2D with the projected bounding circles of visible objects in a 3D scene. Positive force attracts; negative force repels. This is not a 3D mesh collision.");
        var scenes = new ComboBox { Header = "Interaction scene", HorizontalAlignment = HorizontalAlignment.Stretch };
        scenes.Items.Add(new ComboBoxItem { Content = "None", Tag = "" });
        foreach (InkCompositeNode scene in _graph!.Nodes.Where(candidate => candidate.Kind == InkCompositeNodeKind.Scene3D))
            scenes.Items.Add(new ComboBoxItem { Content = scene.Id, Tag = scene.Id });
        foreach (ComboBoxItem choice in scenes.Items)
            if ((string)choice.Tag == (particles.InteractionSceneId ?? "")) scenes.SelectedItem = choice;
        scenes.SelectionChanged += (_, _) =>
        {
            if (scenes.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            particles.InteractionSceneId = id.Length == 0 ? null : id;
            Commit("Changed the particle interaction scene.");
            Redraw();
        };
        _properties.Children.Add(scenes);
        AddSlider("Attraction / repulsion", -2, 2, 0.05, particles.InteractionForce, value => particles.InteractionForce = value);
        var collision = new ToggleSwitch { Header = "Bounce off object bounds", IsOn = particles.CollideWithObjects };
        collision.Toggled += (_, _) =>
        {
            particles.CollideWithObjects = collision.IsOn;
            Commit("Changed particle collisions.");
            Redraw();
        };
        _properties.Children.Add(collision);
        AddSlider("Bounce", 0, 1, 0.05, particles.Restitution, value => particles.Restitution = value);
    }
}
