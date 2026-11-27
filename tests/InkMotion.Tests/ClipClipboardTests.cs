using Xunit;

namespace InkMotion.Tests;

public sealed class ClipClipboardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitVideo_CopyMoveCutPasteAndSnapshotsKeepIndependentRanges(bool reversed)
    {
        var video = new TimelineClipProject
        {
            SourcePath = "scene.mp4", SourceDurationSeconds = 20, SourceInSeconds = 2, SourceOutSeconds = 12,
            StartFrame = 25, IsReversed = reversed, ProxyPath = "proxy.mp4", UseProxy = true,
            BlurAmount = 0.2,
            TransformKeyframes =
            [
                new() { FrameOffset = 0, X = 0.2, Y = 0.3 },
                new() { FrameOffset = 96, Scale = 1.5, X = 0.6, Y = 0.7, RotationDegrees = 20 },
            ],
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [video] };
        Assert.True(VideoTimelineEditing.TrySetSpeedRamp(track, video, [new(0, 1), new(1, 3)], 24));
        var audio = new AudioTrackProject
        {
            AudioPath = "dialogue.wav", LinkedVideoClipId = video.Id, StartFrame = 31,
            SourceInSeconds = 1, SourceOutSeconds = 7, TempoPercent = 125, Volume = 0.7,
        };
        var project = new EditorProject { VideoTracks = [track], AudioTracks = [audio] };
        string beforeSplit = EditorProjectSerialiser.Serialise(project);
        double sourceAtFour = VideoTimelineEditing.GetSourceBoundarySeconds(video, 4, 24);
        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            LinkedAudioVideoEditing.SplitVideo(track, video, 73, 24, audio, 10, out AudioTrackProject? rightAudio));
        project.AudioTracks.Add(Assert.IsType<AudioTrackProject>(rightAudio));
        Assert.Same(video, VideoTimelineEditing.FindClipAt(track, 3 - 1d / 24, 24)!.Clip);
        Assert.Same(right, VideoTimelineEditing.FindClipAt(track, 3, 24)!.Clip);
        Assert.Equal(sourceAtFour, VideoTimelineEditing.GetSourceBoundarySeconds(right, 4, 24), 9);
        Assert.Equal(3.1875, rightAudio.SourceInSeconds, 9);
        Assert.Equal(rightAudio.SourceInSeconds, audio.SourceOutSeconds);
        Assert.Equal(right.Id, rightAudio.LinkedVideoClipId);
        TimelineClipProject clipboard = VideoTimelineEditing.Clone(right);
        AudioTrackProject audioClipboard = AudioClipEditing.Clone(rightAudio);
        string beforeCut = EditorProjectSerialiser.Serialise(project);
        Assert.True(LinkedAudioVideoEditing.DeleteVideo(track, right, project.AudioTracks, 24));
        Assert.Single(track.Clips);
        Assert.Single(project.AudioTracks);

        TimelineClipProject paste = VideoTimelineEditing.CopyForPlacement(clipboard, 193);
        Assert.True(LinkedAudioVideoEditing.TryCopyLinkedAudio(clipboard, paste, audioClipboard, out AudioTrackProject? pastedAudio));
        Assert.Same(track, VideoTimelineEditing.PlaceClip(project.VideoTracks, track.Kind, track, paste, 24));
        project.AudioTracks.Add(Assert.IsType<AudioTrackProject>(pastedAudio));
        Assert.Equal(193, pastedAudio.StartFrame);
        Assert.True(LinkedAudioVideoEditing.TryMoveVideo(track, paste, 241, project.AudioTracks, 24));
        Assert.Equal(241, pastedAudio.StartFrame);
        Assert.Equal(clipboard.SourceInSeconds, paste.SourceInSeconds);
        Assert.Equal(clipboard.SourceOutSeconds, paste.SourceOutSeconds);
        Assert.Equal(clipboard.ProxyPath, paste.ProxyPath);
        Assert.Equal(clipboard.BlurAmount, paste.BlurAmount);
        Assert.Equal(clipboard.SpeedRamp, paste.SpeedRamp);
        Assert.NotSame(clipboard.SpeedRamp, paste.SpeedRamp);
        Assert.NotSame(clipboard.TransformKeyframes, paste.TransformKeyframes);
        Assert.NotEqual(clipboard.Id, paste.Id);
        Assert.Equal(0.7, pastedAudio.Volume);
        Assert.Null(LinkedAudioVideoEditing.Validate(project.VideoTracks, project.AudioTracks));
        Assert.Null(VideoTimelineEditing.Validate(project.VideoTracks, 24));
        Assert.Equal(2, EditorProjectSerialiser.Deserialise(beforeCut).VideoTracks[0].Clips.Count);
        Assert.Equal(2, EditorProjectSerialiser.Deserialise(beforeCut).AudioTracks.Count);
        Assert.Single(EditorProjectSerialiser.Deserialise(beforeSplit).VideoTracks[0].Clips);
        Assert.Equal(2, EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project)).AudioTracks.Count);
    }

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
