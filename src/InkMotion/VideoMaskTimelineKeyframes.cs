namespace InkMotion;

public static class VideoMaskTimelineKeyframes
{
    public static IEnumerable<int> GetFrameOffsets(TimelineClipProject clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.MaskKeyframes.Select(key => key.FrameOffset)
            .Concat(clip.MaskShapeKeyframes.Select(key => key.FrameOffset))
            .Concat(clip.MaskInversionKeyframes.Select(key => key.FrameOffset))
            .Concat(clip.MaskFeatherKeyframes.Select(key => key.FrameOffset))
            .Distinct()
            .OrderBy(frameOffset => frameOffset);
    }

    public static int? FindNearestFrame(TimelineClipProject clip, int frameOffset, int maximumDistance)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (maximumDistance < 0)
        {
            return null;
        }

        return GetFrameOffsets(clip)
            .Where(keyFrame => Math.Abs((long)keyFrame - frameOffset) <= maximumDistance)
            .OrderBy(keyFrame => Math.Abs((long)keyFrame - frameOffset))
            .ThenBy(keyFrame => keyFrame)
            .Select(keyFrame => (int?)keyFrame)
            .FirstOrDefault();
    }

    public static int? FindAdjacentFrame(TimelineClipProject clip, int frameOffset, bool next)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return next
            ? GetFrameOffsets(clip).Where(keyFrame => keyFrame > frameOffset).Select(keyFrame => (int?)keyFrame).FirstOrDefault()
            : GetFrameOffsets(clip).Where(keyFrame => keyFrame < frameOffset).Select(keyFrame => (int?)keyFrame).LastOrDefault();
    }
}
