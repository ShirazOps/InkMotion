namespace InkMotion;

public static class AudioClipJoining
{
    public static AudioTrackProject Join(AudioTrackProject left, AudioTrackProject right, double frameRate, double sourceDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (!double.IsFinite(frameRate) || frameRate <= 0 || !double.IsFinite(sourceDurationSeconds) ||
            sourceDurationSeconds <= 0 || !AudioClipEditing.HasValidClipSettings(left) ||
            !AudioClipEditing.HasValidClipSettings(right) || left.StartFrame < 1 || right.StartFrame < 1)
        {
            throw new ArgumentException("Join requires valid audio clips, source duration and frame rate.");
        }

        if (string.IsNullOrWhiteSpace(left.AudioPath) || string.IsNullOrWhiteSpace(right.AudioPath) ||
            !string.Equals(Path.GetFullPath(left.AudioPath), Path.GetFullPath(right.AudioPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only consecutive portions of the same audio source can be joined without rendering.");
        }

        double leftEnd = AudioClipEditing.GetClipEnd(left.SourceInSeconds, left.SourceOutSeconds, sourceDurationSeconds);
        double rightEnd = AudioClipEditing.GetClipEnd(right.SourceInSeconds, right.SourceOutSeconds, sourceDurationSeconds);
        double timelineEnd = AudioClipEditing.GetClipStartSeconds(left, frameRate) +
            AudioClipEditing.GetTimelineDuration(left, sourceDurationSeconds);
        if (leftEnd <= left.SourceInSeconds || rightEnd <= right.SourceInSeconds ||
            Math.Abs(leftEnd - right.SourceInSeconds) > 1e-7 ||
            Math.Abs(timelineEnd - AudioClipEditing.GetClipStartSeconds(right, frameRate)) > 1e-7)
        {
            throw new InvalidOperationException("Clips must touch in timeline and source order, without a gap, overlap or repeated source section.");
        }

        if (left.FadeOutSeconds != 0 || right.FadeInSeconds != 0)
        {
            throw new InvalidOperationException("Remove the fades at the join boundary first; joining must not silently discard them.");
        }

        if (left.Volume != right.Volume || left.GainDecibels != right.GainDecibels ||
            left.Muted != right.Muted || left.Solo != right.Solo || left.Balance != right.Balance ||
            left.TempoPercent != right.TempoPercent || left.PitchSemitones != right.PitchSemitones ||
            left.Effects != right.Effects)
        {
            throw new InvalidOperationException("Use matching volume, gain, mute/solo, balance, tempo, pitch and effect settings before joining.");
        }

        if (left.Title != right.Title || left.Artist != right.Artist || left.Album != right.Album ||
            left.Genre != right.Genre || left.Comments != right.Comments || left.Year != right.Year ||
            left.TagsInitialised != right.TagsInitialised)
        {
            throw new InvalidOperationException("Clip tags differ. Match them before joining so no metadata is discarded.");
        }

        AudioTrackProject joined = AudioClipEditing.Clone(left);
        joined.SourceOutSeconds = rightEnd;
        joined.FadeOutSeconds = right.FadeOutSeconds;
        return joined;
    }
}
