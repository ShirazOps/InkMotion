using Xunit;

namespace InkMotion.Tests;

public sealed class VideoTransitionsTests
{
    private const double FrameRate = 24;

    [Fact]
    public void TrySetCrossDissolve_UsesSourceHandlesWithoutMovingTheCut()
    {
        TimelineTrackProject track = CreateAdjacentTrack();
        TimelineClipProject outgoing = track.Clips[0];
        TimelineClipProject incoming = track.Clips[1];
        int originalStart = incoming.StartFrame;

        Assert.True(VideoTransitions.TrySetCrossDissolve(track, incoming, 1, FrameRate));

        Assert.Equal(originalStart, incoming.StartFrame);
        Assert.Equal(1, incoming.TransitionInSeconds);
        Assert.Null(VideoTimelineEditing.Validate([track], FrameRate));
    }

    [Fact]
    public void GetAt_ProvidesLinearProgressAndCompositorLayersAcrossCut()
    {
        TimelineTrackProject track = CreateAdjacentTrack();
        TimelineClipProject outgoing = track.Clips[0];
        TimelineClipProject incoming = track.Clips[1];
        Assert.True(VideoTransitions.TrySetCrossDissolve(track, incoming, 1, FrameRate));
        double cut = VideoTimelineEditing.GetStartSeconds(incoming, FrameRate);

        Assert.Null(VideoTransitions.GetAt(track, cut - 0.501, FrameRate));
        Assert.Equal(0, VideoTransitions.GetAt(track, cut - 0.5, FrameRate)!.Progress);
        Assert.Equal(0.5, VideoTransitions.GetAt(track, cut, FrameRate)!.Progress);
        Assert.Equal(1, VideoTransitions.GetAt(track, cut + 0.5, FrameRate)!.Progress);

        TimelineClipLocation[] layers = VideoTimelineEditing.GetVisibleLayers([track], cut, FrameRate).ToArray();
        Assert.Equal(new[] { outgoing, incoming }, layers.Select(layer => layer.Clip));
        Assert.Equal(1, VideoTimelineEditing.GetOpacity(layers[0], cut, FrameRate));
        Assert.Equal(0.5, VideoTimelineEditing.GetOpacity(layers[1], cut, FrameRate));
        Assert.Equal(0.5, VideoTimelineEditing.GetSourceSeconds(incoming, cut - 0.5, FrameRate));
        Assert.Equal(5.5, VideoTimelineEditing.GetSourceSeconds(outgoing, cut + 0.5, FrameRate));
    }

    [Fact]
    public void GetOpacity_AppliesAnimatedOpacityToBothSidesOfDissolve()
    {
        TimelineTrackProject track = CreateAdjacentTrack();
        TimelineClipProject outgoing = track.Clips[0];
        TimelineClipProject incoming = track.Clips[1];
        outgoing.OpacityKeyframes =
        [
            new() { FrameOffset = 0, Opacity = 0.8 },
            new() { FrameOffset = 119, Opacity = 0.4 },
        ];
        incoming.OpacityKeyframes = [new() { FrameOffset = 0, Opacity = 0.25 }];
        Assert.True(VideoTransitions.TrySetCrossDissolve(track, incoming, 1, FrameRate));
        double cut = VideoTimelineEditing.GetStartSeconds(incoming, FrameRate);

        Assert.Equal(0.4, VideoTimelineEditing.GetOpacity(
            new TimelineClipLocation(track, outgoing), cut, FrameRate), 6);
        Assert.Equal(0.125, VideoTimelineEditing.GetOpacity(
            new TimelineClipLocation(track, incoming), cut, FrameRate), 6);
    }

    [Fact]
    public void AudioCrossDissolve_UsesLinearGainsAndReadsTheSameSourceHandlesAsVideo()
    {
        TimelineTrackProject track = CreateAdjacentTrack();
        TimelineClipProject outgoing = track.Clips[0];
        TimelineClipProject incoming = track.Clips[1];
        Assert.True(VideoTransitions.TrySetCrossDissolve(track, incoming, 1, FrameRate));

        VideoAudioTransition outgoingTransition = VideoTransitions.GetAudioTransition(track, outgoing, FrameRate);
        VideoAudioTransition incomingTransition = VideoTransitions.GetAudioTransition(track, incoming, FrameRate);
        VideoAudioSourceRange outgoingRange = VideoTransitions.GetAudioSourceRange(outgoing, outgoingTransition);
        VideoAudioSourceRange incomingRange = VideoTransitions.GetAudioSourceRange(incoming, incomingTransition);

        Assert.Equal(new VideoAudioTransition(0, 1), outgoingTransition);
        Assert.Equal(new VideoAudioTransition(1, 0), incomingTransition);
        Assert.Equal((0, 5.5), (outgoingRange.StartSeconds, outgoingRange.EndSeconds));
        Assert.Equal((0.5, 6), (incomingRange.StartSeconds, incomingRange.EndSeconds));
        Assert.Equal(1, ExportPlanning.GetVideoClipAudioGain(outgoing, 4.5, outgoingTransition), 6);
        Assert.Equal(0.5, ExportPlanning.GetVideoClipAudioGain(outgoing, 5, outgoingTransition), 6);
        Assert.Equal(0, ExportPlanning.GetVideoClipAudioGain(outgoing, 5.5, outgoingTransition), 6);
        Assert.Equal(0, ExportPlanning.GetVideoClipAudioGain(incoming, -0.5, incomingTransition), 6);
        Assert.Equal(0.5, ExportPlanning.GetVideoClipAudioGain(incoming, 0, incomingTransition), 6);
        Assert.Equal(1, ExportPlanning.GetVideoClipAudioGain(incoming, 0.5, incomingTransition), 6);
    }

    [Fact]
    public void TrySetCrossDissolve_RejectsMissingHandlesFadesAndNonAdjacentClipsWithoutMutation()
    {
        TimelineTrackProject track = CreateAdjacentTrack();
        TimelineClipProject incoming = track.Clips[1];
        incoming.SourceInSeconds = 0;

        Assert.False(VideoTransitions.TrySetCrossDissolve(track, incoming, 1, FrameRate));
        Assert.Equal(0, incoming.TransitionInSeconds);

        incoming.SourceInSeconds = 1;
        track.Clips[0].FadeOutSeconds = 0.25;
        Assert.False(VideoTransitions.TrySetCrossDissolve(track, incoming, 1, FrameRate));
        Assert.Equal(0, incoming.TransitionInSeconds);

        track.Clips[0].FadeOutSeconds = 0;
        incoming.StartFrame++;
        Assert.False(VideoTransitions.TrySetCrossDissolve(track, incoming, 1, FrameRate));
        Assert.Equal(0, incoming.TransitionInSeconds);
    }

    [Fact]
    public void ReverseAndSpeedTransitions_ValidateHandlesInPlaybackDirection()
    {
        TimelineTrackProject track = CreateAdjacentTrack();
        TimelineClipProject outgoing = track.Clips[0];
        TimelineClipProject incoming = track.Clips[1];
        outgoing.PlaybackSpeed = 2;
        outgoing.IsReversed = true;
        outgoing.SourceInSeconds = 1;
        outgoing.SourceOutSeconds = 6;
        incoming.StartFrame = 61;
        incoming.PlaybackSpeed = 2;
        incoming.IsReversed = true;
        incoming.SourceOutSeconds = 5;

        Assert.True(VideoTransitions.TrySetCrossDissolve(track, incoming, 1, FrameRate));
        VideoAudioSourceRange outgoingAudio = VideoTransitions.GetAudioSourceRange(
            outgoing, VideoTransitions.GetAudioTransition(track, outgoing, FrameRate));
        VideoAudioSourceRange incomingAudio = VideoTransitions.GetAudioSourceRange(
            incoming, VideoTransitions.GetAudioTransition(track, incoming, FrameRate));
        Assert.Equal(0, outgoingAudio.StartSeconds);
        Assert.Equal(6, outgoingAudio.EndSeconds);
        Assert.Equal(1, incomingAudio.StartSeconds);
        Assert.Equal(6, incomingAudio.EndSeconds);

        incoming.SourceOutSeconds = 5.9;
        Assert.False(VideoTransitions.TrySetCrossDissolve(track, incoming, 1, FrameRate));
    }

    [Fact]
    public void SplitAndCopy_KeepTransitionsOnlyWithTheOriginalIncomingClip()
    {
        TimelineTrackProject track = CreateAdjacentTrack();
        TimelineClipProject incoming = track.Clips[1];
        Assert.True(VideoTransitions.TrySetCrossDissolve(track, incoming, 1, FrameRate));

        TimelineClipProject? right = VideoTimelineEditing.Split(track, incoming, incoming.StartFrame + 24, FrameRate);

        Assert.NotNull(right);
        Assert.Equal(1, incoming.TransitionInSeconds);
        Assert.Equal(0, right.TransitionInSeconds);
        Assert.Equal(0, VideoTimelineEditing.CopyForPlacement(incoming, 300).TransitionInSeconds);
    }

    [Fact]
    public void ClearInvalid_RemovesTransitionWhenItsAdjacentClipIsRemoved()
    {
        TimelineTrackProject track = CreateAdjacentTrack();
        TimelineClipProject incoming = track.Clips[1];
        Assert.True(VideoTransitions.TrySetCrossDissolve(track, incoming, 1, FrameRate));

        track.Clips.RemoveAt(0);
        VideoTransitions.ClearInvalid([track], FrameRate);

        Assert.Equal(0, incoming.TransitionInSeconds);
    }

    [Fact]
    public void LockedTrack_PreservesAndRendersExistingTransition()
    {
        TimelineTrackProject track = CreateAdjacentTrack();
        TimelineClipProject incoming = track.Clips[1];
        Assert.True(VideoTransitions.TrySetCrossDissolve(track, incoming, 1, FrameRate));
        track.IsLocked = true;

        Assert.Null(VideoTimelineEditing.Validate([track], FrameRate));
        Assert.NotNull(VideoTransitions.GetAt(track, VideoTimelineEditing.GetStartSeconds(incoming, FrameRate), FrameRate));
    }

    private static TimelineTrackProject CreateAdjacentTrack()
    {
        return new TimelineTrackProject
        {
            Kind = TimelineTrackKind.Video,
            Clips =
            [
                new TimelineClipProject
                {
                    Id = "outgoing",
                    StartFrame = 1,
                    SourcePath = @"C:\Media\outgoing.mp4",
                    SourceInSeconds = 0,
                    SourceOutSeconds = 5,
                    SourceDurationSeconds = 6,
                },
                new TimelineClipProject
                {
                    Id = "incoming",
                    StartFrame = 121,
                    SourcePath = @"C:\Media\incoming.mp4",
                    SourceInSeconds = 1,
                    SourceOutSeconds = 6,
                    SourceDurationSeconds = 6,
                },
            ],
        };
    }
}
