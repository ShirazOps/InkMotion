namespace InkMotion;

public static class InkEditing
{
    public static EditorFrame Clone(EditorFrame frame) => new()
    {
        Number = frame.Number,
        ExposureFrames = frame.ExposureFrames,
        IsKeyframe = frame.IsKeyframe,
        InterpolationMode = frame.InterpolationMode,
        TextOverlays = frame.TextOverlays.Select(text => new InkTextOverlay
        {
            Text = text.Text,
            X = text.X,
            Y = text.Y,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            Colour = text.Colour,
        }).ToList(),
        Strokes = CloneStrokes(frame.Strokes),
        LayerStrokes = frame.LayerStrokes.ToDictionary(pair => pair.Key, pair => CloneStrokes(pair.Value), StringComparer.Ordinal),
    };

    public static List<List<InkPoint>> CloneStrokes(List<List<InkPoint>> strokes) =>
        strokes.Select(stroke => stroke.Select(point => new InkPoint
        {
            X = point.X,
            Y = point.Y,
            Pressure = point.Pressure,
            BrushWidth = point.BrushWidth,
            BrushColour = point.BrushColour,
        }).ToList()).ToList();

    public static int InsertCopy(List<EditorFrame> frames, int afterIndex, EditorFrame source)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(source);
        if (afterIndex < 0 || afterIndex >= frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(afterIndex));
        }

        int inserted = afterIndex + 1;
        frames.Insert(inserted, Clone(source));
        for (int index = 0; index < frames.Count; index++)
        {
            frames[index].Number = index + 1;
        }

        return inserted;
    }

    public static bool MoveLayer(List<InkLayerProject> layers, string id, int offset)
    {
        int index = layers.FindIndex(layer => layer.Id == id);
        int target = index + offset;
        if (index < 0 || target < 0 || target >= layers.Count || Math.Abs(offset) != 1)
        {
            return false;
        }

        (layers[index], layers[target]) = (layers[target], layers[index]);
        return true;
    }
}
