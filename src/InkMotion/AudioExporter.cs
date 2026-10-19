using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace InkMotion;

public enum AudioExportCodec
{
    PcmWave,
    FloatWave,
    Mp3,
    Aac,
}

public sealed record AudioExportSettings(AudioExportCodec Codec, int SampleRate, int BitsPerSample = 24, int BitRate = 192000)
{
    public static IReadOnlyList<int> SampleRates { get; } = Array.AsReadOnly(new[] { 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000 });

    public bool IsWave => Codec is AudioExportCodec.PcmWave or AudioExportCodec.FloatWave;

    public string Extension => Codec switch
    {
        AudioExportCodec.PcmWave or AudioExportCodec.FloatWave => ".wav",
        AudioExportCodec.Mp3 => ".mp3",
        AudioExportCodec.Aac => ".m4a",
        _ => throw new ArgumentOutOfRangeException(nameof(Codec)),
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Codec) || !SampleRates.Contains(SampleRate) ||
            (!IsWave && SampleRate is not (44100 or 48000)) ||
            (Codec == AudioExportCodec.PcmWave && BitsPerSample is not (16 or 24 or 32)) ||
            (Codec == AudioExportCodec.FloatWave && BitsPerSample != 32) ||
            (!IsWave && BitRate is not (128000 or 192000 or 256000 or 320000)))
        {
            throw new ArgumentException("Unsupported audio export settings. Lossy formats require 44.1 or 48 kHz and 128–320 kbps.");
        }
    }
}

public sealed record AudioExportResult(int SampleRate, int Channels, IReadOnlyList<string> Warnings);

public static class AudioExporter
{
    public static AudioExportResult Export(EditorProject project, AudioExportSettings settings, string path,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        settings.Validate();
        ExportPlanning.ValidateOutputPath(project, path);
        using var mixer = new ExportAudioMixer(project, settings.SampleRate);
        if (mixer.SourceCount == 0)
        {
            throw new InvalidOperationException("Nothing to export: add audio or an unmuted video clip with audio.");
        }

        var warnings = mixer.Warnings.ToList();
        ISampleProvider source = new FiniteMix(mixer, cancellationToken);
        AudioChannelLayout layout = mixer.Layout;
        if (!settings.IsWave && source.WaveFormat.Channels > 2)
        {
            source = new ChannelMixingSampleProvider(source,
                AudioChannelLayouts.CreateMixMatrix(layout, AudioChannelLayout.Stereo));
            layout = AudioChannelLayout.Stereo;
            warnings.Add("The compressed audio file contains a stereo downmix. Use WAV to preserve the full channel layout.");
        }

        bool completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (settings.IsWave)
            {
                var info = AudioChannelLayouts.Get(layout);
                long frames = (long)Math.Round(mixer.DurationSeconds * settings.SampleRate);
                long bytes = frames * info.ChannelCount * (settings.BitsPerSample / 8);
                if (bytes > uint.MaxValue - 68)
                {
                    throw new InvalidOperationException("This WAV would exceed the 4 GB RIFF limit. Choose a lower rate/depth or shorten the mix.");
                }

                using var writer = new MultichannelWaveWriter(path, settings.SampleRate, info.ChannelCount, info.ChannelMask,
                    settings.BitsPerSample, settings.Codec == AudioExportCodec.FloatWave);
                float[] buffer = new float[4096 * info.ChannelCount];
                long remaining = frames * info.ChannelCount;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0)
                    {
                        throw new InvalidOperationException("The audio resampler stopped before the end of the mix.");
                    }

                    writer.Write(buffer, read);
                    remaining -= read;
                    progress?.Report(1 - remaining / (double)(frames * info.ChannelCount));
                }
            }
            else
            {
                var provider = new SampleToWaveProvider16(source);
                if (settings.Codec == AudioExportCodec.Mp3)
                {
                    MediaFoundationEncoder.EncodeToMp3(provider, path, settings.BitRate);
                }
                else
                {
                    MediaFoundationEncoder.EncodeToAac(provider, path, settings.BitRate);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            completed = true;
            progress?.Report(1);
            return new(settings.SampleRate, source.WaveFormat.Channels, warnings);
        }
        finally
        {
            if (!completed && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class FiniteMix(ExportAudioMixer mixer, CancellationToken cancellationToken) : ISampleProvider
    {
        private long _remaining = (long)Math.Round(mixer.DurationSeconds * mixer.SampleRate) * mixer.Channels;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(mixer.SampleRate, mixer.Channels);

        public int Read(float[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = mixer.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }
    }
}
