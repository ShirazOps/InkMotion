using Xunit;

namespace InkMotion.Tests;

public sealed class VideoBatchRelinkTests
{
    private static readonly string Source = Path.Combine(Path.GetTempPath(), "batch-original.mp4");
    private static readonly string Replacement = Path.Combine(Path.GetTempPath(), "batch-replacement.mp4");

    private static EditorProject Project() => new()
    {
        VideoTracks =
        [
            new()
            {
                Id = "first", Kind = TimelineTrackKind.Video,
                Clips =
                [
                    new() { Id = "a", SourcePath = Source, SourceDurationSeconds = 4, StartFrame = 1 },
                    new()
                    {
                        Id = "b", SourcePath = Source.ToUpperInvariant(), SourceDurationSeconds = 4,
                        SourceInSeconds = 1, SourceOutSeconds = 2, StartFrame = 121,
                        Volume = 0.4, Opacity = 0.6, FadeInSeconds = 0.1, FadeOutSeconds = 0.2, X = 0.1, Scale = 0.8,
                    },
                ],
            },
            new()
            {
                Id = "second", Kind = TimelineTrackKind.Video, IsVisible = false,
                Clips = [new() { Id = "c", SourcePath = Source, SourceDurationSeconds = 4, SourceOutSeconds = 3 }],
            },
            new()
            {
                Id = "other", Kind = TimelineTrackKind.Video, IsLocked = true,
                Clips = [new() { Id = "d", SourcePath = Path.Combine(Path.GetTempPath(), "different", "batch-original.mp4"), SourceDurationSeconds = 4 }],
            },
        ],
    };

    [Fact]
    public void RelinksAllUsesCaseInsensitivelyWhilePreservingPlacementPropertiesAndDuration()
    {
        EditorProject project = Project();
        TimelineClipProject[] originalClips = project.VideoTracks.SelectMany(track => track.Clips).ToArray();
        EditorProject before = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        int count = VideoEditTools.RelinkMatchingSources(project.VideoTracks, project.VideoTracks[0],
            originalClips[0], Replacement, 8);
        Assert.Equal(3, count);
        Assert.Equal(Replacement, originalClips[0].SourcePath);
        Assert.Equal(4, originalClips[0].SourceOutSeconds);
        Assert.Equal(2, originalClips[1].SourceOutSeconds);
        Assert.Equal(3, originalClips[2].SourceOutSeconds);
        Assert.Equal(before.VideoTracks[2].Clips[0].SourcePath, originalClips[3].SourcePath);
        for (int track = 0; track < 2; track++)
        {
            for (int clip = 0; clip < project.VideoTracks[track].Clips.Count; clip++)
            {
                TimelineClipProject previous = before.VideoTracks[track].Clips[clip];
                TimelineClipProject current = project.VideoTracks[track].Clips[clip];
                Assert.Same(originalClips.Single(original => original.Id == current.Id), current);
                Assert.Equal(8, current.SourceDurationSeconds);
                Assert.Equal(previous.Id, current.Id);
                Assert.Equal(previous.StartFrame, current.StartFrame);
                Assert.Equal(previous.SourceInSeconds, current.SourceInSeconds);
                Assert.Equal(previous.Volume, current.Volume);
                Assert.Equal(previous.Opacity, current.Opacity);
                Assert.Equal(previous.FadeInSeconds, current.FadeInSeconds);
                Assert.Equal(previous.FadeOutSeconds, current.FadeOutSeconds);
                Assert.Equal(previous.X, current.X);
                Assert.Equal(previous.Scale, current.Scale);
                Assert.Equal(VideoTimelineEditing.GetDurationSeconds(previous, TimelineTrackKind.Video),
                    VideoTimelineEditing.GetDurationSeconds(current, TimelineTrackKind.Video));
            }
        }

        EditorProject reopened = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(Replacement, reopened.VideoTracks[1].Clips[0].SourcePath);
        Assert.Equal(Source, before.VideoTracks[0].Clips[0].SourcePath);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("too-short")]
    [InlineData("invalid-duration")]
    [InlineData("missing-clip")]
    [InlineData("missing-track")]
    [InlineData("empty-path")]
    public void RejectsEntireBatchBeforeMutation(string reason)
    {
        EditorProject project = Project();
        TimelineTrackProject selectedTrack = project.VideoTracks[0];
        TimelineClipProject selectedClip = selectedTrack.Clips[0];
        double duration = 8;
        if (reason == "locked") project.VideoTracks[1].IsLocked = true;
        if (reason == "too-short")
        {
            selectedClip.SourceOutSeconds = 1;
            duration = 2;
        }
        if (reason == "invalid-duration") duration = double.NaN;
        if (reason == "missing-clip") selectedClip = VideoTimelineEditing.Clone(selectedClip);
        if (reason == "missing-track") selectedTrack = VideoTimelineEditing.Clone(selectedTrack);
        string before = EditorProjectSerialiser.Serialise(project);
        Assert.Throws<InvalidOperationException>(() => VideoEditTools.RelinkMatchingSources(project.VideoTracks,
            selectedTrack, selectedClip, reason == "empty-path" ? "" : Replacement, duration));
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void ImagesRelinkWithoutChangingStillTimingOrOtherMediaKinds()
    {
        string path = Path.ChangeExtension(Source, ".png");
        var first = new TimelineClipProject { SourcePath = path, DurationSeconds = 2, StartFrame = 25 };
        var second = new TimelineClipProject { SourcePath = path, DurationSeconds = 7, StartFrame = 97 };
        var unrelated = new TimelineClipProject { SourcePath = path, SourceDurationSeconds = 9 };
        List<TimelineTrackProject> tracks =
        [
            new() { Kind = TimelineTrackKind.Image, Clips = [first] },
            new() { Kind = TimelineTrackKind.Image, Clips = [second] },
            new() { Kind = TimelineTrackKind.Video, Clips = [unrelated] },
        ];
        string replacement = Path.ChangeExtension(Replacement, ".png");
        Assert.Equal(2, VideoEditTools.RelinkMatchingSources(tracks, tracks[0], first, replacement, 0));
        Assert.Equal(replacement, first.SourcePath);
        Assert.Equal(replacement, second.SourcePath);
        Assert.Equal(path, unrelated.SourcePath);
        Assert.Equal(2, first.DurationSeconds);
        Assert.Equal(7, second.DurationSeconds);
        Assert.Equal(25, first.StartFrame);
        Assert.Equal(97, second.StartFrame);
    }
}
