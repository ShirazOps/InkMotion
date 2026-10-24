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

    public static void ValidateEmbeddedAudio(int sampleRate, int bitRate)
    {
        if (sampleRate is not (44100 or 48000))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "MP4 AAC supports 44100 or 48000 Hz.");
        }

        if (bitRate is not (128000 or 160000 or 192000))
        {
            throw new ArgumentOutOfRangeException(nameof(bitRate), "Choose 128, 160 or 192 kbps for MP4 AAC.");
        }
    }

    public static void ValidateOutputPath(EditorProject project, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string fullPath = Path.GetFullPath(outputPath);
        IEnumerable<string?> sources = project.AudioTracks.Select(track => track.AudioPath)
            .Concat(project.VideoTracks.SelectMany(track => track.Clips)
                .SelectMany(clip => new[] { clip.SourcePath, clip.ProxyPath }));
        if (sources.OfType<string>().Any(path => !string.IsNullOrWhiteSpace(path) &&
            string.Equals(Path.GetFullPath(path), fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Choose a different output file: export cannot overwrite a source used by this project.");
        }
    }

    public static string CodecName(VideoExportCodec codec) => codec switch
    {
        VideoExportCodec.H264 => "H.264",
        VideoExportCodec.Hevc => "HEVC (H.265)",
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    /// <summary>Average video bit rate: bits per pixel per frame, scaled by resolution and frame rate.</summary>
    public static int GetVideoBitRate(VideoExportProfile profile, VideoExportCodec codec, double quality = 0.5)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!double.IsFinite(quality) || quality < 0 || quality > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(quality));
        }

        double bitsPerPixel = codec == VideoExportCodec.Hevc ? 0.09 : 0.15;
        double bitRate = profile.Width * (double)profile.Height * profile.FrameRate * bitsPerPixel * Math.Pow(4, quality - 0.5);
        return (int)Math.Clamp(Math.Round(bitRate), 1_000_000, 240_000_000);
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
    public static long GetAudioFramesThrough(int frameIndex, double frameRate, int sampleRate = AudioSampleRate) =>
        (long)Math.Round((frameIndex + 1) * (double)sampleRate / frameRate);

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

    public static int GetInkFrameIndex(EditorProject project, int outputFrameIndex, double outputFrameRate)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!double.IsFinite(outputFrameRate) || outputFrameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputFrameRate));
        }

        int projectFrame = (int)Math.Floor(Math.Max(0, outputFrameIndex) * project.FrameRate / outputFrameRate + 1e-9);
        return GetInkFrameIndex(project, projectFrame);
    }

    /// <summary>
    /// Audio from video clips follows the clip's volume with the same fade ramps as its picture, and stops when
    /// the clip ends. Muted tracks contribute nothing.
    /// </summary>
    public static double GetVideoClipAudioGain(TimelineClipProject clip, double clipElapsedSeconds)
        => GetVideoClipAudioGain(clip, clipElapsedSeconds, default);

    public static double GetVideoClipAudioGain(
        TimelineClipProject clip,
        double clipElapsedSeconds,
        VideoAudioTransition transition)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Volume * GetVideoClipAudioEnvelope(clip, clipElapsedSeconds, transition);
    }

    public static double GetVideoClipAudioGain(
        TimelineClipProject clip,
        double clipElapsedSeconds,
        VideoAudioTransition transition,
        double frameRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!double.IsFinite(frameRate) || frameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        return clip.Volume * VideoClipVolumeKeyframes.GetValue(clip, clipElapsedSeconds * frameRate) *
            GetVideoClipAudioEnvelope(clip, clipElapsedSeconds, transition);
    }

    public static double GetVideoClipAudioEnvelope(
        TimelineClipProject clip,
        double clipElapsedSeconds,
        VideoAudioTransition transition)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        double remaining = duration - clipElapsedSeconds;
        double incomingHalf = transition.InSeconds / 2;
        double outgoingHalf = transition.OutSeconds / 2;
        if (duration <= 0 ||
            (transition.InSeconds <= 0 && clipElapsedSeconds < 0) ||
            (transition.OutSeconds <= 0 && remaining <= 0) ||
            clipElapsedSeconds < -incomingHalf || remaining < -outgoingHalf)
        {
            return 0;
        }

        double gain = 1;
        if (clip.FadeInSeconds > 0 && clipElapsedSeconds >= 0 && clipElapsedSeconds < clip.FadeInSeconds)
        {
            gain *= clipElapsedSeconds / clip.FadeInSeconds;
        }

        if (clip.FadeOutSeconds > 0 && remaining >= 0 && remaining < clip.FadeOutSeconds)
        {
            gain *= remaining / clip.FadeOutSeconds;
        }

        if (transition.InSeconds > 0 && clipElapsedSeconds < incomingHalf)
        {
            gain *= Math.Clamp((clipElapsedSeconds + incomingHalf) / transition.InSeconds, 0, 1);
        }

        if (transition.OutSeconds > 0 && clipElapsedSeconds > duration - outgoingHalf)
        {
            gain *= Math.Clamp((remaining + outgoingHalf) / transition.OutSeconds, 0, 1);
        }

        return Math.Max(0, gain);
    }
}
