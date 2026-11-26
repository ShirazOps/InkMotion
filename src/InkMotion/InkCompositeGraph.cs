using System.Text.Json.Serialization;

namespace InkMotion;

public enum InkCompositeNodeKind
{
    /// <summary>The picture beneath the ink: video, images and titles at the current time.</summary>
    Picture,
    /// <summary>One drawing layer with its opacity, tracking, glow and pixel effects.</summary>
    InkLayer,
    /// <summary>Text overlays on the current cel.</summary>
    Text,
    /// <summary>Places a foreground over a background with a blend mode and opacity.</summary>
    Merge,
    Blur,
    Glow,
    Pixelate,
    ColourAdjust,
    /// <summary>Keeps an image only where a matte has alpha, or only where it has none when inverted.</summary>
    Mask,
    Output,
    Scene3D,
}

/// <summary>A node in the optional ink compositing graph; parameters not used by its kind are ignored.</summary>
public sealed class InkCompositeNode
{
    public string Id { get; set; } = "";
    public InkCompositeNodeKind Kind { get; set; }
    /// <summary>Position in the node editor, in editor pixels.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Connected source node per input slot; null leaves that input transparent.</summary>
    public List<string?> Inputs { get; set; } = [];
    public string? LayerId { get; set; }
    public InkLayerBlendMode BlendMode { get; set; }
    public double Opacity { get; set; } = 1;
    /// <summary>Blur or glow radius, or pixel-block size, in preview pixels before output scaling.</summary>
    public double Radius { get; set; } = 8;
    public double Intensity { get; set; } = 1;
    public uint Colour { get; set; } = 0xFFFFFFFF;
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    /// <summary>Hue rotation in degrees.</summary>
    public double Hue { get; set; }
    public bool Invert { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InkCompositeScene3D? Scene3D { get; set; }
}

/// <summary>
/// Optional node graph that replaces the ink layer stack while enabled. Layer blend modes, clipping and
/// occlusion settings are ignored then; the graph alone decides how layers, text and the picture combine.
/// </summary>
public sealed class InkCompositeGraph
{
    public bool Enabled { get; set; }
    public List<InkCompositeNode> Nodes { get; set; } = [];
}

public static class InkCompositeGraphs
{
    public const int MaxNodes = 256;
    public const double MaxBlurRadius = 250;
    public const double MaxEditorCoordinate = 100_000;

    public static int GetInputCount(InkCompositeNodeKind kind) => kind switch
    {
        InkCompositeNodeKind.Picture or InkCompositeNodeKind.InkLayer or InkCompositeNodeKind.Text or
            InkCompositeNodeKind.Scene3D => 0,
        InkCompositeNodeKind.Merge or InkCompositeNodeKind.Mask => 2,
        _ => 1,
    };

    public static IReadOnlyList<string> GetInputNames(InkCompositeNodeKind kind) => kind switch
    {
        InkCompositeNodeKind.Merge => ["Background", "Foreground"],
        InkCompositeNodeKind.Mask => ["Image", "Matte"],
        InkCompositeNodeKind.Output => ["Result"],
        InkCompositeNodeKind.Picture or InkCompositeNodeKind.InkLayer or InkCompositeNodeKind.Text or
            InkCompositeNodeKind.Scene3D => [],
        _ => ["Image"],
    };

    public static string GetDisplayName(InkCompositeNodeKind kind) => kind switch
    {
        InkCompositeNodeKind.InkLayer => "Ink layer",
        InkCompositeNodeKind.ColourAdjust => "Colour adjust",
        InkCompositeNodeKind.Scene3D => "3D scene",
        _ => kind.ToString(),
    };

    public static InkCompositeNode CreateNode(InkCompositeNodeKind kind, string id, double x, double y)
    {
        var node = new InkCompositeNode { Id = id, Kind = kind, X = x, Y = y };
        for (int index = 0; index < GetInputCount(kind); index++) node.Inputs.Add(null);
        if (kind == InkCompositeNodeKind.Pixelate) node.Radius = 8;
        if (kind == InkCompositeNodeKind.Glow) node.Radius = 16;
        if (kind == InkCompositeNodeKind.Scene3D) node.Scene3D = new InkCompositeScene3D();
        return node;
    }

    public static string CreateNodeId(InkCompositeGraph graph)
    {
        for (int number = graph.Nodes.Count + 1; ; number++)
        {
            string id = $"node-{number}";
            if (!graph.Nodes.Any(node => node.Id == id)) return id;
        }
    }

    public static InkCompositeGraph Clone(InkCompositeGraph? graph) => graph is null
        ? new InkCompositeGraph()
        : new InkCompositeGraph
        {
            Enabled = graph.Enabled,
            Nodes = graph.Nodes.Select(node => new InkCompositeNode
            {
                Id = node.Id, Kind = node.Kind, X = node.X, Y = node.Y, Inputs = [.. node.Inputs],
                LayerId = node.LayerId, BlendMode = node.BlendMode, Opacity = node.Opacity, Radius = node.Radius,
                Intensity = node.Intensity, Colour = node.Colour, Brightness = node.Brightness,
                Contrast = node.Contrast, Saturation = node.Saturation, Hue = node.Hue, Invert = node.Invert,
                Scene3D = node.Scene3D?.Clone(),
            }).ToList(),
        };

    public static InkCompositeNode? GetOutput(InkCompositeGraph graph) =>
        graph.Nodes.FirstOrDefault(node => node.Kind == InkCompositeNodeKind.Output);

    /// <summary>Returns an error message, or null when the graph is structurally sound and every parameter is in range.</summary>
    public static string? Validate(InkCompositeGraph? graph)
    {
        if (graph is null) return null;
        if (graph.Nodes is null || graph.Nodes.Count > MaxNodes || graph.Nodes.Any(node => node is null))
            return $"A compositing graph must have at most {MaxNodes} nodes.";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (InkCompositeNode node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || node.Id.Length > 64 || !ids.Add(node.Id))
                return "Compositing graph nodes must have unique ids.";
            if (!Enum.IsDefined(node.Kind) || !Enum.IsDefined(node.BlendMode))
                return "A compositing graph node has an unknown kind or blend mode.";
            if (node.Inputs is null || node.Inputs.Count != GetInputCount(node.Kind))
                return $"The {GetDisplayName(node.Kind)} node has the wrong number of inputs.";
            if (!IsCoordinate(node.X) || !IsCoordinate(node.Y))
                return "A compositing graph node has an invalid editor position.";
            if (node.Kind == InkCompositeNodeKind.InkLayer && string.IsNullOrWhiteSpace(node.LayerId))
                return "An ink layer node must name a layer.";
            if (node.Kind == InkCompositeNodeKind.Scene3D && !InkCompositeScenes3D.IsValid(node.Scene3D))
                return "A 3D scene has an invalid camera, light or object setting.";
            if (!InRange(node.Opacity, 0, 1) || !InRange(node.Radius, 0, MaxBlurRadius) ||
                !InRange(node.Intensity, 0, InkLayerEffects.MaxGlowIntensity) || !InRange(node.Brightness, -1, 1) ||
                !InRange(node.Contrast, 0, 2) || !InRange(node.Saturation, 0, 2) || !InRange(node.Hue, -180, 180) ||
                (node.Kind == InkCompositeNodeKind.Pixelate &&
                    !InRange(node.Radius, InkLayerEffects.MinPixelSize, InkLayerEffects.MaxPixelSize)))
                return $"The {GetDisplayName(node.Kind)} node has a setting out of range.";
        }

        if (graph.Nodes.Count(node => node.Kind == InkCompositeNodeKind.Output) != 1)
            return "A compositing graph must have exactly one Output node.";
        foreach (InkCompositeNode node in graph.Nodes)
        {
            if (node.Inputs.Any(input => input is not null && (input == node.Id || !ids.Contains(input))))
                return "A compositing graph connection refers to a missing node.";
            if (node.Inputs.Any(input => input is not null &&
                    graph.Nodes.First(source => source.Id == input).Kind == InkCompositeNodeKind.Output))
                return "The Output node cannot feed another node.";
        }

        return HasCycle(graph) ? "A compositing graph cannot contain loops." : null;
    }

    /// <summary>True when connecting <paramref name="sourceId"/> into <paramref name="targetId"/> would create a loop.</summary>
    public static bool WouldCreateCycle(InkCompositeGraph graph, string sourceId, string targetId)
    {
        var lookup = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var stack = new Stack<string>([sourceId]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (stack.Count > 0)
        {
            string id = stack.Pop();
            if (id == targetId) return true;
            if (!seen.Add(id) || !lookup.TryGetValue(id, out InkCompositeNode? node)) continue;
            foreach (string? input in node.Inputs)
                if (input is not null) stack.Push(input);
        }

        return false;
    }

    public static bool HasProceduralContent(InkCompositeGraph? graph)
    {
        if (graph is not { Enabled: true } || GetOutput(graph) is not InkCompositeNode output) return false;
        var lookup = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var pending = new Stack<string>(output.Inputs.OfType<string>());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out string? id))
        {
            if (!seen.Add(id) || !lookup.TryGetValue(id, out InkCompositeNode? node)) continue;
            if (node.Kind == InkCompositeNodeKind.Scene3D &&
                node.Scene3D?.Objects.Any(item => item.Visible) == true) return true;
            foreach (string? input in node.Inputs)
                if (input is not null) pending.Push(input);
        }

        return false;
    }

    /// <summary>Builds a graph that reproduces the layer stack: blend modes, clipping masks, occlusion mattes and text.</summary>
    public static InkCompositeGraph FromLayerStack(IReadOnlyList<InkLayerProject> layers, bool enabled = true)
    {
        var graph = new InkCompositeGraph { Enabled = enabled };
        double column = 40;
        InkCompositeNode Add(InkCompositeNodeKind kind, double x, double y)
        {
            InkCompositeNode node = CreateNode(kind, CreateNodeId(graph), x, y);
            graph.Nodes.Add(node);
            return node;
        }

        InkCompositeNode picture = Add(InkCompositeNodeKind.Picture, column, 40);
        string current = picture.Id;
        string? clipBase = null;
        foreach (InkLayerProject layer in layers)
        {
            column += 220;
            InkCompositeNode layerNode = Add(InkCompositeNodeKind.InkLayer, column, 220);
            layerNode.LayerId = layer.Id;
            if (layer.IsHoldout)
            {
                clipBase = null;
                InkCompositeNode inside = Add(InkCompositeNodeKind.Mask, column, 360);
                inside.Inputs[0] = picture.Id;
                inside.Inputs[1] = layerNode.Id;
                InkCompositeNode outside = Add(InkCompositeNodeKind.Mask, column, 500);
                outside.Inputs[0] = current;
                outside.Inputs[1] = layerNode.Id;
                outside.Invert = true;
                InkCompositeNode restored = Add(InkCompositeNodeKind.Merge, column, 40);
                restored.Inputs[0] = inside.Id;
                restored.Inputs[1] = outside.Id;
                current = restored.Id;
                continue;
            }

            string foreground = layerNode.Id;
            if (layer.ClipToLayerBelow)
            {
                if (clipBase is null) continue;
                InkCompositeNode mask = Add(InkCompositeNodeKind.Mask, column, 360);
                mask.Inputs[0] = layerNode.Id;
                mask.Inputs[1] = clipBase;
                foreground = mask.Id;
            }
            else
            {
                clipBase = layerNode.Id;
            }

            InkCompositeNode merge = Add(InkCompositeNodeKind.Merge, column, 40);
            merge.BlendMode = layer.BlendMode;
            merge.Inputs[0] = current;
            merge.Inputs[1] = foreground;
            current = merge.Id;
        }

        column += 220;
        InkCompositeNode text = Add(InkCompositeNodeKind.Text, column, 220);
        InkCompositeNode withText = Add(InkCompositeNodeKind.Merge, column, 40);
        withText.Inputs[0] = current;
        withText.Inputs[1] = text.Id;
        InkCompositeNode output = Add(InkCompositeNodeKind.Output, column + 220, 40);
        output.Inputs[0] = withText.Id;
        return graph;
    }

    private static bool HasCycle(InkCompositeGraph graph)
    {
        var lookup = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (state.TryGetValue(id, out int value)) return value == 1;
            state[id] = 1;
            foreach (string? input in lookup[id].Inputs)
                if (input is not null && Visit(input)) return true;
            state[id] = 2;
            return false;
        }

        return graph.Nodes.Any(node => Visit(node.Id));
    }

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private static bool IsCoordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= MaxEditorCoordinate;
}
