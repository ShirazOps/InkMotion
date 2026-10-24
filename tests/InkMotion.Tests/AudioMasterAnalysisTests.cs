using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioMasterAnalysisTests : IDisposable
{
    private readonly List<string> _files = [];

    private string WriteSource(int channels, float[] samples)
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-master-{Guid.NewGuid():N}.wav");
        _files.Add(path);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, channels));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasuresSummedSamplesIncludingPhaseCancellationRatherThanAddingTrackPeaks(bool oppositePhase)
    {
        const int frames = 10008;
        string first = WriteSource(1, Enumerable.Repeat(0.75f, frames).ToArray());
        string second = WriteSource(1, Enumerable.Repeat(oppositePhase ? -0.75f : 0.75f, frames).ToArray());
        var project = new EditorProject
        {
            AudioMixLayout = AudioChannelLayout.Mono,
            AudioTracks = [new() { AudioPath = first }, new() { AudioPath = second }],
        };
        string before = EditorProjectSerialiser.Serialise(project);
        AudioMasterAnalysisResult result = AudioMasterAnalysis.Analyse(project, CancellationToken.None);
        Assert.Equal(frames, result.SampleFrames);
        Assert.Equal(48000, result.SampleRate);
        Assert.Equal(oppositePhase ? 0f : 1.5f, result.Overall.Peak);
        Assert.Equal(oppositePhase ? 0d : 1.5d, result.Overall.Rms, 6);
        Assert.Equal(!oppositePhase, result.Overall.Clipped);
        Assert.Equal(result.Overall, Assert.Single(result.Channels));
        Assert.Empty(result.Warnings);
        Assert.Equal(before, EditorProjectSerialiser.Serialise(project));
    }

    [Fact]
    public void MeasuresEachChannelAndTheFinalPartialBuffer()
    {
        const int frames = 4101;
        float[] samples = new float[frames * 2];
        samples[^2] = 0.5f;
        samples[^1] = -1;
        string source = WriteSource(2, samples);
        var project = new EditorProject { AudioTracks = [new() { AudioPath = source }] };
        AudioMasterAnalysisResult result = AudioMasterAnalysis.Analyse(project, CancellationToken.None);
        Assert.Equal(frames, result.SampleFrames);
        Assert.Equal(0.5f, result.Channels[0].Peak);
        Assert.Equal(1f, result.Channels[1].Peak);
        Assert.Equal(Math.Sqrt(0.25 / frames), result.Channels[0].Rms, 8);
        Assert.Equal(Math.Sqrt(1d / frames), result.Channels[1].Rms, 8);
        Assert.Equal(Math.Sqrt(1.25 / frames / 2), result.Overall.Rms, 8);
        Assert.False(result.Channels[0].Clipped);
        Assert.True(result.Channels[1].Clipped);
        Assert.True(result.Overall.Clipped);
    }

    [Fact]
    public void IncludesLeadingSilenceAndRespectsSoloMuteAndSpatialRouting()
    {
        string source = WriteSource(1, Enumerable.Repeat(0.5f, 48000).ToArray());
        var project = new EditorProject
        {
            FrameRate = 24,
            AudioMixLayout = AudioChannelLayout.Surround71,
            AudioTracks =
            [
                new()
                {
                    AudioPath = source, StartFrame = 25, Solo = true,
                    SpatialPanEnabled = true, SpatialPanAzimuthDegrees = 90,
                },
                new() { AudioPath = source },
                new() { AudioPath = source, Solo = true, Muted = true },
            ],
        };
        AudioMasterAnalysisResult result = AudioMasterAnalysis.Analyse(project, CancellationToken.None);
        Assert.Equal(96000, result.SampleFrames);
        Assert.Equal(8, result.Channels.Count);
        Assert.Equal(0.5f, result.Channels[7].Peak);
        Assert.Equal(Math.Sqrt(0.25 / 2), result.Channels[7].Rms, 6);
        Assert.All(result.Channels.Take(7), channel => Assert.Equal(0f, channel.Peak));
        Assert.False(result.Overall.Clipped);
    }

    [Fact]
    public void ReportsMissingSourcesInsteadOfPresentingAnIncompleteMixAsComplete()
    {
        string source = WriteSource(1, new float[100]);
        string missing = Path.Combine(Path.GetTempPath(), $"inkmotion-missing-{Guid.NewGuid():N}.wav");
        var project = new EditorProject
        {
            AudioTracks = [new() { AudioPath = source }, new() { AudioPath = missing }],
        };
        AudioMasterAnalysisResult result = AudioMasterAnalysis.Analyse(project, CancellationToken.None);
        Assert.Contains(Path.GetFileName(missing), Assert.Single(result.Warnings));
        Assert.Equal(double.NegativeInfinity, result.Overall.PeakDecibels);
        Assert.Equal(double.NegativeInfinity, result.Overall.RmsDecibels);
        Assert.Throws<InvalidOperationException>(() =>
            AudioMasterAnalysis.Analyse(new EditorProject(), CancellationToken.None));
    }

    [Fact]
    public void SupportsCancellationDuringStreamingAndBoundsProgressNotifications()
    {
        string source = WriteSource(1, new float[48000]);
        var project = new EditorProject { AudioTracks = [new() { AudioPath = source }] };
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => AudioMasterAnalysis.Analyse(project, cancellation.Token,
            new CallbackProgress(_ => cancellation.Cancel())));
        Assert.Throws<OperationCanceledException>(() =>
            AudioMasterAnalysis.Analyse(project, cancellation.Token));

        var reports = new List<double>();
        AudioMasterAnalysis.Analyse(project, CancellationToken.None, new CallbackProgress(reports.Add));
        Assert.InRange(reports.Count, 1, 101);
        Assert.Equal(1d, reports[^1]);
        Assert.True(reports.SequenceEqual(reports.Order()));
    }

    [Fact]
    public void AppliesGainVolumeAndFadesBeforeMeasuring()
    {
        const int frames = 48000;
        string source = WriteSource(1, Enumerable.Repeat(0.5f, frames).ToArray());
        var project = new EditorProject
        {
            AudioMixLayout = AudioChannelLayout.Mono,
            AudioTracks = [new()
            {
                AudioPath = source, Volume = 0.5, GainDecibels = -6, FadeInSeconds = 1,
            }],
        };
        AudioMasterAnalysisResult result = AudioMasterAnalysis.Analyse(project, CancellationToken.None);
        double fullGain = 0.25 * Math.Pow(10, -6d / 20);
        double rampSquares = (frames - 1d) * (2 * frames - 1d) / (6d * frames * frames);
        Assert.Equal(fullGain * (frames - 1d) / frames, result.Overall.Peak, 6);
        Assert.Equal(fullGain * Math.Sqrt(rampSquares), result.Overall.Rms, 6);
        Assert.False(result.Overall.Clipped);
    }

    [Fact]
    public void MeasuresPostEffectsSamplesAndDoesNotLatchClippingAcrossIndependentScans()
    {
        string source = WriteSource(1, Enumerable.Repeat(0.75f, 48000).ToArray());
        var project = new EditorProject
        {
            AudioMixLayout = AudioChannelLayout.Mono,
            AudioTracks = [new() { AudioPath = source }, new() { AudioPath = source }],
        };
        Assert.True(AudioMasterAnalysis.Analyse(project, CancellationToken.None).Overall.Clipped);
        foreach (AudioTrackProject track in project.AudioTracks)
        {
            track.Effects = new() { Enabled = true, LimiterEnabled = true, LimiterCeilingDecibels = -12 };
        }
        AudioMasterAnalysisResult result = AudioMasterAnalysis.Analyse(project, CancellationToken.None);
        Assert.Equal(2 * Math.Pow(10, -12d / 20), result.Overall.Peak, 6);
        Assert.False(result.Overall.Clipped);
    }

    [Fact]
    public void MeasuresIntegratedLoudnessWithKWeightingAndReturnsNoValueForSubBlockOrSilence()
    {
        const int frames = 96000;
        float[] tone = Enumerable.Range(0, frames)
            .Select(index => (float)(0.1 * Math.Sin(2 * Math.PI * 997 * index / 48000)))
            .ToArray();
        AudioMasterAnalysisResult result = AudioMasterAnalysis.Analyse(new EditorProject
        {
            AudioMixLayout = AudioChannelLayout.Mono,
            AudioTracks = [new() { AudioPath = WriteSource(1, tone) }],
        }, CancellationToken.None);
        Assert.NotNull(result.IntegratedLufs);
        Assert.InRange(result.IntegratedLufs!.Value, -24, -23);

        AudioMasterAnalysisResult silent = AudioMasterAnalysis.Analyse(new EditorProject
        {
            AudioMixLayout = AudioChannelLayout.Mono,
            AudioTracks = [new() { AudioPath = WriteSource(1, new float[48000]) }],
        }, CancellationToken.None);
        Assert.Null(silent.IntegratedLufs);

        AudioMasterAnalysisResult shortSignal = AudioMasterAnalysis.Analyse(new EditorProject
        {
            AudioMixLayout = AudioChannelLayout.Mono,
            AudioTracks = [new() { AudioPath = WriteSource(1, new float[12000]) }],
        }, CancellationToken.None);
        Assert.Null(shortSignal.IntegratedLufs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncludesVideoSourceAudioAndHonoursVideoTrackMute(bool muted)
    {
        string source = WriteSource(1, Enumerable.Repeat(0.25f, 48000).ToArray());
        var project = new EditorProject
        {
            AudioMixLayout = AudioChannelLayout.Mono,
            AudioTracks = [new() { AudioPath = source }],
            VideoTracks = [new()
            {
                Kind = TimelineTrackKind.Video, IsMuted = muted,
                Clips = [new() { SourcePath = source, SourceDurationSeconds = 1, Volume = 0.5 }],
            }],
        };
        AudioMasterAnalysisResult result = AudioMasterAnalysis.Analyse(project, CancellationToken.None);
        Assert.Empty(result.Warnings);
        Assert.Equal(muted ? 0.25 : 0.375, result.Overall.Peak, 6);
        Assert.Equal(muted ? 0.25 : 0.375, result.Overall.Rms, 6);
    }

    [Fact]
    public void WarnsWhenVideoAudioCannotBeDecoded()
    {
        string source = WriteSource(1, new float[100]);
        string missing = Path.Combine(Path.GetTempPath(), $"inkmotion-missing-video-{Guid.NewGuid():N}.mp4");
        var project = new EditorProject
        {
            AudioTracks = [new() { AudioPath = source }],
            VideoTracks = [new()
            {
                Kind = TimelineTrackKind.Video,
                Clips = [new() { SourcePath = missing, SourceDurationSeconds = 1 }],
            }],
        };
        AudioMasterAnalysisResult result = AudioMasterAnalysis.Analyse(project, CancellationToken.None);
        Assert.Contains(Path.GetFileName(missing), Assert.Single(result.Warnings));
    }

    private sealed class CallbackProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    public void Dispose()
    {
        foreach (string path in _files)
        {
            File.Delete(path);
        }
    }
}
