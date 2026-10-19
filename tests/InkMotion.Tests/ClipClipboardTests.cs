using Xunit;

namespace InkMotion.Tests;

public sealed class ClipClipboardTests
{
    [Theory]
    [InlineData(TimelineTrackKind.Video)]
    [InlineData(TimelineTrackKind.Image)]
    [InlineData(TimelineTrackKind.Text)]
    public void RepeatedPaste_IsIndependentAndPreservesOverlaps(TimelineTrackKind kind)
    {
        var source = new TimelineClipProject
        {
            SourcePath = kind == TimelineTrackKind.Video ? "source.mp4" : "source.png",
            SourceDurationSeconds = 10,
            SourceInSeconds = 2,
            SourceOutSeconds = 8,
            DurationSeconds = 6,
            Text = "Caption",
            X = 0.3,
            Opacity = 0.75,
            Volume = 0.5,
            FadeInSeconds = 0.5,
            FontSize = 64,
        };
        var original = new TimelineTrackProject { Kind = kind, Clips = [source] };
        List<TimelineTrackProject> tracks = [original];
        TimelineClipProject first = VideoTimelineEditing.CopyForPlacement(source, 1);
        TimelineClipProject second = VideoTimelineEditing.CopyForPlacement(source, 1);
        TimelineTrackProject firstTrack = VideoTimelineEditing.PlaceClip(tracks, kind, original, first, 24);
        TimelineTrackProject secondTrack = VideoTimelineEditing.PlaceClip(tracks, kind, original, second, 24);
        Assert.NotSame(original, firstTrack);
        Assert.NotSame(firstTrack, secondTrack);
        Assert.Equal(3, tracks.SelectMany(track => track.Clips).Select(clip => clip.Id).Distinct().Count());
        Assert.Equal(2, first.SourceInSeconds);
        Assert.Equal(8, first.SourceOutSeconds);
        Assert.Equal(0.75, first.Opacity);
        Assert.Equal(0.5, first.Volume);
        Assert.Equal(64, first.FontSize);
        first.Text = "Edited";
        Assert.Equal("Caption", source.Text);
        Assert.Equal("Caption", second.Text);
        Assert.Null(VideoTimelineEditing.Validate(tracks, 24));
    }

    [Fact]
    public void Paste_SkipsLockedAndIncompatibleTracks()
    {
        var locked = new TimelineTrackProject { Kind = TimelineTrackKind.Text, IsLocked = true };
        var incompatible = new TimelineTrackProject { Kind = TimelineTrackKind.Video };
        var available = new TimelineTrackProject { Kind = TimelineTrackKind.Text };
        List<TimelineTrackProject> tracks = [locked, incompatible, available];
        var copy = VideoTimelineEditing.CopyForPlacement(new() { Text = "Title", DurationSeconds = 2 }, 25);
        Assert.Same(available, VideoTimelineEditing.PlaceClip(tracks, TimelineTrackKind.Text, locked, copy, 24));
        Assert.Empty(locked.Clips);
        Assert.Empty(incompatible.Clips);
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoTimelineEditing.CopyForPlacement(copy, 0));
    }

    [Fact]
    public void AudioCopy_PreservesProcessingTagsAndTempoAdjustedDuration()
    {
        var source = new AudioTrackProject
        {
            AudioPath = "voice.wav",
            SourceInSeconds = 2,
            SourceOutSeconds = 8,
            TempoPercent = 200,
            PitchSemitones = 3,
            Volume = 0.75,
            FadeInSeconds = 0.2,
            GainDecibels = -2,
            Title = "Voice",
            Artist = "Artist",
            Comments = "Notes",
            TagsInitialised = true,
        };
        AudioTrackProject copy = AudioClipEditing.Clone(source);
        copy.StartFrame = 73;
        Assert.Equal(3, AudioClipEditing.GetTimelineDuration(copy, 10));
        Assert.Equal(3, copy.PitchSemitones);
        Assert.Equal(0.75, copy.Volume);
        Assert.Equal(0.2, copy.FadeInSeconds);
        Assert.Equal(-2, copy.GainDecibels);
        Assert.Equal("Artist", copy.Artist);
        Assert.True(copy.TagsInitialised);
        copy.Title = "Copy";
        Assert.Equal("Voice", source.Title);
        Assert.Equal(1, source.StartFrame);
    }
}
