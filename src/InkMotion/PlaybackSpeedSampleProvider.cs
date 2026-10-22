using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace InkMotion;

public sealed class PlaybackSpeedSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _resampled;

    public PlaybackSpeedSampleProvider(ISampleProvider source, double speed)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(speed) || speed is < VideoTimelineEditing.MinimumPlaybackSpeed or > VideoTimelineEditing.MaximumPlaybackSpeed)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }

        // Keep the original nominal rate so the resampled sample count changes playback duration and pitch.
        WaveFormat = source.WaveFormat;
        int adjustedSampleRate = checked((int)Math.Round(source.WaveFormat.SampleRate / speed));
        _resampled = adjustedSampleRate == source.WaveFormat.SampleRate
            ? source
            : new WdlResamplingSampleProvider(source, adjustedSampleRate);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count) => _resampled.Read(buffer, offset, count);
}
