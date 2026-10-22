using System.Runtime.InteropServices;
using NAudio.MediaFoundation;

namespace InkMotion;

internal static class MediaFoundationGuids
{
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
    public static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    public static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid VideoFormatHevc = new("43564548-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid AudioFormatAac = new("00001610-0000-0010-8000-00aa00389b71");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid AverageBitRate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    public static readonly Guid AllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid AudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid AudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid AudioAverageBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid AudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid AudioChannelMask = new("55fb5765-644a-4caf-8479-938983bb1588");
    public static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    public static readonly Guid TranscodeContainerType = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    public static readonly Guid ContainerTypeMpeg4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");

    public const int ProgressiveInterlaceMode = 2;

    public static long Pack(int high, int low) => ((long)high << 32) | (uint)low;

    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }
}

/// <summary>
/// Sequential, seekable video frame decoder built on the Media Foundation source reader. Frames are converted
/// to top-down BGRA (RGB32) so they can be uploaded straight into a Win2D bitmap.
/// </summary>
public sealed class MediaFoundationVideoReader : IDisposable
{
    private const int FirstVideoStream = unchecked((int)0xFFFFFFFC);
    private const int AllStreams = unchecked((int)0xFFFFFFFE);
    private const long SeekAheadTicks = 20_000_000;
    private const long ToleranceTicks = 10_000;
    private const short PropVariantInt64 = 20;

    private IMFSourceReader? _reader;
    private byte[] _current = [];
    private byte[] _pending = [];
    private long _currentTime;
    private long _pendingTime;
    private bool _hasCurrent;
    private bool _hasPending;
    private bool _endOfStream;
    private int _stride;

    public MediaFoundationVideoReader(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        MediaFoundationApi.Startup();
        IMFAttributes attributes = MediaFoundationApi.CreateAttributes(1);
        try
        {
            attributes.SetUINT32(MediaFoundationGuids.EnableVideoProcessing, 1);
            MediaFoundationInterop.MFCreateSourceReaderFromURL(path, attributes, out IMFSourceReader reader);
            _reader = reader;
        }
        finally
        {
            MediaFoundationGuids.Release(attributes);
        }

        try
        {
            _reader.SetStreamSelection(AllStreams, false);
            _reader.SetStreamSelection(FirstVideoStream, true);
            IMFMediaType requested = MediaFoundationApi.CreateMediaType();
            try
            {
                requested.SetGUID(MediaFoundationGuids.MajorType, MediaFoundationGuids.MediaTypeVideo);
                requested.SetGUID(MediaFoundationGuids.Subtype, MediaFoundationGuids.VideoFormatRgb32);
                _reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, requested);
            }
            finally
            {
                MediaFoundationGuids.Release(requested);
            }

            ReadCurrentFormat();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Decoded buffer width in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>Decoded buffer height in pixels (may include encoder padding outside <see cref="Aperture"/>).</summary>
    public int Height { get; private set; }

    /// <summary>The displayable region of the decoded buffer, as (X, Y, Width, Height).</summary>
    public (int X, int Y, int Width, int Height) Aperture { get; private set; }

    /// <summary>Tightly packed top-down BGRA pixels of the current frame (<see cref="Width"/> × <see cref="Height"/> × 4).</summary>
    public byte[] Pixels => _current;

    /// <summary>Changes whenever <see cref="Pixels"/> holds a different frame, so callers can skip redundant uploads.</summary>
    public long FrameVersion { get; private set; }

    /// <summary>Advances to the frame that should be on screen at <paramref name="sourceSeconds"/>.</summary>
    public bool TryGetFrame(double sourceSeconds)
    {
        ObjectDisposedException.ThrowIf(_reader is null, this);
        long target = (long)Math.Round(Math.Max(0, sourceSeconds) * 10_000_000);
        if (!_hasCurrent && !_hasPending)
        {
            if (target > SeekAheadTicks)
            {
                Seek(target);
            }
        }
        else
        {
            long latest = _hasPending ? _pendingTime : _currentTime;
            if ((_hasCurrent && target < _currentTime - ToleranceTicks) || target > latest + SeekAheadTicks)
            {
                Seek(target);
            }
        }

        while (true)
        {
            if (!_hasPending && (_endOfStream || !ReadNext()))
            {
                break;
            }

            if (_pendingTime > target + ToleranceTicks && _hasCurrent)
            {
                break;
            }

            (_current, _pending) = (_pending, _current);
            _currentTime = _pendingTime;
            _hasCurrent = true;
            _hasPending = false;
            FrameVersion++;
            if (_currentTime > target + ToleranceTicks)
            {
                break;
            }
        }

        return _hasCurrent;
    }

    public void Dispose()
    {
        MediaFoundationGuids.Release(_reader);
        _reader = null;
    }

    private void Seek(long target)
    {
        IntPtr variant = Marshal.AllocHGlobal(24);
        try
        {
            for (int offset = 0; offset < 24; offset += 8)
            {
                Marshal.WriteInt64(variant, offset, 0);
            }

            Marshal.WriteInt16(variant, 0, PropVariantInt64);
            Marshal.WriteInt64(variant, 8, target);
            _reader!.SetCurrentPosition(Guid.Empty, variant);
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
        }

        _hasCurrent = false;
        _hasPending = false;
        _endOfStream = false;
    }

    private bool ReadNext()
    {
        while (true)
        {
            _reader!.ReadSample(
                FirstVideoStream,
                0,
                out _,
                out MF_SOURCE_READER_FLAG flags,
                out ulong timestamp,
                out IMFSample? sample);
            try
            {
                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                {
                    ReadCurrentFormat();
                }

                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    _endOfStream = true;
                    return false;
                }

                if (sample is null)
                {
                    continue;
                }

                CopySample(sample);
                _pendingTime = (long)timestamp;
                _hasPending = true;
                return true;
            }
            finally
            {
                MediaFoundationGuids.Release(sample);
            }
        }
    }

    private void CopySample(IMFSample sample)
    {
        int rowBytes = Width * 4;
        int length = rowBytes * Height;
        if (_pending.Length != length)
        {
            _pending = new byte[length];
        }

        sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        try
        {
            buffer.Lock(out IntPtr data, out _, out int currentLength);
            try
            {
                int stride = Math.Abs(_stride);
                int rows = Math.Min(Height, stride == 0 ? 0 : currentLength / stride);
                for (int row = 0; row < rows; row++)
                {
                    int sourceRow = _stride < 0 ? Height - 1 - row : row;
                    Marshal.Copy(data + (sourceRow * stride), _pending, row * rowBytes, rowBytes);
                }
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            MediaFoundationGuids.Release(buffer);
        }
    }

    private void ReadCurrentFormat()
    {
        _reader!.GetCurrentMediaType(FirstVideoStream, out IMFMediaType type);
        try
        {
            type.GetUINT64(MediaFoundationGuids.FrameSize, out long size);
            Width = (int)(size >> 32);
            Height = (int)(size & 0xFFFFFFFF);
            if (Width <= 0 || Height <= 0)
            {
                throw new InvalidOperationException("The video stream reports an empty frame size.");
            }

            try
            {
                type.GetUINT32(MediaFoundationGuids.DefaultStride, out int stride);
                _stride = stride;
            }
            catch (COMException)
            {
                _stride = Width * 4;
            }

            if (Math.Abs(_stride) < Width * 4)
            {
                _stride = Width * 4;
            }

            Aperture = (0, 0, Width, Height);
            try
            {
                byte[] area = new byte[16];
                type.GetBlob(MediaFoundationGuids.MinimumDisplayAperture, area, area.Length, out int read);
                if (read >= 16)
                {
                    int x = BitConverter.ToInt16(area, 2);
                    int y = BitConverter.ToInt16(area, 6);
                    int width = BitConverter.ToInt32(area, 8);
                    int height = BitConverter.ToInt32(area, 12);
                    if (width > 0 && height > 0 && x >= 0 && y >= 0 && x + width <= Width && y + height <= Height)
                    {
                        Aperture = (x, y, width, height);
                    }
                }
            }
            catch (COMException)
            {
            }
        }
        finally
        {
            MediaFoundationGuids.Release(type);
        }
    }
}

/// <summary>The Windows AAC encoder rejected the requested channel count (for example, 5.1 on some Windows Server images).</summary>
public sealed class AudioEncoderUnavailableException(int channels, Exception inner)
    : InvalidOperationException($"Windows could not start the AAC encoder for {channels}-channel audio (0x{inner.HResult:X8}). Try a different AAC rate/bit rate or export a WAV mix separately.", inner)
{
    public int Channels { get; } = channels;
}

/// <summary>
/// MP4 writer built on the Media Foundation sink writer. Video is supplied as top-down BGRA frames and audio as
/// interleaved 16-bit PCM; Media Foundation converts colour and may select available hardware transforms.
/// </summary>
public sealed class MediaFoundationVideoWriter : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private IMFSinkWriter? _writer;
    private int _videoStream;
    private int _audioStream = -1;

    public MediaFoundationVideoWriter(
        string path,
        int width,
        int height,
        double frameRate,
        VideoExportCodec codec,
        int bitRate,
        int audioChannels,
        int audioChannelMask,
        bool useHardware,
        int audioSampleRate = ExportPlanning.AudioSampleRate,
        int audioBitRate = 192000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ExportPlanning.ValidateEmbeddedAudio(audioSampleRate, audioBitRate);
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive and even.");
        }

        _width = width;
        _height = height;
        MediaFoundationApi.Startup();
        IMFAttributes attributes = MediaFoundationApi.CreateAttributes(2);
        try
        {
            attributes.SetUINT32(MediaFoundationGuids.EnableHardwareTransforms, useHardware ? 1 : 0);
            attributes.SetGUID(MediaFoundationGuids.TranscodeContainerType, MediaFoundationGuids.ContainerTypeMpeg4);
            MediaFoundationInterop.MFCreateSinkWriterFromURL(path, null!, attributes, out IMFSinkWriter writer);
            _writer = writer;
        }
        finally
        {
            MediaFoundationGuids.Release(attributes);
        }

        try
        {
            (int numerator, int denominator) = ExportPlanning.GetFrameRateRatio(frameRate);
            ConfigureVideo(codec, bitRate, numerator, denominator);
            if (audioChannels > 0)
            {
                try
                {
                    ConfigureAudio(audioChannels, audioChannelMask, audioSampleRate, audioBitRate);
                }
                catch (COMException ex)
                {
                    throw new AudioEncoderUnavailableException(audioChannels, ex);
                }
            }

            _writer.BeginWriting();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool HasAudio => _audioStream >= 0;

    public void WriteVideoFrame(byte[] bgra, long sampleTime, long sampleDuration)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        int length = _width * _height * 4;
        if (bgra.Length < length)
        {
            throw new ArgumentException("The frame buffer is smaller than the output frame.", nameof(bgra));
        }

        WriteSample(_videoStream, bgra, length, sampleTime, sampleDuration);
    }

    public void WriteAudio(byte[] pcm, int length, long sampleTime, long sampleDuration)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (_audioStream < 0 || length <= 0)
        {
            return;
        }

        WriteSample(_audioStream, pcm, length, sampleTime, sampleDuration);
    }

    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_writer is null, this);
        _writer.DoFinalize();
    }

    public void Dispose()
    {
        MediaFoundationGuids.Release(_writer);
        _writer = null;
    }

    private void ConfigureVideo(VideoExportCodec codec, int bitRate, int numerator, int denominator)
    {
        IMFMediaType output = MediaFoundationApi.CreateMediaType();
        try
        {
            output.SetGUID(MediaFoundationGuids.MajorType, MediaFoundationGuids.MediaTypeVideo);
            output.SetGUID(
                MediaFoundationGuids.Subtype,
                codec == VideoExportCodec.Hevc ? MediaFoundationGuids.VideoFormatHevc : MediaFoundationGuids.VideoFormatH264);
            output.SetUINT32(MediaFoundationGuids.AverageBitRate, bitRate);
            SetVideoFrameAttributes(output, numerator, denominator);
            _writer!.AddStream(output, out _videoStream);
        }
        finally
        {
            MediaFoundationGuids.Release(output);
        }

        IMFMediaType input = MediaFoundationApi.CreateMediaType();
        try
        {
            input.SetGUID(MediaFoundationGuids.MajorType, MediaFoundationGuids.MediaTypeVideo);
            input.SetGUID(MediaFoundationGuids.Subtype, MediaFoundationGuids.VideoFormatRgb32);
            input.SetUINT32(MediaFoundationGuids.DefaultStride, _width * 4);
            input.SetUINT32(MediaFoundationGuids.AllSamplesIndependent, 1);
            SetVideoFrameAttributes(input, numerator, denominator);
            _writer!.SetInputMediaType(_videoStream, input, null!);
        }
        finally
        {
            MediaFoundationGuids.Release(input);
        }
    }

    private void SetVideoFrameAttributes(IMFMediaType type, int numerator, int denominator)
    {
        type.SetUINT32(MediaFoundationGuids.InterlaceMode, MediaFoundationGuids.ProgressiveInterlaceMode);
        type.SetUINT64(MediaFoundationGuids.FrameSize, MediaFoundationGuids.Pack(_width, _height));
        type.SetUINT64(MediaFoundationGuids.FrameRate, MediaFoundationGuids.Pack(numerator, denominator));
        type.SetUINT64(MediaFoundationGuids.PixelAspectRatio, MediaFoundationGuids.Pack(1, 1));
    }

    private void ConfigureAudio(int channels, int channelMask, int sampleRate, int bitRate)
    {
        IMFMediaType output = MediaFoundationApi.CreateMediaType();
        try
        {
            output.SetGUID(MediaFoundationGuids.MajorType, MediaFoundationGuids.MediaTypeAudio);
            output.SetGUID(MediaFoundationGuids.Subtype, MediaFoundationGuids.AudioFormatAac);
            output.SetUINT32(MediaFoundationGuids.AudioBitsPerSample, 16);
            output.SetUINT32(MediaFoundationGuids.AudioSamplesPerSecond, sampleRate);
            output.SetUINT32(MediaFoundationGuids.AudioChannels, channels);
            output.SetUINT32(MediaFoundationGuids.AudioAverageBytesPerSecond, bitRate / 8);
            if (channelMask != 0)
            {
                output.SetUINT32(MediaFoundationGuids.AudioChannelMask, channelMask);
            }

            _writer!.AddStream(output, out _audioStream);
        }
        finally
        {
            MediaFoundationGuids.Release(output);
        }

        IMFMediaType input = MediaFoundationApi.CreateMediaType();
        try
        {
            input.SetGUID(MediaFoundationGuids.MajorType, MediaFoundationGuids.MediaTypeAudio);
            input.SetGUID(MediaFoundationGuids.Subtype, MediaFoundationGuids.AudioFormatPcm);
            input.SetUINT32(MediaFoundationGuids.AudioBitsPerSample, 16);
            input.SetUINT32(MediaFoundationGuids.AudioSamplesPerSecond, sampleRate);
            input.SetUINT32(MediaFoundationGuids.AudioChannels, channels);
            input.SetUINT32(MediaFoundationGuids.AudioBlockAlignment, channels * 2);
            input.SetUINT32(MediaFoundationGuids.AudioAverageBytesPerSecond, sampleRate * channels * 2);
            if (channelMask != 0)
            {
                input.SetUINT32(MediaFoundationGuids.AudioChannelMask, channelMask);
            }

            _writer!.SetInputMediaType(_audioStream, input, null!);
        }
        finally
        {
            MediaFoundationGuids.Release(input);
        }
    }

    private void WriteSample(int stream, byte[] data, int length, long sampleTime, long sampleDuration)
    {
        ObjectDisposedException.ThrowIf(_writer is null, this);
        IMFMediaBuffer buffer = MediaFoundationApi.CreateMemoryBuffer(length);
        IMFSample? sample = null;
        try
        {
            buffer.Lock(out IntPtr destination, out _, out _);
            try
            {
                Marshal.Copy(data, 0, destination, length);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.SetCurrentLength(length);
            sample = MediaFoundationApi.CreateSample();
            sample.AddBuffer(buffer);
            sample.SetSampleTime(sampleTime);
            sample.SetSampleDuration(sampleDuration);
            _writer.WriteSample(stream, sample);
        }
        finally
        {
            MediaFoundationGuids.Release(sample);
            MediaFoundationGuids.Release(buffer);
        }
    }
}
