namespace InkMotion;

public sealed record AudioRangeParts(AudioTrackProject? Before, AudioTrackProject Range, AudioTrackProject? After);

public static class AudioRangeEditing
{
    public static AudioRangeParts Partition(
        AudioTrackProject source, int startFrame, int endFrame, double frameRate, double sourceDuration)
    {
        ArgumentNullException.ThrowIfNull(source);
        double duration = AudioClipEditing.GetTimelineDuration(source, sourceDuration);
        double end = source.StartFrame + duration * frameRate;
        if (!double.IsFinite(frameRate) || frameRate <= 0 || !double.IsFinite(duration) || duration <= 0 ||
            startFrame < source.StartFrame || endFrame <= startFrame ||
            endFrame > Math.Ceiling(end - 1e-8) || startFrame >= end - 1e-8)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrame), "Select a non-empty range within the audio clip.");
        }

        AudioTrackProject range = AudioClipEditing.Clone(source);
        AudioTrackProject? before = null;
        if (startFrame > source.StartFrame)
        {
            before = range;
            range = AudioClipEditing.Split(before, startFrame, frameRate, sourceDuration)
                ?? throw new InvalidOperationException("The selection start could not split the audio clip.");
        }

        AudioTrackProject? after = null;
        // The final partial timeline frame includes the exact source endpoint.
        if (endFrame < end - 1e-8)
        {
            after = AudioClipEditing.Split(range, endFrame, frameRate, sourceDuration)
                ?? throw new InvalidOperationException("The selection end could not split the audio clip.");
        }

        return new AudioRangeParts(before, range, after);
    }
}
