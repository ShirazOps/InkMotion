using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class VideoExportTests
{
    private static bool HasH264Encoder =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "mfh264enc.dll"));

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
                new VideoExportRequest(surround, VideoExportProfile.Create(ExportResolution.P720, 30), VideoExportCodec.H264, second, 720),
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
                Assert.Equal(6, audio.WaveFormat.Channels);
            }

            using (var wave = new WaveFileReader(stem))
            {
                Assert.Equal(8, wave.WaveFormat.Channels);
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
