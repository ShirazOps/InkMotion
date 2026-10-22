using Xunit;

namespace InkMotion.Tests;

public sealed class VideoRollingShutterTests
{
    [Fact]
    public void EstimateRowBandMotionFindsDifferentPerBandTranslation()
    {
        const int width = VideoRollingShutter.AnalysisWidth;
        const int height = VideoRollingShutter.AnalysisHeight;
        int[] dx = [-2, -1, 0, 0, 1, 2];
        int[] dy = [1, 0, -1, 1, 0, -1];
        byte[] previous = Enumerable.Range(0, width * height)
            .Select(index => (byte)((index * 73 + index / width * 29 + index % width * 11) % 251))
            .ToArray();
        byte[] current = new byte[previous.Length];
        for (int y = 0; y < height; y++)
        {
            int band = y / (height / VideoRollingShutter.RowBands);
            for (int x = 0; x < width; x++)
            {
                int sourceX = x - dx[band];
                int sourceY = y - dy[band];
                current[y * width + x] = (uint)sourceX < width && (uint)sourceY < height
                    ? previous[sourceY * width + sourceX]
                    : (byte)0;
            }
        }

        IReadOnlyList<(int X, int Y)> motion =
            VideoRollingShutter.EstimateRowBandMotion(previous, current, width, height);
        Assert.Equal(dx, motion.Select(value => value.X));
        Assert.Equal(dy, motion.Select(value => value.Y));
    }

    [Fact]
    public void RowWarpAppliesInterpolatedInverseDisplacementsAndTransparentEdges()
    {
        const int width = 24;
        const int height = 12;
        byte[] source = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                source[offset] = (byte)x;
                source[offset + 1] = (byte)(y * 10);
                source[offset + 2] = (byte)(x + y);
                source[offset + 3] = 255;
            }
        }
        var offsets = Enumerable.Range(0, VideoRollingShutter.RowBands)
            .Select(_ => new VideoRollingShutterOffset { X = 3d / width }).ToArray();
        byte[] output = new byte[source.Length];
        VideoRollingShutter.WarpBgra(source, output, width, height, offsets);
        int shifted = (5 * width + 12) * 4;
        Assert.Equal(9, output[shifted]);
        Assert.Equal(50, output[shifted + 1]);
        Assert.Equal(255, output[shifted + 3]);
        int transparent = (5 * width + 1) * 4;
        Assert.Equal(new byte[4], output.Skip(transparent).Take(4));
        VideoRollingShutter.WarpBgra(source, output, width, height,
            Enumerable.Range(0, VideoRollingShutter.RowBands).Select(_ => new VideoRollingShutterOffset()).ToArray());
        Assert.Equal(source, output);
    }

    [Fact]
    public void ProfilesInterpolateBySourceTimeAndCloneIndependently()
    {
        List<VideoRollingShutterKeyframe> profile =
        [
            new()
            {
                SourceSeconds = 1,
                Rows = Enumerable.Range(0, VideoRollingShutter.RowBands)
                    .Select(_ => new VideoRollingShutterOffset { X = 0, Y = 0 }).ToList(),
            },
            new()
            {
                SourceSeconds = 3,
                Rows = Enumerable.Range(0, VideoRollingShutter.RowBands)
                    .Select(_ => new VideoRollingShutterOffset { X = 0.2, Y = -0.2 }).ToList(),
            },
        ];
        VideoRollingShutterOffset middle = VideoRollingShutter.GetOffsets(profile, 2)[0];
        Assert.Equal(0.1, middle.X);
        Assert.Equal(-0.1, middle.Y);
        List<VideoRollingShutterKeyframe> clone = VideoRollingShutter.CloneProfile(profile);
        clone[0].Rows[0].X = 0.25;
        Assert.Equal(0, profile[0].Rows[0].X);
        VideoRollingShutter.ValidateProfile(profile);
        Assert.Throws<InvalidDataException>(() => VideoRollingShutter.ValidateProfile(
        [
            new()
            {
                SourceSeconds = 1,
                Rows = [new()],
            },
        ]));
        Assert.Throws<InvalidDataException>(() => VideoRollingShutter.ValidateProfile(
        [
            MakeKeyframe(1, VideoRollingShutter.MaximumCorrection + 0.01),
            MakeKeyframe(2, 0),
        ]));
    }

    [Fact]
    public void ProfilesRoundTripWithClipAndRejectUnorderedSamples()
    {
        var clip = new TimelineClipProject
        {
            SourcePath = "rolling.mp4",
            SourceDurationSeconds = 4,
            SourceOutSeconds = 4,
            RollingShutterProfile =
            [
                MakeKeyframe(0, 0),
                MakeKeyframe(2, 0.01),
                MakeKeyframe(4, 0.02),
            ],
        };
        var track = new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] };
        var project = new EditorProject
        {
            VideoTracks = [track],
        };
        TimelineClipProject restored = Assert.Single(Assert.Single(
            EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project)).VideoTracks).Clips);
        Assert.Equal(3, restored.RollingShutterProfile.Count);
        Assert.Equal(0.01, restored.RollingShutterProfile[1].Rows[0].X);
        Assert.Equal(0.01, VideoTimelineEditing.Clone(clip).RollingShutterProfile[1].Rows[0].X);
        TimelineClipProject right = VideoTimelineEditing.Split(track, clip, 49, 24)!;
        TimelineClipProject joined = VideoClipJoining.Join(track, clip, right, 24);
        Assert.Equal(new[] { 0d, 2d, 4d }, joined.RollingShutterProfile.Select(keyframe => keyframe.SourceSeconds));
        Assert.Equal(new[] { 0d, 0.01, 0.02 }, joined.RollingShutterProfile.Select(keyframe => keyframe.Rows[0].X));
        restored.RollingShutterProfile.Reverse();
        project.VideoTracks[0].Clips[0] = restored;
        Assert.Throws<InvalidDataException>(() => EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void CorrectionProfileIsAppliedToEncodedVideoAndLegacyClipsMigrateUncorrected()
    {
        if (!VideoExportTests.HasH264Encoder)
        {
            return;
        }
        string source = Path.Combine(Path.GetTempPath(), $"inkmotion-row-source-{Guid.NewGuid():N}.mp4");
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-row-output-{Guid.NewGuid():N}.mp4");
        try
        {
            var profile = VideoExportProfile.Create(ExportResolution.P720, 24);
            var background = new EditorProject
            {
                Frames =
                [
                    new()
                    {
                        IsKeyframe = true,
                        ExposureFrames = 24,
                        Strokes = [InkStrokeEditing.CreateShape(InkShape.Rectangle, 0, 0, 1, 1, 4, 0xFF00FF00, true)],
                    },
                ],
            };
            VideoExporter.Export(new(background, profile, VideoExportCodec.H264, source, 400, PreferHardware: false),
                null, CancellationToken.None);
            var clip = new TimelineClipProject
            {
                SourcePath = source,
                SourceDurationSeconds = 1,
                SourceOutSeconds = 1,
                RollingShutterProfile =
                [
                    MakeKeyframe(0, 0.1),
                    MakeKeyframe(1, 0.1),
                ],
            };
            var project = new EditorProject
            {
                VideoTracks = [new() { Kind = TimelineTrackKind.Video, Clips = [clip] }],
            };
            project = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
            VideoExporter.Export(new(project, profile, VideoExportCodec.H264, output, 400, PreferHardware: false),
                null, CancellationToken.None);
            using var reader = new MediaFoundationVideoReader(output);
            Assert.True(reader.TryGetFrame(0.5));
            int y = reader.Height / 2;
            int left = (y * reader.Width + 20) * 4;
            int centre = (y * reader.Width + reader.Width / 2) * 4;
            Assert.InRange(reader.Pixels[left], 0, 35);
            Assert.InRange(reader.Pixels[left + 1], 0, 35);
            Assert.InRange(reader.Pixels[left + 2], 0, 35);
            Assert.True(reader.Pixels[centre + 1] > 180);

            EditorProject legacy = EditorProjectSerialiser.Deserialise(
                """{"Version":42,"VideoTracks":[{"Kind":"Video","Clips":[{"SourcePath":"old.mp4","SourceDurationSeconds":1}]}],"Frames":[{"Number":1}],"SelectedFrameIndex":0}""");
            Assert.Empty(legacy.VideoTracks[0].Clips[0].RollingShutterProfile);
            Assert.Equal(EditorProject.CurrentVersion, legacy.Version);
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    [Fact]
    public void AnalyseCreatesTimeVaryingRowCorrectionsFromSyntheticRollingMotion()
    {
        if (!VideoExportTests.HasH264Encoder)
        {
            return;
        }
        const int width = 320;
        const int height = 180;
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-rolling-analysis-{Guid.NewGuid():N}.mp4");
        try
        {
            using (var writer = new MediaFoundationVideoWriter(
                       path, width, height, 24, VideoExportCodec.H264, 2_000_000, 0, 0, false))
            {
                for (int frameIndex = 0; frameIndex <= 12; frameIndex++)
                {
                    int step = frameIndex / 4;
                    byte[] pixels = new byte[width * height * 4];
                    for (int y = 0; y < height; y++)
                    {
                        int band = y / (height / VideoRollingShutter.RowBands);
                        int shift = (band - 2) * step * 5;
                        for (int x = 0; x < width; x++)
                        {
                            int sourceX = x - shift;
                            int cellX = sourceX / 5;
                            int cellY = y / 5;
                            byte luma = sourceX >= 0 && sourceX < width &&
                                ((cellX * 73 + cellY * 29 + cellX * cellY * 11) % 7 < 3) ? (byte)220 : (byte)32;
                            int offset = (y * width + x) * 4;
                            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = luma;
                            pixels[offset + 3] = 255;
                        }
                    }
                    long timestamp = frameIndex * 10_000_000L / 24;
                    writer.WriteVideoFrame(pixels, timestamp, 416_667L);
                }
                writer.Finish();
            }

            var clip = new TimelineClipProject
            {
                SourcePath = path,
                SourceDurationSeconds = 0.5,
                SourceOutSeconds = 0.5,
            };
            IReadOnlyList<VideoRollingShutterKeyframe> profile = VideoRollingShutter.Analyse(clip, 24);
            Assert.True(profile.Count >= 3);
            Assert.All(profile, keyframe => Assert.Equal(VideoRollingShutter.RowBands, keyframe.Rows.Count));
            Assert.Contains(profile, keyframe => keyframe.Rows.Any(offset => Math.Abs(offset.X) > 0.001));
            VideoRollingShutter.ValidateProfile(profile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static VideoRollingShutterKeyframe MakeKeyframe(double sourceSeconds, double x) => new()
    {
        SourceSeconds = sourceSeconds,
        Rows = Enumerable.Range(0, VideoRollingShutter.RowBands)
            .Select(_ => new VideoRollingShutterOffset { X = x }).ToList(),
    };
}
