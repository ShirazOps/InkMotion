using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoSpillSuppressionTests
{
    private static byte[] Render(uint key, double amount, byte r, byte g, byte b, byte alpha = 255,
        double tolerance = 0, bool feather = false, int frameOffset = 0,
        List<VideoChromaKeySpillKeyframe>? spillKeyframes = null)
    {
        using var device = new CanvasDevice(true);
        byte[] pixels = Enumerable.Range(0, 16 * 16).SelectMany(_ => new[]
        {
            (byte)(b * alpha / 255), (byte)(g * alpha / 255), (byte)(r * alpha / 255), alpha,
        }).ToArray();
        using var source = CanvasBitmap.CreateFromBytes(device, pixels, 16, 16,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var target = new CanvasRenderTarget(device, 16, 16, 96);
        using var effect = new VideoChromaKeyEffect();
        var clip = new TimelineClipProject
        {
            ChromaKeyEnabled = true, ChromaKeyColour = key, ChromaKeyTolerance = tolerance,
            ChromaKeyFeather = feather, ChromaKeySpillSuppression = amount,
            ChromaKeySpillKeyframes = spillKeyframes ?? [],
        };
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            TimelineCompositor.DrawFitted(session, source, source.Bounds, 16, 16, 1, 0.5, 0.5, 1,
                colourGrade: clip, chromaKeyEffect: effect,
                animatedChromaKeySpillSuppression: VideoChromaKeySpillKeyframes.GetValue(clip, frameOffset));
        }
        return target.GetPixelBytes().Skip((8 * 16 + 8) * 4).Take(4).ToArray();
    }

    [Theory]
    [InlineData(0xFF00FF00u, 80, 200, 120, 80, 120, 120)]
    [InlineData(0xFF0000FFu, 80, 120, 200, 80, 120, 120)]
    [InlineData(0xFFFF00FFu, 200, 80, 160, 80, 80, 80)]
    [InlineData(0xFF00FF00u, 200, 80, 120, 200, 80, 120)]
    [InlineData(0xFF0000FFu, 120, 200, 80, 120, 200, 80)]
    [InlineData(0xFFFF00FFu, 80, 200, 120, 80, 200, 120)]
    public void FullStrengthCapsOnlyExcessKeyChannels(uint key, byte r, byte g, byte b,
        byte expectedR, byte expectedG, byte expectedB)
    {
        byte[] output = Render(key, 1, r, g, b);
        Assert.InRange(output[0], expectedB - 2, expectedB + 2);
        Assert.InRange(output[1], expectedG - 2, expectedG + 2);
        Assert.InRange(output[2], expectedR - 2, expectedR + 2);
        Assert.Equal(255, output[3]);
    }

    [Theory]
    [InlineData(0, 128)]
    [InlineData(0.5, 128)]
    [InlineData(1, 128)]
    [InlineData(1, 32)]
    [InlineData(1, 0)]
    public void StrengthInterpolatesAndPreservesPremultipliedAlpha(double amount, byte alpha)
    {
        byte[] output = Render(0xFF00FF00, amount, 80, 200, 120, alpha);
        Assert.InRange(output[0], 120 * alpha / 255 - 2, 120 * alpha / 255 + 2);
        Assert.InRange(output[1], (200 - 80 * amount) * alpha / 255 - 2,
            (200 - 80 * amount) * alpha / 255 + 2);
        Assert.InRange(output[2], 80 * alpha / 255 - 2, 80 * alpha / 255 + 2);
        Assert.Equal(alpha, output[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyedAndFeatheredAlphaIsUnchanged(bool feather)
    {
        foreach (byte red in new byte[] { 0, 40, 80, 160 })
        {
            byte[] original = Render(0xFF00FF00, 0, red, 255, 0, tolerance: 0.3, feather: feather);
            byte[] suppressed = Render(0xFF00FF00, 1, red, 255, 0, tolerance: 0.3, feather: feather);
            Assert.Equal(original[3], suppressed[3]);
            Assert.All(suppressed.Take(3), channel => Assert.True(channel <= suppressed[3]));
            if (red == 0)
            {
                Assert.Equal(new byte[4], suppressed);
            }
        }
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidAmountsAreRejectedEvenWhenKeyingIsDisabled(double amount)
    {
        var clip = new TimelineClipProject { SourcePath = "scene.mp4", SourceDurationSeconds = 2,
            ChromaKeySpillSuppression = amount };
        Assert.NotNull(VideoChromaKey.Validate(clip, TimelineTrackKind.Video));
    }

    [Fact]
    public void AnimatedSpillKeyframes_InterpolateAndChangeRenderedSuppression()
    {
        List<VideoChromaKeySpillKeyframe> keys =
        [
            new() { FrameOffset = 0, Amount = 0 },
            new() { FrameOffset = 24, Amount = 1 },
        ];

        Assert.Equal(0.5, VideoChromaKeySpillKeyframes.GetValue(
            new TimelineClipProject { ChromaKeySpillKeyframes = keys }, 12));
        byte[] unsuppressed = Render(0xFF00FF00, 0, 80, 200, 120, tolerance: 0, spillKeyframes: keys);
        byte[] halfSuppressed = Render(0xFF00FF00, 0, 80, 200, 120,
            tolerance: 0, frameOffset: 12, spillKeyframes: keys);
        byte[] fullySuppressed = Render(0xFF00FF00, 0, 80, 200, 120,
            tolerance: 0, frameOffset: 24, spillKeyframes: keys);

        Assert.True(unsuppressed[1] > halfSuppressed[1]);
        Assert.True(halfSuppressed[1] > fullySuppressed[1]);
        Assert.Equal(unsuppressed[3], halfSuppressed[3]);
        Assert.Equal(unsuppressed[3], fullySuppressed[3]);
    }

    [Fact]
    public void SpillKeyframes_PersistAndFollowEditsAndMigration()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 4,
            ChromaKeySpillSuppression = 0.1,
            ChromaKeySpillKeyframes =
            [
                new() { FrameOffset = 0, Amount = 0 },
                new() { FrameOffset = 48, Amount = 1 },
            ],
        };
        TimelineTrackProject track = new() { Kind = TimelineTrackKind.Video, Clips = [clip] };

        Assert.Equal(0.5, VideoChromaKeySpillKeyframes.GetValue(clip, 24), 6);
        Assert.True(VideoChromaKeySpillKeyframes.SetValueAtTime(clip, track.Kind, 1, 24, 0.75));
        Assert.Equal(0.75, VideoChromaKeySpillKeyframes.GetValue(clip, 24));
        TimelineClipProject restored = Assert.Single(Assert.Single(
            EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(
                new EditorProject { VideoTracks = [track] })).VideoTracks).Clips);
        Assert.Equal(clip.ChromaKeySpillKeyframes, restored.ChromaKeySpillKeyframes);
        Assert.Equal(clip.ChromaKeySpillKeyframes.Select(key => (key.FrameOffset, key.Amount)),
            VideoTimelineEditing.Clone(clip).ChromaKeySpillKeyframes.Select(key => (key.FrameOffset, key.Amount)));

        TimelineClipProject right = Assert.IsType<TimelineClipProject>(
            VideoTimelineEditing.Split(track, clip, 25, 24));
        Assert.Equal(0, right.ChromaKeySpillKeyframes[0].FrameOffset);
        Assert.Equal(0.75, right.ChromaKeySpillKeyframes[0].Amount, 6);
        Assert.True(VideoTimelineEditing.TryTrimStart(track, right, 37, 24));
        Assert.Equal(0, right.ChromaKeySpillKeyframes[0].FrameOffset);
        Assert.Equal(0.875, right.ChromaKeySpillKeyframes[0].Amount, 6);
        VideoTimelineEditing.RescaleFrameRate([track], 24, 48);
        Assert.Null(VideoTimelineEditing.Validate([track], 48));

        EditorProject legacy = EditorProjectSerialiser.Deserialise(
            """{"Version":51,"VideoTracks":[{"Kind":"Video","Clips":[{"SourcePath":"scene.mp4","SourceDurationSeconds":2,"ChromaKeySpillSuppression":0.65}]}],"Frames":[{"Number":1}],"SelectedFrameIndex":0}""");
        TimelineClipProject migrated = Assert.Single(Assert.Single(legacy.VideoTracks).Clips);
        Assert.Equal(EditorProject.CurrentVersion, legacy.Version);
        Assert.Equal(0.65, migrated.ChromaKeySpillSuppression);
        Assert.Empty(migrated.ChromaKeySpillKeyframes);
    }

    [Fact]
    public void RemovingLastSpillKeyframe_BakesItsValueIntoStaticSuppression()
    {
        TimelineClipProject clip = new()
        {
            SourcePath = "scene.mp4",
            SourceDurationSeconds = 2,
            ChromaKeySpillSuppression = 0.1,
            ChromaKeySpillKeyframes = [new() { FrameOffset = 0, Amount = 0.8 }],
        };

        Assert.True(VideoChromaKeySpillKeyframes.TryRemoveAtFrame(clip, 0));
        Assert.Empty(clip.ChromaKeySpillKeyframes);
        Assert.Equal(0.8, clip.ChromaKeySpillSuppression);
        Assert.Equal(0.8, VideoChromaKeySpillKeyframes.GetValue(clip, 12));
    }

    [Fact]
    public void AnimatedSpillKeyframes_PreserveOverwriteAndStillDuration()
    {
        TimelineClipProject original = new()
        {
            Id = "original",
            SourcePath = "old.mp4",
            SourceDurationSeconds = 6,
            ChromaKeySpillKeyframes =
            [
                new() { FrameOffset = 0, Amount = 0 },
                new() { FrameOffset = 143, Amount = 0.8 },
            ],
        };
        TimelineTrackProject videoTrack = new() { Kind = TimelineTrackKind.Video, Clips = [original] };
        TimelineClipProject replacement = new() { Id = "replacement", SourcePath = "new.mp4", SourceDurationSeconds = 2 };
        Assert.True(VideoTimelinePlacement.TryPlace(
            videoTrack, replacement, 49, 24, VideoClipPlacementMode.Overwrite));
        TimelineClipProject[] remainders = videoTrack.Clips.Where(clip => clip.Id != replacement.Id).ToArray();
        Assert.Equal(new[] { 0, 47 }, remainders[0].ChromaKeySpillKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(new[] { 0, 47 }, remainders[1].ChromaKeySpillKeyframes.Select(key => key.FrameOffset));
        Assert.Equal(0.8 * 47 / 143, remainders[0].ChromaKeySpillKeyframes[^1].Amount, 6);
        Assert.Equal(0.8 * 96 / 143, remainders[1].ChromaKeySpillKeyframes[0].Amount, 6);
        Assert.Null(VideoTimelineEditing.Validate([videoTrack], 24));

        TimelineClipProject image = new()
        {
            SourcePath = "image.png",
            DurationSeconds = 4,
            ChromaKeySpillKeyframes =
            [
                new() { FrameOffset = 0, Amount = 0 },
                new() { FrameOffset = 72, Amount = 0.9 },
            ],
        };
        TimelineTrackProject imageTrack = new() { Kind = TimelineTrackKind.Image, Clips = [image] };
        Assert.True(VideoTimelineEditing.TrySetStillDuration(imageTrack, image, 2, 24));
        Assert.Equal(47, image.ChromaKeySpillKeyframes[^1].FrameOffset);
        Assert.Equal(0.9 * 47 / 72, image.ChromaKeySpillKeyframes[^1].Amount, 6);
        Assert.Null(VideoTimelineEditing.Validate([imageTrack], 24));
    }

    [Fact]
    public void SettingsMigrateRoundTripCloneSplitAndProtectJoinCompatibility()
    {
        EditorProject old = EditorProjectSerialiser.Deserialise(
            """{"Version":41,"VideoTracks":[{"Kind":"Video","Clips":[{"SourcePath":"scene.mp4","SourceDurationSeconds":2}]}],"Frames":[{"Number":1}],"SelectedFrameIndex":0}""");
        Assert.Equal(EditorProject.CurrentVersion, old.Version);
        Assert.Equal(0, old.VideoTracks[0].Clips[0].ChromaKeySpillSuppression);
        var clip = new TimelineClipProject
        {
            SourcePath = "scene.mp4", SourceDurationSeconds = 2, SourceOutSeconds = 1,
            ChromaKeyEnabled = true, ChromaKeySpillSuppression = 0.75,
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] };
        var project = new EditorProject { VideoTracks = [track] };
        var saved = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
        Assert.Equal(0.75, saved.VideoTracks[0].Clips[0].ChromaKeySpillSuppression);
        Assert.Equal(0.75, VideoTimelineEditing.Clone(clip).ChromaKeySpillSuppression);
        var right = VideoTimelineEditing.Clone(clip);
        right.Id = "right";
        right.StartFrame = 25;
        right.SourceInSeconds = 1;
        right.SourceOutSeconds = 2;
        right.ChromaKeySpillSuppression = 0.5;
        track.Clips.Add(right);
        Assert.Throws<InvalidOperationException>(() => VideoClipJoining.Join(track, clip, right, 24));
        right.ChromaKeySpillSuppression = 0.75;
        var joined = VideoClipJoining.Join(track, clip, right, 24);
        Assert.Equal(0.75, joined.ChromaKeySpillSuppression);
        track.Clips = [joined];
        Assert.Equal(0.75, VideoTimelineEditing.Split(track, joined, 13, 24)!.ChromaKeySpillSuppression);
    }

    [Fact]
    public void EncodedVideoRetainsSuppressionAndZeroStrengthKeepsSourceColour()
    {
        if (!VideoExportTests.HasH264Encoder)
        {
            return;
        }
        string source = Path.Combine(Path.GetTempPath(), $"inkmotion-spill-source-{Guid.NewGuid():N}.mp4");
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-spill-output-{Guid.NewGuid():N}.mp4");
        try
        {
            var profile = VideoExportProfile.Create(ExportResolution.P720, 24);
            var background = new EditorProject
            {
                Frames = [new()
                {
                    IsKeyframe = true, ExposureFrames = 24,
                    Strokes =
                    [
                        InkStrokeEditing.CreateShape(InkShape.Rectangle, 0, 0, 1, 1, 4, 0xFF50C878, true),
                        InkStrokeEditing.CreateShape(InkShape.Rectangle, 0, 0, 0.25, 1, 4, 0xFF00FF00, true),
                    ],
                }],
            };
            VideoExporter.Export(new(background, profile, VideoExportCodec.H264, source, 400, PreferHardware: false),
                null, CancellationToken.None);
            using (var sourceReader = new MediaFoundationVideoReader(source))
            {
                Assert.True(sourceReader.TryGetFrame(0.2));
                int sample = (sourceReader.Width / 2 + sourceReader.Height / 2 * sourceReader.Width) * 4;
                Assert.InRange(sourceReader.Pixels[sample + 1], 185, 210);
            }
            var clip = new TimelineClipProject
            {
                SourcePath = source, SourceDurationSeconds = 1, SourceOutSeconds = 0.5,
                ChromaKeyEnabled = true, ChromaKeyTolerance = 0.1, ChromaKeyFeather = false,
            };
            var suppressed = VideoTimelineEditing.Clone(clip);
            suppressed.Id = "suppressed";
            suppressed.StartFrame = 13;
            suppressed.ChromaKeyEnabled = true;
            suppressed.ChromaKeySpillKeyframes =
            [
                new() { FrameOffset = 0, Amount = 0 },
                new() { FrameOffset = 11, Amount = 1 },
            ];
            var project = new EditorProject
            {
                VideoTracks = [new() { Kind = TimelineTrackKind.Video, Clips = [clip, suppressed] }],
            };
            project = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
            var result = VideoExporter.Export(new(project, profile, VideoExportCodec.H264, output, 400, PreferHardware: false),
                null, CancellationToken.None);
            Assert.True(result.Warnings.Count == 0, string.Join("; ", result.Warnings));
            using var reader = new MediaFoundationVideoReader(output);
            Assert.True(reader.TryGetFrame(0.2));
            int centre = (reader.Width / 2 + reader.Height / 2 * reader.Width) * 4;
            byte[] original = reader.Pixels.Skip(centre).Take(3).ToArray();
            Assert.InRange(original[1], 185, 210);
            int keyed = (reader.Width / 10 + reader.Height / 2 * reader.Width) * 4;
            Assert.All(reader.Pixels.Skip(keyed).Take(3), channel => Assert.InRange(channel, 0, 20));
            Assert.True(reader.TryGetFrame(0.7));
            byte[] animated = reader.Pixels.Skip(centre).Take(3).ToArray();
            Assert.InRange(animated[1], 145, 175);
            Assert.InRange(Math.Abs(animated[0] - original[0]), 0, 10);
            Assert.InRange(Math.Abs(animated[2] - original[2]), 0, 10);
            Assert.All(reader.Pixels.Skip(keyed).Take(3), channel => Assert.InRange(channel, 0, 20));
            Assert.True(reader.TryGetFrame(0.96));
            byte[] corrected = reader.Pixels.Skip(centre).Take(3).ToArray();
            Assert.InRange(corrected[1], 105, 135);
            Assert.InRange(Math.Abs(corrected[0] - original[0]), 0, 10);
            Assert.InRange(Math.Abs(corrected[2] - original[2]), 0, 10);
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }
}
