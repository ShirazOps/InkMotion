using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class CompoundTimelineTests
{
    private const double FrameRate = 24;

    [Fact]
    public void EmbeddedTimeline_RoundTripsAndClonesIndependently()
    {
        var nested = new EditorProject
        {
            VideoTracks = [new()
            {
                Kind = TimelineTrackKind.Text,
                Clips = [new() { Text = "Nested title", DurationSeconds = 2 }],
            }],
            AudioTracks = [new() { AudioPath = @"C:\media\sound.wav" }],
        };
        var project = new EditorProject { VideoTracks = [CompoundTrack(nested, duration: 2)] };

        EditorProject restored = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        EditorProject copy = CompoundTimelines.Clone(restored);
        EditorProject restoredChild = restored.VideoTracks[0].Clips[0].NestedProject!;
        EditorProject copiedChild = copy.VideoTracks[0].Clips[0].NestedProject!;
        copiedChild.VideoTracks[0].Clips[0].Text = "Changed";
        copiedChild.AudioTracks[0].AudioPath = @"C:\media\other.wav";

        Assert.Equal("Nested title", restoredChild.VideoTracks[0].Clips[0].Text);
        Assert.Equal(@"C:\media\sound.wav", restoredChild.AudioTracks[0].AudioPath);
        Assert.Equal("Changed", copiedChild.VideoTracks[0].Clips[0].Text);
        Assert.Equal(EditorProject.CurrentVersion, EditorProjectSerialiser.Deserialise("""{"Version":86}""").Version);
    }

    [Fact]
    public void Validation_RejectsCyclesSharedProjectsAndExcessiveDepth()
    {
        var cyclic = new EditorProject();
        cyclic.VideoTracks = [CompoundTrack(cyclic)];
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(cyclic));

        var shared = new EditorProject();
        var parent = new EditorProject
        {
            VideoTracks = [CompoundTrack(shared), CompoundTrack(shared)],
        };
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(parent));

        var root = new EditorProject();
        EditorProject current = root;
        for (int level = 0; level <= CompoundTimelines.MaximumDepth; level++)
        {
            var child = new EditorProject();
            current.VideoTracks = [CompoundTrack(child)];
            current = child;
        }
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(root));
    }

    [Fact]
    public void CreateTrack_PreservesGapsTimingAndParentSettings()
    {
        var track = new TimelineTrackProject
        {
            Kind = TimelineTrackKind.Text,
            Name = "Titles",
            IsVisible = false,
            Clips =
            [
                new() { Text = "First", StartFrame = 49, DurationSeconds = 2 },
                new() { Text = "Second", StartFrame = 121, DurationSeconds = 1 },
            ],
        };
        var project = new EditorProject { VideoTracks = [track], CanvasSize = new(1920, 1080) };

        string? error = CompoundTimelines.TryCreateTrack(project, track.Id, 0, out TimelineTrackProject? compound);

        Assert.Null(error);
        Assert.NotNull(compound);
        Assert.Equal(track.Id, compound.Id);
        Assert.False(compound.IsVisible);
        TimelineClipProject clip = Assert.Single(compound.Clips);
        Assert.Equal(49, clip.StartFrame);
        Assert.Equal(4, clip.DurationSeconds, 6);
        TimelineTrackProject inner = Assert.Single(clip.NestedProject!.VideoTracks);
        Assert.True(inner.IsVisible);
        Assert.Equal([1, 73], inner.Clips.Select(item => item.StartFrame));
        Assert.Equal(project.CanvasSize, clip.NestedProject.CanvasSize);
        Assert.Equal(49, track.Clips[0].StartFrame);
        Assert.Null(VideoTimelineEditing.Validate([compound], FrameRate));
    }

    [Fact]
    public void CreateTrack_RefusesLinkedAudioWithoutChangingTheProject()
    {
        var track = new TimelineTrackProject
        {
            Kind = TimelineTrackKind.Video,
            Clips = [new() { SourcePath = @"C:\media\video.mp4", SourceDurationSeconds = 2 }],
        };
        var project = new EditorProject
        {
            VideoTracks = [track],
            AudioTracks = [new() { AudioPath = @"C:\media\sound.wav", LinkedVideoClipId = track.Clips[0].Id }],
        };

        string? error = CompoundTimelines.TryCreateTrack(project, track.Id, 0, out TimelineTrackProject? compound);

        Assert.Contains("Unlink audio", error);
        Assert.Null(compound);
        Assert.Same(track, Assert.Single(project.VideoTracks));
    }

    [Fact]
    public void SplittingAndTrimming_PreserveTheNestedSourceRange()
    {
        TimelineTrackProject track = CompoundTrack(new(), startFrame: 25, sourceIn: 0.5, duration: 2.5, sourceDuration: 3);
        TimelineClipProject left = Assert.Single(track.Clips);

        TimelineClipProject? right = VideoTimelineEditing.Split(track, left, 49, FrameRate);

        Assert.NotNull(right);
        Assert.Equal(1, left.DurationSeconds, 6);
        Assert.Equal(0.5, left.SourceInSeconds, 6);
        Assert.Equal(1.5, right.DurationSeconds, 6);
        Assert.Equal(1.5, right.SourceInSeconds, 6);
        Assert.NotSame(left.NestedProject, right.NestedProject);
        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 61, FrameRate));
        Assert.Equal(2, right.SourceInSeconds, 6);
        Assert.Equal(1, right.DurationSeconds, 6);
        Assert.Null(VideoTimelineEditing.Validate([track], FrameRate));
    }

    [Fact]
    public void EdgeEditing_RespectsSourceBoundsAndOneFrameMinimum()
    {
        TimelineTrackProject track = CompoundTrack(new(), startFrame: 25, sourceIn: 1, duration: 1, sourceDuration: 3);
        TimelineClipProject clip = Assert.Single(track.Clips);
        Assert.True(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.Start, 1, FrameRate));
        Assert.Equal(0, clip.SourceInSeconds, 6);
        Assert.Equal(2, clip.DurationSeconds, 6);
        Assert.False(VideoEditTools.TryTrimEdge(track, clip, TimelineTrimEdge.End, 97, FrameRate));
        Assert.False(VideoTimelineEditing.TrySetStillDuration(track, clip, 1 / 48d, FrameRate));
        Assert.False(VideoEditTools.Relink(track, clip, @"C:\media\image.png", 0));
        Assert.Null(clip.SourcePath);

        clip.DurationSeconds = 1 + 1 / 48d;
        Assert.Null(VideoTimelineEditing.Split(track, clip, 25, FrameRate));
        Assert.False(VideoTimelineEditing.TryTrimStart(track, clip, 25, FrameRate));
        Assert.True(VideoTimelineEditing.TryTrimEnd(track, clip, 25, FrameRate));
        Assert.Equal(1, clip.DurationSeconds, 6);
    }

    [Fact]
    public void OutputProtection_IncludesSourcesInsideNestedTimelines()
    {
        var nested = new EditorProject
        {
            AudioTracks = [new() { AudioPath = @"C:\media\sound.wav" }],
            VideoTracks = [new()
            {
                Kind = TimelineTrackKind.Video,
                Clips = [new()
                {
                    SourcePath = @"C:\media\video.mp4",
                    ProxyPath = @"C:\media\proxy.mp4",
                    ColourLutPath = @"C:\media\look.cube",
                    SourceDurationSeconds = 1,
                }],
            }],
        };
        var project = new EditorProject { VideoTracks = [CompoundTrack(nested)] };

        foreach (string path in new[] { @"C:\media\sound.wav", @"C:\media\video.mp4",
                     @"C:\media\proxy.mp4", @"C:\media\look.cube" })
        {
            Assert.Throws<InvalidOperationException>(() => ExportPlanning.ValidateOutputPath(project, path));
        }
        ExportPlanning.ValidateOutputPath(project, @"C:\media\output.mp4");
    }

    [Fact]
    public void NestedRender_PreservesTransparencyOrderingAndSourceTime()
    {
        EditorProject blue = InkProject(0xFF0000FF, fullWidth: true);
        EditorProject red = InkProject(0xFFFF0000, fullWidth: false);
        red.Frames.Insert(0, new EditorFrame { Number = 1 });
        red.Frames[1].Number = 2;
        TimelineTrackProject top = CompoundTrack(red, sourceIn: 1 / FrameRate, duration: 1, sourceDuration: 2);
        var project = new EditorProject { VideoTracks = [top, CompoundTrack(blue)] };
        var profile = VideoExportProfile.Create(64, 64, FrameRate);
        var request = new VideoExportRequest(project, profile, VideoExportCodec.H264, string.Empty, 64);
        var warnings = new List<string>();
        using var device = new CanvasDevice(true);
        using var export = new VideoExporter.FrameRenderer(device, request, warnings);
        using var preview = new VideoExporter.FrameRenderer(device, request, warnings, previewSdr: true);

        byte[] rendered = export.RenderSurface(0, transparent: true).GetPixelBytes();
        byte[] previewed = preview.RenderSurface(0, transparent: true).GetPixelBytes();

        Assert.Equal(rendered, previewed);
        AssertPixel(rendered, 16, 32, red: 255, blue: 0);
        AssertPixel(rendered, 48, 32, red: 0, blue: 255);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CreateTrack_PreservesRenderedOutputAtTheSameTimelineTime()
    {
        var title = new TimelineTrackProject
        {
            Kind = TimelineTrackKind.Text,
            Name = "Titles",
            Clips = [new() { Text = "Nested", StartFrame = 49, DurationSeconds = 2 }],
        };
        var original = new EditorProject { VideoTracks = [title] };
        Assert.Null(CompoundTimelines.TryCreateTrack(original, title.Id, 0, out TimelineTrackProject? compound));
        var grouped = new EditorProject { VideoTracks = [compound!] };
        var profile = VideoExportProfile.Create(128, 72, FrameRate);
        using var device = new CanvasDevice(true);
        using var before = new VideoExporter.FrameRenderer(device,
            new(original, profile, VideoExportCodec.H264, string.Empty, 72), []);
        using var after = new VideoExporter.FrameRenderer(device,
            new(grouped, profile, VideoExportCodec.H264, string.Empty, 72), []);

        foreach (double seconds in new[] { 1.5, 2.5, 4.5 })
        {
            byte[] expected = before.RenderSurface(seconds, transparent: true).GetPixelBytes();
            byte[] actual = after.RenderSurface(seconds, transparent: true).GetPixelBytes();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void NestedHdrRender_UsesFloatingPointComposition()
    {
        var colour = new ProjectColourSettings();
        EditorProject nested = InkProject(0xFFFF0000, fullWidth: true);
        nested.Colour = colour;
        var project = new EditorProject { Colour = colour, VideoTracks = [CompoundTrack(nested)] };
        var request = new VideoExportRequest(project, VideoExportProfile.Create(64, 64, FrameRate),
            VideoExportCodec.Hevc, string.Empty, 64);
        using var device = new CanvasDevice(true);
        using var renderer = new VideoExporter.FrameRenderer(device, request, []);
        using var preview = new VideoExporter.FrameRenderer(device, request, [], previewSdr: true);

        byte[] bytes = renderer.RenderSurface(0, transparent: true).GetPixelBytes(32, 32, 1, 1);
        Half[] pixel = MemoryMarshal.Cast<byte, Half>(bytes.AsSpan()).ToArray();

        Assert.Equal(4, pixel.Length);
        Assert.True((float)pixel[0] > 0.5f);
        Assert.True((float)pixel[3] > 0.9f);
        Assert.True(renderer.IsHdr);
        Assert.False(preview.IsHdr);
        Assert.Equal(64 * 64 * 4, preview.Render(0).Length);
    }

    [Fact]
    public void NestedAudio_SeeksAndRoutesConsistentlyWithExport()
    {
        string input = Path.Combine(Path.GetTempPath(), $"inkmotion-compound-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
            {
                for (int frame = 0; frame < 96000; frame++)
                {
                    writer.WriteSample(frame < 48000 ? 0.1f : 0.5f);
                }
            }

            var nested = new EditorProject
            {
                AudioMixLayout = AudioChannelLayout.Mono,
                AudioTracks = [new() { AudioPath = input }],
            };
            TimelineTrackProject track = CompoundTrack(nested, startFrame: 25, sourceIn: 1, duration: 0.5, sourceDuration: 2);
            TimelineClipProject clip = track.Clips[0];
            clip.Volume = 0.5;
            clip.FadeInSeconds = 0.25;
            var bus = new AudioBusProject { GainDecibels = -6 };
            track.AudioBusId = bus.Id;
            var project = new EditorProject
            {
                AudioMixLayout = AudioChannelLayout.Mono,
                AudioBuses = [bus],
                VideoTracks = [track],
            };
            using var mixer = new ExportAudioMixer(project, 48000);
            var exported = new float[48000 * 2];
            Assert.Equal(exported.Length, mixer.Read(exported, 0, exported.Length));
            Assert.InRange(Math.Abs(exported[12000]), 0, 1e-6);
            Assert.InRange(exported[60000], 0.11, 0.14);
            Assert.InRange(Math.Abs(exported[78000]), 0, 1e-6);

            using CompoundAudioPreview? preview = CompoundAudioPreview.Prepare(clip, 0.25, CancellationToken.None);
            Assert.NotNull(preview);
            var samples = new float[1024];
            Assert.Equal(samples.Length, preview.Read(samples, 0, samples.Length));
            double busGain = AudioBuses.GetLinearGain(bus);
            for (int index = 0; index < samples.Length; index++)
            {
                Assert.InRange(Math.Abs(samples[index] * busGain - exported[60000 + index]), 0, 1e-4);
            }
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public void NestedAudio_ReportsMissingSources()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"inkmotion-missing-{Guid.NewGuid():N}.wav");
        TimelineClipProject clip = CompoundTrack(new EditorProject
        {
            AudioTracks = [new() { AudioPath = missing }],
        }).Clips[0];

        Assert.Throws<InvalidOperationException>(() =>
            CompoundAudioPreview.Prepare(clip, 0, CancellationToken.None));
    }

    private static TimelineTrackProject CompoundTrack(EditorProject nested, int startFrame = 1,
        double sourceIn = 0, double duration = 1, double? sourceDuration = null) => new()
    {
        Kind = TimelineTrackKind.Compound,
        Clips = [new()
        {
            StartFrame = startFrame,
            NestedName = "Nested",
            NestedProject = nested,
            SourceInSeconds = sourceIn,
            DurationSeconds = duration,
            SourceDurationSeconds = sourceDuration ?? sourceIn + duration,
        }],
    };

    private static EditorProject InkProject(uint colour, bool fullWidth) => new()
    {
        Frames = [new()
        {
            Strokes = [InkStrokeEditing.CreateShape(InkShape.Rectangle, 0, 0,
                fullWidth ? 1 : 0.5, 1, 1, colour, filled: true)],
        }],
    };

    private static void AssertPixel(byte[] pixels, int x, int y, int red, int blue)
    {
        int offset = (y * 64 + x) * 4;
        Assert.InRange(pixels[offset], blue - 2, blue + 2);
        Assert.InRange(pixels[offset + 2], red - 2, red + 2);
        Assert.InRange(pixels[offset + 3], 253, 255);
    }
}
