using Xunit;

namespace InkMotion.Tests;

public sealed class VideoAudioPreviewTests
{
    [Fact]
    public void ModifiedPreviewUsesVideoAudioEditsAndHonoursTrackMuteAndSolo()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "source.mp4",
            SourceInSeconds = 2,
            SourceOutSeconds = 8,
            PlaybackSpeed = 1.5,
            Volume = 0.4,
            FadeInSeconds = 0.5,
            FadeOutSeconds = 0.75,
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, IsMuted = true };
        var active = new TimelineClipLocation(track, clip);

        AudioTrackProject muted = VideoTimelineEditing.CreatePreviewAudioProject(active, originalPreview: false,
            hasSoloAudio: false);
        Assert.True(muted.Muted);
        Assert.Equal(0.4, muted.Volume);
        Assert.Equal(0.5, muted.FadeInSeconds);
        Assert.Equal(0.75, muted.FadeOutSeconds);
        Assert.Equal(150, muted.TempoPercent);
        Assert.Equal(2, muted.SourceInSeconds);
        Assert.Equal(8, muted.SourceOutSeconds);

        track.IsMuted = false;
        AudioTrackProject soloSuppressed = VideoTimelineEditing.CreatePreviewAudioProject(active,
            originalPreview: false, hasSoloAudio: true);
        Assert.True(soloSuppressed.Muted);

        AudioTrackProject audible = VideoTimelineEditing.CreatePreviewAudioProject(active, originalPreview: false,
            hasSoloAudio: false);
        Assert.False(audible.Muted);
    }

    [Fact]
    public void OriginalPreviewBypassesClipVolumeFadesTrackMuteAndSolo()
    {
        var clip = new TimelineClipProject
        {
            Volume = 0.2,
            FadeInSeconds = 1,
            FadeOutSeconds = 2,
            PlaybackSpeed = 2,
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, IsMuted = true };
        AudioTrackProject original = VideoTimelineEditing.CreatePreviewAudioProject(
            new TimelineClipLocation(track, clip), originalPreview: true, hasSoloAudio: true);

        Assert.False(original.Muted);
        Assert.Equal(1, original.Volume);
        Assert.Equal(0, original.FadeInSeconds);
        Assert.Equal(0, original.FadeOutSeconds);
        Assert.Equal(200, original.TempoPercent);
    }

    [Fact]
    public void ReversedVideoAudioIsMutedInBothPreviewModes()
    {
        var active = new TimelineClipLocation(
            new TimelineTrackProject { Kind = TimelineTrackKind.Video },
            new TimelineClipProject { IsReversed = true });

        Assert.True(VideoTimelineEditing.CreatePreviewAudioProject(active, originalPreview: false,
            hasSoloAudio: false).Muted);
        Assert.True(VideoTimelineEditing.CreatePreviewAudioProject(active, originalPreview: true,
            hasSoloAudio: false).Muted);
    }
}
