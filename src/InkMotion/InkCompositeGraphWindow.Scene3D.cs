using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace InkMotion;

public sealed partial class InkCompositeGraphWindow
{
    private string? _selectedSceneNodeId;
    private int _selectedSceneObjectIndex;

    private void ShowScene3DProperties(InkCompositeNode node)
    {
        InkCompositeScene3D scene = node.Scene3D ??
            throw new InvalidOperationException("The 3D scene settings are missing.");
        if (_selectedSceneNodeId != node.Id)
        {
            _selectedSceneNodeId = node.Id;
            _selectedSceneObjectIndex = 0;
        }

        Hint("A perspective scene with opaque primitives and depth. Position is in scene units; spin is in degrees per second.");
        AddSlider("Camera yaw", -180, 180, 1, scene.CameraYaw, value => scene.CameraYaw = value);
        AddSlider("Camera pitch", -89, 89, 1, scene.CameraPitch, value => scene.CameraPitch = value);
        AddSlider("Camera distance", 1, 100, 0.1, scene.CameraDistance, value => scene.CameraDistance = value);
        AddSlider("Field of view", 15, 120, 1, scene.FieldOfView, value => scene.FieldOfView = value);
        AddSlider("Light yaw", -180, 180, 1, scene.LightYaw, value => scene.LightYaw = value);
        AddSlider("Light pitch", -90, 90, 1, scene.LightPitch, value => scene.LightPitch = value);
        AddSlider("Ambient light", 0, 1, 0.01, scene.AmbientLight, value => scene.AmbientLight = value);

        _selectedSceneObjectIndex = scene.Objects.Count == 0
            ? -1
            : Math.Clamp(_selectedSceneObjectIndex, 0, scene.Objects.Count - 1);
        var objects = new ComboBox { Header = "Object", HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int index = 0; index < scene.Objects.Count; index++)
        {
            objects.Items.Add($"Object {index + 1}: {scene.Objects[index].Shape}");
        }
        objects.SelectedIndex = _selectedSceneObjectIndex;
        objects.SelectionChanged += (_, _) =>
        {
            if (objects.SelectedIndex < 0 || objects.SelectedIndex == _selectedSceneObjectIndex) return;
            _commitPending();
            _selectedSceneObjectIndex = objects.SelectedIndex;
            ShowProperties();
        };
        _properties.Children.Add(objects);

        var add = new Button { Content = "Add object", IsEnabled = scene.Objects.Count < InkCompositeScenes3D.MaxObjects };
        var remove = new Button { Content = "Remove object", IsEnabled = _selectedSceneObjectIndex >= 0 };
        add.Click += (_, _) =>
        {
            _commitPending();
            scene.Objects.Add(new InkComposite3DObject());
            _selectedSceneObjectIndex = scene.Objects.Count - 1;
            Commit("Added a 3D object.");
            Redraw();
            ShowProperties();
        };
        remove.Click += (_, _) =>
        {
            if (_selectedSceneObjectIndex < 0) return;
            _commitPending();
            scene.Objects.RemoveAt(_selectedSceneObjectIndex);
            _selectedSceneObjectIndex = Math.Min(_selectedSceneObjectIndex, scene.Objects.Count - 1);
            Commit("Removed a 3D object.");
            Redraw();
            ShowProperties();
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(add);
        actions.Children.Add(remove);
        _properties.Children.Add(actions);
        if (_selectedSceneObjectIndex < 0) return;

        InkComposite3DObject item = scene.Objects[_selectedSceneObjectIndex];
        var shapes = new ComboBox { Header = "Shape", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (InkComposite3DShape shape in Enum.GetValues<InkComposite3DShape>())
            shapes.Items.Add(shape.ToString());
        shapes.SelectedItem = item.Shape.ToString();
        shapes.SelectionChanged += (_, _) =>
        {
            if (shapes.SelectedItem is not string text ||
                !Enum.TryParse(text, out InkComposite3DShape shape) || shape == item.Shape) return;
            item.Shape = shape;
            Commit("Changed the 3D object shape.");
            Redraw();
            ShowProperties();
        };
        _properties.Children.Add(shapes);
        var visible = new ToggleSwitch { Header = "Object visibility", IsOn = item.Visible };
        visible.Toggled += (_, _) =>
        {
            item.Visible = visible.IsOn;
            Commit("Changed 3D object visibility.");
            Redraw();
        };
        _properties.Children.Add(visible);
        AddSlider("Position X", -50, 50, 0.1, item.X, value => item.X = value);
        AddSlider("Position Y", -50, 50, 0.1, item.Y, value => item.Y = value);
        AddSlider("Position Z", -50, 50, 0.1, item.Z, value => item.Z = value);
        AddSlider("Rotation X", -180, 180, 1, item.RotationX, value => item.RotationX = value);
        AddSlider("Rotation Y", -180, 180, 1, item.RotationY, value => item.RotationY = value);
        AddSlider("Rotation Z", -180, 180, 1, item.RotationZ, value => item.RotationZ = value);
        AddSlider("Size X", 0.05, 10, 0.05, item.ScaleX, value => item.ScaleX = value);
        AddSlider("Size Y", 0.05, 10, 0.05, item.ScaleY, value => item.ScaleY = value);
        AddSlider("Size Z", 0.05, 10, 0.05, item.ScaleZ, value => item.ScaleZ = value);
        AddSlider("Spin X", -360, 360, 1, item.SpinX, value => item.SpinX = value);
        AddSlider("Spin Y", -360, 360, 1, item.SpinY, value => item.SpinY = value);
        AddSlider("Spin Z", -360, 360, 1, item.SpinZ, value => item.SpinZ = value);
        var colour = new ColorPicker
        {
            Color = TimelineCompositor.ToColour(item.Colour),
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            ColorSpectrumShape = ColorSpectrumShape.Box,
        };
        colour.ColorChanged += (_, args) =>
        {
            if (_loading) return;
            Color selected = args.NewColor;
            item.Colour = 0xFF000000u | ((uint)selected.R << 16) | ((uint)selected.G << 8) | selected.B;
            Commit("3D object colour changed.", commit: false);
        };
        colour.LostFocus += (_, _) => _commitPending();
        _properties.Children.Add(new TextBlock { Text = "Object colour" });
        _properties.Children.Add(colour);
    }
}
