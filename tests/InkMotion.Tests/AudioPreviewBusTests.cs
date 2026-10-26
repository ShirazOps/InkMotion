using NAudio.Wave;
using Xunit;

namespace InkMotion.Tests;

public sealed class AudioPreviewBusTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public void BusMetersTheSummedSamplesAndLatchesClipping()
    {
        using var bus = new AudioPreviewBus();
        using AudioPreviewInput first = bus.AddInput(new ConstantSamples(0.6f));
        using AudioPreviewInput second = bus.AddInput(new ConstantSamples(0.6f));
        first.SetEnabled(true);
        second.SetEnabled(true);

        float[] output = new float[8];
        Assert.Equal(output.Length, bus.Read(output, 0, output.Length));
        Assert.All(output, sample => Assert.Equal(1.2f, sample, 5));
        AudioMeterReading reading = bus.TakeMasterReading();
        Assert.Equal(1.2f, reading.Peak, 5);
        Assert.Equal(1.2, reading.Rms, 5);
        Assert.True(reading.Clipped);
    }

    [Fact]
    public void BusExposesRecentPostMixSamplesForLiveAnalysis()
    {
        using var bus = new AudioPreviewBus();
        using AudioPreviewInput first = bus.AddInput(new ConstantSamples(0.25f));
        using AudioPreviewInput second = bus.AddInput(new ConstantSamples(0.5f));
        first.SetEnabled(true);
        second.SetEnabled(true);

        float[] output = new float[8];
        bus.Read(output, 0, output.Length);
        float[] recent = new float[output.Length];

        Assert.Equal(output.Length, bus.CopyRecentSamples(recent));
        Assert.Equal(output, recent);
        Assert.All(recent, sample => Assert.Equal(0.75f, sample, 5));
    }

    [Fact]
    public void BusMeasuresPhaseCancellationAndDisabledInputsAsSilence()
    {
        using var bus = new AudioPreviewBus();
        using AudioPreviewInput first = bus.AddInput(new ConstantSamples(0.75f));
        using AudioPreviewInput second = bus.AddInput(new ConstantSamples(-0.75f));
        first.SetEnabled(true);
        second.SetEnabled(true);

        float[] output = new float[8];
        bus.Read(output, 0, output.Length);
        Assert.All(output, sample => Assert.Equal(0, sample));
        AudioMeterReading cancelled = bus.TakeMasterReading();
        Assert.Equal(0, cancelled.Peak);
        Assert.Equal(0, cancelled.Rms);
        Assert.False(cancelled.Clipped);

        second.SetEnabled(false);
        bus.Read(output, 0, output.Length);
        Assert.All(output, sample => Assert.Equal(0.75f, sample));
        Assert.Equal(0.75f, bus.TakeMasterReading().Peak);
    }

    [Fact]
    public void ResettingMasterClipIndicatorKeepsTheNextIntervalIndependent()
    {
        using var bus = new AudioPreviewBus();
        using AudioPreviewInput input = bus.AddInput(new ConstantSamples(1.1f));
        input.SetEnabled(true);

        float[] output = new float[4];
        bus.Read(output, 0, output.Length);
        Assert.True(bus.TakeMasterReading(resetClip: true).Clipped);
        input.SetEnabled(false);

        bus.Read(output, 0, output.Length);
        AudioMeterReading silent = bus.TakeMasterReading();
        Assert.Equal(0, silent.Peak);
        Assert.Equal(0, silent.Rms);
        Assert.False(silent.Clipped);
    }

    [Fact]
    public void AudioClipPlayersShareThePostSumMeterAndRespectMuteAndSolo()
    {
        string firstPath = WriteStereoSource(0.6f, 0.6f);
        string secondPath = WriteStereoSource(-0.6f, -0.6f);
        using var bus = new AudioPreviewBus();
        var firstProject = new AudioTrackProject { AudioPath = firstPath, Solo = true };
        var secondProject = new AudioTrackProject { AudioPath = secondPath };
        using var first = new AudioClipPlayer(firstPath, firstProject, AudioChannelLayout.Stereo, bus);
        using var second = new AudioClipPlayer(secondPath, secondProject, AudioChannelLayout.Stereo, bus);
        first.Play();
        second.Play();

        float[] output = new float[8];
        bus.Read(output, 0, output.Length);
        Assert.All(output, sample => Assert.Equal(0, sample, 5));
        Assert.Equal(0, bus.TakeMasterReading().Peak);

        second.SetSoloSuppressed(firstProject.Solo && !secondProject.Solo);
        bus.Read(output, 0, output.Length);
        AudioMeterReading solo = bus.TakeMasterReading();
        Assert.Equal(0.6f, solo.Peak, 5);
        Assert.False(solo.Clipped);

        firstProject.Muted = true;
        first.Update(firstProject, AudioChannelLayout.Stereo);
        bus.Read(output, 0, output.Length);
        Assert.Equal(0, bus.TakeMasterReading().Peak);
    }

    [Fact]
    public void AudioClipPlayerSeekControlsSamplesMeasuredByTheMasterBus()
    {
        float[] samples = new float[96000];
        Array.Fill(samples, 0.5f, 48000, 48000);
        string path = WriteStereoSource(samples);

        using var bus = new AudioPreviewBus();
        using var player = new AudioClipPlayer(path, new AudioTrackProject { AudioPath = path },
            AudioChannelLayout.Stereo, bus);
        player.Seek(0.5);
        player.Play();

        float[] output = new float[8];
        bus.Read(output, 0, output.Length);
        Assert.All(output, sample => Assert.Equal(0.5f, sample, 4));
        Assert.Equal(0.5f, bus.TakeMasterReading().Peak, 4);
    }

    [Fact]
    public void ResampledAudioClipPlayerFlushesOldSamplesAfterSeek()
    {
        const int sourceRate = 44100;
        float[] samples = new float[sourceRate * 4];
        Array.Fill(samples, 0.25f, 0, sourceRate * 2);
        Array.Fill(samples, 0.5f, sourceRate * 2, sourceRate * 2);
        string path = WriteStereoSource(samples, sourceRate);
        using var bus = new AudioPreviewBus();
        using var player = new AudioClipPlayer(path, new AudioTrackProject { AudioPath = path },
            AudioChannelLayout.Stereo, bus);
        player.Play();
        bus.Read(new float[96], 0, 96);
        bus.TakeMasterReading();
        player.Seek(1);

        float[] output = new float[192];
        bus.Read(output, 0, output.Length);
        Assert.All(output.Skip(64), sample => Assert.Equal(0.5f, sample, 3));
        Assert.InRange(bus.TakeMasterReading().Peak, 0.49f, 0.6f);
    }

    [Fact]
    public void MasterPeakCanExceedEachSafeTrackPeak()
    {
        string path = WriteStereoSource(0.6f, 0.6f);
        using var bus = new AudioPreviewBus();
        using var first = new AudioClipPlayer(path, new AudioTrackProject { AudioPath = path },
            AudioChannelLayout.Stereo, bus);
        using var second = new AudioClipPlayer(path, new AudioTrackProject { AudioPath = path },
            AudioChannelLayout.Stereo, bus);
        first.Play();
        second.Play();

        float[] output = new float[8];
        bus.Read(output, 0, output.Length);
        Assert.Equal(0.6f, first.TakeMeterReading().Peak, 5);
        Assert.Equal(0.6f, second.TakeMeterReading().Peak, 5);
        AudioMeterReading master = bus.TakeMasterReading();
        Assert.Equal(1.2f, master.Peak, 5);
        Assert.True(master.Clipped);
    }

    private string WriteStereoSource(float left, float right, int frames = 4096)
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-live-master-{Guid.NewGuid():N}.wav");
        _files.Add(path);
        float[] samples = new float[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            samples[frame * 2] = left;
            samples[(frame * 2) + 1] = right;
        }

        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private string WriteStereoSource(float[] samples, int sampleRate = 48000)
    {
        string path = Path.Combine(Path.GetTempPath(), $"inkmotion-live-master-{Guid.NewGuid():N}.wav");
        _files.Add(path);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    public void Dispose()
    {
        foreach (string file in _files)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private sealed class ConstantSamples(float value) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, value, offset, count);
            return count;
        }
    }
}
