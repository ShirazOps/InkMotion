using Xunit;

namespace InkMotion.Tests;

public sealed class VideoNeighbourEditingTests
{
    [Theory]
    [InlineData(-1, 24)]
    [InlineData(1, 24)]
    [InlineData(-5, 30)]
    [InlineData(5, 60)]
    public void RollEnd_MovesOnlyTheSharedCutAndPreservesTotalDuration(int frames, int rate)
    {
        EditorProject project = Project(rate);
        TimelineTrackProject track = project.VideoTracks[0];
        TimelineClipProject left = track.Clips[1];
        TimelineClipProject right = track.Clips[2];
        left.FadeInSeconds = 0.25;
        right.FadeOutSeconds = 0.5;
        double end = VideoTimelineEditing.GetTimelineEndSeconds(project.VideoTracks, rate);
        int leftStart = left.StartFrame;
        int rightStart = right.StartFrame;
        string first = System.Text.Json.JsonSerializer.Serialize(track.Clips[0]);
        Assert.True(VideoEditTools.RollEnd(track, left, frames, rate));
        Assert.Equal(leftStart, left.StartFrame);
        Assert.Equal(rightStart + frames, right.StartFrame);
        Assert.Equal(4 + frames / (double)rate, left.SourceOutSeconds!.Value, 10);
        Assert.Equal(3 + frames / (double)rate, right.SourceInSeconds, 10);
        Assert.Equal(2, left.SourceInSeconds);
        Assert.Equal(5, right.SourceOutSeconds);
        Assert.Equal(end, VideoTimelineEditing.GetTimelineEndSeconds(project.VideoTracks, rate), 10);
        Assert.Equal(VideoTimelineEditing.GetEndSeconds(left, track.Kind, rate),
            VideoTimelineEditing.GetStartSeconds(right, rate), 10);
        Assert.Equal(first, System.Text.Json.JsonSerializer.Serialize(track.Clips[0]));
        Assert.Equal(0.25, left.FadeInSeconds);
        Assert.Equal(0.5, right.FadeOutSeconds);
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(right.StartFrame, reopened.VideoTracks[0].Clips[2].StartFrame);
        Assert.Equal(left.SourceOutSeconds, reopened.VideoTracks[0].Clips[1].SourceOutSeconds);
    }

    [Theory]
    [InlineData(-1, 24)]
    [InlineData(1, 24)]
    [InlineData(-5, 30)]
    [InlineData(5, 60)]
    public void Slide_PreservesCentreSourceAndOuterEdgesWithNoGapsOrOverlaps(int frames, int rate)
    {
        EditorProject project = Project(rate);
        TimelineTrackProject track = project.VideoTracks[0];
        TimelineClipProject left = track.Clips[0];
        TimelineClipProject centre = track.Clips[1];
        TimelineClipProject right = track.Clips[2];
        centre.FadeInSeconds = 0.25;
        centre.FadeOutSeconds = 0.5;
        int centreStart = centre.StartFrame;
        int rightStart = right.StartFrame;
        double end = VideoTimelineEditing.GetTimelineEndSeconds(project.VideoTracks, rate);
        Assert.True(VideoEditTools.Slide(track, centre, frames, rate));
        Assert.Equal(1, left.StartFrame);
        Assert.Equal(centreStart + frames, centre.StartFrame);
        Assert.Equal(rightStart + frames, right.StartFrame);
        Assert.Equal(2, centre.SourceInSeconds);
        Assert.Equal(4, centre.SourceOutSeconds);
        Assert.Equal(3 + frames / (double)rate, left.SourceOutSeconds!.Value, 10);
        Assert.Equal(3 + frames / (double)rate, right.SourceInSeconds, 10);
        Assert.Equal(5, right.SourceOutSeconds);
        Assert.Equal(end, VideoTimelineEditing.GetTimelineEndSeconds(project.VideoTracks, rate), 10);
        Assert.Equal(VideoTimelineEditing.GetEndSeconds(left, track.Kind, rate),
            VideoTimelineEditing.GetStartSeconds(centre, rate), 10);
        Assert.Equal(VideoTimelineEditing.GetEndSeconds(centre, track.Kind, rate),
            VideoTimelineEditing.GetStartSeconds(right, rate), 10);
        Assert.Equal(0.25, centre.FadeInSeconds);
        Assert.Equal(0.5, centre.FadeOutSeconds);
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(centre.StartFrame, reopened.VideoTracks[0].Clips[1].StartFrame);
        Assert.Equal(right.SourceInSeconds, reopened.VideoTracks[0].Clips[2].SourceInSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperationsRejectUnavailableHandlesConsumedClipsAndFadesWithoutMutation(bool slide)
    {
        foreach (string failure in new[] { "locked", "gap", "overlap", "left-handle", "right-handle", "left-consumed", "right-consumed", "left-fade", "right-fade", "zero", "bad-rate", "absent" })
        {
            EditorProject project = Project(24);
            TimelineTrackProject track = project.VideoTracks[0];
            TimelineClipProject selected = track.Clips[1];
            TimelineClipProject shrinkingLeft = slide ? track.Clips[0] : selected;
            TimelineClipProject right = track.Clips[2];
            int frames = 1;
            double rate = 24;
            switch (failure)
            {
                case "locked": track.IsLocked = true; break;
                case "gap": right.StartFrame++; break;
                case "overlap": right.StartFrame--; break;
                case "left-handle": shrinkingLeft.SourceDurationSeconds = shrinkingLeft.SourceOutSeconds!.Value; break;
                case "right-handle": right.SourceOutSeconds -= right.SourceInSeconds; right.SourceInSeconds = 0; frames = -1; break;
                case "left-consumed": frames = -48; break;
                case "right-consumed": frames = 48; break;
                case "left-fade": shrinkingLeft.FadeInSeconds = 1; shrinkingLeft.FadeOutSeconds = 1; frames = -1; break;
                case "right-fade": right.FadeInSeconds = 1; right.FadeOutSeconds = 1; break;
                case "zero": frames = 0; break;
                case "bad-rate": rate = double.NaN; break;
                case "absent": selected = new(); break;
            }
            // Include deliberately overlapping inputs when asserting that failed edits do not mutate anything.
            string before = System.Text.Json.JsonSerializer.Serialize(project);
            bool result = slide ? VideoEditTools.Slide(track, selected, frames, rate)
                : VideoEditTools.RollEnd(track, selected, frames, rate);
            Assert.False(result, failure);
            Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(project));
        }
    }

    [Fact]
    public void OperationsRequireVideoNeighboursButDoNotDependOnListStorageOrder()
    {
        EditorProject project = Project(24);
        TimelineTrackProject track = project.VideoTracks[0];
        Assert.False(VideoEditTools.RollEnd(track, track.Clips[2], 1, 24));
        Assert.False(VideoEditTools.Slide(track, track.Clips[0], 1, 24));
        TimelineClipProject centre = track.Clips[1];
        track.Clips.Reverse();
        Assert.True(VideoEditTools.Slide(track, centre, 1, 24));
        Assert.True(VideoEditTools.RollEnd(track, centre, -1, 24));
        track.Kind = TimelineTrackKind.Image;
        Assert.False(VideoEditTools.RollEnd(track, centre, 1, 24));
        Assert.False(VideoEditTools.Slide(track, centre, 1, 24));
    }

    [Fact]
    public void SlideRejectsMissingPreviousNeighbourAndIntegerOverflow()
    {
        EditorProject project = Project(24);
        TimelineTrackProject track = project.VideoTracks[0];
        track.Clips[0].SourceOutSeconds = 2;
        Assert.False(VideoEditTools.Slide(track, track.Clips[1], 1, 24));
        project = Project(24);
        track = project.VideoTracks[0];
        Assert.False(VideoEditTools.RollEnd(track, track.Clips[1], int.MaxValue, 24));
        Assert.False(VideoEditTools.Slide(track, track.Clips[1], int.MaxValue, 24));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditedSequenceMapsEveryOutputFrameToExactlyOneValidSource(bool slide)
    {
        EditorProject project = Project(24);
        TimelineTrackProject track = project.VideoTracks[0];
        Assert.True(slide ? VideoEditTools.Slide(track, track.Clips[1], 1, 24)
            : VideoEditTools.RollEnd(track, track.Clips[1], 1, 24));
        for (int frame = 0; frame < 144; frame++)
        {
            double seconds = frame / 24d;
            TimelineClipProject expected = Assert.Single(track.Clips, clip =>
                VideoTimelineEditing.GetStartSeconds(clip, 24) <= seconds + 1e-6 &&
                seconds < VideoTimelineEditing.GetEndSeconds(clip, track.Kind, 24) - 1e-6);
            TimelineClipLocation actual = Assert.IsType<TimelineClipLocation>(
                VideoTimelineEditing.GetActiveVideoClip(project.VideoTracks, seconds, 24));
            Assert.Same(expected, actual.Clip);
            double source = VideoTimelineEditing.GetSourceSeconds(actual.Clip, seconds, 24);
            Assert.True(source >= actual.Clip.SourceInSeconds - 1e-6);
            Assert.True(source < actual.Clip.SourceOutSeconds);
        }
        TimelineClipProject centre = track.Clips[1];
        double atCut = VideoTimelineEditing.GetStartSeconds(centre, 24);
        Assert.Equal(2, VideoTimelineEditing.GetSourceSeconds(centre, atCut, 24), 10);
        Assert.Null(VideoTimelineEditing.GetActiveVideoClip(project.VideoTracks, 6, 24));
    }

    private static EditorProject Project(int rate) => new()
    {
        FrameRate = rate,
        VideoTracks =
        [
            new()
            {
                Clips =
                [
                    new() { SourcePath = "left.mp4", SourceInSeconds = 1, SourceOutSeconds = 3, SourceDurationSeconds = 10 },
                    new() { SourcePath = "centre.mp4", StartFrame = 1 + 2 * rate, SourceInSeconds = 2, SourceOutSeconds = 4, SourceDurationSeconds = 10 },
                    new() { SourcePath = "right.mp4", StartFrame = 1 + 4 * rate, SourceInSeconds = 3, SourceOutSeconds = 5, SourceDurationSeconds = 10 },
                ],
            },
        ],
    };
}
