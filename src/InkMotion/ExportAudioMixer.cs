using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace InkMotion;

/// <summary>
/// Offline mix of every audible source in a project, rendered at the requested rate in the project's channel layout.
/// Audio tracks keep their trim, gain, fades, tempo, pitch, speaker balance and mono spatial pan. Solo isolates marked audio
/// tracks; otherwise video clips contribute their sound with clip volume and fades applied.
/// </summary>
public sealed class ExportAudioMixer : IDisposable
{
    private readonly List<IDisposable> _readers = [];
    private readonly List<string> _warnings = [];
    private readonly MixingSampleProvider _mixer;
    private readonly double _frameRate;

    public ExportAudioMixer(EditorProject project, int sampleRate = ExportPlanning.AudioSampleRate)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!AudioExportSettings.SampleRates.Contains(sampleRate))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        SampleRate = sampleRate;
        _frameRate = project.FrameRate;
        Layout = project.AudioMixLayout;
        Channels = AudioChannelLayouts.Get(Layout).ChannelCount;
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, Channels))
        {
            ReadFully = true,
        };

        try
        {
            bool hasSolo = project.AudioTracks.Any(track => track.Solo);
            foreach (AudioTrackProject track in project.AudioTracks)
            {
                AddAudioTrack(track, suppressed: hasSolo && !track.Solo);
            }

            foreach (TimelineTrackProject track in project.VideoTracks.Where(track =>
                !hasSolo && track.Kind == TimelineTrackKind.Video && !track.IsMuted))
            {
                foreach (TimelineClipProject clip in track.Clips)
                {
                    AddVideoClipAudio(clip);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public AudioChannelLayout Layout { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    public int SourceCount { get; private set; }

    /// <summary>Timeline time at which the last included source ends, including muted audio segments.</summary>
    public double DurationSeconds { get; private set; }

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Reads interleaved float samples; silence is returned once every source has finished.</summary>
    public int Read(float[] buffer, int offset, int count) => _mixer.Read(buffer, offset, count);

    public void Dispose()
    {
        foreach (IDisposable reader in _readers)
        {
            reader.Dispose();
        }

        _readers.Clear();
    }

    private void AddAudioTrack(AudioTrackProject track, bool suppressed)
    {
        if (string.IsNullOrWhiteSpace(track.AudioPath))
        {
            return;
        }

        string path = track.AudioPath;
        AudioFileReader reader;
        try
        {
            reader = new AudioFileReader(path);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            _warnings.Add($"Skipped audio {Path.GetFileName(path)}: {ex.Message}");
            return;
        }

        _readers.Add(reader);
        double sourceDuration = AudioSourcePosition.DurationSeconds(reader);
        double timelineDuration = AudioClipEditing.GetTimelineDuration(track, sourceDuration);
        if (!double.IsFinite(timelineDuration) || timelineDuration <= 0)
        {
            return;
        }

        if (track.Muted || suppressed)
        {
            DurationSeconds = Math.Max(DurationSeconds, AudioClipEditing.GetClipStartSeconds(track, _frameRate) + timelineDuration);
            SourceCount++;
            return;
        }

        var clip = new AudioClipSampleProvider(
            reader,
            sourceDuration,
            AudioClipEditing.CreateGainSettings(track),
            seconds => AudioSourcePosition.Seek(reader, seconds));
        var stretch = new TimeStretchSampleProvider(clip, clip.Seek);
        stretch.SetParameters(AudioClipEditing.GetTempoRatio(track), track.PitchSemitones);
        stretch.Seek(track.SourceInSeconds);
        var effects = new AudioEffectsSampleProvider(stretch, track.Effects);
        AddSource(effects, AudioClipEditing.GetClipStartSeconds(track, _frameRate), timelineDuration,
            track.Balance, track.SpatialPanEnabled, track.SpatialPanAzimuthDegrees);
    }

    private void AddVideoClipAudio(TimelineClipProject clip)
    {
        if (string.IsNullOrWhiteSpace(clip.SourcePath) || clip.Volume <= 0)
        {
            return;
        }

        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        if (duration <= 0)
        {
            return;
        }

        MediaFoundationReader reader;
        try
        {
            reader = new MediaFoundationReader(clip.SourcePath);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            // Video files without a sound track are common; they simply contribute no audio.
            return;
        }

        _readers.Add(reader);
        reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(clip.SourceInSeconds, reader.TotalTime.TotalSeconds));
        var envelope = new GainEnvelopeSampleProvider(
            reader.ToSampleProvider(),
            elapsed => ExportPlanning.GetVideoClipAudioGain(clip, elapsed));
        AddSource(envelope, VideoTimelineEditing.GetStartSeconds(clip, _frameRate), duration);
    }

    private void AddSource(ISampleProvider source, double startSeconds, double durationSeconds,
        double balance = 0, bool spatialPanEnabled = false, double spatialPanAzimuthDegrees = 0)
    {
        ISampleProvider mixed = new ChannelMixingSampleProvider(
            source,
            AudioMixControls.CreateMatrix(source.WaveFormat.Channels, Layout, balance,
                spatialPanEnabled, spatialPanAzimuthDegrees));
        if (mixed.WaveFormat.SampleRate != SampleRate)
        {
            mixed = new WdlResamplingSampleProvider(mixed, SampleRate);
        }

        double start = Math.Max(0, startSeconds);
        _mixer.AddMixerInput(new OffsetSampleProvider(mixed)
        {
            DelayBy = TimeSpan.FromSeconds(start),
            Take = TimeSpan.FromSeconds(durationSeconds),
        });
        SourceCount++;
        DurationSeconds = Math.Max(DurationSeconds, start + durationSeconds);
    }

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or COMException or
            FormatException or ArgumentException or NotSupportedException;

    /// <summary>Applies a time-varying gain, with time measured from the first sample read.</summary>
    private sealed class GainEnvelopeSampleProvider(ISampleProvider source, Func<double, double> gainAtSeconds) : ISampleProvider
    {
        private long _framePosition;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            int channels = WaveFormat.Channels;
            double sampleRate = WaveFormat.SampleRate;
            for (int sample = 0; sample + channels <= read; sample += channels, _framePosition++)
            {
                float gain = (float)gainAtSeconds(_framePosition / sampleRate);
                for (int channel = 0; channel < channels; channel++)
                {
                    buffer[offset + sample + channel] *= gain;
                }
            }

            return read;
        }
    }
}

/// <summary>
/// Writes PCM or float WAV, with an extensible speaker mask for layouts above stereo.
/// Writing stops at the 4 GB RIFF limit and <see cref="Truncated"/> is set.
/// </summary>
public sealed class MultichannelWaveWriter : IDisposable
{
    private readonly int _headerSize;
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private readonly FileStream _stream;
    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly bool _floatingPoint;
    private readonly Random _dither = new(0);
    private byte[] _scratch = [];
    private long _dataBytes;
    private bool _disposed;

    public MultichannelWaveWriter(string path, int sampleRate, int channels, int channelMask, int bitsPerSample = 24, bool floatingPoint = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        if (bitsPerSample is not (16 or 24 or 32) || (floatingPoint && bitsPerSample != 32))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
        }

        _channels = channels;
        _headerSize = channels > 2 ? 68 : 44;
        _bytesPerSample = bitsPerSample / 8;
        _floatingPoint = floatingPoint;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var header = new BinaryWriter(_stream, System.Text.Encoding.ASCII, leaveOpen: true);
        int blockAlign = channels * _bytesPerSample;
        header.Write("RIFF"u8);
        header.Write(0u);
        header.Write("WAVE"u8);
        header.Write("fmt "u8);
        header.Write(channels > 2 ? 40 : 16);
        header.Write((ushort)(channels > 2 ? 0xFFFE : floatingPoint ? 3 : 1));
        header.Write((ushort)channels);
        header.Write(sampleRate);
        header.Write(sampleRate * blockAlign);
        header.Write((ushort)blockAlign);
        header.Write((ushort)bitsPerSample);
        if (channels > 2)
        {
            header.Write((ushort)22);
            header.Write((ushort)bitsPerSample);
            header.Write(channelMask);
            header.Write((floatingPoint ? new Guid("00000003-0000-0010-8000-00aa00389b71") : PcmSubFormat).ToByteArray());
        }
        header.Write("data"u8);
        header.Write(0u);
    }

    public bool Truncated { get; private set; }

    public void Write(float[] samples, int count)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (Truncated || count <= 0)
        {
            return;
        }

        int frames = count / _channels;
        int bytes = frames * _channels * _bytesPerSample;
        if (_dataBytes + bytes > uint.MaxValue - _headerSize)
        {
            Truncated = true;
            return;
        }

        if (_scratch.Length < bytes)
        {
            _scratch = new byte[bytes];
        }

        for (int index = 0, target = 0; index < frames * _channels; index++, target += _bytesPerSample)
        {
            if (_floatingPoint)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(_scratch.AsSpan(target, 4), samples[index]);
                continue;
            }

            double scale = Math.Pow(2, _bytesPerSample * 8 - 1);
            double noise = _dither.NextDouble() - _dither.NextDouble();
            long value = (long)Math.Clamp(Math.Round(samples[index] * scale + noise), -scale, scale - 1);
            for (int part = 0; part < _bytesPerSample; part++)
            {
                _scratch[target + part] = (byte)(value >> (part * 8));
            }
        }

        _stream.Write(_scratch, 0, bytes);
        _dataBytes += bytes;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_dataBytes % 2 == 1)
        {
            _stream.WriteByte(0);
        }

        _stream.Position = 4;
        using (var writer = new BinaryWriter(_stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((uint)(_headerSize - 8 + _dataBytes + (_dataBytes % 2)));
            _stream.Position = _headerSize - 4;
            writer.Write((uint)_dataBytes);
        }

        _stream.Dispose();
    }
}
