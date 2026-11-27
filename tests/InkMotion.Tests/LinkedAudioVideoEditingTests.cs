using Xunit;

namespace InkMotion.Tests;

public sealed class LinkedAudioVideoEditingTests
{
    private const double FrameRate = 24;

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(73, 2, true)]
    public void Split_OffsetAudioOutsideCutStaysLinkedToItsSegment(int audioStart, double sourceOut, bool linkedToRight)
    {
        TimelineTrackProject track = CreateVideoTrack();
        TimelineClipProject video = track.Clips[0];
        var audio = new AudioTrackProject { AudioPath = "voice.wav", StartFrame = audioStart,
            SourceOutSeconds = sourceOut, LinkedVideoClipId = video.Id };
        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            LinkedAudioVideoEditing.SplitVideo(track, video, 58, FrameRate, audio, 10, out AudioTrackProject? rightAudio));
        Assert.Null(rightAudio);
        Assert.Equal(linkedToRight ? right.Id : video.Id, audio.LinkedVideoClipId);
        Assert.Equal(audioStart, audio.StartFrame);
        Assert.Equal(sourceOut, audio.SourceOutSeconds);
    }

    [Fact]
    public void InvalidSplitOrLockedDelete_LeavesLinkedModelsUnchanged()
    {
        TimelineTrackProject track = CreateVideoTrack();
        TimelineClipProject video = track.Clips[0];
        var audio = new AudioTrackProject { AudioPath = "voice.wav", StartFrame = 16,
            SourceOutSeconds = 8, LinkedVideoClipId = video.Id };
        Assert.Null(LinkedAudioVideoEditing.SplitVideo(track, video, 1, FrameRate, audio, 10, out _));
        Assert.Equal(8, audio.SourceOutSeconds);
        Assert.Single(track.Clips);
        track.IsLocked = true;
        List<AudioTrackProject> clips = [audio];
        Assert.False(LinkedAudioVideoEditing.DeleteVideo(track, video, clips, FrameRate));
        Assert.Single(clips);
        TimelineClipProject copy = VideoTimelineEditing.CopyForPlacement(video, 1);
        audio.StartFrame = 1;
        Assert.False(LinkedAudioVideoEditing.TryCopyLinkedAudio(video, copy, audio, out _));
    }

    [Fact]
    public void RippleDelete_RemovesOnlyLinkedAudioAndShiftsLaterPairsAtomically()
    {
        var first = new TimelineClipProject { SourcePath = "video.mp4", SourceDurationSeconds = 10, SourceOutSeconds = 2 };
        var later = new TimelineClipProject { SourcePath = "video.mp4", SourceDurationSeconds = 10, SourceOutSeconds = 2, StartFrame = 49 };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [first, later] };
        var removed = new AudioTrackProject { AudioPath = "audio.wav", SourceOutSeconds = 2, LinkedVideoClipId = first.Id };
        var retained = new AudioTrackProject { AudioPath = "audio.wav", SourceOutSeconds = 2, StartFrame = 55, LinkedVideoClipId = later.Id };
        var unrelated = new AudioTrackProject { AudioPath = "music.wav", StartFrame = 60 };
        List<AudioTrackProject> audio = [removed, retained, unrelated];
        retained.StartFrame = 40;
        Assert.False(LinkedAudioVideoEditing.DeleteVideo(track, first, audio, FrameRate, ripple: true));
        Assert.Equal(2, track.Clips.Count);
        Assert.Equal(3, audio.Count);
        retained.StartFrame = 55;
        Assert.True(LinkedAudioVideoEditing.DeleteVideo(track, first, audio, FrameRate, ripple: true));
        Assert.Same(later, Assert.Single(track.Clips));
        Assert.Equal(1, later.StartFrame);
        Assert.Equal(7, retained.StartFrame);
        Assert.Equal(60, unrelated.StartFrame);
        Assert.DoesNotContain(removed, audio);
        Assert.Null(LinkedAudioVideoEditing.Validate([track], audio));
    }

    [Fact]
    public void TryLink_PreservesAudioTimingOffsetAndPreventsDuplicateVideoLinks()
    {
        TimelineTrackProject track = CreateVideoTrack();
        TimelineClipProject video = Assert.Single(track.Clips);
        var audio = new AudioTrackProject { AudioPath = @"C:\Media\dialog.wav", StartFrame = 16 };

        Assert.True(LinkedAudioVideoEditing.TryLink(track, video, audio, [audio]));
        Assert.Equal(video.Id, audio.LinkedVideoClipId);
        Assert.Equal(16, audio.StartFrame);
        Assert.False(LinkedAudioVideoEditing.TryLink(track, video, new AudioTrackProject
        {
            AudioPath = @"C:\Media\other.wav",
        }, [audio]));
    }

    [Fact]
    public void TryMoveVideo_MovesLinkedAudioByTheSameFrameDelta()
    {
        TimelineTrackProject track = CreateVideoTrack();
        TimelineClipProject video = Assert.Single(track.Clips);
        var audio = new AudioTrackProject { AudioPath = @"C:\Media\dialog.wav", StartFrame = 16 };
        Assert.True(LinkedAudioVideoEditing.TryLink(track, video, audio, [audio]));

        Assert.True(LinkedAudioVideoEditing.TryMoveVideo(track, video, 30, [audio], FrameRate));

        Assert.Equal(30, video.StartFrame);
        Assert.Equal(36, audio.StartFrame);
    }

    [Fact]
    public void TryMoveAudio_MovesLinkedVideoAndRejectsBlockedVideoMoveAtomically()
    {
        TimelineTrackProject track = CreateVideoTrack();
        TimelineClipProject video = Assert.Single(track.Clips);
        var audio = new AudioTrackProject { AudioPath = @"C:\Media\dialog.wav", StartFrame = 16 };
        Assert.True(LinkedAudioVideoEditing.TryLink(track, video, audio, [audio]));

        Assert.True(LinkedAudioVideoEditing.TryMoveAudio(audio, 20, [track], FrameRate));
        Assert.Equal(14, video.StartFrame);
        Assert.Equal(20, audio.StartFrame);

        track.IsLocked = true;
        Assert.False(LinkedAudioVideoEditing.TryMoveAudio(audio, 28, [track], FrameRate));
        Assert.Equal(14, video.StartFrame);
        Assert.Equal(20, audio.StartFrame);
    }

    [Fact]
    public void Validate_RejectsOrphanedAndDuplicateLinks()
    {
        TimelineTrackProject track = CreateVideoTrack();
        TimelineClipProject video = Assert.Single(track.Clips);
        var first = new AudioTrackProject { AudioPath = "first.wav", LinkedVideoClipId = video.Id };
        var second = new AudioTrackProject { AudioPath = "second.wav", LinkedVideoClipId = video.Id };

        Assert.NotNull(LinkedAudioVideoEditing.Validate([track], [first, second]));
        Assert.NotNull(LinkedAudioVideoEditing.Validate([track], [
            new AudioTrackProject { AudioPath = "orphan.wav", LinkedVideoClipId = "missing" },
        ]));
    }

    private static TimelineTrackProject CreateVideoTrack()
    {
        var track = new TimelineTrackProject
        {
            Kind = TimelineTrackKind.Video,
            Clips =
            [
                new TimelineClipProject
                {
                    StartFrame = 10,
                    SourcePath = @"C:\Media\scene.mp4",
                    SourceDurationSeconds = 10,
                    SourceOutSeconds = 4,
                },
            ],
        };
        return track;
    }
}
