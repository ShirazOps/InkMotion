namespace InkMotion;

public static class VideoSourceRangeMarks
{
    public static bool TryCreateClip(
        string sourcePath, double sourceDurationSeconds, int timelineStartFrame, double frameRate,
        double sourceInSeconds, double sourceOutSeconds, out TimelineClipProject? clip)
    {
        clip = null;
        if (string.IsNullOrWhiteSpace(sourcePath) || !double.IsFinite(sourceDurationSeconds) ||
            sourceDurationSeconds <= 0 || timelineStartFrame < 1 || !double.IsFinite(frameRate) || frameRate <= 0 ||
            !double.IsFinite(sourceInSeconds) || !double.IsFinite(sourceOutSeconds) ||
            sourceInSeconds < 0 || sourceOutSeconds > sourceDurationSeconds ||
            sourceOutSeconds - sourceInSeconds < 1d / frameRate - 1e-8)
        {
            return false;
        }

        clip = new TimelineClipProject
        {
            SourcePath = sourcePath,
            SourceDurationSeconds = sourceDurationSeconds,
            SourceInSeconds = sourceInSeconds,
            SourceOutSeconds = sourceOutSeconds,
            StartFrame = timelineStartFrame,
        };
        return true;
    }
}
