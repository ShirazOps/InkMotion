using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace InkMotion;

/// <summary>Drag-and-connect editor for the optional ink compositing graph.</summary>
public sealed partial class InkCompositeGraphWindow : Window
{
    private const double NodeWidth = 190;
    private const double HeaderHeight = 46;
    private const double PortSpacing = 24;
    private const double PortRadius = 7;
    private const double PortHitRadius = 14;

    private readonly Func<InkCompositeGraph?> _getGraph;
    private readonly Func<IReadOnlyList<InkLayerProject>> _getLayers;
    private readonly Action<InkCompositeGraph?, bool, string> _apply;
    private readonly Action _commitPending;
    private readonly Canvas _surface = new() { Width = 4000, Height = 2400, Background = new SolidColorBrush(ColorHelper.FromArgb(255, 16, 18, 24)) };
    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Enabled,
        VerticalScrollMode = ScrollMode.Enabled,
        ZoomMode = ZoomMode.Disabled,
    };
    private readonly StackPanel _properties = new() { Spacing = 10, Padding = new Thickness(16) };
    private readonly ToggleSwitch _enabled = new() { Header = "Use node graph", OffContent = "Layer stack", OnContent = "Node graph" };
    private readonly DropDownButton _addNode = new() { Content = "Add node" };
    private readonly Button _build = new() { Content = "Build from layer stack" };
    private readonly Button _delete = new() { Content = "Delete node" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly Path _pendingWire = new() { StrokeThickness = 2.5, Stroke = new SolidColorBrush(Colors.Gold), IsHitTestVisible = false };
    private InkCompositeGraph? _graph;
    private string? _selectedId;
    private string? _dragId;
    private Point _dragOffset;
    private bool _dragMoved;
    private string? _wireSourceId;
    private bool _loading;

    public InkCompositeGraphWindow(
        Func<InkCompositeGraph?> getGraph,
        Func<IReadOnlyList<InkLayerProject>> getLayers,
        Action<InkCompositeGraph?, bool, string> apply,
        Action commitPending)
    {
        _getGraph = getGraph;
        _getLayers = getLayers;
        _apply = apply;
        _commitPending = commitPending;
        Title = "InkMotion node compositing";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 860));

        var root = new Grid
        {
            Padding = new Thickness(16), RowSpacing = 10, RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 20, 22, 28)),
        };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        toolbar.Children.Add(_enabled);
        toolbar.Children.Add(Centred(_addNode));
        toolbar.Children.Add(Centred(_build));
        toolbar.Children.Add(Centred(_delete));
        root.Children.Add(toolbar);
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);
        var body = new Grid { ColumnSpacing = 12 };
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(320) });
        _scroll.Content = _surface;
        body.Children.Add(_scroll);
        var propertiesScroll = new ScrollViewer
        {
            Content = _properties, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 28, 31, 38)),
        };
        Grid.SetColumn(propertiesScroll, 1);
        body.Children.Add(propertiesScroll);
        Grid.SetRow(body, 2);
        root.Children.Add(body);
        Content = root;

        var menu = new MenuFlyout();
        foreach (InkCompositeNodeKind kind in Enum.GetValues<InkCompositeNodeKind>())
        {
            var item = new MenuFlyoutItem { Text = InkCompositeGraphs.GetDisplayName(kind), Tag = kind };
            item.Click += AddNode_Click;
            menu.Items.Add(item);
        }

        _addNode.Flyout = menu;
        _enabled.Toggled += Enabled_Toggled;
        _build.Click += Build_Click;
        _delete.Click += (_, _) => DeleteSelected();
        _surface.PointerPressed += Surface_PointerPressed;
        _surface.PointerMoved += Surface_PointerMoved;
        _surface.PointerReleased += Surface_PointerReleased;
        _surface.PointerCaptureLost += (_, _) => CancelGesture();
        root.KeyDown += Root_KeyDown;
        AutomationPropertiesHelper(toolbar);
        Reload();
    }

    /// <summary>Re-reads the project graph, for example after undo or redo.</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            CancelGesture();
            InkCompositeGraph? graph = _getGraph();
            _graph = graph is null ? null : InkCompositeGraphs.Clone(graph);
            if (_selectedId is not null && _graph?.Nodes.Any(node => node.Id == _selectedId) != true) _selectedId = null;
            _enabled.IsOn = _graph?.Enabled == true;
            Redraw();
            ShowProperties();
            UpdateStatus(null);
        }
        finally { _loading = false; }
    }

    private static FrameworkElement Centred(FrameworkElement element)
    {
        element.VerticalAlignment = VerticalAlignment.Bottom;
        return element;
    }

    private static void AutomationPropertiesHelper(Panel toolbar)
    {
        foreach (UIElement child in toolbar.Children)
        {
            if (child is ContentControl { Content: string text } control)
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, text);
        }
    }

    private void Enabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (_graph is null)
        {
            _graph = InkCompositeGraphs.FromLayerStack(_getLayers(), _enabled.IsOn);
        }
        else
        {
            _graph.Enabled = _enabled.IsOn;
        }

        Commit(_enabled.IsOn
            ? "Node graph on: the graph now decides how the picture, ink layers and text are composited."
            : "Node graph off: the layer stack is used again. The graph is kept in the project.");
        Redraw();
    }

    private void Build_Click(object sender, RoutedEventArgs e)
    {
        _graph = InkCompositeGraphs.FromLayerStack(_getLayers(), enabled: true);
        _selectedId = null;
        _loading = true;
        _enabled.IsOn = true;
        _loading = false;
        Commit("Built a graph that reproduces the current layer stack, including blend modes, clipping and occlusion mattes.");
        Redraw();
        ShowProperties();
    }

    private void AddNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: InkCompositeNodeKind kind }) return;
        _graph ??= InkCompositeGraphs.FromLayerStack(_getLayers(), enabled: false);
        if (kind == InkCompositeNodeKind.Output && InkCompositeGraphs.GetOutput(_graph) is not null)
        {
            UpdateStatus("A graph has a single Output node.");
            return;
        }

        if (_graph.Nodes.Count >= InkCompositeGraphs.MaxNodes)
        {
            UpdateStatus($"A graph can hold at most {InkCompositeGraphs.MaxNodes} nodes.");
            return;
        }

        InkCompositeNode node = InkCompositeGraphs.CreateNode(kind, InkCompositeGraphs.CreateNodeId(_graph),
            Math.Round(_scroll.HorizontalOffset + 60), Math.Round(_scroll.VerticalOffset + 60));
        if (kind == InkCompositeNodeKind.InkLayer) node.LayerId = _getLayers().FirstOrDefault()?.Id;
        _graph.Nodes.Add(node);
        _selectedId = node.Id;
        Commit($"Added a {InkCompositeGraphs.GetDisplayName(kind)} node. Drag from its right-hand port to another node's input to connect it.");
        Redraw();
        ShowProperties();
    }

    private void DeleteSelected()
    {
        if (_graph is null || _selectedId is null) return;
        InkCompositeNode node = _graph.Nodes.First(candidate => candidate.Id == _selectedId);
        if (node.Kind == InkCompositeNodeKind.Output)
        {
            UpdateStatus("The Output node cannot be deleted.");
            return;
        }

        _graph.Nodes.Remove(node);
        foreach (InkCompositeNode other in _graph.Nodes)
        {
            if (other.Particles?.InteractionSceneId == node.Id) other.Particles.InteractionSceneId = null;
            for (int index = 0; index < other.Inputs.Count; index++)
                if (other.Inputs[index] == node.Id) other.Inputs[index] = null;
        }

        _selectedId = null;
        Commit($"Deleted the {InkCompositeGraphs.GetDisplayName(node.Kind)} node.");
        Redraw();
        ShowProperties();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete &&
            FocusManager.GetFocusedElement(Content.XamlRoot) is not (TextBox or NumberBox or Slider or ComboBox))
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    private void Commit(string status, bool commit = true)
    {
        if (_graph is not null && InkCompositeGraphs.Validate(_graph) is string error)
        {
            UpdateStatus(error);
            Reload();
            return;
        }

        _apply(_graph is null ? null : InkCompositeGraphs.Clone(_graph), commit, status);
        UpdateStatus(status);
    }

    private void UpdateStatus(string? message)
    {
        string mode = _graph?.Enabled == true
            ? "The node graph is on and replaces the layer stack in the preview and in exports; layer blend modes, clipping and occlusion mattes are ignored."
            : "The node graph is off; the layer stack is used. Switch it on, or choose Build from layer stack to start from the current layers.";
        _status.Text = message is null ? mode : $"{message} {mode}";
        _delete.IsEnabled = _selectedId is not null;
    }

    private static Point InputPort(InkCompositeNode node, int slot) =>
        new(node.X, node.Y + HeaderHeight + (slot * PortSpacing) + (PortSpacing / 2));

    private static Point OutputPort(InkCompositeNode node) =>
        new(node.X + NodeWidth, node.Y + HeaderHeight + (PortSpacing / 2));

    private static double NodeHeight(InkCompositeNode node) =>
        HeaderHeight + (Math.Max(1, node.Inputs.Count) * PortSpacing) + 8;

    private void Redraw()
    {
        _surface.Children.Clear();
        if (_graph is null)
        {
            var empty = new TextBlock
            {
                Text = "No node graph yet. Choose Build from layer stack, switch on Use node graph, or add a node.",
                Margin = new Thickness(40), Opacity = 0.7,
            };
            _surface.Children.Add(empty);
            return;
        }

        var lookup = _graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (InkCompositeNode node in _graph.Nodes)
        {
            for (int slot = 0; slot < node.Inputs.Count; slot++)
            {
                if (node.Inputs[slot] is string sourceId && lookup.TryGetValue(sourceId, out InkCompositeNode? source))
                    _surface.Children.Add(Wire(OutputPort(source), InputPort(node, slot), Colors.LightSkyBlue));
            }
        }

        foreach (InkCompositeNode node in _graph.Nodes) DrawNode(node);
        _pendingWire.Data = null;
        _surface.Children.Add(_pendingWire);
    }

    private static Path Wire(Point from, Point to, Color colour)
    {
        var wire = new Path { StrokeThickness = 2.5, Stroke = new SolidColorBrush(colour), IsHitTestVisible = false };
        SetWire(wire, from, to);
        return wire;
    }

    private static void SetWire(Path wire, Point from, Point to)
    {
        double bend = Math.Max(40, Math.Abs(to.X - from.X) / 2);
        var figure = new PathFigure { StartPoint = from };
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(from.X + bend, from.Y),
            Point2 = new Point(to.X - bend, to.Y),
            Point3 = to,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        wire.Data = geometry;
    }

    private void DrawNode(InkCompositeNode node)
    {
        bool selected = node.Id == _selectedId;
        var content = new StackPanel { Padding = new Thickness(12, 6, 12, 6) };
        content.Children.Add(new TextBlock
        {
            Text = InkCompositeGraphs.GetDisplayName(node.Kind),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = Describe(node), Opacity = 0.7, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            Height = HeaderHeight - 26,
        });
        foreach (string name in InkCompositeGraphs.GetInputNames(node.Kind))
        {
            content.Children.Add(new TextBlock { Text = name, FontSize = 12, Height = PortSpacing, Padding = new Thickness(4, 3, 0, 0) });
        }

        var card = new Border
        {
            Width = NodeWidth,
            Height = NodeHeight(node),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = new SolidColorBrush(selected ? Colors.Gold : ColorHelper.FromArgb(255, 80, 88, 104)),
            Background = new SolidColorBrush(node.Kind == InkCompositeNodeKind.Output
                ? ColorHelper.FromArgb(255, 44, 58, 44)
                : ColorHelper.FromArgb(255, 38, 42, 52)),
            Child = content,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(card, node.X);
        Canvas.SetTop(card, node.Y);
        _surface.Children.Add(card);

        for (int slot = 0; slot < node.Inputs.Count; slot++)
            AddPort(InputPort(node, slot), node.Inputs[slot] is null ? Colors.Gray : Colors.LightSkyBlue);
        if (node.Kind != InkCompositeNodeKind.Output) AddPort(OutputPort(node), Colors.Gold);
    }

    private void AddPort(Point centre, Color colour)
    {
        var port = new Ellipse
        {
            Width = PortRadius * 2, Height = PortRadius * 2, Fill = new SolidColorBrush(colour),
            Stroke = new SolidColorBrush(Colors.Black), StrokeThickness = 1, IsHitTestVisible = false,
        };
        Canvas.SetLeft(port, centre.X - PortRadius);
        Canvas.SetTop(port, centre.Y - PortRadius);
        _surface.Children.Add(port);
    }

    private string Describe(InkCompositeNode node) => node.Kind switch
    {
        InkCompositeNodeKind.InkLayer => _getLayers().FirstOrDefault(layer => layer.Id == node.LayerId)?.Name ?? "Missing layer (transparent)",
        InkCompositeNodeKind.Merge => $"{node.BlendMode} · {node.Opacity:P0}",
        InkCompositeNodeKind.Blur => $"Radius {node.Radius:0}",
        InkCompositeNodeKind.Glow => $"Radius {node.Radius:0} · strength {node.Intensity:0.##}",
        InkCompositeNodeKind.Pixelate => $"{node.Radius:0} px blocks",
        InkCompositeNodeKind.ColourAdjust => $"B {node.Brightness:+0.##;-0.##;0} C {node.Contrast:0.##} S {node.Saturation:0.##} H {node.Hue:0}°",
        InkCompositeNodeKind.Mask => node.Invert ? "Outside the matte" : "Inside the matte",
        InkCompositeNodeKind.Picture => "Video, images and titles",
        InkCompositeNodeKind.Text => "Cel text overlays",
        InkCompositeNodeKind.Scene3D => $"{node.Scene3D?.Objects.Count ?? 0} object(s)",
        InkCompositeNodeKind.Particles => $"{node.Particles?.Rate ?? 0} particles/s",
        InkCompositeNodeKind.Transform => $"Rotation {node.Transform?.Rotation ?? 0:0}°",
        InkCompositeNodeKind.DropShadow => $"Radius {node.Radius:0} · opacity {node.Opacity:P0}",
        InkCompositeNodeKind.Sharpen or InkCompositeNodeKind.Vignette or InkCompositeNodeKind.InvertColours or
            InkCompositeNodeKind.Sepia => $"Strength {node.Intensity:0.##}",
        _ => "Final composite",
    };

    private InkCompositeNode? HitNode(Point point) => _graph?.Nodes.LastOrDefault(node =>
        point.X >= node.X && point.X <= node.X + NodeWidth && point.Y >= node.Y && point.Y <= node.Y + NodeHeight(node));

    private static bool Near(Point a, Point b) =>
        ((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)) <= PortHitRadius * PortHitRadius;

    private (InkCompositeNode Node, int Slot)? HitInput(Point point)
    {
        if (_graph is null) return null;
        foreach (InkCompositeNode node in _graph.Nodes)
        {
            for (int slot = 0; slot < node.Inputs.Count; slot++)
                if (Near(point, InputPort(node, slot))) return (node, slot);
        }

        return null;
    }

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_graph is null) return;
        Point point = e.GetCurrentPoint(_surface).Position;
        InkCompositeNode? output = _graph.Nodes.FirstOrDefault(node =>
            node.Kind != InkCompositeNodeKind.Output && Near(point, OutputPort(node)));
        if (output is not null)
        {
            BeginWire(output.Id, point, e);
            return;
        }

        if (HitInput(point) is (InkCompositeNode target, int slot) && target.Inputs[slot] is string sourceId)
        {
            target.Inputs[slot] = null;
            Commit($"Disconnected the {InkCompositeGraphs.GetInputNames(target.Kind)[slot]} input of the {InkCompositeGraphs.GetDisplayName(target.Kind)} node.");
            Redraw();
            BeginWire(sourceId, point, e);
            return;
        }

        InkCompositeNode? hit = HitNode(point);
        _selectedId = hit?.Id;
        if (hit is not null)
        {
            _dragId = hit.Id;
            _dragOffset = new Point(point.X - hit.X, point.Y - hit.Y);
            _dragMoved = false;
            _surface.CapturePointer(e.Pointer);
        }

        Redraw();
        ShowProperties();
        UpdateStatus(null);
        e.Handled = true;
    }

    private void BeginWire(string sourceId, Point point, PointerRoutedEventArgs e)
    {
        _wireSourceId = sourceId;
        InkCompositeNode source = _graph!.Nodes.First(node => node.Id == sourceId);
        SetWire(_pendingWire, OutputPort(source), point);
        _surface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_graph is null) return;
        Point point = e.GetCurrentPoint(_surface).Position;
        if (_wireSourceId is not null)
        {
            InkCompositeNode source = _graph.Nodes.First(node => node.Id == _wireSourceId);
            SetWire(_pendingWire, OutputPort(source), point);
            return;
        }

        if (_dragId is null) return;
        InkCompositeNode dragged = _graph.Nodes.First(node => node.Id == _dragId);
        double x = Math.Clamp(Math.Round(point.X - _dragOffset.X), 0, _surface.Width - NodeWidth);
        double y = Math.Clamp(Math.Round(point.Y - _dragOffset.Y), 0, _surface.Height - NodeHeight(dragged));
        if (x == dragged.X && y == dragged.Y) return;
        dragged.X = x;
        dragged.Y = y;
        _dragMoved = true;
        Redraw();
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Point point = e.GetCurrentPoint(_surface).Position;
        string? wireSource = _wireSourceId;
        bool moved = _dragId is not null && _dragMoved;
        _wireSourceId = null;
        _dragId = null;
        _dragMoved = false;
        _pendingWire.Data = null;
        _surface.ReleasePointerCapture(e.Pointer);
        if (_graph is null) return;
        if (moved)
        {
            Commit("Moved the node.");
            return;
        }

        if (wireSource is null) return;
        if (HitInput(point) is not (InkCompositeNode target, int slot))
        {
            UpdateStatus("Release the connection on a node's input port to connect it.");
            return;
        }

        if (target.Id == wireSource || InkCompositeGraphs.WouldCreateCycle(_graph, wireSource, target.Id))
        {
            UpdateStatus("That connection would make a loop, so it was not made.");
            return;
        }

        target.Inputs[slot] = wireSource;
        Commit($"Connected to the {InkCompositeGraphs.GetInputNames(target.Kind)[slot]} input of the {InkCompositeGraphs.GetDisplayName(target.Kind)} node.");
        Redraw();
    }

    private void CancelGesture()
    {
        if (_dragId is not null && _dragMoved) Commit("Moved the node.");
        _wireSourceId = null;
        _dragId = null;
        _dragMoved = false;
        _pendingWire.Data = null;
    }

    private void ShowProperties()
    {
        _properties.Children.Clear();
        InkCompositeNode? node = _graph?.Nodes.FirstOrDefault(candidate => candidate.Id == _selectedId);
        _delete.IsEnabled = node is not null && node.Kind != InkCompositeNodeKind.Output;
        if (node is null)
        {
            _properties.Children.Add(new TextBlock
            {
                Text = "Select a node to edit its settings. Drag a node's header to move it. Drag from a gold output port to an input port to connect; press a connected input to detach it. Missing or hidden layers render as transparent.",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
            });
            return;
        }

        _properties.Children.Add(new TextBlock
        {
            Text = InkCompositeGraphs.GetDisplayName(node.Kind), FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        switch (node.Kind)
        {
            case InkCompositeNodeKind.InkLayer:
                var layers = new ComboBox { Header = "Drawing layer", HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (InkLayerProject layer in _getLayers())
                {
                    var item = new ComboBoxItem { Content = layer.Name, Tag = layer.Id };
                    layers.Items.Add(item);
                    if (layer.Id == node.LayerId) layers.SelectedItem = item;
                }

                layers.SelectionChanged += (_, _) =>
                {
                    if (layers.SelectedItem is not ComboBoxItem { Tag: string id } || id == node.LayerId) return;
                    node.LayerId = id;
                    Commit("Changed the node's drawing layer.");
                    Redraw();
                };
                _properties.Children.Add(layers);
                Hint("Draws the layer with its opacity, Follow object tracking, glow and pixel effects. Its blend mode, clipping and occlusion settings are not used here; build them with Merge and Mask nodes.");
                break;
            case InkCompositeNodeKind.Merge:
                var modes = new ComboBox { Header = "Blend mode", HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (InkLayerBlendMode mode in Enum.GetValues<InkLayerBlendMode>())
                {
                    modes.Items.Add(mode.ToString());
                    if (mode == node.BlendMode) modes.SelectedItem = mode.ToString();
                }

                modes.SelectionChanged += (_, _) =>
                {
                    if (modes.SelectedItem is string text && Enum.TryParse(text, out InkLayerBlendMode mode) && mode != node.BlendMode)
                    {
                        node.BlendMode = mode;
                        Commit($"Merge blend mode set to {mode}.");
                        Redraw();
                    }
                };
                _properties.Children.Add(modes);
                AddSlider("Foreground opacity", 0, 1, 0.01, node.Opacity, value => node.Opacity = value);
                Hint("Places the Foreground input over the Background input.");
                break;
            case InkCompositeNodeKind.Blur:
                AddSlider("Radius", 0, InkCompositeGraphs.MaxBlurRadius, 1, node.Radius, value => node.Radius = value);
                break;
            case InkCompositeNodeKind.Glow:
                AddSlider("Radius", 0, InkCompositeGraphs.MaxBlurRadius, 1, node.Radius, value => node.Radius = value);
                AddSlider("Strength", 0, InkLayerEffects.MaxGlowIntensity, 0.05, node.Intensity, value => node.Intensity = value);
                AddOpaqueColourPicker("Glow colour", node.Colour, value => node.Colour = value, "Glow colour changed.");
                break;
            case InkCompositeNodeKind.Pixelate:
                AddSlider("Block size", InkLayerEffects.MinPixelSize, InkLayerEffects.MaxPixelSize, 1, node.Radius, value => node.Radius = value);
                break;
            case InkCompositeNodeKind.ColourAdjust:
                AddSlider("Brightness", -1, 1, 0.01, node.Brightness, value => node.Brightness = value);
                AddSlider("Contrast", 0, 2, 0.01, node.Contrast, value => node.Contrast = value);
                AddSlider("Saturation", 0, 2, 0.01, node.Saturation, value => node.Saturation = value);
                AddSlider("Hue (degrees)", -180, 180, 1, node.Hue, value => node.Hue = value);
                break;
            case InkCompositeNodeKind.Scene3D:
                ShowScene3DProperties(node);
                break;
            case InkCompositeNodeKind.Particles:
                ShowParticleProperties(node);
                break;
            case InkCompositeNodeKind.Transform:
                InkCompositeTransformSettings transform = node.Transform ??
                    throw new InvalidOperationException("The transform settings are missing.");
                AddSlider("Offset X (% of width)", -200, 200, 1, transform.OffsetX * 100, value => transform.OffsetX = value / 100);
                AddSlider("Offset Y (% of height)", -200, 200, 1, transform.OffsetY * 100, value => transform.OffsetY = value / 100);
                AddSlider("Scale X (%)", 5, 1000, 5, transform.ScaleX * 100, value => transform.ScaleX = value / 100);
                AddSlider("Scale Y (%)", 5, 1000, 5, transform.ScaleY * 100, value => transform.ScaleY = value / 100);
                AddSlider("Rotation (degrees)", -180, 180, 1, transform.Rotation, value => transform.Rotation = value);
                Hint("Scales and rotates around the canvas centre, then offsets the image.");
                break;
            case InkCompositeNodeKind.DropShadow:
                AddSlider("Blur radius", 0, InkCompositeGraphs.MaxBlurRadius, 1, node.Radius, value => node.Radius = value);
                AddSlider("Offset X", -500, 500, 1, node.ShadowOffsetX, value => node.ShadowOffsetX = value);
                AddSlider("Offset Y", -500, 500, 1, node.ShadowOffsetY, value => node.ShadowOffsetY = value);
                AddSlider("Shadow opacity", 0, 1, 0.01, node.Opacity, value => node.Opacity = value);
                AddOpaqueColourPicker("Shadow colour", node.Colour, value => node.Colour = value, "Shadow colour changed.");
                break;
            case InkCompositeNodeKind.Sharpen:
                AddSlider("Strength", 0, VideoSharpen.MaximumAmount, 0.1, node.Intensity, value => node.Intensity = value);
                break;
            case InkCompositeNodeKind.Vignette:
                AddSlider("Strength", 0, 1, 0.01, node.Intensity, value => node.Intensity = value);
                AddOpaqueColourPicker("Vignette colour", node.Colour, value => node.Colour = value, "Vignette colour changed.");
                break;
            case InkCompositeNodeKind.InvertColours:
            case InkCompositeNodeKind.Sepia:
                AddSlider("Strength", 0, 1, 0.01, node.Intensity, value => node.Intensity = value);
                break;
            case InkCompositeNodeKind.Mask:
                var invert = new ToggleSwitch { Header = "Keep", OffContent = "Inside the matte", OnContent = "Outside the matte", IsOn = node.Invert };
                invert.Toggled += (_, _) =>
                {
                    node.Invert = invert.IsOn;
                    Commit(invert.IsOn ? "Mask keeps the image outside the matte." : "Mask keeps the image inside the matte.");
                    Redraw();
                };
                _properties.Children.Add(invert);
                Hint("Uses the Matte input's alpha. A painted layer used as the matte, inverted, lets artwork pass behind a foreground object.");
                break;
            case InkCompositeNodeKind.Picture:
                Hint("The picture beneath the ink at the current time: timeline video, images and titles.");
                break;
            case InkCompositeNodeKind.Text:
                Hint("Text overlays on the current cel, with their Follow object tracking.");
                break;
            default:
                Hint("The final composite shown in the preview and written to exports. Nothing is drawn while its input is unconnected.");
                break;
        }
    }

    private void Hint(string text) =>
        _properties.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });

    private void AddOpaqueColourPicker(string label, uint initialColour, Action<uint> set, string status)
    {
        var picker = new ColorPicker
        {
            Color = TimelineCompositor.ToColour(initialColour), IsAlphaEnabled = false, IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false, IsHexInputVisible = true, ColorSpectrumShape = ColorSpectrumShape.Box,
        };
        picker.ColorChanged += (_, args) =>
        {
            if (_loading) return;
            Color colour = args.NewColor;
            set(0xFF000000u | ((uint)colour.R << 16) | ((uint)colour.G << 8) | colour.B);
            Commit(status, commit: false);
            Redraw();
        };
        picker.LostFocus += (_, _) => _commitPending();
        _properties.Children.Add(new TextBlock { Text = label });
        _properties.Children.Add(picker);
    }

    private void AddSlider(string label, double minimum, double maximum, double step, double value, Action<double> set)
    {
        var slider = new Slider
        {
            Header = label, Minimum = minimum, Maximum = maximum, StepFrequency = step, Value = value,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, label);
        slider.ValueChanged += (_, args) =>
        {
            if (_loading || !double.IsFinite(args.NewValue)) return;
            set(Math.Clamp(args.NewValue, minimum, maximum));
            Commit($"{label} changed.", commit: false);
            Redraw();
        };
        slider.PointerCaptureLost += (_, _) => _commitPending();
        slider.LostFocus += (_, _) => _commitPending();
        _properties.Children.Add(slider);
    }
}
