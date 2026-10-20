using System.Text.Json;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoRippleTrimTests
{
    [Theory]
    [InlineData(-1, 24)]
    [InlineData(1, 24)]
    [InlineData(-5, 30)]
    [InlineData(5, 60)]
    public void RippleStart_KeepsPlacementAndOutPointAndShiftsLaterClipsInOppositeDirection(int frames, int rate)
    {
        EditorProject project = Project(rate);
        TimelineTrackProject track = project.VideoTracks[0];
        TimelineClipProject selected = track.Clips[1];
        TimelineClipProject next = track.Clips[2];
        TimelineClipProject last = track.Clips[3];
        string unrelated = JsonSerializer.Serialize(project.VideoTracks[1]);
        double end = VideoTimelineEditing.GetTimelineEndSeconds([track], rate);
        double gap = VideoTimelineEditing.GetStartSeconds(next, rate) - VideoTimelineEditing.GetEndSeconds(selected, track.Kind, rate);
        int nextStart = next.StartFrame;
        int lastStart = last.StartFrame;
        Assert.True(VideoEditTools.RippleTrimStart(track, selected, frames, rate));
        Assert.Equal(1 + 2 * rate, selected.StartFrame);
        Assert.Equal(2 + frames / (double)rate, selected.SourceInSeconds, 10);
        Assert.Equal(4, selected.SourceOutSeconds);
        Assert.Equal(nextStart - frames, next.StartFrame);
        Assert.Equal(lastStart - frames, last.StartFrame);
        Assert.Equal(1, track.Clips[0].StartFrame);
        Assert.Equal(0.25, selected.FadeInSeconds);
        Assert.Equal(0.5, selected.FadeOutSeconds);
        Assert.Equal(1, next.SourceInSeconds);
        Assert.Equal(3, next.SourceOutSeconds);
        Assert.Equal(gap, VideoTimelineEditing.GetStartSeconds(next, rate) -
            VideoTimelineEditing.GetEndSeconds(selected, track.Kind, rate), 10);
        Assert.Equal(end - frames / (double)rate, VideoTimelineEditing.GetTimelineEndSeconds([track], rate), 10);
        Assert.Equal(unrelated, JsonSerializer.Serialize(project.VideoTracks[1]));
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(selected.SourceInSeconds, reopened.VideoTracks[0].Clips[1].SourceInSeconds);
        Assert.Equal(next.StartFrame, reopened.VideoTracks[0].Clips[2].StartFrame);
    }

    [Theory]
    [InlineData(-49)]
    [InlineData(48)]
    [InlineData(36)]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void RippleStart_RejectsSourceUnderflowConsumedClipInvalidFadesAndExtremeChanges(int frames)
    {
        EditorProject project = Project(24);
        TimelineTrackProject track = project.VideoTracks[0];
        string before = JsonSerializer.Serialize(project);
        Assert.False(VideoEditTools.RippleTrimStart(track, track.Clips[1], frames, 24));
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void RippleStart_RejectsLockedTracksAndLaterOverflowWithoutMutation()
    {
        EditorProject project = Project(24);
        TimelineTrackProject track = project.VideoTracks[0];
        track.IsLocked = true;
        Assert.False(VideoEditTools.RippleTrimStart(track, track.Clips[1], 1, 24));
        track.IsLocked = false;
        track.Clips[^1].StartFrame = int.MaxValue;
        string before = JsonSerializer.Serialize(project);
        Assert.False(VideoEditTools.RippleTrimStart(track, track.Clips[1], -1, 24));
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Theory]
    [InlineData(-1, 24)]
    [InlineData(1, 24)]
    [InlineData(-5, 30)]
    [InlineData(5, 60)]
    public void RippleEnd_PreservesGapsAndLaterSourceRangesWhileChangingTrackLength(int frames, int rate)
    {
        EditorProject project = Project(rate);
        TimelineTrackProject track = project.VideoTracks[0];
        TimelineClipProject selected = track.Clips[1];
        TimelineClipProject next = track.Clips[2];
        TimelineClipProject last = track.Clips[3];
        TimelineClipProject before = VideoTimelineEditing.Clone(track.Clips[0]);
        string unrelated = JsonSerializer.Serialize(project.VideoTracks[1]);
        int start = selected.StartFrame;
        int nextStart = next.StartFrame;
        int lastStart = last.StartFrame;
        double gap = VideoTimelineEditing.GetStartSeconds(next, rate) -
            VideoTimelineEditing.GetEndSeconds(selected, track.Kind, rate);
        double end = VideoTimelineEditing.GetTimelineEndSeconds([track], rate);
        Assert.True(VideoEditTools.RippleTrimEnd(track, selected, frames, rate));
        Assert.Equal(start, selected.StartFrame);
        Assert.Equal(2, selected.SourceInSeconds);
        Assert.Equal(4 + frames / (double)rate, selected.SourceOutSeconds!.Value, 10);
        Assert.Equal(nextStart + frames, next.StartFrame);
        Assert.Equal(lastStart + frames, last.StartFrame);
        Assert.Equal(1, next.SourceInSeconds);
        Assert.Equal(3, next.SourceOutSeconds);
        Assert.Equal(0.25, selected.FadeInSeconds);
        Assert.Equal(0.5, selected.FadeOutSeconds);
        Assert.Equal(0.2, next.FadeInSeconds);
        Assert.Equal(gap, VideoTimelineEditing.GetStartSeconds(next, rate) -
            VideoTimelineEditing.GetEndSeconds(selected, track.Kind, rate), 10);
        Assert.Equal(end + frames / (double)rate, VideoTimelineEditing.GetTimelineEndSeconds([track], rate), 10);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(track.Clips[0]));
        Assert.Equal(unrelated, JsonSerializer.Serialize(project.VideoTracks[1]));
        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(next.StartFrame, reopened.VideoTracks[0].Clips[2].StartFrame);
        Assert.Equal(selected.SourceOutSeconds, reopened.VideoTracks[0].Clips[1].SourceOutSeconds);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("still")]
    [InlineData("absent")]
    [InlineData("zero")]
    [InlineData("nan-rate")]
    [InlineData("zero-rate")]
    [InlineData("no-handle")]
    [InlineData("consumed")]
    [InlineData("fades")]
    [InlineData("overflow")]
    [InlineData("overlap")]
    public void RippleEnd_RejectsUnsafeEditsWithoutMutatingAnyClip(string reason)
    {
        EditorProject project = Project(24);
        TimelineTrackProject track = project.VideoTracks[0];
        TimelineClipProject selected = track.Clips[1];
        int frames = 1;
        double rate = 24;
        switch (reason)
        {
            case "locked": track.IsLocked = true; break;
            case "still": track.Kind = TimelineTrackKind.Image; break;
            case "absent": selected = VideoTimelineEditing.Clone(selected); break;
            case "zero": frames = 0; break;
            case "nan-rate": rate = double.NaN; break;
            case "zero-rate": rate = 0; break;
            case "no-handle": selected.SourceDurationSeconds = 4; break;
            case "consumed": frames = -48; break;
            case "fades": frames = -36; break;
            case "overflow": track.Clips[^1].StartFrame = int.MaxValue; break;
            case "overlap": track.Clips[2].StartFrame = selected.StartFrame; break;
        }
        string before = JsonSerializer.Serialize(project);
        Assert.False(VideoEditTools.RippleTrimEnd(track, selected, frames, rate));
        Assert.Equal(before, JsonSerializer.Serialize(project));
    }

    [Fact]
    public void RippleLastClip_UpdatesItsDurationAndSupportsImplicitOutPoint()
    {
        EditorProject project = Project(24);
        TimelineTrackProject track = project.VideoTracks[0];
        TimelineClipProject selected = track.Clips[^1];
        selected.SourceOutSeconds = null;
        Assert.True(VideoEditTools.RippleTrimEnd(track, selected, -1, 24));
        Assert.Equal(10 - 1 / 24d, selected.SourceOutSeconds!.Value, 10);
        Assert.False(VideoEditTools.RippleTrimEnd(track, selected, 2, 24));
    }

    [Fact]
    public void RippleEnd_UsesTimelineOrderAndShiftsOnlyFollowingClips()
    {
        EditorProject project = Project(24);
        TimelineTrackProject track = project.VideoTracks[0];
        TimelineClipProject selected = track.Clips[1];
        TimelineClipProject first = track.Clips[0];
        track.Clips.Reverse();
        Assert.True(VideoEditTools.RippleTrimEnd(track, selected, -1, 24));
        Assert.Equal(1, first.StartFrame);
        Assert.Equal(49, selected.StartFrame);
        Assert.Equal(new[] { 1, 49, 120, 192 }, track.Clips.OrderBy(clip => clip.StartFrame).Select(clip => clip.StartFrame));
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
                    new() { SourcePath = "first.mp4", SourceOutSeconds = 2, SourceDurationSeconds = 10 },
                    new() { SourcePath = "edit.mp4", StartFrame = 1 + 2 * rate, SourceInSeconds = 2, SourceOutSeconds = 4,
                        SourceDurationSeconds = 10, FadeInSeconds = 0.25, FadeOutSeconds = 0.5 },
                    new() { SourcePath = "next.mp4", StartFrame = 1 + 5 * rate, SourceInSeconds = 1, SourceOutSeconds = 3,
                        SourceDurationSeconds = 10, FadeInSeconds = 0.2 },
                    new() { SourcePath = "last.mp4", StartFrame = 1 + 8 * rate, SourceOutSeconds = 2, SourceDurationSeconds = 10 },
                ],
            },
            new() { Clips = [new() { SourcePath = "unrelated.mp4", SourceOutSeconds = 3, SourceDurationSeconds = 10 }] },
        ],
    };
}
