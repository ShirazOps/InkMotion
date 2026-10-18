using Xunit;

namespace InkMotion.Tests;

public sealed class VideoTimelineEditingTests
{
    private const double Fps = 24;

    [Fact]
    public void VideoClipDuration_UsesInAndOutPoints()
    {
        var clip = new TimelineClipProject { SourcePath = "a.mp4", SourceInSeconds = 2, SourceDurationSeconds = 10, StartFrame = 25 };

        Assert.Equal(8, VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video));
        clip.SourceOutSeconds = 5;
        Assert.Equal(3, VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video));
        Assert.Equal(1, VideoTimelineEditing.GetStartSeconds(clip, Fps));
        Assert.Equal(4, VideoTimelineEditing.GetEndSeconds(clip, TimelineTrackKind.Video, Fps));
        Assert.Equal(3.5, VideoTimelineEditing.GetSourceSeconds(clip, 2.5, Fps));
    }

    [Fact]
    public void TrimStart_AdvancesSourceInAndStartFrame()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = VideoTrack(sourceIn: 1, duration: 10);

        Assert.True(VideoTimelineEditing.TryTrimStart(track, clip, playheadFrame: 49, Fps));

        Assert.Equal(49, clip.StartFrame);
        Assert.Equal(3, clip.SourceInSeconds, 6);
        Assert.Equal(7, VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video), 6);
    }

    [Fact]
    public void TrimEnd_SetsSourceOut()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = VideoTrack(sourceIn: 1, duration: 10);

        Assert.True(VideoTimelineEditing.TryTrimEnd(track, clip, playheadFrame: 73, Fps));

        Assert.Equal(4, clip.SourceOutSeconds!.Value, 6);
        Assert.Equal(1, clip.StartFrame);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(217)]
    [InlineData(300)]
    public void TrimAndSplit_RequirePlayheadInsideClip(int playheadFrame)
    {
        (TimelineTrackProject track, TimelineClipProject clip) = VideoTrack(sourceIn: 1, duration: 10);

        Assert.False(VideoTimelineEditing.TryTrimStart(track, clip, playheadFrame, Fps));
        Assert.False(VideoTimelineEditing.TryTrimEnd(track, clip, playheadFrame, Fps));
        Assert.Null(VideoTimelineEditing.Split(track, clip, playheadFrame, Fps));
        Assert.Equal(1, clip.SourceInSeconds);
        Assert.Null(clip.SourceOutSeconds);
    }

    [Fact]
    public void Split_VideoClipKeepsBothPartsOnSameTrack()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = VideoTrack(sourceIn: 1, duration: 10);
        clip.FadeInSeconds = 1;
        clip.FadeOutSeconds = 2;

        TimelineClipProject? right = VideoTimelineEditing.Split(track, clip, playheadFrame: 97, Fps);

        Assert.NotNull(right);
        Assert.Equal(2, track.Clips.Count);
        Assert.Same(clip, track.Clips[0]);
        Assert.Same(right, track.Clips[1]);
        Assert.NotEqual(clip.Id, right.Id);
        Assert.Equal(5, clip.SourceOutSeconds!.Value, 6);
        Assert.Equal(5, right.SourceInSeconds, 6);
        Assert.Null(right.SourceOutSeconds);
        Assert.Equal(97, right.StartFrame);
        Assert.Equal((1d, 0d), (clip.FadeInSeconds, clip.FadeOutSeconds));
        Assert.Equal((0d, 2d), (right.FadeInSeconds, right.FadeOutSeconds));
        Assert.Null(VideoTimelineEditing.Validate([track], Fps));
    }

    [Fact]
    public void Split_TitleClipDividesDuration()
    {
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Text, Name = "Title 1" };
        var clip = new TimelineClipProject { Text = "Hello", StartFrame = 25, DurationSeconds = 5 };
        track.Clips.Add(clip);

        TimelineClipProject? right = VideoTimelineEditing.Split(track, clip, playheadFrame: 73, Fps);

        Assert.NotNull(right);
        Assert.Equal(2, clip.DurationSeconds, 6);
        Assert.Equal(3, right.DurationSeconds, 6);
        Assert.Equal("Hello", right.Text);
    }

    [Fact]
    public void LockedTrack_RejectsEdits()
    {
        (TimelineTrackProject track, TimelineClipProject clip) = VideoTrack(sourceIn: 0, duration: 10);
        track.IsLocked = true;

        Assert.False(VideoTimelineEditing.TryTrimStart(track, clip, 25, Fps));
        Assert.False(VideoTimelineEditing.TryMove(track, clip, 100, Fps));
        Assert.False(VideoTimelineEditing.Delete(track, clip));
        Assert.False(VideoTimelineEditing.RippleDelete(track, clip, Fps));
        Assert.Single(track.Clips);
    }

    [Fact]
    public void Move_RejectsOverlapAndKeepsClipsSorted()
    {
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Image, Name = "Image 1" };
        var first = new TimelineClipProject { SourcePath = "a.png", StartFrame = 1, DurationSeconds = 2 };
        var second = new TimelineClipProject { SourcePath = "b.png", StartFrame = 97, DurationSeconds = 2 };
        track.Clips.AddRange([first, second]);

        Assert.False(VideoTimelineEditing.TryMove(track, first, 90, Fps));
        Assert.Equal(1, first.StartFrame);
        Assert.True(VideoTimelineEditing.TryMove(track, first, 145, Fps));
        Assert.Equal([second, first], track.Clips);
        Assert.True(VideoTimelineEditing.TryMove(track, second, 49, Fps));
        Assert.False(VideoTimelineEditing.TryMove(track, second, 0, Fps));
    }

    [Fact]
    public void RippleDelete_ClosesGapOnTrack()
    {
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Image, Name = "Image 1" };
        var first = new TimelineClipProject { SourcePath = "a.png", StartFrame = 1, DurationSeconds = 2 };
        var middle = new TimelineClipProject { SourcePath = "b.png", StartFrame = 49, DurationSeconds = 3 };
        var last = new TimelineClipProject { SourcePath = "c.png", StartFrame = 145, DurationSeconds = 1 };
        track.Clips.AddRange([first, middle, last]);

        Assert.True(VideoTimelineEditing.RippleDelete(track, middle, Fps));

        Assert.Equal([first, last], track.Clips);
        Assert.Equal(1, first.StartFrame);
        Assert.Equal(73, last.StartFrame);
    }

    [Fact]
    public void PlaceClip_UsesPreferredTrackOrCreatesNewTrackOnOverlap()
    {
        var tracks = new List<TimelineTrackProject>();
        TimelineTrackProject video = VideoTimelineEditing.CreateTrack(tracks, TimelineTrackKind.Video);
        VideoTimelineEditing.InsertTrack(tracks, video);
        var first = new TimelineClipProject { SourcePath = "a.mp4", SourceDurationSeconds = 4 };

        Assert.Same(video, VideoTimelineEditing.PlaceClip(tracks, TimelineTrackKind.Video, video, first, Fps));

        var overlapping = new TimelineClipProject { SourcePath = "b.mp4", SourceDurationSeconds = 4, StartFrame = 25 };
        TimelineTrackProject placed = VideoTimelineEditing.PlaceClip(tracks, TimelineTrackKind.Video, video, overlapping, Fps);

        Assert.NotSame(video, placed);
        Assert.Equal("Video 2", placed.Name);
        Assert.Equal([placed, video], tracks);

        var after = new TimelineClipProject { SourcePath = "c.mp4", SourceDurationSeconds = 1, StartFrame = 97 };
        Assert.Same(video, VideoTimelineEditing.PlaceClip(tracks, TimelineTrackKind.Video, video, after, Fps));
    }

    [Fact]
    public void InsertTrack_PutsTitlesOnTopAndImagesAboveVideo()
    {
        var tracks = new List<TimelineTrackProject>();
        TimelineTrackProject video = Insert(tracks, TimelineTrackKind.Video);
        TimelineTrackProject image = Insert(tracks, TimelineTrackKind.Image);
        TimelineTrackProject title = Insert(tracks, TimelineTrackKind.Text);
        TimelineTrackProject video2 = Insert(tracks, TimelineTrackKind.Video);
        TimelineTrackProject image2 = Insert(tracks, TimelineTrackKind.Image);

        Assert.Equal([title, image, image2, video, video2], tracks);
        Assert.Equal(["Title 1", "Image 1", "Image 2", "Video 1", "Video 2"], tracks.Select(track => track.Name));
        Assert.True(VideoTimelineEditing.MoveTrack(tracks, video2, -1));
        Assert.Equal([title, image, image2, video2, video], tracks);
        Assert.False(VideoTimelineEditing.MoveTrack(tracks, title, -1));
    }

    [Fact]
    public void MoveToTrack_RequiresSameKindAndFreeRange()
    {
        var upper = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Name = "Video 2" };
        var lower = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Name = "Video 1" };
        var titles = new TimelineTrackProject { Kind = TimelineTrackKind.Text, Name = "Title 1" };
        var clip = new TimelineClipProject { SourcePath = "a.mp4", SourceDurationSeconds = 2 };
        lower.Clips.Add(clip);

        Assert.False(VideoTimelineEditing.TryMoveToTrack(lower, titles, clip, Fps));
        Assert.True(VideoTimelineEditing.TryMoveToTrack(lower, upper, clip, Fps));
        Assert.Empty(lower.Clips);
        Assert.Same(clip, Assert.Single(upper.Clips));
        lower.Clips.Add(new TimelineClipProject { SourcePath = "b.mp4", SourceDurationSeconds = 5 });
        Assert.False(VideoTimelineEditing.TryMoveToTrack(upper, lower, clip, Fps));
    }

    [Fact]
    public void ActiveVideo_UsesTopVisibleTrack()
    {
        var top = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Name = "Video 2" };
        var bottom = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Name = "Video 1" };
        var topClip = new TimelineClipProject { SourcePath = "top.mp4", StartFrame = 25, SourceDurationSeconds = 1 };
        var bottomClip = new TimelineClipProject { SourcePath = "bottom.mp4", SourceDurationSeconds = 10 };
        top.Clips.Add(topClip);
        bottom.Clips.Add(bottomClip);
        TimelineTrackProject[] tracks = [top, bottom];

        Assert.Same(bottomClip, VideoTimelineEditing.GetActiveVideoClip(tracks, 0.5, Fps)?.Clip);
        Assert.Same(topClip, VideoTimelineEditing.GetActiveVideoClip(tracks, 1.5, Fps)?.Clip);
        Assert.Same(bottomClip, VideoTimelineEditing.GetActiveVideoClip(tracks, 2, Fps)?.Clip);
        top.IsVisible = false;
        Assert.Same(bottomClip, VideoTimelineEditing.GetActiveVideoClip(tracks, 1.5, Fps)?.Clip);
        Assert.Null(VideoTimelineEditing.GetActiveVideoClip(tracks, 10, Fps));
    }

    [Fact]
    public void Overlays_AreReturnedBottomTrackFirst()
    {
        var title = new TimelineTrackProject { Kind = TimelineTrackKind.Text, Name = "Title 1" };
        var image = new TimelineTrackProject { Kind = TimelineTrackKind.Image, Name = "Image 1" };
        var video = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Name = "Video 1" };
        title.Clips.Add(new TimelineClipProject { Text = "Hi", DurationSeconds = 2 });
        image.Clips.Add(new TimelineClipProject { SourcePath = "a.png", DurationSeconds = 4 });
        video.Clips.Add(new TimelineClipProject { SourcePath = "a.mp4", SourceDurationSeconds = 4 });
        TimelineTrackProject[] tracks = [title, image, video];

        Assert.Equal([image, title], VideoTimelineEditing.GetVisibleOverlays(tracks, 1, Fps).Select(location => location.Track));
        Assert.Equal([image], VideoTimelineEditing.GetVisibleOverlays(tracks, 3, Fps).Select(location => location.Track));
        Assert.Equal(4, VideoTimelineEditing.GetTimelineEndSeconds(tracks, Fps));
    }

    [Fact]
    public void Opacity_FollowsFades()
    {
        var clip = new TimelineClipProject { Text = "Hi", DurationSeconds = 4, FadeInSeconds = 1, FadeOutSeconds = 2, Opacity = 0.8 };

        Assert.Equal(0.4, VideoTimelineEditing.GetOpacity(clip, TimelineTrackKind.Text, 0.5, Fps), 6);
        Assert.Equal(0.8, VideoTimelineEditing.GetOpacity(clip, TimelineTrackKind.Text, 1.5, Fps), 6);
        Assert.Equal(0.4, VideoTimelineEditing.GetOpacity(clip, TimelineTrackKind.Text, 3, Fps), 6);
        Assert.Equal(0, VideoTimelineEditing.GetOpacity(clip, TimelineTrackKind.Text, 4, Fps));
    }

    [Fact]
    public void ClampFades_FitsWithinClip()
    {
        var clip = new TimelineClipProject { Text = "Hi", DurationSeconds = 3, FadeInSeconds = 2, FadeOutSeconds = 2 };

        VideoTimelineEditing.ClampFades(clip, TimelineTrackKind.Text);

        Assert.Equal((2d, 1d), (clip.FadeInSeconds, clip.FadeOutSeconds));
    }

    [Fact]
    public void Validate_AllowsUnprobedVideoAndRejectsOverlap()
    {
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Name = "Video 1" };
        track.Clips.Add(new TimelineClipProject { SourcePath = "a.mp4" });
        Assert.Null(VideoTimelineEditing.Validate([track], Fps));

        track.Clips[0].SourceDurationSeconds = 2;
        track.Clips.Add(new TimelineClipProject { SourcePath = "b.mp4", StartFrame = 24, SourceDurationSeconds = 2 });
        Assert.Equal("Clips on Video 1 overlap.", VideoTimelineEditing.Validate([track], Fps));
        track.Clips[1].StartFrame = 49;
        Assert.Null(VideoTimelineEditing.Validate([track], Fps));
        track.Clips[1].SourceOutSeconds = 3;
        Assert.NotNull(VideoTimelineEditing.Validate([track], Fps));
    }

    [Theory]
    [InlineData(24, 12)]
    [InlineData(24, 60)]
    [InlineData(60, 12)]
    [InlineData(12, 30)]
    public void RescaleFrameRate_KeepsTimesWithoutCreatingOverlaps(double oldRate, double newRate)
    {
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Image, Name = "Image 1" };
        track.Clips.Add(new TimelineClipProject { SourcePath = "a.png", StartFrame = 1, DurationSeconds = 1.01 });
        int secondStart = (int)Math.Ceiling(1.01 * oldRate) + 1;
        track.Clips.Add(new TimelineClipProject { SourcePath = "b.png", StartFrame = secondStart, DurationSeconds = 2 });
        double secondSeconds = (secondStart - 1) / oldRate;

        VideoTimelineEditing.RescaleFrameRate([track], oldRate, newRate);

        Assert.Null(VideoTimelineEditing.Validate([track], newRate));
        Assert.Equal(1, track.Clips[0].StartFrame);
        Assert.InRange(VideoTimelineEditing.GetStartSeconds(track.Clips[1], newRate), secondSeconds - 1 / newRate, secondSeconds + 1 / newRate);
    }

    [Fact]
    public void StillDuration_RejectsOverlapAndClampsFades()
    {
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Text, Name = "Title 1" };
        var first = new TimelineClipProject { Text = "A", DurationSeconds = 2, FadeOutSeconds = 1.5 };
        track.Clips.AddRange([first, new TimelineClipProject { Text = "B", StartFrame = 73, DurationSeconds = 1 }]);

        Assert.False(VideoTimelineEditing.TrySetStillDuration(track, first, 3.5, Fps));
        Assert.True(VideoTimelineEditing.TrySetStillDuration(track, first, 1, Fps));
        Assert.Equal((1d, 1d), (first.DurationSeconds, first.FadeOutSeconds));
        Assert.False(VideoTimelineEditing.TrySetStillDuration(track, first, 0, Fps));
    }

    private static (TimelineTrackProject Track, TimelineClipProject Clip) VideoTrack(double sourceIn, double duration)
    {
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Name = "Video 1" };
        var clip = new TimelineClipProject { SourcePath = "a.mp4", SourceInSeconds = sourceIn, SourceDurationSeconds = duration };
        track.Clips.Add(clip);
        return (track, clip);
    }

    private static TimelineTrackProject Insert(List<TimelineTrackProject> tracks, TimelineTrackKind kind)
    {
        TimelineTrackProject track = VideoTimelineEditing.CreateTrack(tracks, kind);
        VideoTimelineEditing.InsertTrack(tracks, track);
        return track;
    }
}
