using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace InkMotion;

internal sealed class TimelineVideoFrameSource : IDisposable
{
    private readonly MediaFoundationVideoReader _reader;
    private CanvasBitmap? _bitmap;
    private CanvasBitmap? _rollingBitmap;
    private byte[]? _rollingPixels;
    private long _version = -1;
    private double _sourceSeconds = double.NaN;

    public TimelineVideoFrameSource(string path)
    {
        _reader = new MediaFoundationVideoReader(path);
    }

    public (int X, int Y, int Width, int Height) Aperture => _reader.Aperture;

    public CanvasBitmap? GetFrame(CanvasDevice device, double sourceSeconds,
        IReadOnlyList<VideoRollingShutterKeyframe>? rollingProfile = null)
    {
        if (!_reader.TryGetFrame(sourceSeconds) && _bitmap is null)
        {
            return null;
        }

        bool frameChanged = _reader.FrameVersion != _version;
        bool positionChanged = !double.IsFinite(_sourceSeconds) || Math.Abs(_sourceSeconds - sourceSeconds) > 1e-7;
        if ((frameChanged || positionChanged) && _reader.Width > 0 && _reader.Height > 0)
        {
            // RGB32 decoder output has an unused padding byte, not source transparency.
            byte[] pixels = _reader.Pixels;
            for (int index = 3; index < pixels.Length; index += 4)
            {
                pixels[index] = 255;
            }
            if (_bitmap is null ||
                _bitmap.SizeInPixels.Width != _reader.Width ||
                _bitmap.SizeInPixels.Height != _reader.Height)
            {
                _bitmap?.Dispose();
                _bitmap = CanvasBitmap.CreateFromBytes(
                    device,
                    pixels,
                    _reader.Width,
                    _reader.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    96,
                    CanvasAlphaMode.Premultiplied);
            }
            else
            {
                _bitmap.SetPixelBytes(pixels);
            }

            _version = _reader.FrameVersion;
            _sourceSeconds = sourceSeconds;
        }

        if (rollingProfile is { Count: > 1 })
        {
            VideoRollingShutterOffset[] offsets = VideoRollingShutter.GetOffsets(rollingProfile, sourceSeconds);
            if (!VideoRollingShutter.HasCorrection(offsets))
            {
                return _bitmap;
            }
            if (_rollingPixels is null || _rollingPixels.Length != _reader.Pixels.Length)
            {
                _rollingPixels = new byte[_reader.Pixels.Length];
            }
            VideoRollingShutter.WarpBgra(_reader.Pixels, _rollingPixels, _reader.Width, _reader.Height, offsets);
            if (_rollingBitmap is null ||
                _rollingBitmap.SizeInPixels.Width != _reader.Width ||
                _rollingBitmap.SizeInPixels.Height != _reader.Height)
            {
                _rollingBitmap?.Dispose();
                _rollingBitmap = CanvasBitmap.CreateFromBytes(device, _rollingPixels, _reader.Width, _reader.Height,
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
        _reader.Dispose();
    }
}
