using System.Text.Json;

namespace InkMotion;

public static class InkLinkedCels
{
    private static string Drawing(EditorFrame frame) => JsonSerializer.Serialize(new
    {
        frame.Strokes,
        Layers = frame.LayerStrokes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new { pair.Key, pair.Value }),
        frame.TextOverlays,
    });

    public static void Validate(IReadOnlyList<EditorFrame> frames)
    {
        var drawings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (EditorFrame frame in frames)
        {
            if (frame.DrawingLinkId is null ||
                (frame.DrawingLinkId.Length > 0 && (!Guid.TryParseExact(frame.DrawingLinkId, "N", out _) || !frame.IsKeyframe)))
            {
                throw new InvalidDataException("Linked ink cels need valid drawing identities and explicit keyframes.");
            }
            if (frame.DrawingLinkId.Length == 0)
            {
                continue;
            }
            string drawing = Drawing(frame);
            if (drawings.TryGetValue(frame.DrawingLinkId, out string? expected) && expected != drawing)
            {
                throw new InvalidDataException("Linked ink cels contain inconsistent drawings. Their layers and text must match.");
            }
            drawings[frame.DrawingLinkId] = drawing;
        }
    }

    public static int Repeat(List<EditorFrame> frames, IReadOnlyList<InkLayerProject> layers,
        int first, int last, int copies)
    {
        if (first < 0 || last < first || last >= frames.Count || copies is < 1 or > 100 ||
            !frames[first].IsKeyframe || !frames[last].IsKeyframe)
        {
            throw new InvalidOperationException("Choose a keyframe-bounded cel range and 1-100 additional linked copies.");
        }
        long added = (long)(last - first + 1) * copies;
        long exposure = frames.Skip(first).Take(last - first + 1).Sum(frame => (long)frame.ExposureFrames);
        if (added > 10000 || (long)frames.Count + added > int.MaxValue ||
            frames.Sum(frame => (long)frame.ExposureFrames) + exposure * copies > int.MaxValue)
        {
            throw new InvalidOperationException("A linked repeat can add at most 10,000 cels and must fit the timeline frame limit.");
        }
        if (frames.Skip(first).Take(last - first + 1).Any(frame => frame.DrawingLinkId.Length > 0))
        {
            throw new InvalidOperationException("Unlink the source cels before creating a new linked cycle.");
        }

        var sources = new List<EditorFrame>();
        for (int index = first; index <= last; index++)
        {
            EditorFrame cel = InkEditing.Clone(frames[index]);
            cel.DrawingLinkId = Guid.NewGuid().ToString("N");
            cel.IsKeyframe = true;
            cel.Strokes = InkEditing.CloneStrokes(InkAnimation.GetDisplayedStrokes(frames, index).ToList());
            foreach (InkLayerProject layer in layers.Where(layer => layer.Id != ExportPlanning.BaseInkLayerId))
            {
                cel.LayerStrokes[layer.Id] = InkEditing.CloneStrokes(
                    InkAnimation.GetDisplayedStrokes(frames, index,
                        frame => ExportPlanning.GetLayerStrokes(frame, layer.Id)).ToList());
            }
            sources.Add(cel);
        }
        var repeats = new List<EditorFrame>((int)added);
        for (int copy = 0; copy < copies; copy++)
        {
            repeats.AddRange(sources.Select(frame => InkEditing.Clone(frame, preserveDrawingLink: true)));
        }
        for (int index = 0; index < sources.Count; index++)
        {
            frames[first + index] = sources[index];
        }
        frames.InsertRange(last + 1, repeats);
        for (int index = 0; index < frames.Count; index++)
        {
            frames[index].Number = index + 1;
        }
        return last + 1;
    }

    public static void Synchronise(IReadOnlyList<EditorFrame> frames, IReadOnlyList<EditorFrame> before,
        int selectedIndex)
    {
        Dictionary<string, EditorFrame> previous = before.Where(frame => frame.DrawingLinkId.Length > 0)
            .GroupBy(frame => frame.DrawingLinkId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var group in frames.Where(frame => frame.DrawingLinkId.Length > 0)
            .GroupBy(frame => frame.DrawingLinkId, StringComparer.Ordinal))
        {
            EditorFrame[] linked = group.ToArray();
            if (linked.Length < 2 || !previous.TryGetValue(group.Key, out EditorFrame? old))
            {
                continue;
            }
            string original = Drawing(old);
            EditorFrame? selected = selectedIndex >= 0 && selectedIndex < frames.Count &&
                frames[selectedIndex].DrawingLinkId == group.Key ? frames[selectedIndex] : null;
            EditorFrame? source = selected is not null && Drawing(selected) != original ? selected :
                linked.FirstOrDefault(frame => Drawing(frame) != original);
            if (source is null)
            {
                continue;
            }
            string changed = Drawing(source);
            foreach (EditorFrame cel in linked)
            {
                if (ReferenceEquals(cel, source) || Drawing(cel) == changed)
                {
                    continue;
                }
                EditorFrame content = InkEditing.Clone(source);
                cel.Strokes = content.Strokes;
                cel.LayerStrokes = content.LayerStrokes;
                cel.TextOverlays = content.TextOverlays;
            }
        }
    }

    public static bool Unlink(IReadOnlyList<EditorFrame> frames, int first, int last)
    {
        if (first < 0 || last < first || last >= frames.Count || last - first >= 10000)
        {
            throw new InvalidOperationException("Choose up to 10,000 existing cels to unlink.");
        }
        bool changed = false;
        for (int index = first; index <= last; index++)
        {
            changed |= frames[index].DrawingLinkId.Length > 0;
            frames[index].DrawingLinkId = "";
        }
        return changed;
    }
}
