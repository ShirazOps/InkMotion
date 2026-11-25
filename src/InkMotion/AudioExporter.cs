using System.IO.Compression;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace InkMotion;

public enum AudioExportCodec
{
    PcmWave,
    FloatWave,
    Mp3,
    Aac,
    Flac,
    Opus,
    Wma,
}

public sealed record AudioExportSettings(AudioExportCodec Codec, int SampleRate, int BitsPerSample = 24,
    int BitRate = 192000, string? FFmpegPath = null, bool ExportStems = false,
    bool TruePeakLimiterEnabled = false, double TruePeakCeilingDecibels = -1)
{
    public static IReadOnlyList<int> SampleRates { get; } = Array.AsReadOnly(new[] { 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000 });

    public bool IsWave => Codec is AudioExportCodec.PcmWave or AudioExportCodec.FloatWave;
    public bool IsLossless => IsWave || Codec == AudioExportCodec.Flac;
    public bool UsesFFmpeg => Codec is AudioExportCodec.Flac or AudioExportCodec.Opus or AudioExportCodec.Wma;

    public string Extension => ExportStems ? ".zip" : Codec switch
    {
        AudioExportCodec.PcmWave or AudioExportCodec.FloatWave => ".wav",
        AudioExportCodec.Mp3 => ".mp3",
        AudioExportCodec.Aac => ".m4a",
        AudioExportCodec.Flac => ".flac",
        AudioExportCodec.Opus => ".opus",
        AudioExportCodec.Wma => ".wma",
        _ => throw new ArgumentOutOfRangeException(nameof(Codec)),
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Codec) || !SampleRates.Contains(SampleRate) ||
            (ExportStems && Codec is not (AudioExportCodec.PcmWave or AudioExportCodec.FloatWave)) ||
            (!IsLossless && SampleRate is not (44100 or 48000)) ||
            (Codec == AudioExportCodec.Opus && SampleRate != 48000) ||
            (Codec == AudioExportCodec.PcmWave && BitsPerSample is not (16 or 24 or 32)) ||
            (Codec == AudioExportCodec.FloatWave && BitsPerSample != 32) ||
            (Codec == AudioExportCodec.Flac && BitsPerSample is not (16 or 24)) ||
            !double.IsFinite(TruePeakCeilingDecibels) || TruePeakCeilingDecibels is < -24 or > 0 ||
            (!IsLossless && BitRate is not (128000 or 160000 or 192000 or 256000 or 320000)) ||
            (UsesFFmpeg && (string.IsNullOrWhiteSpace(FFmpegPath) || !Path.IsPathFullyQualified(FFmpegPath))))
        {
            throw new ArgumentException("Unsupported audio export settings. FLAC requires 16/24-bit PCM; Opus requires 48 kHz. FLAC/Opus/WMA require an absolute path to a local FFmpeg executable. Lossy formats use 128–320 kbps.");
        }
    }
}

public sealed record AudioExportResult(int SampleRate, int Channels, IReadOnlyList<string> Warnings, int StemCount = 0);

public static class AudioExporter
{
    public static AudioExportResult Export(EditorProject project, AudioExportSettings settings, string path,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        settings.Validate();
        ExportPlanning.ValidateOutputPath(project, path);
        if (settings.ExportStems)
        {
            return ExportStems(project, settings, path, progress, cancellationToken);
        }

        using var mixer = new ExportAudioMixer(project, settings.SampleRate);
        if (mixer.SourceCount == 0)
        {
            throw new InvalidOperationException("Nothing to export: add audio or an unmuted video clip with audio.");
        }

        var warnings = mixer.Warnings.ToList();
        ISampleProvider source = new FiniteMix(mixer, cancellationToken);
        AudioChannelLayout layout = mixer.Layout;
        if (!settings.IsLossless && source.WaveFormat.Channels > 2)
        {
            source = new ChannelMixingSampleProvider(source,
                AudioChannelLayouts.CreateMixMatrix(layout, AudioChannelLayout.Stereo));
            layout = AudioChannelLayout.Stereo;
            warnings.Add("The compressed audio file contains a stereo downmix. Use WAV to preserve the full channel layout.");
        }
        if (settings.Codec == AudioExportCodec.Flac && source.WaveFormat.Channels > 8)
        {
            throw new InvalidOperationException("FLAC supports at most 8 channels. Choose WAV to preserve this project layout.");
        }
        if (settings.TruePeakLimiterEnabled)
        {
            source = new TruePeakLimiterSampleProvider(source, settings.TruePeakCeilingDecibels);
        }

        bool completed = false;
        bool outputStarted = false;
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

                outputStarted = true;
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
            else if (settings.UsesFFmpeg)
            {
                FFmpegAudioEncoder.Encode(source, layout, mixer.DurationSeconds, settings, path, progress, cancellationToken);
            }
            else
            {
                var provider = new SampleToWaveProvider16(source);
                AudioEncoderCapabilities.EncodeExact(provider, path, settings, () => outputStarted = true);
            }

            if (!settings.UsesFFmpeg) cancellationToken.ThrowIfCancellationRequested();
            completed = true;
            progress?.Report(1);
            return new(settings.SampleRate, source.WaveFormat.Channels, warnings);
        }
        finally
        {
            if (!completed && outputStarted && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static AudioExportResult ExportStems(EditorProject project, AudioExportSettings settings, string path,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Choose a ZIP output path for the audio stems.", nameof(path));
        }

        EditorProject baseline = CloneProject(project);
        baseline.AudioBuses ??= [];
        foreach (AudioBusProject bus in baseline.AudioBuses)
        {
            bus.Solo = false;
        }

        double durationSeconds;
        int channels;
        var warnings = new List<string>();
        using (var fullMix = new ExportAudioMixer(baseline, settings.SampleRate))
        {
            if (fullMix.SourceCount == 0)
            {
                throw new InvalidOperationException("Nothing to export: add audio or an unmuted video clip with audio.");
            }

            durationSeconds = fullMix.DurationSeconds;
            channels = fullMix.Channels;
            warnings.AddRange(fullMix.Warnings);
        }

        var stems = new List<(string EntryName, string? BusId)>
        {
            ("Master (direct).wav", null),
        };
        for (int index = 0; index < baseline.AudioBuses.Count; index++)
        {
            AudioBusProject bus = baseline.AudioBuses[index];
            string safeName = string.Concat((bus.Name ?? string.Empty).Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-')).Trim('-', '.');
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = "Bus";
            }

            string safeId = string.Concat((bus.Id ?? string.Empty).Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-')).Trim('-', '.');
            if (string.IsNullOrWhiteSpace(safeId))
            {
                safeId = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            stems.Add(($"{safeName} [{safeId}].wav", bus.Id));
        }

        bool completed = false;
        bool outputStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                outputStarted = true;
                using var archive = new ZipArchive(output, ZipArchiveMode.Create);
                for (int index = 0; index < stems.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (string entryName, string? busId) = stems[index];
                    EditorProject stemProject = CloneProject(baseline);
                    if (busId is null)
                    {
                        foreach (AudioBusProject bus in stemProject.AudioBuses)
                        {
                            bus.Muted = true;
                            bus.Solo = false;
                        }
                    }
                    else
                    {
                        foreach (AudioBusProject bus in stemProject.AudioBuses)
                        {
                            bus.Solo = string.Equals(bus.Id, busId, StringComparison.Ordinal);
                        }
                    }

                    WriteStem(archive, entryName, stemProject, settings, durationSeconds,
                        index, stems.Count, progress, cancellationToken, warnings);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            completed = true;
            progress?.Report(1);
            return new(settings.SampleRate, channels, warnings.Distinct(StringComparer.Ordinal).ToArray(), stems.Count);
        }
        finally
        {
            if (!completed && outputStarted && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void WriteStem(ZipArchive archive, string entryName, EditorProject project,
        AudioExportSettings settings, double durationSeconds, int stemIndex, int stemCount,
        IProgress<double>? progress, CancellationToken cancellationToken, List<string> warnings)
    {
        using var mixer = new ExportAudioMixer(project, settings.SampleRate);
        warnings.AddRange(mixer.Warnings);
        AudioChannelLayoutInfo info = AudioChannelLayouts.Get(mixer.Layout);
        long frames = (long)Math.Round(durationSeconds * settings.SampleRate);
        long bytes = checked(frames * info.ChannelCount * (settings.BitsPerSample / 8));
        int headerSize = info.ChannelCount > 2 ? 68 : 44;
        if (bytes > uint.MaxValue - headerSize)
        {
            throw new InvalidOperationException("A stem would exceed the 4 GB WAV limit. Choose a lower rate/depth or shorten the mix.");
        }

        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using Stream destination = entry.Open();
        using var writer = new MultichannelWaveWriter(destination, settings.SampleRate, info.ChannelCount,
            info.ChannelMask, frames, settings.BitsPerSample, settings.Codec == AudioExportCodec.FloatWave, leaveOpen: true);
        ISampleProvider source = new FiniteMix(mixer, cancellationToken, durationSeconds);
        if (settings.TruePeakLimiterEnabled)
        {
            source = new TruePeakLimiterSampleProvider(source, settings.TruePeakCeilingDecibels);
        }
        int samplesPerBuffer = 4096 * info.ChannelCount;
        float[] buffer = new float[samplesPerBuffer];
        long remaining = frames * info.ChannelCount;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = source.Read(buffer, 0, (int)Math.Min(samplesPerBuffer, remaining));
            if (read == 0)
            {
                throw new InvalidOperationException("An audio stem stopped before the end of the aligned mix.");
            }

            writer.Write(buffer, read);
            if (writer.Truncated)
            {
                throw new InvalidOperationException("An audio stem exceeded the WAV size limit.");
            }

            remaining -= read;
            double stemProgress = 1 - remaining / (double)(frames * info.ChannelCount);
            progress?.Report((stemIndex + stemProgress) / stemCount);
        }
    }

    private static EditorProject CloneProject(EditorProject project) =>
        EditorProjectSerialiser.Deserialise(EditorProjectSerialiser.Serialise(project));

    private sealed class FiniteMix(ExportAudioMixer mixer, CancellationToken cancellationToken,
        double? durationSeconds = null) : ISampleProvider
    {
        private long _remaining = (long)Math.Round((durationSeconds ?? mixer.DurationSeconds) * mixer.SampleRate) * mixer.Channels;

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
