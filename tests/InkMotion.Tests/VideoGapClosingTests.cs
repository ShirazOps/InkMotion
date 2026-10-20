using Xunit;

namespace InkMotion.Tests;

public sealed class VideoGapClosingTests
{
    [Theory]
    [InlineData(TimelineTrackKind.Video)]
    [InlineData(TimelineTrackKind.Image)]
    [InlineData(TimelineTrackKind.Text)]
    public void ClosesSelectedGapAndShiftsLaterClipsWithoutChangingProperties(TimelineTrackKind kind)
    {
        TimelineClipProject previous = Clip(1);
        TimelineClipProject selected = Clip(97);
        TimelineClipProject later = Clip(193);
        var track = new TimelineTrackProject { Kind = kind, Clips = [later, previous, selected], IsVisible = false };
        selected.SourceInSeconds = 2;
        selected.SourceOutSeconds = 4;
        selected.FadeInSeconds = 0.2;
        selected.FadeOutSeconds = 0.3;
        selected.Opacity = 0.6;
        selected.Volume = 0.4;
        var project = new EditorProject { VideoTracks = [track] };
        Assert.True(VideoEditTools.CloseGapBefore(track, selected, 24));
        Assert.Equal(1, previous.StartFrame);
        Assert.Equal(49, selected.StartFrame);
        Assert.Equal(145, later.StartFrame);
        Assert.Same(later, track.Clips[0]);
        Assert.Same(selected, track.Clips[2]);
        Assert.Equal(2, selected.SourceInSeconds);
        Assert.Equal(4, selected.SourceOutSeconds);
        Assert.Equal(0.2, selected.FadeInSeconds);
        Assert.Equal(0.3, selected.FadeOutSeconds);
        Assert.Equal(0.6, selected.Opacity);
        Assert.Equal(0.4, selected.Volume);
        Assert.Null(VideoTimelineEditing.Validate([track], 24));
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(selected.StartFrame, reopened.VideoTracks[0].Clips.Single(clip => clip.Id == selected.Id).StartFrame);
    }

    [Theory]
    [InlineData(24, 0.1, 4)]
    [InlineData(29.97, 1, 31)]
    [InlineData(120, 1d / 120, 2)]
    [InlineData(24, 2, 49)]
    public void UsesFirstFrameBoundaryAtOrAfterPreviousEnd(double rate, double duration, int expectedStart)
    {
        TimelineClipProject previous = Clip(1);
        previous.SourceOutSeconds = duration;
        TimelineClipProject selected = Clip(121);
        var track = new TimelineTrackProject { Clips = [previous, selected] };
        Assert.True(VideoEditTools.CloseGapBefore(track, selected, rate));
        Assert.Equal(expectedStart, selected.StartFrame);
        double gap = VideoTimelineEditing.GetStartSeconds(selected, rate) -
            VideoTimelineEditing.GetEndSeconds(previous, track.Kind, rate);
        Assert.InRange(gap, -1e-10, 1 / rate);
        Assert.False(VideoEditTools.CloseGapBefore(track, selected, rate));
    }

    [Fact]
    public void FirstClipClosesLeadingGapAndLeavesOtherTracksUnchanged()
    {
        TimelineClipProject first = Clip(49);
        TimelineClipProject later = Clip(int.MaxValue);
        var track = new TimelineTrackProject { Clips = [later, first] };
        var other = new TimelineTrackProject { Clips = [Clip(97)] };
        Assert.True(VideoEditTools.CloseGapBefore(track, first, 24));
        Assert.Equal(1, first.StartFrame);
        Assert.Equal(int.MaxValue - 48, later.StartFrame);
        Assert.Equal(97, other.Clips[0].StartFrame);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("foreign")]
    [InlineData("overlap")]
    [InlineData("unknown-duration")]
    [InlineData("no-gap")]
    [InlineData("zero-rate")]
    [InlineData("nan-rate")]
    [InlineData("infinite-rate")]
    public void InvalidOrUnchangedOperationsLeaveEveryClipUntouched(string reason)
    {
        TimelineClipProject first = Clip(1);
        TimelineClipProject selected = Clip(reason == "overlap" ? 25 : reason == "no-gap" ? 49 : 97);
        var track = new TimelineTrackProject { Clips = [first, selected], IsLocked = reason == "locked" };
        if (reason == "unknown-duration")
        {
            first.SourceDurationSeconds = 0;
            first.SourceOutSeconds = null;
        }

        int start = selected.StartFrame;
        double rate = reason switch { "zero-rate" => 0, "nan-rate" => double.NaN, "infinite-rate" => double.PositiveInfinity, _ => 24 };
        Assert.False(VideoEditTools.CloseGapBefore(track, reason == "foreign" ? Clip(97) : selected, rate));
        Assert.Equal(1, first.StartFrame);
        Assert.Equal(start, selected.StartFrame);
    }

    private static TimelineClipProject Clip(int start) => new()
    {
        Id = VideoTimelineEditing.NewClipId(), StartFrame = start, SourcePath = "source.mp4",
        SourceDurationSeconds = 10, SourceOutSeconds = 2, DurationSeconds = 2, Text = "Title",
    };
}
