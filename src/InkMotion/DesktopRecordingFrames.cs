using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace InkMotion;

public sealed record RecordingDisplay(string Name, ScreenRegion Bounds)
{
    public override string ToString() => $"{Name}: {Bounds}";
}

public sealed class DesktopRecordingFrames : IScreenRecordingFrames
{
    private readonly ScreenCapturePlan _plan;
    private readonly bool _cursor;
    private nint _desktop, _memory, _bitmap, _original, _pixels;
    private nint _clipRegion;
    private readonly byte[] _buffer;
    private readonly CanvasDevice _device;
    private readonly CanvasRenderTarget? _target;
    private readonly CanvasBitmap? _image;
    private readonly ScreenRegion[] _displayBounds;
    private readonly Stopwatch _displayCheck = Stopwatch.StartNew();

    public static IReadOnlyList<RecordingDisplay> Displays()
    {
        var displays = new List<RecordingDisplay>();
        MonitorCallback callback = (nint monitor, nint dc, ref NativeRect rect, nint data) =>
        {
            displays.Add(new($"Display {displays.Count + 1}", new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return displays;
    }

    public DesktopRecordingFrames(ScreenCapturePlan plan, bool cursor)
    {
        _plan = plan;
        _cursor = cursor;
        _displayBounds = Displays().Select(display => display.Bounds).ToArray();
        _buffer = new byte[checked(plan.Bounds.Width * plan.Bounds.Height * 4)];
        _device = new CanvasDevice();
        try
        {
            _target = new CanvasRenderTarget(_device, plan.Width, plan.Height, 96);
            _image = CanvasBitmap.CreateFromBytes(_device, _buffer, plan.Bounds.Width, plan.Bounds.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
            _desktop = GetDC(0);
            if (_desktop == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The interactive desktop is unavailable.");
            _memory = CreateCompatibleDC(_desktop);
            if (_memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = plan.Bounds.Width,
                Height = -plan.Bounds.Height, Planes = 1, BitCount = 32 };
            _bitmap = CreateDIBSection(_desktop, ref info, 0, out _pixels, 0, 0);
            if (_bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _original = SelectObject(_memory, _bitmap);
            if (_original == 0 || _original == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            _clipRegion = CreateRectRgn(0, 0, 0, 0);
            if (_clipRegion == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            foreach (ScreenRegion region in plan.Regions)
            {
                nint piece = CreateRectRgn(region.X - plan.Bounds.X, region.Y - plan.Bounds.Y,
                    (int)(region.Right - plan.Bounds.X), (int)(region.Bottom - plan.Bounds.Y));
                if (piece == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (region.Polygon is { } polygon)
                    {
                        var points = polygon.Select(p => new NativePoint { X = p.X - plan.Bounds.X, Y = p.Y - plan.Bounds.Y }).ToArray();
                        nint shape = CreatePolygonRgn(points, points.Length, 1);
                        if (shape == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                        try
                        {
                            if (CombineRgn(piece, piece, shape, 1) == 0)
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                        finally { DeleteObject(shape); }
                    }
                    if (CombineRgn(_clipRegion, _clipRegion, piece, 2) == 0)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { DeleteObject(piece); }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public byte[] Capture()
    {
        if (_displayCheck.Elapsed.TotalSeconds >= 1)
        {
            if (!_displayBounds.SequenceEqual(Displays().Select(display => display.Bounds)))
                throw new InvalidOperationException("The display layout changed. Existing segments are retained; start a new recording with the new layout.");
            _displayCheck.Restart();
        }
        if (!PatBlt(_memory, 0, 0, _plan.Bounds.Width, _plan.Bounds.Height, 0x00000042))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not clear the capture canvas.");
        if (SelectClipRgn(_memory, _clipRegion) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        foreach (ScreenRegion region in _plan.Regions)
        {
            if (!BitBlt(_memory, region.X - _plan.Bounds.X, region.Y - _plan.Bounds.Y, region.Width, region.Height,
                _desktop, region.X, region.Y, 0x40CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Desktop capture failed. Unlock the interactive desktop and check the display configuration.");
        }
        if (_cursor)
        {
            if (SelectClipRgn(_memory, _clipRegion) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref cursor)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (cursor.Flags == 1 && _plan.Regions.Any(region => cursor.X >= region.X && cursor.X < region.Right &&
                cursor.Y >= region.Y && cursor.Y < region.Bottom))
            {
                if (GetIconInfo(cursor.Cursor, out IconInfo icon))
                {
                    try
                    {
                        if (!DrawIconEx(_memory, cursor.X - _plan.Bounds.X - (int)icon.HotspotX,
                            cursor.Y - _plan.Bounds.Y - (int)icon.HotspotY, cursor.Cursor, 0, 0, 0, 0, 3))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    finally
                    {
                        if (icon.Mask != 0) DeleteObject(icon.Mask);
                        if (icon.Colour != 0) DeleteObject(icon.Colour);
                    }
                }
                else throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not obtain the cursor image.");
            }
        }
        if (SelectClipRgn(_memory, 0) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GdiFlush()) throw new Win32Exception(Marshal.GetLastWin32Error());
        Marshal.Copy(_pixels, _buffer, 0, _buffer.Length);
        _image!.SetPixelBytes(_buffer);
        using (CanvasDrawingSession session = _target!.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Black);
            session.DrawImage(_image, new Rect(0, 0, _plan.Width, _plan.Height), _image.Bounds);
        }
        return _target.GetPixelBytes();
    }

    public void Dispose()
    {
        if (_original != 0 && _original != -1) SelectObject(_memory, _original);
        if (_bitmap != 0) DeleteObject(_bitmap);
        if (_clipRegion != 0) DeleteObject(_clipRegion);
        if (_memory != 0) DeleteDC(_memory);
        if (_desktop != 0) ReleaseDC(0, _desktop);
        _original = _bitmap = _clipRegion = _memory = _desktop = 0;
        _image?.Dispose();
        _target?.Dispose();
        _device.Dispose();
    }

    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public nint Cursor; public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int IsIcon; public uint HotspotX, HotspotY; public nint Mask, Colour; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint dst, int x, int y, int width, int height, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool PatBlt(nint dc, int x, int y, int width, int height, uint rop);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreatePolygonRgn(NativePoint[] points, int count, int mode);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int SelectClipRgn(nint dc, nint region);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool GdiFlush();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetIconInfo(nint cursor, out IconInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
}
