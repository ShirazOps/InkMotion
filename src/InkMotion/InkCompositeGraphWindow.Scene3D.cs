using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.Storage.Pickers;
using WinRT.Interop;

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

        Hint("A perspective scene with opaque meshes and depth. Import OBJ/MTL or apply a diffuse texture. Assets are embedded in the project.");
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
            if (shape != InkComposite3DShape.Mesh || item.Mesh is not null) shapes.Items.Add(shape.ToString());
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
        var import = new Button { Content = "Import OBJ mesh..." };
        import.Click += async (_, _) => await ImportSceneAssetAsync(node, scene, item, mesh: true);
        _properties.Children.Add(import);
        if (item.Mesh is not null) Hint($"{item.Mesh.Name}: {item.Mesh.Triangles.Count} triangles, {item.Mesh.Materials.Count} materials.");
        var texture = new Button { Content = "Apply diffuse texture..." };
        texture.Click += async (_, _) => await ImportSceneAssetAsync(node, scene, item, mesh: false);
        _properties.Children.Add(texture);
        if (item.Texture is not null)
        {
            var clear = new Button { Content = "Remove texture override" };
            clear.Click += (_, _) =>
            {
                item.Texture = null;
                Commit("Removed the 3D texture override.");
                Redraw();
                ShowProperties();
            };
            _properties.Children.Add(clear);
        }
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
        AddOpaqueColourPicker("Object colour", item.Colour, value => item.Colour = value, "3D object colour changed.");
    }

    private async Task ImportSceneAssetAsync(InkCompositeNode node, InkCompositeScene3D scene,
        InkComposite3DObject item, bool mesh)
    {
        _commitPending();
        try
        {
            var picker = new FileOpenPicker();
            foreach (string extension in mesh ? new[] { ".obj" } : new[] { ".png", ".jpg", ".jpeg", ".bmp" })
                picker.FileTypeFilter.Add(extension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;
            InkComposite3DObject replacement = item.Clone();
            if (mesh)
            {
                replacement.Mesh = await InkCompositeObjImporter.ImportAsync(file.Path);
                replacement.Shape = InkComposite3DShape.Mesh;
                replacement.Colour = 0xFFFFFFFF;
                replacement.Texture = null;
            }
            else replacement.Texture = await InkCompositeObjImporter.LoadTextureAsync(file.Path);
            if (_graph?.Nodes.Contains(node) != true || node.Scene3D != scene || !scene.Objects.Contains(item))
            {
                UpdateStatus("The scene changed during import. Select the object and import again.");
                return;
            }
            InkCompositeScene3D candidate = scene.Clone();
            int index = scene.Objects.IndexOf(item);
            candidate.Objects[index] = replacement;
            if (!InkCompositeScenes3D.IsValid(candidate))
                throw new InvalidDataException("The imported asset exceeds the scene triangle or embedded texture limits.");
            scene.Objects[index] = replacement;
            Commit(mesh ? "Imported an embedded OBJ mesh and its diffuse materials." : "Applied an embedded diffuse texture.");
            Redraw();
            ShowProperties();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or OverflowException or COMException)
        {
            UpdateStatus($"3D import failed: {ex.Message}");
        }
    }
}
