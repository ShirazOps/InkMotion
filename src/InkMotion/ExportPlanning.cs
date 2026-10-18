namespace InkMotion;

public enum VideoExportCodec
{
    H264,
    Hevc,
}

/// <summary>Pure export decisions shared by the encoder and the UI, kept free of Win2D and Media Foundation.</summary>
public static class ExportPlanning
{
    public const int AudioSampleRate = 48000;
    public const string BaseInkLayerId = "ink-1";
    private const long TicksPerSecond = 10_000_000;

    public static string CodecName(VideoExportCodec codec) => codec switch
    {
        VideoExportCodec.H264 => "H.264",
        VideoExportCodec.Hevc => "HEVC (H.265)",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    /// <summary>Average video bit rate: bits per pixel per frame, scaled by resolution and frame rate.</summary>
    public static int GetVideoBitRate(VideoExportProfile profile, VideoExportCodec codec)
    {
        ArgumentNullException.ThrowIfNull(profile);
        double bitsPerPixel = codec == VideoExportCodec.Hevc ? 0.09 : 0.15;
        double bitRate = profile.Width * (double)profile.Height * profile.FrameRate * bitsPerPixel;
        return (int)Math.Clamp(Math.Round(bitRate), 2_000_000, 120_000_000);
    }

    /// <summary>
    /// MP4 audio uses AAC, which carries mono, stereo or 5.1. Other layouts are folded down to 5.1 and the
    /// full mix is preserved in a multichannel WAV stem alongside the video.
    /// </summary>
    public static AudioChannelLayout GetEncodedAudioLayout(AudioChannelLayout mixLayout) => mixLayout switch
    {
        AudioChannelLayout.Mono => AudioChannelLayout.Mono,
        AudioChannelLayout.Stereo => AudioChannelLayout.Stereo,
        _ => AudioChannelLayout.Surround51,
    };

    public static bool NeedsSurroundStem(AudioChannelLayout mixLayout) => GetEncodedAudioLayout(mixLayout) != mixLayout;

    public static string GetSurroundStemPath(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(outputPath)} (surround mix).wav");
    }

    public static int GetFrameCount(double durationSeconds, double frameRate)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 || frameRate <= 0)
        {
            return 0;
        }

        return (int)Math.Ceiling((durationSeconds * frameRate) - 1e-6);
    }

    public static long GetFrameTime(int frameIndex, double frameRate) =>
        (long)Math.Round(frameIndex * TicksPerSecond / frameRate);

    /// <summary>Number of audio sample frames that must be written by the end of the given video frame.</summary>
    public static long GetAudioFramesThrough(int frameIndex, double frameRate) =>
        (long)Math.Round((frameIndex + 1) * (double)AudioSampleRate / frameRate);

    /// <summary>Frame rate as an exact rational, so 24, 30 and 60 fps encode without drift.</summary>
    public static (int Numerator, int Denominator) GetFrameRateRatio(double frameRate)
    {
        if (!double.IsFinite(frameRate) || frameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        double rounded = Math.Round(frameRate);
        return Math.Abs(rounded - frameRate) < 1e-9
            ? ((int)rounded, 1)
            : ((int)Math.Round(frameRate * 1000), 1000);
    }

    public static List<List<InkPoint>> GetLayerStrokes(EditorFrame frame, string layerId)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (layerId == BaseInkLayerId)
        {
            return frame.Strokes;
        }

        return frame.LayerStrokes.TryGetValue(layerId, out List<List<InkPoint>>? strokes) ? strokes : [];
    }

    public static bool HasInk(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Frames.Any(frame =>
            frame.TextOverlays.Any(overlay => !string.IsNullOrWhiteSpace(overlay.Text)) ||
            project.InkLayers.Any(layer => layer.IsVisible && GetLayerStrokes(frame, layer.Id).Any(stroke => stroke.Count > 0)));
    }

    public static double GetInkDurationSeconds(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return HasInk(project) ? InkTimeline.GetTotalExposureFrames(project.Frames) / project.FrameRate : 0;
    }

    /// <summary>Ink drawing shown at an output frame; the last drawing holds once the animation ends, as in preview.</summary>
    public static int GetInkFrameIndex(EditorProject project, int outputFrameIndex)
    {
        ArgumentNullException.ThrowIfNull(project);
        return InkTimeline.GetFrameIndexAtPosition(project.Frames, Math.Max(0, outputFrameIndex));
    }

    /// <summary>
    /// Audio from video clips follows the clip's volume with the same fade ramps as its picture, and stops when
    /// the clip ends. Muted tracks contribute nothing.
    /// </summary>
    public static double GetVideoClipAudioGain(TimelineClipProject clip, double clipElapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        double remaining = duration - clipElapsedSeconds;
        if (clipElapsedSeconds < 0 || remaining <= 0)
        {
            return 0;
        }

        double gain = clip.Volume;
        if (clip.FadeInSeconds > 0 && clipElapsedSeconds < clip.FadeInSeconds)
        {
            gain *= clipElapsedSeconds / clip.FadeInSeconds;
        }

        if (clip.FadeOutSeconds > 0 && remaining < clip.FadeOutSeconds)
        {
            gain *= remaining / clip.FadeOutSeconds;
        }

        return Math.Max(0, gain);
    }
}
