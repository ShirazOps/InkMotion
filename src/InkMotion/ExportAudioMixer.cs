using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace InkMotion;

/// <summary>
/// Offline mix of every audible source in a project, rendered at 48 kHz in the project's channel layout.
/// Audio tracks keep their trim, gain, fades, tempo and pitch; video clips contribute their own sound with the
/// clip volume and fades applied.
/// </summary>
public sealed class ExportAudioMixer : IDisposable
{
    private readonly List<IDisposable> _readers = [];
    private readonly List<string> _warnings = [];
    private readonly MixingSampleProvider _mixer;
    private readonly double _frameRate;

    public ExportAudioMixer(EditorProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _frameRate = project.FrameRate;
        Layout = project.AudioMixLayout;
        Channels = AudioChannelLayouts.Get(Layout).ChannelCount;
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(ExportPlanning.AudioSampleRate, Channels))
        {
            ReadFully = true,
        };

        try
        {
            foreach (AudioTrackProject track in project.AudioTracks)
            {
                AddAudioTrack(track);
            }

            foreach (TimelineTrackProject track in project.VideoTracks.Where(track => track.Kind == TimelineTrackKind.Video && !track.IsMuted))
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

    public int SourceCount { get; private set; }

    /// <summary>Timeline time at which the last audible source ends.</summary>
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

    private void AddAudioTrack(AudioTrackProject track)
    {
        if (track.Muted || string.IsNullOrWhiteSpace(track.AudioPath))
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
        double sourceDuration = reader.TotalTime.TotalSeconds;
        double timelineDuration = AudioClipEditing.GetTimelineDuration(track, sourceDuration);
        if (!double.IsFinite(timelineDuration) || timelineDuration <= 0)
        {
            return;
        }

        var clip = new AudioClipSampleProvider(
            reader,
            sourceDuration,
            AudioClipEditing.CreateGainSettings(track),
            seconds => reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(seconds, sourceDuration)));
        var stretch = new TimeStretchSampleProvider(clip, clip.Seek);
        stretch.SetParameters(AudioClipEditing.GetTempoRatio(track), track.PitchSemitones);
        stretch.Seek(track.SourceInSeconds);
        AddSource(stretch, AudioClipEditing.GetClipStartSeconds(track, _frameRate), timelineDuration);
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

    private void AddSource(ISampleProvider source, double startSeconds, double durationSeconds)
    {
        ISampleProvider mixed = new ChannelMixingSampleProvider(
            source,
            AudioChannelLayouts.CreateMixMatrix(source.WaveFormat.Channels, Layout));
        if (mixed.WaveFormat.SampleRate != ExportPlanning.AudioSampleRate)
        {
            mixed = new WdlResamplingSampleProvider(mixed, ExportPlanning.AudioSampleRate);
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
/// Writes a 24-bit WAVE_FORMAT_EXTENSIBLE file with an explicit speaker mask, so layouts up to 12.1 keep every
/// discrete channel. Writing stops at the 4 GB RIFF limit and <see cref="Truncated"/> is set.
/// </summary>
public sealed class MultichannelWaveWriter : IDisposable
{
    private const int HeaderSize = 68;
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private readonly FileStream _stream;
    private readonly int _channels;
    private byte[] _scratch = [];
    private long _dataBytes;
    private bool _disposed;

    public MultichannelWaveWriter(string path, int sampleRate, int channels, int channelMask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        _channels = channels;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var header = new BinaryWriter(_stream, System.Text.Encoding.ASCII, leaveOpen: true);
        int blockAlign = channels * 3;
        header.Write("RIFF"u8);
        header.Write(0u);
        header.Write("WAVE"u8);
        header.Write("fmt "u8);
        header.Write(40);
        header.Write((ushort)0xFFFE);
        header.Write((ushort)channels);
        header.Write(sampleRate);
        header.Write(sampleRate * blockAlign);
        header.Write((ushort)blockAlign);
        header.Write((ushort)24);
        header.Write((ushort)22);
        header.Write((ushort)24);
        header.Write(channelMask);
        header.Write(PcmSubFormat.ToByteArray());
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
        int bytes = frames * _channels * 3;
        if (_dataBytes + bytes > uint.MaxValue - HeaderSize)
        {
            Truncated = true;
            return;
        }

        if (_scratch.Length < bytes)
        {
            _scratch = new byte[bytes];
        }

        for (int index = 0, target = 0; index < frames * _channels; index++, target += 3)
        {
            int value = (int)Math.Round(Math.Clamp(samples[index], -1f, 1f) * 8388607f);
            _scratch[target] = (byte)value;
            _scratch[target + 1] = (byte)(value >> 8);
            _scratch[target + 2] = (byte)(value >> 16);
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
            writer.Write((uint)(HeaderSize - 8 + _dataBytes + (_dataBytes % 2)));
            _stream.Position = HeaderSize - 4;
            writer.Write((uint)_dataBytes);
        }

        _stream.Dispose();
    }
}
