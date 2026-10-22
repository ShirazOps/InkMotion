using Xunit;

namespace InkMotion.Tests;

public sealed class VideoMulticamAndProxyTests
{
    [Fact]
    public void GetVisibleLayers_KeepsAllAnglesBeforeGroupAndOnlyActiveAngleAfterwards()
    {
        var firstTrack = new TimelineTrackProject
        {
            Id = "track-a",
            Kind = TimelineTrackKind.Video,
            Name = "Camera A",
            Clips = [new TimelineClipProject { Id = "a", SourcePath = "a.mp4", SourceDurationSeconds = 10 }],
        };
        var secondTrack = new TimelineTrackProject
        {
            Id = "track-b",
            Kind = TimelineTrackKind.Video,
            Name = "Camera B",
            Clips = [new TimelineClipProject { Id = "b", SourcePath = "b.mp4", SourceDurationSeconds = 10 }],
        };
        var group = new VideoMulticamGroup
        {
            StartFrame = 25,
            Angles = [new VideoMulticamAngle { TrackId = "track-a", Name = "Camera A" }, new VideoMulticamAngle { TrackId = "track-b", Name = "Camera B" }],
            Switches = [new VideoMulticamSwitch(49, "track-b")],
        };
        TimelineTrackProject[] tracks = [firstTrack, secondTrack];

        Assert.Equal(2, VideoTimelineEditing.GetVisibleVideoLayers(tracks, 0.5, 24, [group]).Count);
        Assert.Equal(["a"], VideoTimelineEditing.GetVisibleVideoLayers(tracks, 1, 24, [group]).Select(layer => layer.Clip.Id));
        Assert.Equal(["b"], VideoTimelineEditing.GetVisibleVideoLayers(tracks, 2, 24, [group]).Select(layer => layer.Clip.Id));
    }

    [Fact]
    public void SetAngleAtFrame_ReplacesSwitchAndAvoidsRedundantSwitch()
    {
        var group = new VideoMulticamGroup
        {
            StartFrame = 1,
            Angles = [new VideoMulticamAngle { TrackId = "track-a" }, new VideoMulticamAngle { TrackId = "track-b" }],
        };

        VideoMulticamEditing.SetAngleAtFrame(group, 10, "track-b");
        VideoMulticamEditing.SetAngleAtFrame(group, 10, "track-a");

        Assert.Empty(group.Switches);
        Assert.Equal("track-a", VideoMulticamEditing.GetActiveTrackId(group, 10));
    }

    [Fact]
    public void Split_KeepsTheCameraAngleBoundToItsTrack()
    {
        var track = new TimelineTrackProject
        {
            Id = "track-a",
            Kind = TimelineTrackKind.Video,
            Clips = [new TimelineClipProject { Id = "a", SourcePath = "a.mp4", SourceDurationSeconds = 10 }],
        };
        var group = new VideoMulticamGroup
        {
            StartFrame = 1,
            Angles = [new VideoMulticamAngle { TrackId = "track-a", Name = "Camera A" }, new VideoMulticamAngle { TrackId = "track-b", Name = "Camera B" }],
        };

        TimelineClipProject? right = VideoTimelineEditing.Split(track, track.Clips[0], 25, 24);

        Assert.NotNull(right);
        Assert.Equal("track-a", VideoMulticamEditing.GetActiveTrackId(group, 25));
        Assert.Contains(group.Angles, angle => angle.TrackId == "track-a" && angle.Name == "Camera A");
        Assert.Equal(2, track.Clips.Count);
    }

    [Fact]
    public void ProxyOutputSize_IsEvenAndLimitedToMaximumDimension()
    {
        Assert.Equal((960, 540), VideoProxyGenerator.GetOutputSize(1920, 1080));
        Assert.Equal((640, 360), VideoProxyGenerator.GetOutputSize(640, 360));
        (int width, int height) = VideoProxyGenerator.GetOutputSize(3841, 2161);

        Assert.True(width <= VideoProxyGenerator.MaximumDimension);
        Assert.True(height <= VideoProxyGenerator.MaximumDimension);
        Assert.Equal(0, width % 2);
        Assert.Equal(0, height % 2);
    }

    [Theory]
    [InlineData(24, 48)]
    [InlineData(30, 60)]
    [InlineData(60, 120)]
    [InlineData(120, 240)]
    public void PerformanceProbe_UsesTwoSecondsOfFrames(double frameRate, int expectedFrameCount)
    {
        VideoExportProfile profile = VideoExportProfile.Create(ExportResolution.P1080, frameRate);

        Assert.Equal(expectedFrameCount, VideoExportCapabilities.GetPerformanceFrameCount(profile));
    }

    [Fact]
    public void ProxyAvailability_RejectsProxyWhenTheSourceChanges()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), $"inkmotion-proxy-source-{Guid.NewGuid():N}.bin");
        string proxyPath = Path.Combine(Path.GetTempPath(), $"inkmotion-proxy-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllText(sourcePath, "first source");
            File.WriteAllText(proxyPath, "proxy");
            var source = new FileInfo(sourcePath);
            source.Refresh();
            var clip = new TimelineClipProject
            {
                SourcePath = sourcePath,
                ProxyPath = proxyPath,
                UseProxy = true,
                ProxySourceLength = source.Length,
                ProxySourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
            };

            Assert.True(VideoProxy.IsAvailable(clip));
            Assert.Equal(proxyPath, VideoProxy.GetRenderPath(clip));
            File.WriteAllText(sourcePath, "updated source with different length");
            Assert.False(VideoProxy.IsAvailable(clip));
            Assert.Equal(sourcePath, VideoProxy.GetRenderPath(clip));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(proxyPath);
        }
    }

    [Fact]
    public void TrackPoint_FollowsAUniqueTexturedPatch()
    {
        const int width = 100;
        const int height = 80;
        const int patchSize = 9;
        byte[] template = new byte[patchSize * patchSize];
        byte[] current = Enumerable.Repeat((byte)20, width * height).ToArray();
        for (int y = 0; y < patchSize; y++)
        {
            for (int x = 0; x < patchSize; x++)
            {
                byte value = (byte)(40 + ((x * 17 + y * 23) % 190));
                template[y * patchSize + x] = value;
                current[((47 + y - 4) * width) + 55 + x - 4] = value;
            }
        }

        (int trackedX, int trackedY, double error) = VideoMaskTracking.TrackPoint(
            template, current, width, height, 50, 50, 10);

        Assert.Equal((55, 47), (trackedX, trackedY));
        Assert.Equal(0, error);
    }

    [Fact]
    public void TrackPoint_MatchesThroughUniformBrightnessShift()
    {
        const int width = 100;
        const int height = 80;
        const int patchSize = 9;
        byte[] template = new byte[patchSize * patchSize];
        byte[] current = Enumerable.Repeat((byte)80, width * height).ToArray();
        for (int y = 0; y < patchSize; y++)
        {
            for (int x = 0; x < patchSize; x++)
            {
                byte value = (byte)(30 + ((x * 17 + y * 23) % 120));
                template[y * patchSize + x] = value;
                current[((47 + y - 4) * width) + 55 + x - 4] = (byte)(value + 60);
            }
        }

        (int trackedX, int trackedY, double error) = VideoMaskTracking.TrackPoint(
            template, current, width, height, 50, 50, 10);

        Assert.Equal((55, 47), (trackedX, trackedY));
        Assert.Equal(0, error);
    }

    [Fact]
    public void Serialiser_RoundTripsProxyAndMulticamEdits()
    {
        var project = new EditorProject
        {
            VideoTracks =
            [
                new TimelineTrackProject
                {
                    Id = "track-a",
                    Kind = TimelineTrackKind.Video,
                    Name = "Camera A",
                    Clips = [new TimelineClipProject { Id = "a", SourcePath = @"C:\Media\a.mp4", ProxyPath = @"C:\Proxies\a.mp4", ProxySourceLength = 123, ProxySourceLastWriteUtcTicks = 456, UseProxy = true, SharpenAmount = 2.5, SourceDurationSeconds = 10 }],
                },
                new TimelineTrackProject
                {
                    Id = "track-b",
                    Kind = TimelineTrackKind.Video,
                    Name = "Camera B",
                    Clips = [new TimelineClipProject { Id = "b", SourcePath = @"C:\Media\b.mp4", SourceDurationSeconds = 10 }],
                },
            ],
            VideoMulticamGroups =
            [
                new VideoMulticamGroup
                {
                    Name = "Two cameras",
                    StartFrame = 1,
                    Angles = [new VideoMulticamAngle { TrackId = "track-a", Name = "Camera A" }, new VideoMulticamAngle { TrackId = "track-b", Name = "Camera B" }],
                    Switches = [new VideoMulticamSwitch(25, "track-b")],
                },
            ],
        };

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));

        Assert.True(restored.VideoTracks[0].Clips[0].UseProxy);
        Assert.Equal(@"C:\Proxies\a.mp4", restored.VideoTracks[0].Clips[0].ProxyPath);
        Assert.Equal(123, restored.VideoTracks[0].Clips[0].ProxySourceLength);
        Assert.Equal(456, restored.VideoTracks[0].Clips[0].ProxySourceLastWriteUtcTicks);
        Assert.Equal(2.5, restored.VideoTracks[0].Clips[0].SharpenAmount);
        VideoMulticamGroup restoredGroup = Assert.Single(restored.VideoMulticamGroups);
        Assert.Equal("track-b", VideoMulticamEditing.GetActiveTrackId(restoredGroup, 25));
    }

    [Fact]
    public void Generate_CreatesDecodableProxyWithOriginalDuration()
    {
        if (!VideoExportTests.HasH264Encoder)
        {
            return;
        }

        string sourcePath = Path.Combine(Path.GetTempPath(), $"inkmotion-proxy-source-{Guid.NewGuid():N}.mp4");
        string? proxyPath = null;
        try
        {
            byte[] frame = new byte[320 * 180 * 4];
            for (int index = 0; index < frame.Length; index += 4)
            {
                frame[index] = 220;
                frame[index + 3] = 255;
            }

            using (var writer = new MediaFoundationVideoWriter(
                       sourcePath, 320, 180, 24, VideoExportCodec.H264, 500_000, 0, 0, false))
            {
                for (int index = 0; index < 12; index++)
                {
                    writer.WriteVideoFrame(frame, index * 416_667L, 416_667L);
                }

                writer.Finish();
            }

            proxyPath = VideoProxyGenerator.Generate(sourcePath, 0.5).Path;
            using var proxy = new MediaFoundationVideoReader(proxyPath);

            Assert.Equal(320, proxy.Aperture.Width);
            Assert.Equal(180, proxy.Aperture.Height);
            Assert.True(proxy.TryGetFrame(0.25));
        }
        finally
        {
            File.Delete(sourcePath);
            if (proxyPath is not null)
            {
                File.Delete(proxyPath);
            }
        }
    }
}
