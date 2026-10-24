using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace InkMotion;

public sealed class EditorProject
{
    public const int CurrentVersion = 58;
    public int Version { get; set; } = CurrentVersion;
    public double FrameRate { get; set; } = 24;
    /// <summary>Single source video used by project versions 1-8; migrated to <see cref="VideoTracks"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VideoPath { get; set; }
    public List<TimelineTrackProject> VideoTracks { get; set; } = [];
    public List<VideoMulticamGroup> VideoMulticamGroups { get; set; } = [];
    public List<TimelineMarkerProject> Markers { get; set; } = [];
    public string? AudioPath { get; set; }
    public double AudioVolume { get; set; } = 1;
    public bool AudioMuted { get; set; }
    public List<AudioTrackProject> AudioTracks { get; set; } = [];
    public AudioChannelLayout AudioMixLayout { get; set; } = AudioChannelLayout.Stereo;
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
    public bool IsLocked { get; set; }
    public double Opacity { get; set; } = 1;
}

public sealed class AudioTrackProject
{
    public string? AudioPath { get; set; }
    public string? LinkedVideoClipId { get; set; }
    public int StartFrame { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    public double Balance { get; set; }
    public bool SpatialPanEnabled { get; set; }
    public double SpatialPanAzimuthDegrees { get; set; }
    public AudioEffectSettings Effects { get; set; } = new();
    public double SourceInSeconds { get; set; }
    public double? SourceOutSeconds { get; set; }
    public double FadeInSeconds { get; set; }
    public double FadeOutSeconds { get; set; }
    public double GainDecibels { get; set; }
    public double TempoPercent { get; set; } = 100;
    public double PitchSemitones { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public uint Year { get; set; }
    [JsonPropertyName("TagsInitialized")]
    public bool TagsInitialised { get; set; }
}

public sealed class EditorFrame
{
    public int Number { get; set; } = 1;
    public string GroupName { get; set; } = "";
    public string DrawingLinkId { get; set; } = "";
    public int ExposureFrames { get; set; } = 1;
    public bool IsKeyframe { get; set; }
    public InkInterpolationMode InterpolationMode { get; set; } = InkInterpolationMode.Linear;
    public List<List<InkPoint>> Strokes { get; set; } = [];
    public Dictionary<string, List<List<InkPoint>>> LayerStrokes { get; set; } = new(StringComparer.Ordinal);
    public List<InkTextOverlay> TextOverlays { get; set; } = [];
}

public sealed class InkTextOverlay
{
    public string Text { get; set; } = string.Empty;
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 48;
    public uint Colour { get; set; } = 0xFFFFFFFF;
    public List<InkTextStyleRange> StyleRanges { get; set; } = [];
}

public sealed class InkTextStyleRange
{
    public int Start { get; set; }
    public int Length { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 48;
    public uint Colour { get; set; } = 0xFFFFFFFF;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
}

public enum InkInterpolationMode
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}

public enum ExportResolution
{
    P720,
    P1080,
    P1440,
    Uhd4K,
}

public sealed record VideoExportProfile(ExportResolution Resolution, int Width, int Height, double FrameRate)
{
    public static IReadOnlyList<(ExportResolution Resolution, string Name, int Width, int Height)> Resolutions { get; } =
    [
        (ExportResolution.P720, "720p", 1280, 720),
        (ExportResolution.P1080, "1080p", 1920, 1080),
        (ExportResolution.P1440, "1440p", 2560, 1440),
        (ExportResolution.Uhd4K, "4K UHD", 3840, 2160),
    ];

    public static VideoExportProfile Create(ExportResolution resolution, double frameRate)
    {
        var dimensions = Resolutions.FirstOrDefault(item => item.Resolution == resolution);
        if (dimensions.Width == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }

        if (!double.IsFinite(frameRate) || (frameRate != 12 && (frameRate < 24 || frameRate > 120)))
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        return new VideoExportProfile(resolution, dimensions.Width, dimensions.Height, frameRate);
    }
}

public static class InkTimeline
{
    public static int GetStartFramePosition(IReadOnlyList<EditorFrame> frames, int frameIndex)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        if (frameIndex >= frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        int framePosition = 0;
        for (int index = 0; index < frameIndex; index++)
        {
            framePosition = checked(framePosition + frames[index].ExposureFrames);
        }

        return framePosition;
    }

    public static int GetTotalExposureFrames(IReadOnlyList<EditorFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        int total = 0;
        foreach (EditorFrame frame in frames)
        {
            total = checked(total + frame.ExposureFrames);
        }

        return total;
    }

    public static int GetFrameIndexAtPosition(IReadOnlyList<EditorFrame> frames, int framePosition)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(framePosition);
        if (frames.Count == 0)
        {
            throw new ArgumentException("The timeline must contain at least one frame.", nameof(frames));
        }

        int currentPosition = 0;
        for (int index = 0; index < frames.Count; index++)
        {
            currentPosition = checked(currentPosition + frames[index].ExposureFrames);
            if (framePosition < currentPosition)
            {
                return index;
            }
        }

        return frames.Count - 1;
    }

    public static int DeleteFrame(IList<EditorFrame> frames, int frameIndex)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        if (frameIndex >= frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        if (frames.Count == 1)
        {
            throw new InvalidOperationException("A project must contain at least one frame.");
        }

        frames.RemoveAt(frameIndex);
        for (int index = frameIndex; index < frames.Count; index++)
        {
            frames[index].Number = index + 1;
        }

        return Math.Min(frameIndex, frames.Count - 1);
    }
}

public enum InkBrushTexture
{
    Round,
    Grain,
}

public sealed class InkPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Pressure { get; set; } = 0.5;
    public double BrushWidth { get; set; } = 4;
    public InkBrushTexture BrushTexture { get; set; }
    public bool IsFilled { get; set; }
    [JsonPropertyName("BrushColor")]
    public uint BrushColour { get; set; } = 0xFFFFA500;
}

public static class VideoFileTypes
{
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        Array.AsReadOnly(new[] { ".mp4", ".mov", ".m4v", ".wmv", ".avi", ".av1", ".mkv", ".webm" });
}

public static class EditorProjectSerialiser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialise(EditorProject project)
    {
        EditorProjectValidation.Validate(project);
        return JsonSerializer.Serialize(project, Options);
    }

    public static EditorProject Deserialise(string json)
    {
        JsonNode root = JsonNode.Parse(json)
            ?? throw new InvalidDataException("The project file is empty.");
        MigrateLegacyMulticamClipIds(root);
        EditorProject project = JsonSerializer.Deserialize<EditorProject>(root.ToJsonString(), Options)
            ?? throw new InvalidDataException("The project file is empty.");
        if (project.Version == 1)
        {
            MigrateVersionOne(project);
        }
        if (project.Version == 2)
        {
            MigrateVersionTwo(project);
        }
        if (project.Version == 3)
        {
            MigrateVersionThree(project);
        }
        if (project.Version == 4)
        {
            MigrateVersionFour(project);
        }
        if (project.Version == 5)
        {
            MigrateVersionFive(project);
        }
        if (project.Version == 6)
        {
            project.Version = 7;
        }
        if (project.Version == 7)
        {
            project.Version = 8;
        }
        if (project.Version == 8)
        {
            MigrateVersionEight(project);
        }
        if (project.Version == 9)
        {
            project.Version = 10;
        }
        if (project.Version == 10)
        {
            project.Version = 11;
        }
        if (project.Version == 11)
        {
            project.Version = 12;
        }
        if (project.Version == 12)
        {
            project.Version = 13;
        }
        if (project.Version == 13)
        {
            project.Version = 14;
        }
        if (project.Version == 14)
        {
            project.Version = 15;
        }
        if (project.Version == 15)
        {
            project.Version = 16;
        }
        if (project.Version == 16)
        {
            project.Version = 17;
        }
        if (project.Version == 17)
        {
            project.Version = 18;
        }
        if (project.Version == 18)
        {
            project.Version = 19;
        }
        if (project.Version == 19)
        {
            project.Version = 20;
        }
        if (project.Version == 20)
        {
            project.Version = 21;
        }
        if (project.Version == 21)
        {
            project.Version = 22;
        }
        if (project.Version == 22)
        {
            project.Version = 23;
        }
        if (project.Version == 23)
        {
            project.Version = 24;
        }
        if (project.Version == 24)
        {
            project.Version = 25;
        }
        if (project.Version == 25)
        {
            project.Version = 26;
        }
        if (project.Version == 26)
        {
            project.Version = 27;
        }
        if (project.Version == 27)
        {
            project.Version = 28;
        }
        if (project.Version == 28)
        {
            project.Version = 29;
        }
        if (project.Version == 29)
        {
            project.Version = 30;
        }
        if (project.Version == 30)
        {
            project.Version = 31;
        }
        if (project.Version == 31)
        {
            project.Version = 32;
        }
        if (project.Version == 32)
        {
            project.Version = 33;
        }
        if (project.Version == 33)
        {
            project.Version = 34;
        }
        if (project.Version == 34)
        {
            project.Version = 35;
        }
        if (project.Version == 35)
        {
            project.Version = 36;
        }
        if (project.Version == 36)
        {
            project.Version = 37;
        }
        if (project.Version == 37)
        {
            project.Version = 38;
        }
        if (project.Version == 38)
        {
            project.Version = 39;
        }
        if (project.Version == 39)
        {
            project.Version = 40;
        }
        if (project.Version == 40)
        {
            project.Version = 41;
        }
        if (project.Version == 41)
        {
            project.Version = 42;
        }
        if (project.Version == 42)
        {
            project.Version = 43;
        }
        if (project.Version == 43)
        {
            project.Version = 44;
        }
        if (project.Version == 44)
        {
            project.Version = 45;
        }
        if (project.Version == 45)
        {
            project.Version = 46;
        }
        if (project.Version == 46)
        {
            project.Version = 47;
        }
        if (project.Version == 47)
        {
            project.Version = 48;
        }
        if (project.Version == 48)
        {
            project.Version = 49;
        }
        if (project.Version == 49)
        {
            project.Version = 50;
        }
        if (project.Version == 50)
        {
            project.Version = 51;
        }
        if (project.Version == 51)
        {
            project.Version = 52;
        }
        if (project.Version == 52)
        {
            project.Version = 53;
        }
        if (project.Version == 53)
        {
            project.Version = 54;
        }
        if (project.Version == 54)
        {
            project.Version = 55;
        }
        if (project.Version == 55)
        {
            project.Version = 56;
        }
        if (project.Version == 56)
        {
            project.Version = 57;
        }
        if (project.Version == 57)
        {
            project.Version = EditorProject.CurrentVersion;
        }

        EditorProjectValidation.Validate(project);
        return project;
    }

    private static void MigrateLegacyMulticamClipIds(JsonNode root)
    {
        if (root is not JsonObject project ||
            project["VideoTracks"] is not JsonArray tracks ||
            project["VideoMulticamGroups"] is not JsonArray groups)
        {
            return;
        }

        var clipToTrack = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonNode? trackNode in tracks)
        {
            if (trackNode is not JsonObject track ||
                !string.Equals(track["Kind"]?.GetValue<string>(), nameof(TimelineTrackKind.Video), StringComparison.Ordinal) ||
                track["Id"]?.GetValue<string>() is not { Length: > 0 } trackId ||
                track["Clips"] is not JsonArray clips)
            {
                continue;
            }

            foreach (JsonNode? clipNode in clips)
            {
                if (clipNode is JsonObject clip && clip["Id"]?.GetValue<string>() is { Length: > 0 } clipId)
                {
                    clipToTrack.TryAdd(clipId, trackId);
                }
            }
        }

        foreach (JsonNode? groupNode in groups)
        {
            if (groupNode is not JsonObject group)
            {
                continue;
            }

            if (group["Angles"] is JsonArray angles)
            {
                foreach (JsonNode? angleNode in angles)
                {
                    MigrateLegacyClipId(angleNode as JsonObject, clipToTrack);
                }
            }

            if (group["Switches"] is JsonArray switches)
            {
                foreach (JsonNode? switchNode in switches)
                {
                    MigrateLegacyClipId(switchNode as JsonObject, clipToTrack);
                }
            }
        }
    }

    private static void MigrateLegacyClipId(JsonObject? value, IReadOnlyDictionary<string, string> clipToTrack)
    {
        if (value?["ClipId"]?.GetValue<string>() is not { Length: > 0 } clipId ||
            !clipToTrack.TryGetValue(clipId, out string? trackId))
        {
            return;
        }

        value["TrackId"] = trackId;
        value.Remove("ClipId");
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

    private static void MigrateVersionThree(EditorProject project)
    {
        project.Version = 4;
    }

    private static void MigrateVersionFour(EditorProject project)
    {
        project.Version = 5;
    }

    private static void MigrateVersionFive(EditorProject project)
    {
        project.Version = 6;
    }

    private static void MigrateVersionEight(EditorProject project)
    {
        project.VideoTracks ??= [];
        if (project.VideoTracks.Count == 0)
        {
            var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Name = "Video 1" };
            if (!string.IsNullOrWhiteSpace(project.VideoPath))
            {
                track.Clips.Add(new TimelineClipProject { SourcePath = project.VideoPath, StartFrame = 1 });
            }

            project.VideoTracks.Add(track);
        }

        project.VideoPath = null;
        project.Version = 9;
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

        int startFramePosition = InkTimeline.GetStartFramePosition(frames, previousKeyframe);
        int endFramePosition = InkTimeline.GetStartFramePosition(frames, nextKeyframe);
        int currentFramePosition = InkTimeline.GetStartFramePosition(frames, frameIndex);
        double progress = ApplyInterpolation(
            (double)(currentFramePosition - startFramePosition) / (endFramePosition - startFramePosition),
            start.InterpolationMode);
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
                    BrushColour = first[pointIndex].BrushColour,
                    BrushTexture = first[pointIndex].BrushTexture,
                    IsFilled = first[pointIndex].IsFilled,
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
        endStrokes.All(stroke => stroke.Count > 0) &&
        startStrokes.Select((stroke, index) => stroke[0].IsFilled == endStrokes[index][0].IsFilled).All(match => match);

    private static double ApplyInterpolation(double progress, InkInterpolationMode mode) =>
        mode switch
        {
            InkInterpolationMode.Linear => progress,
            InkInterpolationMode.EaseIn => progress * progress,
            InkInterpolationMode.EaseOut => 1 - Math.Pow(1 - progress, 2),
            InkInterpolationMode.EaseInOut => progress < 0.5
                ? 2 * progress * progress
                : 1 - (Math.Pow(-2 * progress + 2, 2) / 2),
            _ => progress,
        };

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
                    BrushColour = stroke[0].BrushColour,
                    BrushTexture = stroke[0].BrushTexture,
                    IsFilled = stroke[0].IsFilled,
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
                    BrushColour = stroke[0].BrushColour,
                    BrushTexture = stroke[0].BrushTexture,
                    IsFilled = stroke[0].IsFilled,
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
                BrushColour = stroke[segment - 1].BrushColour,
                BrushTexture = stroke[segment - 1].BrushTexture,
                IsFilled = stroke[segment - 1].IsFilled,
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
        if (project.Version != EditorProject.CurrentVersion)
        {
            throw new InvalidDataException($"Project version {project.Version} is not supported.");
        }

        if (!ProjectFrameRates.Supported.Contains(project.FrameRate))
        {
            throw new InvalidDataException("The project frame rate must be 12, 24, 30, or 60 fps.");
        }

        if (project.Markers is null)
        {
            throw new InvalidDataException("Timeline markers must be a list.");
        }

        TimelineMarkers.Validate(project.Markers);

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

        if (project.AudioTracks.Any(track => !AudioClipEditing.HasValidClipSettings(track)))
        {
            throw new InvalidDataException("Audio clips must have valid in/out points, fades, gain, tempo, pitch, balance, spatial pan and effect settings.");
        }

        if (project.AudioTracks.Any(track => track.SpatialPanEnabled &&
            (!double.IsFinite(track.SpatialPanAzimuthDegrees) || track.SpatialPanAzimuthDegrees is < -180 or > 180)))
        {
            throw new InvalidDataException("Spatial pan azimuth must be between -180 and 180 degrees.");
        }

        if (!AudioChannelLayouts.IsDefined(project.AudioMixLayout))
        {
            throw new InvalidDataException("The audio mix layout is not supported.");
        }

        if (project.VideoPath is not null)
        {
            throw new InvalidDataException("Video sources must be stored as timeline clips.");
        }

        if (VideoTimelineEditing.Validate(project.VideoTracks, project.FrameRate) is string timelineError)
        {
            throw new InvalidDataException(timelineError);
        }

        if (project.VideoMulticamGroups is null ||
            project.VideoMulticamGroups.Any(group =>
                group is null || string.IsNullOrWhiteSpace(group.Id) || string.IsNullOrWhiteSpace(group.Name) ||
                group.StartFrame < 1 || group.Angles is null || group.Angles.Count < 2 || group.Switches is null) ||
            project.VideoMulticamGroups.Select(group => group.Id).Distinct(StringComparer.Ordinal).Count() != project.VideoMulticamGroups.Count)
        {
            throw new InvalidDataException("Multicamera groups must have unique ids, a name, a start frame and at least two angles.");
        }

        var videoTrackIds = project.VideoTracks
            .Where(track => track.Kind == TimelineTrackKind.Video)
            .Select(track => track.Id)
            .ToHashSet(StringComparer.Ordinal);
        var groupedTrackIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (VideoMulticamGroup group in project.VideoMulticamGroups)
        {
            if (group.Angles.Any(angle =>
                    angle is null || string.IsNullOrWhiteSpace(angle.TrackId) || string.IsNullOrWhiteSpace(angle.Name) ||
                    !videoTrackIds.Contains(angle.TrackId) || !groupedTrackIds.Add(angle.TrackId)) ||
                group.Angles.Select(angle => angle.TrackId).Distinct(StringComparer.Ordinal).Count() != group.Angles.Count ||
                group.Switches.Any(item =>
                    item is null || item.Frame < group.StartFrame ||
                    !group.Angles.Any(angle => angle.TrackId == item.TrackId)) ||
                group.Switches.Select(item => item.Frame).Distinct().Count() != group.Switches.Count)
            {
                throw new InvalidDataException("Multicamera angles and switches must reference unique video tracks and valid frames.");
            }
        }

        if (LinkedAudioVideoEditing.Validate(project.VideoTracks, project.AudioTracks) is string linkError)
        {
            throw new InvalidDataException(linkError);
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
            if (frame is null ||
                frame.GroupName is null || frame.GroupName.Length > 80 ||
                frame.GroupName != frame.GroupName.Trim() || frame.GroupName.Any(char.IsControl) ||
                frame.Number != expectedFrameNumber++ ||
                frame.ExposureFrames is < 1 or > 999 ||
                !Enum.IsDefined(frame.InterpolationMode) ||
                frame.Strokes is null ||
                frame.LayerStrokes is null ||
                frame.TextOverlays is null)
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

            if (frame.TextOverlays.Any(text =>
                    text is null ||
                    string.IsNullOrWhiteSpace(text.Text) ||
                    text.Text.Length > 2048 ||
                    !double.IsFinite(text.X) ||
                    text.X is < 0 or > 1 ||
                    !double.IsFinite(text.Y) ||
                    text.Y is < 0 or > 1 ||
                    string.IsNullOrWhiteSpace(text.FontFamily) ||
                    text.FontFamily.Length > 128 ||
                    !double.IsFinite(text.FontSize) ||
                    text.FontSize is < 6 or > 512))
            {
                throw new InvalidDataException("A text overlay contains invalid content or styling.");
            }
            foreach (InkTextOverlay text in frame.TextOverlays)
            {
                InkTextEditing.ValidateStyles(text);
            }
        }
        InkLinkedCels.Validate(project.Frames);
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
                    point.BrushWidth is < 0.5 or > 64 ||
                    !Enum.IsDefined(point.BrushTexture)))
                {
            throw new InvalidDataException("The project contains invalid ink coordinates or brush settings.");
        }

        if (stroke.Count > 0 && (stroke.Any(point => point.IsFilled != stroke[0].IsFilled) ||
            (stroke[0].IsFilled && stroke.Count < 3)))
        {
            throw new InvalidDataException("A filled ink shape needs at least three points with a consistent fill setting.");
        }
    }
}
