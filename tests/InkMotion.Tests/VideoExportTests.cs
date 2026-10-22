using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoExportTests
{
    private static readonly Lazy<bool> H264EncoderProbe = new(ProbeH264Encoder);

    // Windows Server images (such as CI runners) ship mfh264enc.dll but can reject the H.264 media types,
    // so the encode tests run only where the software encoder actually starts.
    internal static bool HasH264Encoder => H264EncoderProbe.Value;

    [Fact]
    public void EmbeddedAudio_ValidatesBeforeCreatingAnOutputFile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportPlanning.ValidateEmbeddedAudio(384000, 192000));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportPlanning.ValidateEmbeddedAudio(48000, 320000));
        Assert.Equal(44100, ExportPlanning.GetAudioFramesThrough(119, 120, 44100));
        Assert.Equal(1471471, ExportPlanning.GetAudioFramesThrough(999, 29.97, 44100));
        string output = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoExporter.Export(
            new(new(), VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, output, 720, AudioSampleRate: 96000),
            null, CancellationToken.None));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(44100, 128000)]
    [InlineData(44100, 160000)]
    [InlineData(44100, 192000)]
    [InlineData(48000, 128000)]
    [InlineData(48000, 160000)]
    [InlineData(48000, 192000)]
    public void Export_EmbedsSelectedAacRateAndPreservesDuration(int rate, int bitRate)
    {
        if (!HasH264Encoder)
        {
            return;
        }

        string tone = WriteToneWave(2, 1, 0.2f);
        string output = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        try
        {
            var project = new EditorProject { AudioTracks = [new() { AudioPath = tone }] };
            VideoExportResult result = VideoExporter.Export(
                new(project, VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, output, 720,
                    PreferHardware: false, AudioSampleRate: rate, AudioBitRate: bitRate),
                null, CancellationToken.None);
            Assert.Equal(24, result.FrameCount);
            using var audio = new MediaFoundationReader(output);
            Assert.Equal(rate, audio.WaveFormat.SampleRate);
            Assert.Equal(2, audio.WaveFormat.Channels);
            Assert.InRange(audio.TotalTime.TotalSeconds, 0.95, 1.15);
            float[] samples = new float[rate * 2];
            int read = audio.ToSampleProvider().Read(samples, 0, samples.Length);
            Assert.True(read > rate);
            Assert.True(samples.Take(read).Max(value => Math.Abs(value)) > 0.1f);
        }
        finally
        {
            File.Delete(tone);
            File.Delete(output);
        }
    }

    [Fact]
    public void Export_CrossDissolvesAdjacentVideoSourcesAtTheCut()
    {
        if (!HasH264Encoder)
        {
            return;
        }

        string outgoingPath = Path.Combine(Path.GetTempPath(), $"inkmotion-outgoing-{Guid.NewGuid():N}.mp4");
        string incomingPath = Path.Combine(Path.GetTempPath(), $"inkmotion-incoming-{Guid.NewGuid():N}.mp4");
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-dissolve-{Guid.NewGuid():N}.mp4");
        try
        {
            WriteInkColourVideo(outgoingPath, 0xFFFF0000);
            WriteInkColourVideo(incomingPath, 0xFF0000FF);
            var videoTrack = new TimelineTrackProject
            {
                Kind = TimelineTrackKind.Video,
                Clips =
                [
                    new TimelineClipProject
                    {
                        StartFrame = 1,
                        SourcePath = outgoingPath,
                        SourceOutSeconds = 0.5,
                        SourceDurationSeconds = 1,
                    },
                    new TimelineClipProject
                    {
                        StartFrame = 13,
                        SourcePath = incomingPath,
                        SourceInSeconds = 0.25,
                        SourceOutSeconds = 0.75,
                        SourceDurationSeconds = 1,
                        TransitionInSeconds = 0.5,
                    },
                ],
            };
            var project = new EditorProject { VideoTracks = [videoTrack] };

            VideoExportResult result = VideoExporter.Export(
                new(project, VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, output, 720,
                    PreferHardware: false),
                null,
                CancellationToken.None);

            Assert.Equal(24, result.FrameCount);
            using var video = new MediaFoundationVideoReader(output);
            Assert.True(video.TryGetFrame(0.5));
            (byte b, byte g, byte r) = GetPixel(video, 640, 504);
            Assert.True(r > 80 && b > 80 && Math.Abs(r - b) < 45 && g < 50,
                $"Expected an even red/blue blend, got {r},{g},{b}.");
        }
        finally
        {
            foreach (string path in new[] { outgoingPath, incomingPath, output })
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Export_ReversedVideoAtDoubleSpeedUsesTheRetimedClipDuration()
    {
        if (!HasH264Encoder)
        {
            return;
        }

        string source = Path.Combine(Path.GetTempPath(), $"inkmotion-reverse-source-{Guid.NewGuid():N}.mp4");
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-reverse-output-{Guid.NewGuid():N}.mp4");
        try
        {
            WriteInkColourVideo(source, 0xFFFF0000);
            var project = new EditorProject
            {
                VideoTracks =
                [
                    new TimelineTrackProject
                    {
                        Kind = TimelineTrackKind.Video,
                        Clips =
                        [
                            new TimelineClipProject
                            {
                                SourcePath = source,
                                SourceOutSeconds = 1,
                                SourceDurationSeconds = 1,
                                PlaybackSpeed = 2,
                                IsReversed = true,
                            },
                        ],
                    },
                ],
            };

            VideoExportResult result = VideoExporter.Export(
                new(project, VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, output, 720),
                null,
                CancellationToken.None);

            Assert.Equal(12, result.FrameCount);
            using var reader = new MediaFoundationVideoReader(output);
            Assert.True(reader.TryGetFrame(0.25));
            (byte b, byte g, byte r) = GetPixel(reader, 640, 504);
            Assert.True(r > 150 && g < 100 && b < 100, $"Expected red video, got {r},{g},{b}.");
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    [Fact]
    public void Export_AppliesClipColorGradingToVideoFrames()
    {
        if (!HasH264Encoder)
        {
            return;
        }

        string source = Path.Combine(Path.GetTempPath(), $"inkmotion-color-source-{Guid.NewGuid():N}.mp4");
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-color-output-{Guid.NewGuid():N}.mp4");
        try
        {
            WriteInkColourVideo(source, 0xFFFF0000);
            var project = new EditorProject
            {
                VideoTracks =
                [
                    new TimelineTrackProject
                    {
                        Kind = TimelineTrackKind.Video,
                        Clips =
                        [
                            new TimelineClipProject
                            {
                                SourcePath = source,
                                SourceOutSeconds = 1,
                                SourceDurationSeconds = 1,
                                Saturation = 0,
                            },
                        ],
                    },
                ],
            };

            VideoExporter.Export(
                new(project, VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, output, 720,
                    PreferHardware: false),
                null,
                CancellationToken.None);

            using var reader = new MediaFoundationVideoReader(output);
            Assert.True(reader.TryGetFrame(0.25));
            (byte b, byte g, byte r) = GetPixel(reader, 640, 360);
            Assert.InRange(Math.Abs(r - g), 0, 10);
            Assert.InRange(Math.Abs(g - b), 0, 10);
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    [Fact]
    public void Export_InterpolatesVideoTransformKeyframes()
    {
        if (!HasH264Encoder)
        {
            return;
        }

        string source = Path.Combine(Path.GetTempPath(), $"inkmotion-transform-source-{Guid.NewGuid():N}.mp4");
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-transform-{Guid.NewGuid():N}.mp4");
        try
        {
            WriteInkColourVideo(source, 0xFFFF0000);
            var clip = new TimelineClipProject
            {
                SourcePath = source,
                SourceOutSeconds = 1,
                SourceDurationSeconds = 1,
                TransformKeyframes =
                [
                    new() { FrameOffset = 0, X = 0.25, Y = 0.5, Scale = 1 },
                    new() { FrameOffset = 23, X = 0.75, Y = 0.5, Scale = 1 },
                ],
            };
            var project = new EditorProject
            {
                VideoTracks = [new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] }],
            };

            VideoExportResult result = VideoExporter.Export(
                new(project, VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, output, 720,
                    PreferHardware: false),
                null,
                CancellationToken.None);
            Assert.Empty(result.Warnings);

            using var video = new MediaFoundationVideoReader(output);
            Assert.True(video.TryGetFrame(0.1));
            (byte B, byte G, byte R) firstLeft = GetPixel(video, 320, 504);
            (byte B, byte G, byte R) firstCentre = GetPixel(video, 640, 504);
            (byte B, byte G, byte R) firstRight = GetPixel(video, 960, 504);
            Assert.True(firstLeft.R > 80 && firstRight.R < 30,
                $"Expected the early frame to be translated left, got left {firstLeft}, centre {firstCentre}, and right {firstRight}.");

            Assert.True(video.TryGetFrame(0.9));
            (byte B, byte G, byte R) lastLeft = GetPixel(video, 320, 504);
            (byte B, byte G, byte R) lastRight = GetPixel(video, 960, 504);
            Assert.True(lastRight.R > 80 && lastLeft.R < 30,
                $"Expected the last frame to be translated right, got left {lastLeft.R} and right {lastRight.R} red.");
        }
        finally
        {
            foreach (string path in new[] { source, output })
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Export_CropsVideoSourceBeforeFittingItToTheFrame()
    {
        if (!HasH264Encoder)
        {
            return;
        }

        string source = Path.Combine(Path.GetTempPath(), $"inkmotion-crop-source-{Guid.NewGuid():N}.mp4");
        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-crop-{Guid.NewGuid():N}.mp4");
        try
        {
            WriteInkColourVideo(source, 0xFFFF0000);
            var clip = new TimelineClipProject
            {
                SourcePath = source,
                SourceOutSeconds = 1,
                SourceDurationSeconds = 1,
                CropLeftPercent = 40,
            };
            var project = new EditorProject
            {
                VideoTracks = [new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [clip] }],
            };

            VideoExporter.Export(
                new(project, VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, output, 720,
                    PreferHardware: false),
                null,
                CancellationToken.None);

            using var video = new MediaFoundationVideoReader(output);
            Assert.True(video.TryGetFrame(0.1));
            Assert.True(GetPixel(video, 320, 504).R > 80);
            Assert.True(GetPixel(video, 100, 504).R < 30);
        }
        finally
        {
            foreach (string path in new[] { source, output })
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Quality_ChangesTheTargetBitRate()
    {
        var profile = VideoExportProfile.Create(ExportResolution.P1080, 24);
        int low = ExportPlanning.GetVideoBitRate(profile, VideoExportCodec.H264, 0);
        int high = ExportPlanning.GetVideoBitRate(profile, VideoExportCodec.H264, 1);
        Assert.Equal(low * 4, high);
        Assert.Throws<ArgumentOutOfRangeException>(() => ExportPlanning.GetVideoBitRate(profile, VideoExportCodec.H264, double.NaN));
    }

    [Theory]
    [InlineData(0, 120, 0)]
    [InlineData(119, 120, 0)]
    [InlineData(120, 120, 1)]
    [InlineData(24, 24, 1)]
    [InlineData(60, 60, 1)]
    public void IndependentOutputRate_PreservesInkExposure(int outputFrame, double rate, int expectedCel)
    {
        var project = new EditorProject
        {
            FrameRate = 24,
            Frames = [new() { ExposureFrames = 24 }, new() { Number = 2, ExposureFrames = 24 }],
        };
        Assert.Equal(expectedCel, ExportPlanning.GetInkFrameIndex(project, outputFrame, rate));
    }

    private static bool ProbeH264Encoder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-probe-{Guid.NewGuid():N}.mp4");
        try
        {
            using var writer = new MediaFoundationVideoWriter(path, 1280, 720, 24, VideoExportCodec.H264, 1_000_000, 2, 0, false);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    [Theory]
    [InlineData(ExportResolution.P720, VideoExportCodec.H264, 24, 3_317_760)]
    [InlineData(ExportResolution.P1080, VideoExportCodec.H264, 30, 9_331_200)]
    [InlineData(ExportResolution.P1080, VideoExportCodec.Hevc, 30, 5_598_720)]
    [InlineData(ExportResolution.Uhd4K, VideoExportCodec.H264, 60, 74_649_600)]
    public void GetVideoBitRate_ScalesWithPixelsAndCodec(ExportResolution resolution, VideoExportCodec codec, double fps, int expected)
    {
        VideoExportProfile profile = VideoExportProfile.Create(resolution, fps);

        Assert.Equal(expected, ExportPlanning.GetVideoBitRate(profile, codec));
    }

    [Theory]
    [InlineData(AudioChannelLayout.Mono, AudioChannelLayout.Mono, false)]
    [InlineData(AudioChannelLayout.Stereo, AudioChannelLayout.Stereo, false)]
    [InlineData(AudioChannelLayout.Surround51, AudioChannelLayout.Surround51, false)]
    [InlineData(AudioChannelLayout.Quad, AudioChannelLayout.Surround51, true)]
    [InlineData(AudioChannelLayout.Surround71, AudioChannelLayout.Surround51, true)]
    [InlineData(AudioChannelLayout.Surround121, AudioChannelLayout.Surround51, true)]
    public void GetEncodedAudioLayout_KeepsAacLayoutsAndStemsTheRest(
        AudioChannelLayout mix,
        AudioChannelLayout expected,
        bool needsStem)
    {
        Assert.Equal(expected, ExportPlanning.GetEncodedAudioLayout(mix));
        Assert.Equal(needsStem, ExportPlanning.NeedsSurroundStem(mix));
    }

    [Fact]
    public void GetSurroundStemPath_SitsBesideTheVideo()
    {
        string path = Path.Combine("C:\\Exports", "Short film.mp4");

        Assert.Equal(Path.Combine("C:\\Exports", "Short film (surround mix).wav"), ExportPlanning.GetSurroundStemPath(path));
    }

    [Theory]
    [InlineData(1.0, 24, 24)]
    [InlineData(1.01, 24, 25)]
    [InlineData(0.5, 30, 15)]
    [InlineData(0, 24, 0)]
    public void GetFrameCount_RoundsPartialFramesUp(double seconds, double fps, int expected)
    {
        Assert.Equal(expected, ExportPlanning.GetFrameCount(seconds, fps));
    }

    [Fact]
    public void FrameAndAudioTimes_StayInStep()
    {
        Assert.Equal(0, ExportPlanning.GetFrameTime(0, 24));
        Assert.Equal(10_000_000, ExportPlanning.GetFrameTime(24, 24));
        Assert.Equal(2000, ExportPlanning.GetAudioFramesThrough(0, 24));
        Assert.Equal(48000, ExportPlanning.GetAudioFramesThrough(29, 30));
        Assert.Equal((30, 1), ExportPlanning.GetFrameRateRatio(30));
        Assert.Equal((23976, 1000), ExportPlanning.GetFrameRateRatio(23.976));
    }

    [Fact]
    public void GetInkFrameIndex_HoldsTheLastDrawing()
    {
        var project = new EditorProject
        {
            Frames = [new EditorFrame { ExposureFrames = 2 }, new EditorFrame { Number = 2, ExposureFrames = 1 }],
        };

        Assert.Equal(0, ExportPlanning.GetInkFrameIndex(project, 1));
        Assert.Equal(1, ExportPlanning.GetInkFrameIndex(project, 2));
        Assert.Equal(1, ExportPlanning.GetInkFrameIndex(project, 50));
    }

    [Fact]
    public void GetInkDurationSeconds_IsZeroWithoutInk()
    {
        var project = new EditorProject { Frames = [new EditorFrame { ExposureFrames = 48 }] };
        Assert.Equal(0, ExportPlanning.GetInkDurationSeconds(project));

        project.Frames[0].Strokes.Add([new InkPoint { X = 0.5, Y = 0.5 }]);
        Assert.Equal(2, ExportPlanning.GetInkDurationSeconds(project));
    }

    [Fact]
    public void GetVideoClipAudioGain_AppliesVolumeAndFades()
    {
        var clip = new TimelineClipProject
        {
            SourceDurationSeconds = 4,
            Volume = 0.5,
            FadeInSeconds = 1,
            FadeOutSeconds = 1,
        };

        Assert.Equal(0.25, ExportPlanning.GetVideoClipAudioGain(clip, 0.5), 3);
        Assert.Equal(0.5, ExportPlanning.GetVideoClipAudioGain(clip, 2), 3);
        Assert.Equal(0.25, ExportPlanning.GetVideoClipAudioGain(clip, 3.5), 3);
        Assert.Equal(0, ExportPlanning.GetVideoClipAudioGain(clip, 4));
    }

    [Fact]
    public void GetVisibleLayers_ReturnsEveryKindBottomTrackFirst()
    {
        var title = new TimelineClipProject { Text = "Title", DurationSeconds = 5 };
        var image = new TimelineClipProject { SourcePath = "still.png", DurationSeconds = 5 };
        var video = new TimelineClipProject { SourcePath = "clip.mp4", SourceDurationSeconds = 5 };
        List<TimelineTrackProject> tracks =
        [
            new() { Kind = TimelineTrackKind.Text, Clips = [title] },
            new() { Kind = TimelineTrackKind.Image, Clips = [image] },
            new() { Kind = TimelineTrackKind.Video, Clips = [video] },
            new() { Kind = TimelineTrackKind.Image, IsVisible = false, Clips = [new TimelineClipProject { SourcePath = "hidden.png" }] },
        ];

        IReadOnlyList<TimelineClipLocation> layers = VideoTimelineEditing.GetVisibleLayers(tracks, 1, 24);

        Assert.Equal([video, image, title], layers.Select(layer => layer.Clip));
    }

    [Fact]
    public void ExportAudioMixer_PlacesTracksAndMixesToTheProjectLayout()
    {
        string path = WriteToneWave(channels: 1, seconds: 1, amplitude: 0.5f);
        try
        {
            var project = new EditorProject
            {
                FrameRate = 24,
                AudioMixLayout = AudioChannelLayout.Stereo,
                AudioTracks = [new AudioTrackProject { AudioPath = path, StartFrame = 25 }],
            };

            using var mixer = new ExportAudioMixer(project);
            float[] buffer = new float[48000 * 2 * 2];
            int read = mixer.Read(buffer, 0, buffer.Length);

            Assert.Equal(1, mixer.SourceCount);
            Assert.Equal(2, mixer.DurationSeconds, 2);
            Assert.Equal(buffer.Length, read);
            Assert.Equal(0, buffer.Take(48000 * 2).Max(Math.Abs));
            float peak = buffer.Skip(48000 * 2).Max(Math.Abs);
            Assert.InRange(peak, 0.3f, 0.51f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExportAudioMixer_WarnsAboutMissingFiles()
    {
        var project = new EditorProject
        {
            AudioTracks = [new AudioTrackProject { AudioPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav") }],
        };

        using var mixer = new ExportAudioMixer(project);

        Assert.Equal(0, mixer.SourceCount);
        Assert.Single(mixer.Warnings);
    }

    [Fact]
    public void MultichannelWaveWriter_WritesAnExtensibleHeaderWithTheSpeakerMask()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        try
        {
            AudioChannelLayoutInfo layout = AudioChannelLayouts.Get(AudioChannelLayout.Surround121);
            using (var writer = new MultichannelWaveWriter(path, 48000, layout.ChannelCount, layout.ChannelMask))
            {
                float[] samples = new float[layout.ChannelCount * 10];
                samples[0] = 1;
                samples[1] = -1;
                writer.Write(samples, samples.Length);
            }

            using var reader = new WaveFileReader(path);
            Assert.Equal(layout.ChannelCount, reader.WaveFormat.Channels);
            Assert.Equal(24, reader.WaveFormat.BitsPerSample);
            Assert.Equal(10, reader.SampleCount);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(layout.ChannelMask, BitConverter.ToInt32(bytes, 40));
            Assert.Equal(0x7FFFFF, bytes[68] | (bytes[69] << 8) | (bytes[70] << 16));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_PreservesTransformedShapesAndPartiallyErasedGaps()
    {
        if (!HasH264Encoder)
        {
            return;
        }

        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-shapes-{Guid.NewGuid():N}.mp4");
        try
        {
            List<List<InkPoint>> strokes =
                [InkStrokeEditing.CreateShape(InkShape.Line, 0.3, 0.7, 0.7, 0.7, 40, 0xFF00FF00)];
            Assert.True(InkSelectionEditing.Transform(strokes, 0, -0.2, 1, 0, 1280, 720));
            Assert.True(InkStrokeEditing.Erase(strokes, 640, 360, 1280, 720, 40, wholeStroke: false));
            var project = new EditorProject
            {
                FrameRate = 24,
                Frames = [new() { ExposureFrames = 12, IsKeyframe = true, Strokes = strokes }],
            };
            project = EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));
            VideoExporter.Export(new(project, VideoExportProfile.Create(ExportResolution.P720, 24),
                VideoExportCodec.H264, output, 720, PreferHardware: false), null, CancellationToken.None);
            using var video = new MediaFoundationVideoReader(output);
            Assert.True(video.TryGetFrame(0.2));
            var remaining = GetPixel(video, 448, 360);
            var erased = GetPixel(video, 640, 360);
            Assert.True(remaining.G > 160 && remaining.R < 80 && remaining.B < 80);
            Assert.True(erased.R < 50 && erased.G < 50 && erased.B < 50);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void Export_120FpsKeepsInkTimingAndUsesSoftwareWhenRequested()
    {
        if (!HasH264Encoder)
        {
            return;
        }

        string output = Path.Combine(Path.GetTempPath(), $"inkmotion-120fps-{Guid.NewGuid():N}.mp4");
        try
        {
            var project = new EditorProject
            {
                FrameRate = 24,
                Frames = [new() { ExposureFrames = 6, IsKeyframe = true }, new() { Number = 2, ExposureFrames = 6, IsKeyframe = true }],
            };
            for (int cel = 0; cel < 2; cel++)
            {
                uint colour = cel == 0 ? 0xFFFF0000 : 0xFF0000FF;
                project.Frames[cel].Strokes.Add([
                    new() { X = 0.3, Y = 0.7, BrushWidth = 40, Pressure = 1, BrushColour = colour },
                    new() { X = 0.7, Y = 0.7, BrushWidth = 40, Pressure = 1, BrushColour = colour },
                ]);
            }

            var result = VideoExporter.Export(new(project, VideoExportProfile.Create(ExportResolution.P720, 120),
                VideoExportCodec.H264, output, 720, PreferHardware: false), null, CancellationToken.None);
            Assert.Equal(60, result.FrameCount);
            Assert.Equal(0.5, result.DurationSeconds, 3);
            Assert.False(result.UsedHardwareEncoder);
            using var video = new MediaFoundationVideoReader(output);
            Assert.True(video.TryGetFrame(0.1));
            var red = GetPixel(video, 640, 504);
            Assert.True(red.R > 180 && red.B < 80);
            Assert.True(video.TryGetFrame(0.4));
            var blue = GetPixel(video, 640, 504);
            Assert.True(blue.B > 180 && blue.R < 80);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void Export_EncodesInkTitlesAndAudioThenReadsBack()
    {
        if (!HasH264Encoder)
        {
            return;
        }

        string tone = WriteToneWave(channels: 2, seconds: 1, amplitude: 0.5f);
        string first = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        string second = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        string stem = ExportPlanning.GetSurroundStemPath(second);
        try
        {
            var project = new EditorProject
            {
                FrameRate = 24,
                AudioTracks = [new AudioTrackProject { AudioPath = tone }],
                VideoTracks = [new TimelineTrackProject { Kind = TimelineTrackKind.Text, Clips = [new TimelineClipProject { Text = "InkMotion", DurationSeconds = 1, Y = 0.2 }] }],
                Frames = [new EditorFrame { ExposureFrames = 24 }],
            };
            project.Frames[0].Strokes.Add(
            [
                new InkPoint { X = 0.3, Y = 0.7, BrushWidth = 40, Pressure = 1, BrushColour = 0xFFFF0000 },
                new InkPoint { X = 0.7, Y = 0.7, BrushWidth = 40, Pressure = 1, BrushColour = 0xFFFF0000 },
            ]);

            VideoExportResult result = VideoExporter.Export(
                new VideoExportRequest(project, VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, first, 720),
                null,
                CancellationToken.None);

            Assert.Equal(24, result.FrameCount);
            Assert.Null(result.SurroundStemPath);
            using (var video = new MediaFoundationVideoReader(first))
            {
                Assert.Equal((1280, 720), (video.Width, video.Height));
                Assert.True(video.TryGetFrame(0.5));
                (byte b, byte g, byte r) = GetPixel(video, 640, 504);
                Assert.True(r > 180 && g < 80 && b < 80, $"Expected red ink, got {r},{g},{b}.");
            }

            using (var audio = new MediaFoundationReader(first))
            {
                Assert.Equal(2, audio.WaveFormat.Channels);
                Assert.InRange(audio.TotalTime.TotalSeconds, 0.9, 1.2);
            }

            // Re-import the export as a video clip, so decoding, clip audio and the surround stem are exercised.
            var surround = new EditorProject
            {
                FrameRate = 30,
                AudioMixLayout = AudioChannelLayout.Surround71,
                VideoTracks = [new TimelineTrackProject { Kind = TimelineTrackKind.Video, Clips = [new TimelineClipProject { SourcePath = first, SourceDurationSeconds = 1 }] }],
            };
            result = VideoExporter.Export(
                new VideoExportRequest(surround, VideoExportProfile.Create(ExportResolution.P720, 30), VideoExportCodec.H264, second, 720, AudioSampleRate: 44100),
                null,
                CancellationToken.None);

            Assert.Equal(30, result.FrameCount);
            Assert.Equal(stem, result.SurroundStemPath);
            using (var video = new MediaFoundationVideoReader(second))
            {
                Assert.True(video.TryGetFrame(0.5));
                (byte b, byte g, byte r) = GetPixel(video, 640, 504);
                Assert.True(r > 150 && g < 100 && b < 100, $"Expected re-encoded red ink, got {r},{g},{b}.");
            }

            using (var audio = new MediaFoundationReader(second))
            {
                // Some Windows editions (such as Server CI images) only encode stereo AAC; the exporter then falls back.
                Assert.Contains(result.AudioChannels, new[] { 6, 2 });
                Assert.Equal(result.AudioChannels, audio.WaveFormat.Channels);
                Assert.Equal(44100, audio.WaveFormat.SampleRate);
                Assert.True(result.AudioChannels == 6 || result.Warnings.Any(w => w.Contains("stereo downmix", StringComparison.Ordinal)));
            }

            using (var wave = new WaveFileReader(stem))
            {
                Assert.Equal(8, wave.WaveFormat.Channels);
                Assert.Equal(44100, wave.WaveFormat.SampleRate);
                Assert.InRange(wave.TotalTime.TotalSeconds, 0.95, 1.05);
            }
        }
        finally
        {
            foreach (string path in new[] { tone, first, second, stem })
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Export_CancellationRemovesThePartialFile()
    {
        if (!HasH264Encoder)
        {
            return;
        }

        string output = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        var project = new EditorProject { Frames = [new EditorFrame { ExposureFrames = 48 }] };
        project.Frames[0].Strokes.Add([new InkPoint { X = 0.5, Y = 0.5 }]);
        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(update =>
        {
            if (update.Frame >= 6)
            {
                cancellation.Cancel();
            }
        });

        Assert.Throws<OperationCanceledException>(() => VideoExporter.Export(
            new VideoExportRequest(project, VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, output, 720),
            progress,
            cancellation.Token));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Export_RejectsAnEmptyProject()
    {
        string output = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");

        Assert.Throws<InvalidOperationException>(() => VideoExporter.Export(
            new VideoExportRequest(new EditorProject(), VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, output, 720),
            null,
            CancellationToken.None));
        Assert.False(File.Exists(output));
    }

    private static void WriteInkColourVideo(string path, uint colour)
    {
        var project = new EditorProject
        {
            Frames = [new EditorFrame { ExposureFrames = 24 }],
        };
        project.Frames[0].Strokes.Add([
            new InkPoint { X = 0.3, Y = 0.7, BrushWidth = 40, Pressure = 1, BrushColour = colour },
            new InkPoint { X = 0.7, Y = 0.7, BrushWidth = 40, Pressure = 1, BrushColour = colour },
        ]);
        VideoExporter.Export(
            new(project, VideoExportProfile.Create(ExportResolution.P720, 24), VideoExportCodec.H264, path, 720,
                PreferHardware: false),
            null,
            CancellationToken.None);
    }

    private static (byte B, byte G, byte R) GetPixel(MediaFoundationVideoReader reader, int x, int y)
    {
        int index = ((y * reader.Width) + x) * 4;
        return (reader.Pixels[index], reader.Pixels[index + 1], reader.Pixels[index + 2]);
    }

    private static string WriteToneWave(int channels, double seconds, float amplitude)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        var format = new WaveFormat(48000, 16, channels);
        using var writer = new WaveFileWriter(path, format);
        int frames = (int)(48000 * seconds);
        for (int frame = 0; frame < frames; frame++)
        {
            float value = amplitude * MathF.Sin(2 * MathF.PI * 440 * frame / 48000f);
            for (int channel = 0; channel < channels; channel++)
            {
                writer.WriteSample(value);
            }
        }

        return path;
    }

    private sealed class SynchronousProgress(Action<VideoExportProgress> handler) : IProgress<VideoExportProgress>
    {
        public void Report(VideoExportProgress value) => handler(value);
    }
}
