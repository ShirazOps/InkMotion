namespace InkMotion;

public sealed record AudioClipGainSettings(
    double SourceInSeconds,
    double? SourceOutSeconds,
    double FadeInSeconds,
    double FadeOutSeconds,
    double LinearGain)
{
    public double GetGain(double sourceSeconds, double sourceDurationSeconds)
    {
        double clipEnd = AudioClipEditing.GetClipEnd(SourceInSeconds, SourceOutSeconds, sourceDurationSeconds);
        if (sourceSeconds < SourceInSeconds || sourceSeconds >= clipEnd)
        {
            return 0;
        }

        double gain = LinearGain;
        double elapsed = sourceSeconds - SourceInSeconds;
        if (FadeInSeconds > 0 && elapsed < FadeInSeconds)
        {
            gain *= elapsed / FadeInSeconds;
        }

        double remaining = clipEnd - sourceSeconds;
        if (FadeOutSeconds > 0 && remaining < FadeOutSeconds)
        {
            gain *= remaining / FadeOutSeconds;
        }

        return gain;
    }
}

public static class AudioClipEditing
{
    public static bool SetSourceSampleRange(AudioTrackProject track, long firstSampleFrame, long endSampleFrame,
        long sourceSampleFrames, int sampleRate)
    {
        if (!HasValidClipSettings(track) || sampleRate <= 0 || sourceSampleFrames <= 0 ||
            sourceSampleFrames > 9007199254740991L ||
            firstSampleFrame < 0 || endSampleFrame <= firstSampleFrame || endSampleFrame > sourceSampleFrames)
        {
            return false;
        }

        double sourceIn = (double)firstSampleFrame / sampleRate;
        double sourceOut = (double)endSampleFrame / sampleRate;
        double timelineDuration = (double)(endSampleFrame - firstSampleFrame) / sampleRate / GetTempoRatio(track);
        if (sourceOut <= sourceIn || timelineDuration < track.FadeInSeconds + track.FadeOutSeconds ||
            (track.SourceInSeconds == sourceIn &&
                (track.SourceOutSeconds ?? (double)sourceSampleFrames / sampleRate) == sourceOut))
        {
            return false;
        }

        track.SourceInSeconds = sourceIn;
        track.SourceOutSeconds = sourceOut;
        return true;
    }

    public const double MinimumGainDecibels = -60;
    public const double MaximumGainDecibels = 24;
    public const double MaximumFadeSeconds = 600;
    public const double NormalisationTargetDecibels = -1;
    public const double MinimumTempoPercent = 50;
    public const double MaximumTempoPercent = 200;
    public const double MinimumPitchSemitones = -12;
    public const double MaximumPitchSemitones = 12;

    public static bool HasValidClipSettings(AudioTrackProject track) =>
        track.Effects is { IsValid: true } &&
        double.IsFinite(track.Balance) && track.Balance is >= -1 and <= 1 &&
        double.IsFinite(track.SpatialPanAzimuthDegrees) && track.SpatialPanAzimuthDegrees is >= -180 and <= 180 &&
        double.IsFinite(track.SourceInSeconds) &&
        track.SourceInSeconds >= 0 &&
        (track.SourceOutSeconds is null ||
            (double.IsFinite(track.SourceOutSeconds.Value) && track.SourceOutSeconds.Value > track.SourceInSeconds)) &&
        IsValidFade(track.FadeInSeconds) &&
        IsValidFade(track.FadeOutSeconds) &&
        double.IsFinite(track.GainDecibels) &&
        track.GainDecibels is >= MinimumGainDecibels and <= MaximumGainDecibels &&
        double.IsFinite(track.TempoPercent) &&
        track.TempoPercent is >= MinimumTempoPercent and <= MaximumTempoPercent &&
        double.IsFinite(track.PitchSemitones) &&
        track.PitchSemitones is >= MinimumPitchSemitones and <= MaximumPitchSemitones;

    public static double GetClipEnd(double sourceInSeconds, double? sourceOutSeconds, double sourceDurationSeconds)
    {
        double end = sourceOutSeconds ?? double.PositiveInfinity;
        if (sourceDurationSeconds > 0)
        {
            end = Math.Min(end, sourceDurationSeconds);
        }

        return Math.Max(sourceInSeconds, end);
    }

    public static double GetClipDuration(AudioTrackProject track, double sourceDurationSeconds) =>
        GetClipEnd(track.SourceInSeconds, track.SourceOutSeconds, sourceDurationSeconds) - track.SourceInSeconds;

    public static double GetTempoRatio(AudioTrackProject track) => track.TempoPercent / 100;

    public static double GetTimelineDuration(AudioTrackProject track, double sourceDurationSeconds) =>
        GetClipDuration(track, sourceDurationSeconds) / GetTempoRatio(track);

    public static double TimelineToSourceSeconds(AudioTrackProject track, double clipOffsetSeconds) =>
        track.SourceInSeconds + clipOffsetSeconds * GetTempoRatio(track);

    public static double GetClipStartSeconds(AudioTrackProject track, double frameRate) =>
        (track.StartFrame - 1) / frameRate;

    public static double DecibelsToLinear(double decibels) => Math.Pow(10, decibels / 20);

    public static AudioClipGainSettings CreateGainSettings(AudioTrackProject track) => new(
        track.SourceInSeconds,
        track.SourceOutSeconds,
        track.FadeInSeconds * GetTempoRatio(track),
        track.FadeOutSeconds * GetTempoRatio(track),
        track.Muted ? 0 : track.Volume * DecibelsToLinear(track.GainDecibels));

    public static double GetGain(AudioTrackProject track, double sourceSeconds, double sourceDurationSeconds) =>
        CreateGainSettings(track).GetGain(sourceSeconds, sourceDurationSeconds);

    public static bool TryGetClipOffset(
        AudioTrackProject track,
        int playheadFrameNumber,
        double frameRate,
        double sourceDurationSeconds,
        out double offsetSeconds)
    {
        offsetSeconds = (playheadFrameNumber - track.StartFrame) / frameRate;
        double duration = GetTimelineDuration(track, sourceDurationSeconds);
        return offsetSeconds > 0 && offsetSeconds < duration;
    }

    public static bool TryTrimStart(AudioTrackProject track, int playheadFrameNumber, double frameRate, double sourceDurationSeconds)
    {
        if (!TryGetClipOffset(track, playheadFrameNumber, frameRate, sourceDurationSeconds, out double offset))
        {
            return false;
        }

        double clipEnd = GetClipEnd(track.SourceInSeconds, track.SourceOutSeconds, sourceDurationSeconds);
        track.SourceInSeconds = TimelineToSourceSeconds(track, offset);
        track.SourceOutSeconds ??= double.IsFinite(clipEnd) ? clipEnd : null;
        track.StartFrame = playheadFrameNumber;
        ClampFades(track, sourceDurationSeconds);
        return true;
    }

    public static bool TryTrimEnd(AudioTrackProject track, int playheadFrameNumber, double frameRate, double sourceDurationSeconds)
    {
        if (!TryGetClipOffset(track, playheadFrameNumber, frameRate, sourceDurationSeconds, out double offset))
        {
            return false;
        }

        track.SourceOutSeconds = TimelineToSourceSeconds(track, offset);
        ClampFades(track, sourceDurationSeconds);
        return true;
    }

    public static AudioTrackProject? Split(AudioTrackProject track, int playheadFrameNumber, double frameRate, double sourceDurationSeconds)
    {
        if (!TryGetClipOffset(track, playheadFrameNumber, frameRate, sourceDurationSeconds, out double offset))
        {
            return null;
        }

        double clipEnd = GetClipEnd(track.SourceInSeconds, track.SourceOutSeconds, sourceDurationSeconds);
        double splitPoint = TimelineToSourceSeconds(track, offset);
        AudioTrackProject right = Clone(track);
        right.StartFrame = playheadFrameNumber;
        right.SourceInSeconds = splitPoint;
        right.SourceOutSeconds = track.SourceOutSeconds ?? (double.IsFinite(clipEnd) ? clipEnd : null);
        right.FadeInSeconds = 0;

        track.SourceOutSeconds = splitPoint;
        track.FadeOutSeconds = 0;
        ClampFades(track, sourceDurationSeconds);
        ClampFades(right, sourceDurationSeconds);
        return right;
    }

    public static double CalculateNormalisationGain(float peak, double targetDecibels = NormalisationTargetDecibels)
    {
        if (!float.IsFinite(peak) || peak <= 0)
        {
            return 0;
        }

        double gain = targetDecibels - 20 * Math.Log10(Math.Min(peak, 1));
        return Math.Clamp(gain, MinimumGainDecibels, MaximumGainDecibels);
    }

    public static void ClampFades(AudioTrackProject track, double sourceDurationSeconds)
    {
        track.FadeInSeconds = Math.Clamp(track.FadeInSeconds, 0, MaximumFadeSeconds);
        track.FadeOutSeconds = Math.Clamp(track.FadeOutSeconds, 0, MaximumFadeSeconds);
        double duration = GetTimelineDuration(track, sourceDurationSeconds);
        double totalFade = track.FadeInSeconds + track.FadeOutSeconds;
        if (!double.IsFinite(duration) || totalFade <= duration)
        {
            return;
        }

        double scale = duration / totalFade;
        track.FadeInSeconds *= scale;
        track.FadeOutSeconds *= scale;
    }

    public static AudioTrackProject Clone(AudioTrackProject track) => new()
    {
        AudioPath = track.AudioPath,
        StartFrame = track.StartFrame,
        Volume = track.Volume,
        Muted = track.Muted,
        Solo = track.Solo,
        Balance = track.Balance,
        SpatialPanEnabled = track.SpatialPanEnabled,
        SpatialPanAzimuthDegrees = track.SpatialPanAzimuthDegrees,
        Effects = track.Effects with { },
        SourceInSeconds = track.SourceInSeconds,
        SourceOutSeconds = track.SourceOutSeconds,
        FadeInSeconds = track.FadeInSeconds,
        FadeOutSeconds = track.FadeOutSeconds,
        GainDecibels = track.GainDecibels,
        TempoPercent = track.TempoPercent,
        PitchSemitones = track.PitchSemitones,
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        Genre = track.Genre,
        Comments = track.Comments,
        Year = track.Year,
        TagsInitialised = track.TagsInitialised,
    };

    private static bool IsValidFade(double seconds) =>
        double.IsFinite(seconds) && seconds is >= 0 and <= MaximumFadeSeconds;
}
