using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioClipEditingTests
{
    private const double FrameRate = 24;

    [Fact]
    public void Split_DividesClipAtPlayheadAndKeepsOuterFades()
    {
        var track = new AudioTrackProject
        {
            AudioPath = @"C:\Media\voice.wav",
            LinkedVideoClipId = "clip-1",
            StartFrame = 1,
            FadeInSeconds = 0.5,
            FadeOutSeconds = 1,
            GainDecibels = 2,
            Title = "Voice",
        };

        AudioTrackProject? right = AudioClipEditing.Split(track, playheadFrameNumber: 49, FrameRate, sourceDurationSeconds: 10);

        Assert.NotNull(right);
        Assert.Equal(0, track.SourceInSeconds);
        Assert.Equal(2, track.SourceOutSeconds);
        Assert.Equal(0.5, track.FadeInSeconds);
        Assert.Equal(0, track.FadeOutSeconds);
        Assert.Equal(49, right.StartFrame);
        Assert.Equal(2, right.SourceInSeconds);
        Assert.Equal(10, right.SourceOutSeconds);
        Assert.Equal(0, right.FadeInSeconds);
        Assert.Equal(1, right.FadeOutSeconds);
        Assert.Equal(2, right.GainDecibels);
        Assert.Equal("Voice", right.Title);
        Assert.Equal(track.AudioPath, right.AudioPath);
        Assert.Equal("clip-1", track.LinkedVideoClipId);
        Assert.Null(right.LinkedVideoClipId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(241)]
    [InlineData(400)]
    public void Split_RejectsPlayheadOutsideClipInterior(int playheadFrameNumber)
    {
        var track = new AudioTrackProject { StartFrame = 1 };

        Assert.Null(AudioClipEditing.Split(track, playheadFrameNumber, FrameRate, sourceDurationSeconds: 10));
        Assert.Null(track.SourceOutSeconds);
    }

    [Fact]
    public void TryTrimStart_MovesClipStartAndSourceInPoint()
    {
        var track = new AudioTrackProject { StartFrame = 25, SourceInSeconds = 1 };

        Assert.True(AudioClipEditing.TryTrimStart(track, playheadFrameNumber: 49, FrameRate, sourceDurationSeconds: 6));

        Assert.Equal(49, track.StartFrame);
        Assert.Equal(2, track.SourceInSeconds);
        Assert.Equal(6, track.SourceOutSeconds);
        Assert.Equal(4, AudioClipEditing.GetClipDuration(track, 6));
    }

    [Fact]
    public void TryTrimEnd_SetsOutPointAndClampsFades()
    {
        var track = new AudioTrackProject { StartFrame = 1, FadeInSeconds = 2, FadeOutSeconds = 2 };

        Assert.True(AudioClipEditing.TryTrimEnd(track, playheadFrameNumber: 25, FrameRate, sourceDurationSeconds: 10));

        Assert.Equal(1, track.SourceOutSeconds);
        Assert.Equal(0.5, track.FadeInSeconds, precision: 6);
        Assert.Equal(0.5, track.FadeOutSeconds, precision: 6);
    }

    [Fact]
    public void GetGain_AppliesVolumeGainFadesAndTrimRange()
    {
        var track = new AudioTrackProject
        {
            Volume = 0.5,
            GainDecibels = 20 * Math.Log10(2),
            SourceInSeconds = 1,
            SourceOutSeconds = 5,
            FadeInSeconds = 1,
            FadeOutSeconds = 2,
        };

        Assert.Equal(0, AudioClipEditing.GetGain(track, 0.5, 10));
        Assert.Equal(0.5, AudioClipEditing.GetGain(track, 1.5, 10), precision: 6);
        Assert.Equal(1, AudioClipEditing.GetGain(track, 2.5, 10), precision: 6);
        Assert.Equal(0.5, AudioClipEditing.GetGain(track, 4, 10), precision: 6);
        Assert.Equal(0, AudioClipEditing.GetGain(track, 5, 10));

        track.Muted = true;
        Assert.Equal(0, AudioClipEditing.GetGain(track, 2.5, 10));
    }

    [Theory]
    [InlineData(0.5f, 5.0206)]
    [InlineData(1f, -1)]
    [InlineData(0.0001f, 24)]
    [InlineData(0f, 0)]
    public void CalculateNormalisationGain_TargetsMinusOneDecibelPeak(float peak, double expected)
    {
        Assert.Equal(expected, AudioClipEditing.CalculateNormalisationGain(peak), precision: 3);
    }

    [Fact]
    public void SampleProvider_AppliesEnvelopeClipsAndPadsSilence()
    {
        var source = new ConstantSampleProvider(sampleRate: 4, channels: 2, value: 0.6f, frames: 6);
        var settings = new AudioClipGainSettings(
            SourceInSeconds: 0.25,
            SourceOutSeconds: 1.25,
            FadeInSeconds: 0.5,
            FadeOutSeconds: 0,
            LinearGain: 2);
        var clip = new AudioClipSampleProvider(source, sourceDurationSeconds: 1.5, settings);
        var buffer = new float[16];

        int read = clip.Read(buffer, 0, buffer.Length);

        Assert.Equal(16, read);
        Assert.Equal(new float[] { 0, 0, 0, 0, 0.6f, 0.6f, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0 }, buffer.Select(value => MathF.Round(value, 4)));
        Assert.Equal(2, clip.PositionSeconds);
    }

    [Fact]
    public void SampleProvider_SeekUpdatesPositionAndCallsSource()
    {
        double? sought = null;
        var clip = new AudioClipSampleProvider(
            new ConstantSampleProvider(sampleRate: 100, channels: 1, value: 0.1f, frames: 1000),
            sourceDurationSeconds: 10,
            new AudioClipGainSettings(0, null, 0, 0, 1),
            seconds => sought = seconds);

        clip.Seek(3.5);

        Assert.Equal(3.5, sought);
        Assert.Equal(3.5, clip.PositionSeconds);
    }

    private sealed class ConstantSampleProvider(int sampleRate, int channels, float value, int frames) : ISampleProvider
    {
        private int _remainingSamples = frames * channels;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, _remainingSamples);
            Array.Fill(buffer, value, offset, read);
            _remainingSamples -= read;
            return read;
        }
    }
}
