using Xunit;

namespace InkMotion.Tests;

public sealed class LinkedAudioVideoEditingTests
{
    private const double FrameRate = 24;

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
