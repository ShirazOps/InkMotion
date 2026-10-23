using Windows.Foundation;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoCropTests
{
    [Fact]
    public void GetSourceRect_CropsEachEdgeByTheRequestedPercentage()
    {
        var clip = new TimelineClipProject
        {
            CropLeftPercent = 10,
            CropTopPercent = 20,
            CropRightPercent = 30,
            CropBottomPercent = 5,
        };

        Assert.Equal(new Rect(20, 40, 120, 150),
            VideoCrop.GetSourceRect(new Rect(0, 0, 200, 200), clip));
    }

    [Fact]
    public void PreviewTransform_RefitsTheCroppedRegionAndAnchorsItsCentre()
    {
        var clip = new TimelineClipProject { CropLeftPercent = 25, CropRightPercent = 25 };

        VideoPreviewTransform transform = VideoCrop.GetPreviewTransform(
            new Size(1280, 720),
            new Size(1920, 1080),
            clip,
            new VideoTransformValue(0.6, 0.4, 1.5, 15));

        Assert.Equal(1.5, transform.Scale, 6);
        Assert.Equal(640, transform.PivotX, 6);
        Assert.Equal(360, transform.PivotY, 6);
        Assert.Equal(128, transform.TranslateX, 6);
        Assert.Equal(-72, transform.TranslateY, 6);
        Assert.Equal(15, transform.RotationDegrees);
    }

    [Theory]
    [InlineData(50, 0, 0, 0)]
    [InlineData(49, 51, 0, 0)]
    [InlineData(0, 0, 50, 0)]
    [InlineData(double.NaN, 0, 0, 0)]
    public void Validate_RejectsInvalidCropEdges(double left, double right, double top, double bottom)
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "video.mp4",
            SourceDurationSeconds = 4,
            CropLeftPercent = left,
            CropRightPercent = right,
            CropTopPercent = top,
            CropBottomPercent = bottom,
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.NotNull(VideoTimelineEditing.Validate([track], 24));
    }

    [Fact]
    public void Validate_AllowsIndependentVideoAndImageCropValues()
    {
        var video = new TimelineClipProject
        {
            SourcePath = "video.mp4",
            SourceDurationSeconds = 4,
            CropLeftPercent = 20,
            CropRightPercent = 10,
        };
        var image = new TimelineClipProject
        {
            SourcePath = "image.png",
            CropTopPercent = 15,
            CropBottomPercent = 25,
        };

        Assert.Null(VideoTimelineEditing.Validate(
        [
            new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [video] },
            new TimelineTrackProject { Kind = TimelineTrackKind.Image, Clips = [image] },
        ], 24));
    }

    [Fact]
    public void Crop_RoundTripsAndClonesWithTheClip()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "image.png",
            CropLeftPercent = 5,
            CropTopPercent = 10,
            CropRightPercent = 15,
            CropBottomPercent = 20,
        };
        var project = new EditorProject
        {
            VideoTracks = [new TimelineTrackProject { Kind = TimelineTrackKind.Image, Clips = [clip] }],
        };

        TimelineClipProject clone = VideoTimelineEditing.Clone(clip);
        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        TimelineClipProject reopened = Assert.Single(Assert.Single(restored.VideoTracks).Clips);

        Assert.Equal((5, 10, 15, 20),
            (clone.CropLeftPercent, clone.CropTopPercent, clone.CropRightPercent, clone.CropBottomPercent));
        Assert.Equal((5, 10, 15, 20),
            (reopened.CropLeftPercent, reopened.CropTopPercent, reopened.CropRightPercent, reopened.CropBottomPercent));
    }

    [Fact]
    public void CropKeyframes_InterpolateValidatePersistAndFollowSplitTrim()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "image.png",
            DurationSeconds = 4,
            StartFrame = 121,
            CropKeyframes =
            [
                new() { FrameOffset = 0, Left = 0, Top = 10, Right = 0, Bottom = 10 },
                new() { FrameOffset = 48, Left = 40, Top = 20, Right = 10, Bottom = 20 },
            ],
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Image, Clips = [clip] };

        Assert.Null(VideoTimelineEditing.Validate([track], 24));
        Assert.Equal(new VideoCropValue(20, 15, 5, 15), VideoCropKeyframes.GetValue(clip, 24));
        Assert.Equal(new Rect(40, 30, 150, 140),
            VideoCrop.GetSourceRect(new Rect(0, 0, 200, 200), VideoCropKeyframes.GetValue(clip, 24)));

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(
            new EditorProject { VideoTracks = [track] }));
        TimelineTrackProject restoredTrack = Assert.Single(restored.VideoTracks);
        TimelineClipProject restoredClip = Assert.Single(restoredTrack.Clips);
        Assert.Equal(new VideoCropValue(20, 15, 5, 15), VideoCropKeyframes.GetValue(restoredClip, 24));

        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(restoredTrack, restoredClip, 145, 24));
        Assert.Equal(0, right.CropKeyframes[0].FrameOffset);
        Assert.Equal(20, right.CropKeyframes[0].Left);
        Assert.True(VideoTimelineEditing.TryTrimStart(restoredTrack, right, 157, 24));
        Assert.Equal(0, right.CropKeyframes[0].FrameOffset);
        Assert.Equal(30, right.CropKeyframes[0].Left, 6);
        Assert.Null(VideoTimelineEditing.Validate([restoredTrack], 24));
    }

    [Fact]
    public void CropKeyframes_RejectInvalidValuesAndMigrateVersion43()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "image.png",
            DurationSeconds = 2,
            CropKeyframes =
            [
                new() { FrameOffset = 0, Left = 50, Right = 49 },
            ],
        };
        Assert.NotNull(VideoTimelineEditing.Validate(
            [new TimelineTrackProject { Kind = TimelineTrackKind.Image, Clips = [clip] }], 24));

        string json = EditorProjectSerialiser.Serialise(new EditorProject { Version = EditorProject.CurrentVersion });
        EditorProject migrated = EditorProjectSerialiser.Deserialise(
            json.Replace($"\"Version\": {EditorProject.CurrentVersion}", "\"Version\": 43", StringComparison.Ordinal));
        Assert.Equal(EditorProject.CurrentVersion, migrated.Version);
    }

    [Fact]
    public void CropKeyframes_RescaleFrameRateAndTrimAtNewEnd()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "image.png",
            DurationSeconds = 2,
            CropKeyframes =
            [
                new() { FrameOffset = 12, Left = 10 },
                new() { FrameOffset = 24, Left = 30 },
            ],
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Image, Clips = [clip] };

        VideoTimelineEditing.RescaleFrameRate([track], 24, 48);
        Assert.Equal(new[] { 24, 48 }, clip.CropKeyframes.Select(key => key.FrameOffset));
        Assert.Null(VideoTimelineEditing.Validate([track], 48));

        Assert.True(VideoTimelineEditing.TrySetStillDuration(track, clip, 0.75, 48));
        Assert.All(clip.CropKeyframes, key => Assert.InRange(key.FrameOffset, 0, 35));
        Assert.Null(VideoTimelineEditing.Validate([track], 48));
    }
}
