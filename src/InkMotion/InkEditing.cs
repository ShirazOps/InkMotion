namespace InkMotion;

public static class InkEditing
{
    public static int RepeatCelRange(List<EditorFrame> frames, int first, int last, int additionalCopies)
    {
        if (first < 0 || last < first || last >= frames.Count ||
            additionalCopies is < 1 or > 100)
        {
            throw new InvalidOperationException("Choose an existing inclusive cel range and 1-100 additional copies.");
        }

        if (!frames[first].IsKeyframe || !frames[last].IsKeyframe)
        {
            throw new InvalidOperationException("Set keyframes on the first and last cels of the cycle so its tween boundaries are self-contained.");
        }

        long added = (long)(last - first + 1) * additionalCopies;
        long cycleExposure = frames.Skip(first).Take(last - first + 1).Sum(frame => (long)frame.ExposureFrames);
        long totalExposure = frames.Sum(frame => (long)frame.ExposureFrames) + cycleExposure * additionalCopies;
        if (added > 10000 || (long)frames.Count + added > int.MaxValue || totalExposure > int.MaxValue)
        {
            throw new InvalidOperationException("Repeat fewer cels: one operation can add at most 10,000 cels and must fit the timeline frame limit.");
        }

        List<EditorFrame> source = frames.GetRange(first, last - first + 1);
        var copies = new List<EditorFrame>((int)added);
        for (int copy = 0; copy < additionalCopies; copy++)
        {
            copies.AddRange(source.Select(Clone));
        }

        frames.InsertRange(last + 1, copies);
        for (int index = 0; index < frames.Count; index++)
        {
            frames[index].Number = index + 1;
        }

        return last + 1;
    }

    public static InkLayerProject MergeLayerDown(List<InkLayerProject> layers, List<EditorFrame> frames, string id)
    {
        int index = layers.FindIndex(layer => layer.Id == id);
        if (index <= 0 || id == ExportPlanning.BaseInkLayerId)
        {
            throw new InvalidOperationException("Select a non-base ink layer with a layer beneath it.");
        }

        InkLayerProject source = layers[index];
        InkLayerProject target = layers[index - 1];
        if (source.IsLocked || target.IsLocked || !source.IsVisible || !target.IsVisible ||
            !double.IsFinite(source.Opacity) || source.Opacity != target.Opacity)
        {
            throw new InvalidOperationException("Unlock and show both adjacent layers, and give them matching opacity before merging.");
        }

        var drawings = new List<List<List<InkPoint>>>(frames.Count);
        for (int frame = 0; frame < frames.Count; frame++)
        {
            var combined = InkAnimation.GetDisplayedStrokes(frames, frame,
                cel => ExportPlanning.GetLayerStrokes(cel, target.Id)).ToList();
            combined.AddRange(InkAnimation.GetDisplayedStrokes(frames, frame,
                cel => ExportPlanning.GetLayerStrokes(cel, source.Id)));
            drawings.Add(CloneStrokes(combined));
        }

        for (int frame = 0; frame < frames.Count; frame++)
        {
            if (target.Id == ExportPlanning.BaseInkLayerId)
            {
                frames[frame].Strokes = drawings[frame];
            }
            else
            {
                frames[frame].LayerStrokes[target.Id] = drawings[frame];
            }

            frames[frame].LayerStrokes.Remove(source.Id);
        }

        layers.RemoveAt(index);
        return target;
    }

    public static InkLayerProject DuplicateLayer(List<InkLayerProject> layers, List<EditorFrame> frames, string id)
    {
        int index = layers.FindIndex(layer => layer.Id == id);
        if (index < 0)
        {
            throw new InvalidOperationException("Select an existing ink layer to duplicate.");
        }

        InkLayerProject copy = CloneLayer(layers[index]);
        copy.Id = $"ink-{Guid.NewGuid():N}";
        copy.Name += " copy";
        copy.IsLocked = false;
        List<List<List<InkPoint>>> drawings = frames.Select(frame => CloneStrokes(ExportPlanning.GetLayerStrokes(frame, id))).ToList();
        for (int frame = 0; frame < frames.Count; frame++)
        {
            if (drawings[frame].Count > 0)
            {
                frames[frame].LayerStrokes.Add(copy.Id, drawings[frame]);
            }
        }

        layers.Insert(index + 1, copy);
        return copy;
    }

    public static void RemoveLayer(List<InkLayerProject> layers, List<EditorFrame> frames, string id)
    {
        InkLayerProject? layer = layers.FirstOrDefault(layer => layer.Id == id);
        if (layer is null || id == ExportPlanning.BaseInkLayerId || layer.IsLocked ||
            (layer.IsVisible && layers.Count(candidate => candidate.IsVisible) == 1))
        {
            throw new InvalidOperationException("Keep the base layer and at least one visible layer; unlock the drawing before removing it.");
        }

        layers.Remove(layer);
        foreach (EditorFrame frame in frames)
        {
            frame.LayerStrokes.Remove(id);
        }
    }

    public static InkLayerProject CloneLayer(InkLayerProject layer) => new()
    {
        Id = layer.Id,
        Name = layer.Name,
        IsVisible = layer.IsVisible,
        IsLocked = layer.IsLocked,
        Opacity = layer.Opacity,
    };

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
        if (index < 0 || target < 0 || target >= layers.Count || Math.Abs(offset) != 1 ||
            layers[index].IsLocked || layers[target].IsLocked)
        {
            return false;
        }

        (layers[index], layers[target]) = (layers[target], layers[index]);
        return true;
    }
}
