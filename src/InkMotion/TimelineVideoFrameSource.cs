using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace InkMotion;

internal sealed class TimelineVideoFrameSource : IDisposable
{
    private readonly MediaFoundationVideoReader? _reader;
    private readonly FFmpegHdrFrameReader? _hdr;
    private CanvasBitmap? _bitmap;
    private CanvasBitmap? _rollingBitmap;
    private byte[]? _rollingPixels;
    private long _version = -1;
    private double _sourceSeconds = double.NaN;

    public TimelineVideoFrameSource(string path, HdrDecodeOptions? hdr = null)
    {
        if (hdr is null)
            _reader = new MediaFoundationVideoReader(path);
        else
            _hdr = new FFmpegHdrFrameReader(path, hdr);
    }

    public (int X, int Y, int Width, int Height) Aperture => _reader?.Aperture ?? (0, 0, _hdr!.Width, _hdr.Height);

    /// <summary>True when frames are half-float working-space pixels decoded from a PQ/HLG source.</summary>
    public bool IsHdrWorkingFloat => _hdr is not null && _hdr.Pixels.Length == _hdr.Width * _hdr.Height * 8;

    public bool IsHdrDecoded => _hdr is not null;

    private int Width => _reader?.Width ?? _hdr!.Width;
    private int Height => _reader?.Height ?? _hdr!.Height;
    private byte[] Pixels => _reader?.Pixels ?? _hdr!.Pixels;

    public CanvasBitmap? GetFrame(CanvasDevice device, double sourceSeconds,
        IReadOnlyList<VideoRollingShutterKeyframe>? rollingProfile = null)
    {
        bool available = _reader?.TryGetFrame(sourceSeconds) ?? _hdr!.TryGetFrame(sourceSeconds);
        if (!available && _bitmap is null)
        {
            return null;
        }

        long frameVersion = _reader?.FrameVersion ?? _hdr!.FrameVersion;
        bool frameChanged = frameVersion != _version;
        bool positionChanged = !double.IsFinite(_sourceSeconds) || Math.Abs(_sourceSeconds - sourceSeconds) > 1e-7;
        if ((frameChanged || positionChanged) && Width > 0 && Height > 0)
        {
            byte[] pixels = Pixels;
            if (_reader is not null)
            {
                // RGB32 decoder output has an unused padding byte, not source transparency.
                for (int index = 3; index < pixels.Length; index += 4)
                {
                    pixels[index] = 255;
                }
            }
            DirectXPixelFormat format = IsHdrWorkingFloat
                ? DirectXPixelFormat.R16G16B16A16Float
                : DirectXPixelFormat.B8G8R8A8UIntNormalized;
            if (_bitmap is null ||
                _bitmap.SizeInPixels.Width != Width ||
                _bitmap.SizeInPixels.Height != Height)
            {
                _bitmap?.Dispose();
                _bitmap = CanvasBitmap.CreateFromBytes(
                    device,
                    pixels,
                    Width,
                    Height,
                    format,
                    96,
                    CanvasAlphaMode.Premultiplied);
            }
            else
            {
                _bitmap.SetPixelBytes(pixels);
            }

            _version = frameVersion;
            _sourceSeconds = sourceSeconds;
        }

        // Rolling-shutter warping works on 8-bit pixels; half-float HDR frames are returned uncorrected.
        if (rollingProfile is { Count: > 1 } && !IsHdrWorkingFloat)
        {
            VideoRollingShutterOffset[] offsets = VideoRollingShutter.GetOffsets(rollingProfile, sourceSeconds);
            if (!VideoRollingShutter.HasCorrection(offsets))
            {
                return _bitmap;
            }
            if (_rollingPixels is null || _rollingPixels.Length != Pixels.Length)
            {
                _rollingPixels = new byte[Pixels.Length];
            }
            VideoRollingShutter.WarpBgra(Pixels, _rollingPixels, Width, Height, offsets);
            if (_rollingBitmap is null ||
                _rollingBitmap.SizeInPixels.Width != Width ||
                _rollingBitmap.SizeInPixels.Height != Height)
            {
                _rollingBitmap?.Dispose();
                _rollingBitmap = CanvasBitmap.CreateFromBytes(device, _rollingPixels, Width, Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
            }
            else
            {
                _rollingBitmap.SetPixelBytes(_rollingPixels);
            }
            return _rollingBitmap;
        }
        return _bitmap;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _rollingBitmap?.Dispose();
        _reader?.Dispose();
        _hdr?.Dispose();
    }
}
