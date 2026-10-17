using System.Text.Json;

namespace InkMotion;

public sealed class EditorProject
{
    public int Version { get; set; } = 3;
    public double FrameRate { get; set; } = 24;
    public string? VideoPath { get; set; }
    public string? AudioPath { get; set; }
    public double AudioVolume { get; set; } = 1;
    public bool AudioMuted { get; set; }
    public List<AudioTrackProject> AudioTracks { get; set; } = [];
    public List<InkLayerProject> InkLayers { get; set; } = [new()];
    public string ActiveInkLayerId { get; set; } = "ink-1";
    public int SelectedFrameIndex { get; set; }
    public List<EditorFrame> Frames { get; set; } = [new()];
}

public sealed class InkLayerProject
{
    public string Id { get; set; } = "ink-1";
    public string Name { get; set; } = "Ink 1";
    public bool IsVisible { get; set; } = true;
    public double Opacity { get; set; } = 1;
}

public sealed class AudioTrackProject
{
    public string? AudioPath { get; set; }
    public int StartFrame { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public uint Year { get; set; }
    public bool TagsInitialized { get; set; }
}

public sealed class EditorFrame
{
    public int Number { get; set; } = 1;
    public bool IsKeyframe { get; set; }
    public List<List<InkPoint>> Strokes { get; set; } = [];
    public Dictionary<string, List<List<InkPoint>>> LayerStrokes { get; set; } = new(StringComparer.Ordinal);
}

public sealed class InkPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Pressure { get; set; } = 0.5;
    public double BrushWidth { get; set; } = 4;
    public uint BrushColor { get; set; } = 0xFFFFA500;
}

public static class EditorProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Serialize(EditorProject project)
    {
        EditorProjectValidation.Validate(project);
        return JsonSerializer.Serialize(project, Options);
    }

    public static EditorProject Deserialize(string json)
    {
        EditorProject project = JsonSerializer.Deserialize<EditorProject>(json)
            ?? throw new InvalidDataException("The project file is empty.");
        if (project.Version == 1)
        {
            MigrateVersionOne(project);
        }
        if (project.Version == 2)
        {
            MigrateVersionTwo(project);
        }

        EditorProjectValidation.Validate(project);
        return project;
    }

    private static void MigrateVersionOne(EditorProject project)
    {
        if (project.AudioTracks is { Count: 0 } && !string.IsNullOrWhiteSpace(project.AudioPath))
        {
            project.AudioTracks.Add(new AudioTrackProject
            {
                AudioPath = project.AudioPath,
                Volume = project.AudioVolume,
                Muted = project.AudioMuted,
            });
        }

        project.Version = 2;
    }

    private static void MigrateVersionTwo(EditorProject project)
    {
        project.InkLayers ??= [];
        if (project.InkLayers.Count == 0)
        {
            project.InkLayers.Add(new InkLayerProject());
        }

        project.ActiveInkLayerId = project.InkLayers.Any(layer => layer is not null && layer.Id == project.ActiveInkLayerId)
            ? project.ActiveInkLayerId
            : project.InkLayers.FirstOrDefault(layer => layer is not null)?.Id ?? "ink-1";
        if (project.Frames is not null)
        {
            foreach (EditorFrame? frame in project.Frames)
            {
                if (frame is not null)
                {
                    frame.LayerStrokes ??= new Dictionary<string, List<List<InkPoint>>>(StringComparer.Ordinal);
                }
            }
        }

        project.Version = 3;
    }

}

public static class InkAnimation
{
    public const int MaximumInterpolatedPoints = 256;

    public static IReadOnlyList<List<InkPoint>> GetDisplayedStrokes(
        IReadOnlyList<EditorFrame> frames,
        int frameIndex)
        => GetDisplayedStrokes(frames, frameIndex, frame => frame.Strokes);

    public static IReadOnlyList<List<InkPoint>> GetDisplayedStrokes(
        IReadOnlyList<EditorFrame> frames,
        int frameIndex,
        Func<EditorFrame, List<List<InkPoint>>> getStrokes)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(getStrokes);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        if (frameIndex >= frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        EditorFrame current = frames[frameIndex];
        List<List<InkPoint>> currentStrokes = getStrokes(current);
        if (current.IsKeyframe || currentStrokes.Count > 0)
        {
            return currentStrokes;
        }

        int previousKeyframe = frameIndex - 1;
        while (previousKeyframe >= 0 && !frames[previousKeyframe].IsKeyframe)
        {
            previousKeyframe--;
        }

        if (previousKeyframe < 0 || getStrokes(frames[previousKeyframe]).Count == 0)
        {
            return currentStrokes;
        }

        int nextKeyframe = frameIndex + 1;
        while (nextKeyframe < frames.Count && !frames[nextKeyframe].IsKeyframe)
        {
            nextKeyframe++;
        }

        if (nextKeyframe >= frames.Count)
        {
            return getStrokes(frames[previousKeyframe]);
        }

        EditorFrame start = frames[previousKeyframe];
        EditorFrame end = frames[nextKeyframe];
        List<List<InkPoint>> startStrokes = getStrokes(start);
        List<List<InkPoint>> endStrokes = getStrokes(end);
        if (!CanInterpolate(startStrokes, endStrokes))
        {
            return startStrokes;
        }

        double progress = (double)(frameIndex - previousKeyframe) / (nextKeyframe - previousKeyframe);
        var interpolated = new List<List<InkPoint>>(startStrokes.Count);
        for (int i = 0; i < startStrokes.Count; i++)
        {
            List<InkPoint> first = Resample(startStrokes[i], GetSampleCount(startStrokes[i], endStrokes[i]));
            List<InkPoint> second = Resample(endStrokes[i], first.Count);
            var stroke = new List<InkPoint>(first.Count);
            for (int pointIndex = 0; pointIndex < first.Count; pointIndex++)
            {
                stroke.Add(new InkPoint
                {
                    X = Lerp(first[pointIndex].X, second[pointIndex].X, progress),
                    Y = Lerp(first[pointIndex].Y, second[pointIndex].Y, progress),
                    Pressure = Lerp(first[pointIndex].Pressure, second[pointIndex].Pressure, progress),
                    BrushWidth = Lerp(first[pointIndex].BrushWidth, second[pointIndex].BrushWidth, progress),
                    BrushColor = first[pointIndex].BrushColor,
                });
            }

            interpolated.Add(stroke);
        }

        return interpolated;
    }

    public static bool IsInterpolated(IReadOnlyList<EditorFrame> frames, int frameIndex)
        => IsInterpolated(frames, frameIndex, frame => frame.Strokes);

    public static bool IsInterpolated(
        IReadOnlyList<EditorFrame> frames,
        int frameIndex,
        Func<EditorFrame, List<List<InkPoint>>> getStrokes)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(getStrokes);
        if (frameIndex < 0 || frameIndex >= frames.Count ||
            frames[frameIndex].IsKeyframe || getStrokes(frames[frameIndex]).Count > 0)
        {
            return false;
        }

        int previousKeyframe = frameIndex - 1;
        while (previousKeyframe >= 0 && !frames[previousKeyframe].IsKeyframe)
        {
            previousKeyframe--;
        }

        int nextKeyframe = frameIndex + 1;
        while (nextKeyframe < frames.Count && !frames[nextKeyframe].IsKeyframe)
        {
            nextKeyframe++;
        }

        return previousKeyframe >= 0 &&
            nextKeyframe < frames.Count &&
            CanInterpolate(getStrokes(frames[previousKeyframe]), getStrokes(frames[nextKeyframe]));
    }

    public static HashSet<int> GetInterpolatedFrameIndices(IReadOnlyList<EditorFrame> frames)
        => GetInterpolatedFrameIndices(frames, frame => frame.Strokes);

    public static HashSet<int> GetInterpolatedFrameIndices(
        IReadOnlyList<EditorFrame> frames,
        Func<EditorFrame, List<List<InkPoint>>> getStrokes)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(getStrokes);
        var interpolatedFrames = new HashSet<int>();
        int previousKeyframe = -1;
        for (int nextKeyframe = 0; nextKeyframe < frames.Count; nextKeyframe++)
        {
            if (!frames[nextKeyframe].IsKeyframe)
            {
                continue;
            }

            if (previousKeyframe >= 0)
            {
                EditorFrame start = frames[previousKeyframe];
                EditorFrame end = frames[nextKeyframe];
                if (CanInterpolate(getStrokes(start), getStrokes(end)))
                {
                    for (int i = previousKeyframe + 1; i < nextKeyframe; i++)
                    {
                        if (!frames[i].IsKeyframe && getStrokes(frames[i]).Count == 0)
                        {
                            interpolatedFrames.Add(i);
                        }
                    }
                }
            }

            previousKeyframe = nextKeyframe;
        }

        return interpolatedFrames;
    }

    private static bool CanInterpolate(
        IReadOnlyList<List<InkPoint>> startStrokes,
        IReadOnlyList<List<InkPoint>> endStrokes) =>
        startStrokes.Count > 0 &&
        startStrokes.Count == endStrokes.Count &&
        startStrokes.All(stroke => stroke.Count > 0) &&
        endStrokes.All(stroke => stroke.Count > 0);

    private static int GetSampleCount(IReadOnlyList<InkPoint> first, IReadOnlyList<InkPoint> second) =>
        Math.Clamp(Math.Max(first.Count, second.Count), 2, MaximumInterpolatedPoints);

    private static List<InkPoint> Resample(IReadOnlyList<InkPoint> stroke, int sampleCount)
    {
        if (stroke.Count == 0)
        {
            return Enumerable.Range(0, sampleCount).Select(_ => new InkPoint()).ToList();
        }

        if (stroke.Count == 1)
        {
            return Enumerable.Range(0, sampleCount)
                .Select(_ => new InkPoint
                {
                    X = stroke[0].X,
                    Y = stroke[0].Y,
                    Pressure = stroke[0].Pressure,
                    BrushWidth = stroke[0].BrushWidth,
                    BrushColor = stroke[0].BrushColor,
                })
                .ToList();
        }

        var cumulativeLengths = new double[stroke.Count];
        for (int i = 1; i < stroke.Count; i++)
        {
            double dx = stroke[i].X - stroke[i - 1].X;
            double dy = stroke[i].Y - stroke[i - 1].Y;
            cumulativeLengths[i] = cumulativeLengths[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }

        double totalLength = cumulativeLengths[^1];
        if (totalLength == 0)
        {
            return Enumerable.Range(0, sampleCount)
                .Select(_ => new InkPoint
                {
                    X = stroke[0].X,
                    Y = stroke[0].Y,
                    Pressure = stroke[0].Pressure,
                    BrushWidth = stroke[0].BrushWidth,
                    BrushColor = stroke[0].BrushColor,
                })
                .ToList();
        }

        var result = new List<InkPoint>(sampleCount);
        int segment = 1;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            double distance = totalLength * sample / (sampleCount - 1);
            while (segment < cumulativeLengths.Length - 1 && cumulativeLengths[segment] < distance)
            {
                segment++;
            }

            double segmentLength = cumulativeLengths[segment] - cumulativeLengths[segment - 1];
            double progress = segmentLength == 0 ? 0 : (distance - cumulativeLengths[segment - 1]) / segmentLength;
            result.Add(new InkPoint
            {
                X = Lerp(stroke[segment - 1].X, stroke[segment].X, progress),
                Y = Lerp(stroke[segment - 1].Y, stroke[segment].Y, progress),
                Pressure = Lerp(stroke[segment - 1].Pressure, stroke[segment].Pressure, progress),
                BrushWidth = Lerp(stroke[segment - 1].BrushWidth, stroke[segment].BrushWidth, progress),
                BrushColor = stroke[segment - 1].BrushColor,
            });
        }

        return result;
    }

    private static double Lerp(double start, double end, double progress) =>
        start + (end - start) * progress;
}

public static class ProjectFrameRates
{
    public static IReadOnlyList<double> Supported { get; } = Array.AsReadOnly(new[] { 12d, 24d, 30d, 60d });
}

internal static class EditorProjectValidation
{
    public static void Validate(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Version != 3)
        {
            throw new InvalidDataException($"Project version {project.Version} is not supported.");
        }

        if (!ProjectFrameRates.Supported.Contains(project.FrameRate))
        {
            throw new InvalidDataException("The project frame rate must be 12, 24, 30, or 60 fps.");
        }

        if (!double.IsFinite(project.AudioVolume) || project.AudioVolume is < 0 or > 1)
        {
            throw new InvalidDataException("Audio volume must be between 0 and 1.");
        }

        if (project.AudioTracks is null ||
            project.AudioTracks.Any(track =>
                track is null ||
                track.StartFrame < 1 ||
                !double.IsFinite(track.Volume) ||
                track.Volume is < 0 or > 1 ||
                track.Year > 9999))
        {
            throw new InvalidDataException("Audio tracks must have a valid start frame, volume, and year.");
        }

        if (project.InkLayers is null || project.InkLayers.Count == 0 ||
            project.InkLayers.Any(layer =>
                layer is null ||
                string.IsNullOrWhiteSpace(layer.Id) ||
                string.IsNullOrWhiteSpace(layer.Name) ||
                !double.IsFinite(layer.Opacity) ||
                layer.Opacity is < 0 or > 1) ||
            project.InkLayers.Select(layer => layer.Id).Distinct(StringComparer.Ordinal).Count() != project.InkLayers.Count ||
            !project.InkLayers.Any(layer => layer.Id == project.ActiveInkLayerId) ||
            !project.InkLayers.Any(layer => layer.Id == "ink-1"))
        {
            throw new InvalidDataException("Ink layers must have unique ids, names, valid opacity, and an active layer.");
        }

        if (project.Frames is null || project.Frames.Count == 0)
        {
            throw new InvalidDataException("A project must contain at least one frame.");
        }

        if (project.SelectedFrameIndex < 0 || project.SelectedFrameIndex >= project.Frames.Count)
        {
            throw new InvalidDataException("The selected frame index is outside the project timeline.");
        }

        int expectedFrameNumber = 1;
        foreach (EditorFrame? frame in project.Frames)
        {
            if (frame is null || frame.Number != expectedFrameNumber++ || frame.Strokes is null || frame.LayerStrokes is null)
            {
                throw new InvalidDataException("The project timeline must contain valid, sequentially numbered frames.");
            }

            foreach (List<List<InkPoint>> layerStrokes in new[] { frame.Strokes }
                         .Concat(frame.LayerStrokes.Values))
            {
                if (layerStrokes is null)
                {
                    throw new InvalidDataException("A frame contains an invalid ink layer.");
                }

                foreach (List<InkPoint>? stroke in layerStrokes)
                {
                    ValidateStroke(stroke);
                }
            }

            if (frame.LayerStrokes.Keys.Any(layerId =>
                    layerId == "ink-1" || !project.InkLayers.Any(layer => layer.Id == layerId)))
            {
                throw new InvalidDataException("A frame contains strokes for an unknown ink layer.");
            }
        }
    }

    private static void ValidateStroke(List<InkPoint>? stroke)
    {
        if (stroke is null || stroke.Any(point =>
                    point is null ||
                    !double.IsFinite(point.X) ||
                    !double.IsFinite(point.Y) ||
                    point.X is < 0 or > 1 ||
                    point.Y is < 0 or > 1 ||
                    !double.IsFinite(point.Pressure) ||
                    point.Pressure is < 0 or > 1 ||
                    !double.IsFinite(point.BrushWidth) ||
                    point.BrushWidth is < 0.5 or > 64))
                {
            throw new InvalidDataException("The project contains invalid ink coordinates or brush settings.");
        }
    }
}
