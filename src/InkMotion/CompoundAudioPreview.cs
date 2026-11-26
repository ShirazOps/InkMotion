using NAudio.Wave;

namespace InkMotion;

internal sealed class CompoundAudioPreview : ISampleProvider, IDisposable
{
    private readonly ExportAudioMixer _mixer;
    private readonly TimelineClipProject _clip;
    private readonly long _endFrame;
    private long _elapsedFrame;
    private int _audible = 1;

    private CompoundAudioPreview(ExportAudioMixer mixer, TimelineClipProject clip, long elapsedFrame)
    {
        _mixer = mixer;
        _clip = clip;
        _elapsedFrame = elapsedFrame;
        _endFrame = (long)Math.Round(clip.DurationSeconds * mixer.SampleRate);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mixer.SampleRate, mixer.Channels);
    }

    public WaveFormat WaveFormat { get; }
    public AudioChannelLayout Layout => _mixer.Layout;
    public IReadOnlyList<string> Warnings => _mixer.Warnings;

    public static CompoundAudioPreview? Prepare(TimelineClipProject clip, double elapsedSeconds,
        CancellationToken cancellationToken)
    {
        EditorProject nested = clip.NestedProject ??
            throw new InvalidDataException("A compound clip is missing its nested timeline.");
        var mixer = new ExportAudioMixer(nested, AudioPreviewBus.SampleRate);
        try
        {
            if (mixer.SourceCount == 0)
            {
                if (mixer.Warnings.Count > 0)
                {
                    throw new InvalidOperationException(string.Join(" ", mixer.Warnings));
                }
                mixer.Dispose();
                return null;
            }

            long elapsedFrames = (long)Math.Round(Math.Clamp(elapsedSeconds, 0, clip.DurationSeconds) * mixer.SampleRate);
            long sourceFrames = (long)Math.Round(clip.SourceInSeconds * mixer.SampleRate) + elapsedFrames;
            float[] buffer = new float[4096 * mixer.Channels];
            while (sourceFrames > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int frames = (int)Math.Min(sourceFrames, 4096);
                int read = mixer.Read(buffer, 0, frames * mixer.Channels);
                if (read != frames * mixer.Channels)
                {
                    throw new InvalidOperationException("The compound audio mixer stopped while preparing playback.");
                }
                sourceFrames -= frames;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new CompoundAudioPreview(mixer, clip, elapsedFrames);
        }
        catch
        {
            mixer.Dispose();
            throw;
        }
    }

    public void SetAudible(bool audible) => Volatile.Write(ref _audible, audible ? 1 : 0);

    public int Read(float[] buffer, int offset, int count)
    {
        int channels = WaveFormat.Channels;
        int requested = count - count % channels;
        int frames = (int)Math.Min(requested / channels, Math.Max(0, _endFrame - _elapsedFrame));
        int read = frames > 0 ? _mixer.Read(buffer, offset, frames * channels) : 0;
        bool audible = Volatile.Read(ref _audible) != 0;
        for (int sample = 0; sample + channels <= read; sample += channels, _elapsedFrame++)
        {
            float gain = audible
                ? (float)CompoundTimelines.GetAudioGain(_clip, (double)_elapsedFrame / WaveFormat.SampleRate)
                : 0;
            for (int channel = 0; channel < channels; channel++)
            {
                buffer[offset + sample + channel] *= gain;
            }
        }
        Array.Clear(buffer, offset + read, requested - read);
        return requested;
    }

    public void Dispose() => _mixer.Dispose();
}
