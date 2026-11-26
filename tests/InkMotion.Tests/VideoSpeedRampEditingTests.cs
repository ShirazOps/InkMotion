using System.Text.Json;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoSpeedRampEditingTests
{
    private const double Fps = 24;
    private static readonly VideoSpeedRampPoint[] Accelerating = [new(0, 1), new(1, 3)];
    private static readonly VideoSpeedRampPoint[] Decelerating = [new(0, 2), new(1, 0.5)];

    [Theory]
    [InlineData(false, 12)]
    [InlineData(false, -12)]
    [InlineData(true, 12)]
    [InlineData(true, -12)]
    public void RippleTrimEnd_KeepsTheRetainedRampAndShiftsLaterClips(bool reversed, int frames)
    {
        TimelineTrackProject track = Track(
            Ramped(1, 20, 30, Accelerating, reversed),
            Constant(121, 40, 42, 1));
        TimelineClipProject clip = track.Clips[0];
        TimelineClipProject before = VideoTimelineEditing.Clone(clip);

        Assert.True(VideoEditTools.RippleTrimEnd(track, clip, frames, Fps));

        Assert.Equal(5 + (frames / Fps), Duration(clip), 9);
        Assert.Equal(121 + frames, track.Clips[1].StartFrame);
        AssertSameMap(before, clip, 0, 0.1, 1, 2.5, 4.4);
        double expectedEndSpeed = frames > 0
            ? VideoSpeedRamp.GetEndSpeed(before)
            : VideoSpeedRamp.GetSpeedAtElapsed(before, Duration(clip));
        Assert.Equal(expectedEndSpeed, VideoSpeedRamp.GetEndSpeed(clip), 9);
        AssertExportMapRoundTrips(clip);
        Assert.Null(VideoTimelineEditing.Validate([track], Fps));
    }

    [Theory]
    [InlineData(false, 12)]
    [InlineData(false, -12)]
    [InlineData(true, 12)]
    [InlineData(true, -12)]
    public void RippleTrimStart_KeepsPlacementAndShiftsTheContent(bool reversed, int frames)
    {
        TimelineTrackProject track = Track(
            Ramped(1, 20, 30, Accelerating, reversed),
            Constant(121, 40, 42, 1));
        TimelineClipProject clip = track.Clips[0];
        TimelineClipProject before = VideoTimelineEditing.Clone(clip);
        double shift = frames / Fps;

        Assert.True(VideoEditTools.RippleTrimStart(track, clip, frames, Fps));

        Assert.Equal(1, clip.StartFrame);
        Assert.Equal(5 - shift, Duration(clip), 9);
        Assert.Equal(121 - frames, track.Clips[1].StartFrame);
        foreach (double seconds in new[] { 0.6, 1, 2.5, 4.9 })
        {
            Assert.Equal(VideoTimelineEditing.GetSourceBoundarySeconds(before, seconds, Fps),
                VideoTimelineEditing.GetSourceBoundarySeconds(clip, seconds - shift, Fps), 9);
        }

        if (frames < 0)
        {
            // The added head plays at the original start speed of 1x.
            double edge = reversed ? 30.5 : 19.5;
            Assert.Equal(edge, VideoTimelineEditing.GetSourceBoundarySeconds(clip, 0, Fps), 9);
            Assert.Equal(1, VideoSpeedRamp.GetSpeedAtElapsed(clip, 0.25), 9);
        }

        Assert.Null(VideoTimelineEditing.Validate([track], Fps));
    }

    [Theory]
    [InlineData(false, 12)]
    [InlineData(false, -12)]
    [InlineData(true, 12)]
    [InlineData(true, -12)]
    public void RollEnd_MovesTheCutBetweenRampedClipsWithoutRetimingEither(bool reversed, int frames)
    {
        TimelineTrackProject track = Track(
            Ramped(1, 20, 30, Accelerating, reversed),
            Ramped(121, 40, 45, Decelerating, reversed));
        TimelineClipProject left = track.Clips[0];
        TimelineClipProject right = track.Clips[1];
        TimelineClipProject leftBefore = VideoTimelineEditing.Clone(left);
        TimelineClipProject rightBefore = VideoTimelineEditing.Clone(right);

        Assert.True(VideoEditTools.RollEnd(track, left, frames, Fps));

        Assert.Equal(121 + frames, right.StartFrame);
        Assert.Equal(9, VideoTimelineEditing.GetEndSeconds(right, TimelineTrackKind.Video, Fps), 9);
        AssertSameMap(leftBefore, left, 0.1, 2.5, 4.4);
        AssertSameMap(rightBefore, right, 5.6, 7, 8.9);
        Assert.Null(VideoTimelineEditing.Validate([track], Fps));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(-6)]
    public void Slide_RetimesNeitherRampedNeighbourAndKeepsTheMiddleRange(int frames)
    {
        TimelineTrackProject track = Track(
            Ramped(1, 20, 30, Accelerating, reversed: false),
            Constant(121, 50, 52, 1),
            Ramped(169, 40, 45, Decelerating, reversed: true));
        TimelineClipProject previous = track.Clips[0];
        TimelineClipProject middle = track.Clips[1];
        TimelineClipProject next = track.Clips[2];
        TimelineClipProject previousBefore = VideoTimelineEditing.Clone(previous);
        TimelineClipProject nextBefore = VideoTimelineEditing.Clone(next);

        Assert.True(VideoEditTools.Slide(track, middle, frames, Fps));

        Assert.Equal(121 + frames, middle.StartFrame);
        Assert.Equal(50, middle.SourceInSeconds);
        Assert.Equal(52, middle.SourceOutSeconds);
        Assert.Equal(11, VideoTimelineEditing.GetEndSeconds(next, TimelineTrackKind.Video, Fps), 9);
        AssertSameMap(previousBefore, previous, 1, 4.7);
        AssertSameMap(nextBefore, next, 7.3, 9, 10.9);
        Assert.Null(VideoTimelineEditing.Validate([track], Fps));
    }

    [Fact]
    public void Join_CombinesARampWithAConstantNeighbourAtTheSameSpeed()
    {
        TimelineTrackProject track = Track(
            Ramped(1, 0, 10, Accelerating, reversed: false),
            Constant(121, 10, 16, 3));
        TimelineClipProject left = track.Clips[0];
        TimelineClipProject right = track.Clips[1];
        left.FadeInSeconds = 0.5;
        right.FadeOutSeconds = 1;

        TimelineClipProject joined = VideoClipJoining.Join(track, left, right, Fps);

        Assert.Equal(7, Duration(joined), 9);
        Assert.Equal(16, joined.SourceOutSeconds!.Value, 9);
        Assert.Equal(2, joined.SpeedRamp.Count);
        foreach (double seconds in new[] { 0.5, 2.5, 4.9 })
        {
            Assert.Equal(VideoTimelineEditing.GetSourceBoundarySeconds(left, seconds, Fps),
                VideoTimelineEditing.GetSourceBoundarySeconds(joined, seconds, Fps), 9);
        }
        foreach (double seconds in new[] { 5.1, 6, 6.9 })
        {
            Assert.Equal(VideoTimelineEditing.GetSourceBoundarySeconds(right, seconds, Fps),
                VideoTimelineEditing.GetSourceBoundarySeconds(joined, seconds, Fps), 9);
        }
        Assert.Equal(0.5, joined.FadeInSeconds);
        Assert.Equal(1, joined.FadeOutSeconds);

        TimelineClipProject restored = JsonSerializer.Deserialize<TimelineClipProject>(JsonSerializer.Serialize(joined))!;
        Assert.Equal(joined.SpeedRamp, restored.SpeedRamp);
        Assert.Null(VideoTimelineEditing.Validate([Track(restored)], Fps));
    }

    [Fact]
    public void Join_RejectsASpeedJumpWithoutChangingEitherClip()
    {
        TimelineTrackProject track = Track(
            Ramped(1, 0, 10, Accelerating, reversed: false),
            Constant(121, 10, 14, 2));
        string before = JsonSerializer.Serialize(track);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => VideoClipJoining.Join(track, track.Clips[0], track.Clips[1], Fps));

        Assert.Contains("jumps from 3x to 2x", error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(track));
    }

    [Fact]
    public void Join_RestoresAFractionalRampedClipAfterSplitting()
    {
        TimelineTrackProject track = Track(Ramped(1, 0, 10.01, Accelerating, reversed: false));
        TimelineClipProject clip = track.Clips[0];
        TimelineClipProject original = VideoTimelineEditing.Clone(clip);
        Assert.Equal(5.005, Duration(clip), 9);

        TimelineClipProject right = VideoTimelineEditing.Split(track, clip, playheadFrame: 61, Fps)!;
        TimelineClipProject joined = VideoClipJoining.Join(track, clip, right, Fps);

        Assert.Equal(5.005, Duration(joined), 9);
        Assert.Equal(original.PlaybackSpeed, joined.PlaybackSpeed, 9);
        AssertSameMap(original, joined, 0.3, 2.5, 4, 5);
    }

    [Fact]
    public void FractionalRampedClip_RippleTrimKeepsItsFractionalLength()
    {
        TimelineTrackProject track = Track(Ramped(1, 0, 10.01, Accelerating, reversed: false, sourceDuration: 20));
        TimelineClipProject clip = track.Clips[0];
        TimelineClipProject before = VideoTimelineEditing.Clone(clip);

        Assert.True(VideoEditTools.RippleTrimEnd(track, clip, -12, Fps));
        Assert.Equal(4.505, Duration(clip), 9);
        Assert.True(VideoEditTools.RippleTrimEnd(track, clip, 12, Fps));

        Assert.Equal(5.005, Duration(clip), 9);
        AssertSameMap(before, clip, 1, 3, 4.5);
    }

    [Fact]
    public void RampedEdits_RejectMissingHandlesShortClipsAndFadesWithoutMutation()
    {
        TimelineTrackProject track = Track(
            Ramped(1, 0, 10, Accelerating, reversed: false, sourceDuration: 10),
            Ramped(121, 10, 15, Decelerating, reversed: false));
        TimelineClipProject left = track.Clips[0];
        left.FadeInSeconds = 2;
        left.FadeOutSeconds = 2.5;
        string before = JsonSerializer.Serialize(track);

        Assert.False(VideoEditTools.RippleTrimEnd(track, left, 1, Fps));
        Assert.False(VideoEditTools.RippleTrimStart(track, left, -1, Fps));
        Assert.False(VideoEditTools.RippleTrimStart(track, left, 120, Fps));
        Assert.False(VideoEditTools.RippleTrimEnd(track, left, -24, Fps));
        Assert.False(VideoEditTools.RollEnd(track, left, -24, Fps));
        Assert.False(VideoEditTools.RollEnd(track, left, 1, Fps));
        Assert.Equal(before, JsonSerializer.Serialize(track));

        // The left clip can lengthen here, but the right clip would fall below one frame.
        TimelineTrackProject roll = Track(
            Ramped(1, 0, 10, Accelerating, reversed: false),
            Ramped(121, 10, 15, Decelerating, reversed: true));
        string rollBefore = JsonSerializer.Serialize(roll);
        Assert.False(VideoEditTools.RollEnd(roll, roll.Clips[0], 96, Fps));
        Assert.Equal(rollBefore, JsonSerializer.Serialize(roll));
    }

    [Fact]
    public void RepeatedLengthening_DoesNotAccumulateRampPoints()
    {
        TimelineTrackProject track = Track(Ramped(1, 0, 10, Accelerating, reversed: false, sourceDuration: 400));
        TimelineClipProject clip = track.Clips[0];

        for (int index = 0; index < VideoSpeedRamp.MaximumPoints + 10; index++)
        {
            Assert.True(VideoEditTools.RippleTrimEnd(track, clip, 1, Fps));
        }

        Assert.InRange(clip.SpeedRamp.Count, 2, 3);
        Assert.Equal(3, VideoSpeedRamp.GetEndSpeed(clip), 9);
        Assert.Equal(5 + ((VideoSpeedRamp.MaximumPoints + 10) / Fps), Duration(clip), 9);
    }

    [Fact]
    public void ConstantSpeedNeighbours_KeepTheirExistingArithmetic()
    {
        TimelineTrackProject track = Track(
            Constant(1, 0, 4, 2),
            Constant(49, 10, 12, 1),
            Constant(97, 20, 21, 0.5));

        Assert.True(VideoEditTools.Slide(track, track.Clips[1], 6, Fps));
        Assert.True(VideoEditTools.RollEnd(track, track.Clips[1], -3, Fps));
        Assert.True(VideoEditTools.RippleTrimEnd(track, track.Clips[2], 4, Fps));

        Assert.Equal(4 + (6 / Fps * 2), track.Clips[0].SourceOutSeconds);
        Assert.Equal(12 - (3 / Fps), track.Clips[1].SourceOutSeconds);
        Assert.Equal(20 + (6 / Fps * 0.5) - (3 / Fps * 0.5), track.Clips[2].SourceInSeconds, 12);
        Assert.All(track.Clips, clip => Assert.Empty(clip.SpeedRamp));
    }

    private static void AssertSameMap(TimelineClipProject before, TimelineClipProject after, params double[] seconds)
    {
        foreach (double time in seconds)
        {
            Assert.Equal(VideoTimelineEditing.GetSourceBoundarySeconds(before, time, Fps),
                VideoTimelineEditing.GetSourceBoundarySeconds(after, time, Fps), 9);
        }
    }

    private static void AssertExportMapRoundTrips(TimelineClipProject clip)
    {
        for (double time = 0; time < Duration(clip); time += 0.37)
        {
            double source = VideoTimelineEditing.GetSourceBoundarySeconds(clip, time, Fps);
            Assert.Equal(time, VideoTimelineEditing.GetTimelineSecondsForSource(clip, source, Fps), 9);
        }
    }

    private static double Duration(TimelineClipProject clip) =>
        VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);

    private static TimelineTrackProject Track(params TimelineClipProject[] clips) =>
        new() { Kind = TimelineTrackKind.Video, Name = "Video 1", Clips = clips.ToList() };

    private static TimelineClipProject Constant(int startFrame, double sourceIn, double sourceOut, double speed) => new()
    {
        SourcePath = "a.mp4",
        StartFrame = startFrame,
        SourceInSeconds = sourceIn,
        SourceOutSeconds = sourceOut,
        SourceDurationSeconds = 60,
        PlaybackSpeed = speed,
    };

    private static TimelineClipProject Ramped(int startFrame, double sourceIn, double sourceOut,
        VideoSpeedRampPoint[] ramp, bool reversed, double sourceDuration = 60)
    {
        TimelineClipProject clip = Constant(startFrame, sourceIn, sourceOut, 1);
        clip.SourceDurationSeconds = sourceDuration;
        clip.IsReversed = reversed;
        Assert.True(VideoSpeedRamp.Apply(clip, ramp));
        return clip;
    }
}
