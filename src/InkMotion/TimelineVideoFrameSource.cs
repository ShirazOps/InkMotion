using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace InkMotion;

internal sealed class TimelineVideoFrameSource : IDisposable
{
    private readonly MediaFoundationVideoReader _reader;
    private CanvasBitmap? _bitmap;
    private long _version = -1;

    public TimelineVideoFrameSource(string path)
    {
        _reader = new MediaFoundationVideoReader(path);
    }

    public (int X, int Y, int Width, int Height) Aperture => _reader.Aperture;

    public CanvasBitmap? GetFrame(CanvasDevice device, double sourceSeconds)
    {
        if (!_reader.TryGetFrame(sourceSeconds) && _bitmap is null)
        {
            return null;
        }

        if (_reader.FrameVersion != _version && _reader.Width > 0 && _reader.Height > 0)
        {
            if (_bitmap is null ||
                _bitmap.SizeInPixels.Width != _reader.Width ||
                _bitmap.SizeInPixels.Height != _reader.Height)
            {
                _bitmap?.Dispose();
                _bitmap = CanvasBitmap.CreateFromBytes(
                    device,
                    _reader.Pixels,
                    _reader.Width,
                    _reader.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    96,
                    CanvasAlphaMode.Ignore);
            }
            else
            {
                _bitmap.SetPixelBytes(_reader.Pixels);
            }

            _version = _reader.FrameVersion;
        }

        return _bitmap;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _reader.Dispose();
    }
}
