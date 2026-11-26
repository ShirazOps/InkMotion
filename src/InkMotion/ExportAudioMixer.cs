using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace InkMotion;

/// <summary>
/// Offline mix of every audible source in a project, rendered at the requested rate in the project's channel layout.
/// Audio tracks keep their trim, gain, fades, tempo, pitch, speaker balance and mono spatial pan. Solo isolates marked audio
/// tracks; otherwise video clips contribute their sound with clip volume and fades applied. Sources routed to a submix bus are
/// summed in the project layout, processed by the bus effects and gain, and routed through parent buses to the master.
/// </summary>
public sealed class ExportAudioMixer : IDisposable
{
    private readonly List<IDisposable> _readers = [];
    private readonly List<string> _warnings = [];
    private readonly List<Vst3InsertChainSampleProvider> _vst3Providers = [];
    private readonly MixingSampleProvider _mixer;
    private readonly Dictionary<string, MixingSampleProvider> _busMixers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _busEndSeconds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _busVst3TailDurationSeconds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _buildingBusMixers = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<AudioBusProject> _buses;
    private readonly double _frameRate;
    private readonly bool _reportVideoReadFailures;

    public ExportAudioMixer(EditorProject project, int sampleRate = ExportPlanning.AudioSampleRate,
        bool reportVideoReadFailures = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!AudioExportSettings.SampleRates.Contains(sampleRate))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        SampleRate = sampleRate;
        _frameRate = project.FrameRate;
        _reportVideoReadFailures = reportVideoReadFailures;
        _buses = project.AudioBuses ?? [];
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
                bool trackSuppressed = hasSolo && !track.Solo;
                AddAudioTrack(track, trackSuppressed || !AudioBuses.IsAudible(_buses, track.BusId),
                    track.BusId, outputGain: 1);
                foreach (AudioAuxSendProject send in track.AuxSends ?? [])
                {
                    AddAudioTrack(track,
                        trackSuppressed || !AudioBuses.IsAudible(_buses, send.BusId),
                        send.BusId, AudioClipEditing.DecibelsToLinear(send.GainDecibels));
                }
            }

            foreach (TimelineTrackProject track in project.VideoTracks.Where(track =>
                !hasSolo && track.Kind == TimelineTrackKind.Video && !track.IsMuted &&
                AudioBuses.IsAudible(_buses, track.AudioBusId)))
            {
                foreach (TimelineClipProject clip in track.Clips)
                {
                    VideoAudioTransition transition = VideoTransitions.GetAudioTransition(track, clip, _frameRate);
                    AddVideoClipAudio(clip, transition, track.AudioBusId);
                }
            }

            foreach (AudioBusProject bus in _buses.Where(bus => bus.ParentBusId is null))
            {
                if (GetBusOutputEndSeconds(bus) is double busEndSeconds)
                {
                    DurationSeconds = Math.Max(DurationSeconds, busEndSeconds);
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

    public IReadOnlyList<string> Warnings => _warnings
        .Concat(_vst3Providers.Select(provider => provider.FailureMessage).OfType<string>())
        .Distinct(StringComparer.Ordinal)
        .ToArray();

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

    private void AddAudioTrack(AudioTrackProject track, bool suppressed, string? outputBusId, double outputGain)
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
        AudioEffectSettings effectsSettings = track.Effects;
        if (effectsSettings.NoiseReductionEnabled &&
            effectsSettings.NoiseProfile is AudioNoiseProfile profile &&
            !profile.MatchesSource(path, reader.WaveFormat.SampleRate, reader.WaveFormat.Channels))
        {
            effectsSettings = effectsSettings with { NoiseReductionEnabled = false };
            _warnings.Add($"Spectral noise reduction was bypassed for {Path.GetFileName(path)} because its saved profile does not match this source.");
        }

        var finiteSource = new FiniteSampleProvider(stretch, timelineDuration);
        var effects = new AudioEffectsSampleProvider(finiteSource, effectsSettings);
        var vst3 = new Vst3InsertChainSampleProvider(effects, track.Vst3Plugins, preservePluginTails: true);
        _readers.Add(vst3);
        _vst3Providers.Add(vst3);
        ISampleProvider output = outputGain == 1
            ? vst3
            : new VolumeSampleProvider(vst3) { Volume = (float)outputGain };
        AddSource(output, AudioClipEditing.GetClipStartSeconds(track, _frameRate),
            timelineDuration + effects.TailDurationSeconds + vst3.TailDurationSeconds,
            track.Balance, track.SpatialPanEnabled, track.SpatialPanAzimuthDegrees, outputBusId);
    }

    private void AddVideoClipAudio(TimelineClipProject clip, VideoAudioTransition transition, string? busId)
    {
        if (string.IsNullOrWhiteSpace(clip.SourcePath) || clip.Volume <= 0 ||
            (clip.IsReversed && !clip.ReverseEmbeddedAudio))
        {
            return;
        }

        double duration = VideoTimelineEditing.GetDurationSeconds(clip, TimelineTrackKind.Video);
        VideoAudioSourceRange sourceRange = VideoTransitions.GetAudioSourceRange(clip, transition);
        if (duration <= 0 || sourceRange.DurationSeconds(clip.PlaybackSpeed) <= 0)
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
            if (_reportVideoReadFailures)
            {
                _warnings.Add($"Skipped video audio {Path.GetFileName(clip.SourcePath)} (no decodable audio track): {ex.Message}");
            }
            return;
        }

        _readers.Add(reader);
        double sourceDuration = reader.TotalTime.TotalSeconds;
        double sourceStart = Math.Clamp(sourceRange.StartSeconds, 0, sourceDuration);
        double sourceEnd = Math.Clamp(sourceRange.EndSeconds, sourceStart, sourceDuration);
        duration = (sourceEnd - sourceStart) / clip.PlaybackSpeed;
        double sourceElapsedAtStart = clip.IsReversed
            ? (clip.SourceOutSeconds ?? clip.SourceDurationSeconds) - sourceEnd
            : sourceStart - clip.SourceInSeconds;
        double clipElapsedAtSourceStart = sourceElapsedAtStart / clip.PlaybackSpeed;
        bool ramped = VideoSpeedRamp.IsRamped(clip);
        if (ramped)
        {
            clipElapsedAtSourceStart = VideoSpeedRamp.GetTimelineElapsed(clip, sourceElapsedAtStart);
            duration = VideoSpeedRamp.GetTimelineElapsed(clip, sourceElapsedAtStart + (sourceEnd - sourceStart)) -
                clipElapsedAtSourceStart;
        }
        if (duration <= 0)
        {
            return;
        }
        double clipStart = VideoTimelineEditing.GetStartSeconds(clip, _frameRate);
        double timelineStart = clipStart + clipElapsedAtSourceStart;

        ISampleProvider source = reader.ToSampleProvider();
        if (clip.IsReversed)
        {
            source = new ReverseSampleProvider(
                source,
                seconds => reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, reader.TotalTime.TotalSeconds)),
                sourceStart,
                sourceEnd);
        }
        else
        {
            reader.CurrentTime = TimeSpan.FromSeconds(sourceStart);
        }

        source = ramped
            ? new VariableSpeedSampleProvider(
                source,
                elapsed => VideoSpeedRamp.GetSourceElapsed(clip, clipElapsedAtSourceStart + elapsed) - sourceElapsedAtStart)
            : new PlaybackSpeedSampleProvider(source, clip.PlaybackSpeed);

        ISampleProvider envelope = new GainEnvelopeSampleProvider(
            source,
            elapsed => ExportPlanning.GetVideoClipAudioGain(
                clip, clipElapsedAtSourceStart + elapsed, transition, _frameRate));
        if (clip.EmbeddedAudioBalanceKeyframes.Count > 0)
        {
            envelope = new BalanceEnvelopeSampleProvider(
                envelope,
                Layout,
                elapsed => VideoClipBalanceKeyframes.GetValue(
                    clip, (clipElapsedAtSourceStart + elapsed) * _frameRate));
            AddSource(envelope, Math.Max(0, timelineStart), duration, busId: busId);
        }
        else
        {
            AddSource(envelope, Math.Max(0, timelineStart), duration, clip.EmbeddedAudioBalance, busId: busId);
        }
    }

    private void AddSource(ISampleProvider source, double startSeconds, double durationSeconds,
        double balance = 0, bool spatialPanEnabled = false, double spatialPanAzimuthDegrees = 0, string? busId = null)
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
        GetBusMixer(busId).AddMixerInput(new OffsetSampleProvider(mixed)
        {
            DelayBy = TimeSpan.FromSeconds(start),
            Take = TimeSpan.FromSeconds(durationSeconds),
        });
        if (AudioBuses.Find(_buses, busId) is AudioBusProject bus)
        {
            _busEndSeconds[bus.Id] = Math.Max(
                _busEndSeconds.GetValueOrDefault(bus.Id), start + durationSeconds);
        }

        SourceCount++;
        DurationSeconds = Math.Max(DurationSeconds, start + durationSeconds);
    }

    private MixingSampleProvider GetBusMixer(string? busId)
    {
        if (AudioBuses.Find(_buses, busId) is not AudioBusProject bus)
        {
            if (busId is not null)
            {
                throw new InvalidDataException("An audio source or bus is routed to a missing bus.");
            }

            return _mixer;
        }

        if (_buildingBusMixers.Contains(bus.Id))
        {
            throw new InvalidDataException("Audio bus parent routes must not contain a cycle.");
        }

        if (_busMixers.TryGetValue(bus.Id, out MixingSampleProvider? existing))
        {
            return existing;
        }

        _buildingBusMixers.Add(bus.Id);
        // ReadFully keeps the bus running after its sources end so delay and reverb tails decay naturally.
        var busMixer = new MixingSampleProvider(_mixer.WaveFormat) { ReadFully = true };
        _busMixers.Add(bus.Id, busMixer);
        var effects = new AudioEffectsSampleProvider(busMixer, bus.Effects);
        var vst3 = new Vst3InsertChainSampleProvider(effects, bus.Vst3Plugins, preservePluginTails: true);
        _readers.Add(vst3);
        _vst3Providers.Add(vst3);
        _busVst3TailDurationSeconds[bus.Id] = vst3.TailDurationSeconds;
        GetBusMixer(bus.ParentBusId).AddMixerInput(
            new VolumeSampleProvider(vst3) { Volume = AudioBuses.GetLinearGain(bus) });
        _buildingBusMixers.Remove(bus.Id);
        return busMixer;
    }

    private double? GetBusOutputEndSeconds(AudioBusProject bus)
    {
        double? inputEnd = _busEndSeconds.TryGetValue(bus.Id, out double directEnd) ? directEnd : null;
        foreach (AudioBusProject child in _buses.Where(candidate => candidate.ParentBusId == bus.Id))
        {
            if (GetBusOutputEndSeconds(child) is double childEnd)
            {
                inputEnd = Math.Max(inputEnd ?? 0, childEnd);
            }
        }

        return inputEnd is double end
            ? end + bus.Effects.TailDurationSeconds + GetBusVst3TailDuration(bus.Id)
            : null;
    }

    private double GetBusVst3TailDuration(string busId) =>
        _busVst3TailDurationSeconds.GetValueOrDefault(busId);

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or COMException or
            FormatException or ArgumentException or NotSupportedException;

    private sealed class FiniteSampleProvider(ISampleProvider source, double durationSeconds) : ISampleProvider
    {
        private long _remainingSamples = Math.Max(0,
            (long)Math.Round(durationSeconds * source.WaveFormat.SampleRate) * source.WaveFormat.Channels);

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int requested = count - count % WaveFormat.Channels;
            int read = source.Read(buffer, offset, (int)Math.Min(requested, _remainingSamples));
            _remainingSamples -= read;
            return read;
        }
    }

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

    private sealed class BalanceEnvelopeSampleProvider(
        ISampleProvider source,
        AudioChannelLayout layout,
        Func<double, double> balanceAtSeconds) : ISampleProvider
    {
        private readonly int _sourceChannels = source.WaveFormat.Channels;
        private readonly float[,] _baseMatrix = AudioChannelLayouts.CreateMixMatrix(source.WaveFormat.Channels, layout);
        private readonly AudioSpeaker[] _speakers = AudioChannelLayouts.Get(layout).Speakers.ToArray();
        private float[] _sourceBuffer = [];
        private long _framePosition;
        private readonly double _sampleRate = source.WaveFormat.SampleRate;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, AudioChannelLayouts.Get(layout).ChannelCount);

        public int Read(float[] buffer, int offset, int count)
        {
            int outputChannels = WaveFormat.Channels;
            int requestedFrames = count / outputChannels;
            int requestedSourceSamples = requestedFrames * _sourceChannels;
            if (_sourceBuffer.Length < requestedSourceSamples)
            {
                _sourceBuffer = new float[requestedSourceSamples];
            }

            int read = source.Read(_sourceBuffer, 0, requestedSourceSamples);
            int frames = read / _sourceChannels;
            for (int frame = 0; frame < frames; frame++, _framePosition++)
            {
                double balance = Math.Clamp(balanceAtSeconds(_framePosition / _sampleRate), -1, 1);
                int sourceStart = frame * _sourceChannels;
                int outputStart = offset + frame * outputChannels;
                for (int target = 0; target < outputChannels; target++)
                {
                    double sum = 0;
                    double balanceGain = AudioMixControls.GetBalanceGain(_speakers[target], balance);
                    for (int input = 0; input < _sourceChannels; input++)
                    {
                        sum += _sourceBuffer[sourceStart + input] * _baseMatrix[target, input];
                    }
                    buffer[outputStart + target] = (float)(sum * balanceGain);
                }
            }

            int outputRead = frames * outputChannels;
            Array.Clear(buffer, offset + outputRead, count - outputRead);
            return outputRead;
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
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long? _expectedDataBytes;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _channelMask;
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
        _sampleRate = sampleRate;
        _channelMask = channelMask;
        _headerSize = channels > 2 ? 68 : 44;
        _bytesPerSample = bitsPerSample / 8;
        _floatingPoint = floatingPoint;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteHeader(0, 0);
    }

    public MultichannelWaveWriter(Stream stream, int sampleRate, int channels, int channelMask, long expectedFrames,
        int bitsPerSample = 24, bool floatingPoint = false, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedFrames);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The WAV destination must be writable.", nameof(stream));
        }
        if (bitsPerSample is not (16 or 24 or 32) || (floatingPoint && bitsPerSample != 32))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
        }

        _channels = channels;
        _sampleRate = sampleRate;
        _channelMask = channelMask;
        _headerSize = channels > 2 ? 68 : 44;
        _bytesPerSample = bitsPerSample / 8;
        _floatingPoint = floatingPoint;
        _stream = stream;
        _leaveOpen = leaveOpen;
        _expectedDataBytes = checked(expectedFrames * channels * _bytesPerSample);
        long paddedDataBytes = _expectedDataBytes.Value + (_expectedDataBytes.Value % 2);
        if (paddedDataBytes > uint.MaxValue - _headerSize + 8L)
        {
            throw new InvalidOperationException("This WAV would exceed the 4 GB RIFF limit.");
        }

        WriteHeader((uint)(_headerSize - 8 + paddedDataBytes), (uint)_expectedDataBytes.Value);
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
        if (_dataBytes + bytes > uint.MaxValue - _headerSize || _expectedDataBytes is long expected && _dataBytes + bytes > expected)
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

        if (_expectedDataBytes is null)
        {
            _stream.Position = 4;
            using var writer = new BinaryWriter(_stream, System.Text.Encoding.ASCII, leaveOpen: true);
            writer.Write((uint)(_headerSize - 8 + _dataBytes + (_dataBytes % 2)));
            _stream.Position = _headerSize - 4;
            writer.Write((uint)_dataBytes);
        }

        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private void WriteHeader(uint riffSize, uint dataSize)
    {
        using var header = new BinaryWriter(_stream, System.Text.Encoding.ASCII, leaveOpen: true);
        int blockAlign = _channels * _bytesPerSample;
        header.Write("RIFF"u8);
        header.Write(riffSize);
        header.Write("WAVE"u8);
        header.Write("fmt "u8);
        header.Write(_channels > 2 ? 40 : 16);
        header.Write((ushort)(_channels > 2 ? 0xFFFE : _floatingPoint ? 3 : 1));
        header.Write((ushort)_channels);
        header.Write(_sampleRate);
        header.Write(_sampleRate * blockAlign);
        header.Write((ushort)blockAlign);
        header.Write((ushort)(_bytesPerSample * 8));
        if (_channels > 2)
        {
            header.Write((ushort)22);
            header.Write((ushort)(_bytesPerSample * 8));
            header.Write(_channelMask);
            header.Write((_floatingPoint ? new Guid("00000003-0000-0010-8000-00aa00389b71") : PcmSubFormat).ToByteArray());
        }
        header.Write("data"u8);
        header.Write(dataSize);
    }
}
