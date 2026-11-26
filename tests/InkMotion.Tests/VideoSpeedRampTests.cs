using System.Text.Json;
using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoSpeedRampTests
{
    private const double Fps = 24;

    [Fact]
    public void SetSpeedRamp_MapsTimelineToSourceThroughTheIntegratedSpeed()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = RampedTrack();

        Assert.Equal(2, clip.PlaybackSpeed, 9);
        Assert.Equal(5, VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video), 9);
        Assert.Equal(1.2, VideoTimelineEditing.GetSourceBoundarySeconds(clip, 1, Fps), 9);
        Assert.Equal(3.75, VideoTimelineEditing.GetSourceBoundarySeconds(clip, 2.5, Fps), 9);
        Assert.Equal(10, VideoTimelineEditing.GetSourceBoundarySeconds(clip, 5, Fps), 9);
        Assert.Equal(2.5, VideoTimelineEditing.GetTimelineSecondsForSource(clip, 3.75, Fps), 9);
        Assert.Equal(2, VideoSpeedRamp.GetSpeedAtElapsed(clip, 2.5), 9);
        Assert.Null(VideoTimelineEditing.Validate([track], Fps));
    }

    [Fact]
    public void ReversedRamp_PlaysTheSourceBackwardsFromItsOutPoint()
    {
        (_, TimelineClipProject clip) = RampedTrack();
        clip.IsReversed = true;

        Assert.Equal(6.25, VideoTimelineEditing.GetSourceBoundarySeconds(clip, 2.5, Fps), 9);
        Assert.Equal(
            VideoTimelineEditing.GetSourceBoundarySeconds(clip, 2.5 + (1 / Fps), Fps),
            VideoTimelineEditing.GetSourceSeconds(clip, 2.5, Fps),
            9);
    }

    [Fact]
    public void Split_KeepsEverySourceFrameAtItsOriginalTimelineTime()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = RampedTrack();
        double before = VideoTimelineEditing.GetSourceBoundarySeconds(clip, 4, Fps);

        TimelineClipProject? right = VideoTimelineEditing.Split(track, clip, playheadFrame: 61, Fps);

        Assert.NotNull(right);
        Assert.Equal(3.75, clip.SourceOutSeconds!.Value, 9);
        Assert.Equal(3.75, right.SourceInSeconds, 9);
        Assert.Equal(2.5, VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video), 9);
        Assert.Equal(2.5, VideoTimelineEditing.GetDurationSeconds(right, TimelineTrackKind.Video), 9);
        Assert.Equal(1.2, VideoTimelineEditing.GetSourceBoundarySeconds(clip, 1, Fps), 9);
        Assert.Equal(before, VideoTimelineEditing.GetSourceBoundarySeconds(right, 4, Fps), 9);
        Assert.Null(VideoTimelineEditing.Validate([track], Fps));
    }

    [Fact]
    public void Split_ReversedRamp_PreservesSourceTimingAndCannotBeJoined()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = RampedTrack();
        clip.IsReversed = true;
        double before = VideoTimelineEditing.GetSourceBoundarySeconds(clip, 4, Fps);

        TimelineClipProject? right = VideoTimelineEditing.Split(track, clip, playheadFrame: 61, Fps);

        Assert.NotNull(right);
        Assert.Equal(6.25, clip.SourceInSeconds, 9);
        Assert.Equal(6.25, right.SourceOutSeconds!.Value, 9);
        Assert.Equal(2.5, VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video), 9);
        Assert.Equal(2.5, VideoTimelineEditing.GetDurationSeconds(right, TimelineTrackKind.Video), 9);
        Assert.Equal(before, VideoTimelineEditing.GetSourceBoundarySeconds(right, 4, Fps), 9);
        Assert.Throws<InvalidOperationException>(() => VideoClipJoining.Join(track, clip, right, Fps));
        Assert.Null(VideoTimelineEditing.Validate([track], Fps));
    }

    [Fact]
    public void TrimStartAndEnd_KeepTheRemainingRampTiming()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = RampedTrack();
        double atFour = VideoTimelineEditing.GetSourceBoundarySeconds(clip, 4, Fps);
        double atOne = VideoTimelineEditing.GetSourceBoundarySeconds(clip, 1, Fps);

        Assert.True(VideoTimelineEditing.TryTrimStart(track, clip, playheadFrame: 61, Fps));
        Assert.Equal(atFour, VideoTimelineEditing.GetSourceBoundarySeconds(clip, 4, Fps), 9);
        Assert.True(VideoTimelineEditing.TryTrimEnd(track, clip, playheadFrame: 109, Fps));

        Assert.Equal(atFour, VideoTimelineEditing.GetSourceBoundarySeconds(clip, 4, Fps), 9);
        Assert.Equal(2, VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video), 9);
        Assert.Null(VideoTimelineEditing.Validate([track], Fps));

        (track, clip) = RampedTrack();
        Assert.True(VideoTimelineEditing.TryTrimEnd(track, clip, playheadFrame: 61, Fps));
        Assert.Equal(atOne, VideoTimelineEditing.GetSourceBoundarySeconds(clip, 1, Fps), 9);
    }

    [Fact]
    public void LengtheningTheEnd_HoldsTheLastSpeedAndKeepsEarlierTiming()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = RampedTrack(sourceDuration: 20);

        Assert.True(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.End, edgeFrame: 145, Fps));

        Assert.Equal(13, clip.SourceOutSeconds!.Value, 9);
        Assert.Equal(6, VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video), 9);
        Assert.Equal(3.75, VideoTimelineEditing.GetSourceBoundarySeconds(clip, 2.5, Fps), 9);
        Assert.Equal(3, VideoSpeedRamp.GetSpeedAtElapsed(clip, 5.5), 9);
        Assert.Null(VideoTimelineEditing.Validate([track], Fps));
    }

    [Fact]
    public void PlaybackSpeed_RescalesTheRampOrRejectsSpeedsOutsideTheRange()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = RampedTrack();

        Assert.False(VideoTimelineEditing.TrySetPlaybackSpeed(track, clip, 3, Fps));
        Assert.Equal(2, clip.PlaybackSpeed, 9);
        Assert.True(VideoTimelineEditing.TrySetPlaybackSpeed(track, clip, 1, Fps));

        Assert.Equal([0.5, 1.5], clip.SpeedRamp.Select(point => Math.Round(point.Speed, 9)));
        Assert.Equal(10, VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video), 9);
    }

    [Fact]
    public void ClearingTheRampKeepsItsAverageAsAConstantSpeed()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = RampedTrack();

        Assert.True(VideoTimelineEditing.TrySetSpeedRamp(track, clip, [], Fps));

        Assert.False(VideoSpeedRamp.IsRamped(clip));
        Assert.Equal(2, clip.PlaybackSpeed, 9);
    }

    [Fact]
    public void ConstantSpeedNeighbourEdits_RefuseRampedClips()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = RampedTrack(sourceDuration: 20);

        Assert.False(VideoEditTools.RippleTrimEnd(track, clip, 12, Fps));
    }

    [Fact]
    public void Validation_RejectsAnAverageThatDoesNotMatchTheRamp()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = RampedTrack();
        clip.PlaybackSpeed = 1.5;

        Assert.NotNull(VideoTimelineEditing.Validate([track], Fps));
    }

    [Fact]
    public void RampSurvivesCloningAndJsonRoundTrip()
    {
        (_, TimelineClipProject clip) = RampedTrack();

        TimelineClipProject clone = VideoTimelineEditing.Clone(clip);
        TimelineClipProject restored = JsonSerializer.Deserialize<TimelineClipProject>(JsonSerializer.Serialize(clip))!;

        Assert.Equal(clip.SpeedRamp, clone.SpeedRamp);
        Assert.NotSame(clip.SpeedRamp, clone.SpeedRamp);
        Assert.Equal(clip.SpeedRamp, restored.SpeedRamp);
    }

    [Fact]
    public void VariableSpeedProvider_FollowsTheTimeMap()
    {
        const int rate = 1000;
        float[] samples = Enumerable.Range(0, 2000).Select(index => (float)index).ToArray();
        var provider = new VariableSpeedSampleProvider(
            new MemorySampleProvider(samples, rate),
            seconds => (0.5 * seconds) + (0.25 * seconds * seconds));
        var output = new List<float>();
        float[] buffer = new float[256];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.AddRange(buffer[..read]);
        }

        Assert.InRange(output.Count, 1995, 2002);
        for (int index = 0; index < 1500; index += 50)
        {
            double seconds = index / (double)rate;
            double expected = ((0.5 * seconds) + (0.25 * seconds * seconds)) * rate;
            Assert.InRange(output[index], expected - 0.5, expected + 0.5);
        }
    }

    private static (TimelineTrackProject Track, TimelineClipProject Clip) RampedTrack(double sourceDuration = 10)
    {
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Name = "Video 1" };
        var clip = new TimelineClipProject
        {
            SourcePath = "a.mp4",
            SourceOutSeconds = 10,
            SourceDurationSeconds = sourceDuration,
        };
        track.Clips.Add(clip);
        Assert.True(VideoTimelineEditing.TrySetSpeedRamp(
            track, clip, [new VideoSpeedRampPoint(0, 1), new VideoSpeedRampPoint(1, 3)], Fps));
        return (track, clip);
    }

    private sealed class MemorySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }
}
