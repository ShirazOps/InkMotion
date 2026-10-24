using System.ComponentModel;
using System.Runtime.InteropServices;

namespace InkMotion;

// Native physical-pixel coordinates avoid DPI seams when dragging across monitors.
internal sealed class ScreenRegionSelector
{
    private static readonly HashSet<ScreenRegionSelector> Active = [];
    private readonly ScreenRegion _desktop;
    private readonly ScreenRegion _reset;
    private readonly bool _freeform;
    private readonly Action<ScreenRegion> _changed;
    private readonly TaskCompletionSource<ScreenRegion?> _completion = new();
    private readonly WindowProcedure _procedure;
    private readonly List<ScreenPoint> _points = [];
    private ScreenRegion? _selection;
    private ScreenRegion? _beforeDrag;
    private ScreenPoint _anchor = new(0, 0);
    private nint _window;
    private nint _visual;
    private ushort _class;
    private readonly string _className = $"InkMotionSelection{Guid.NewGuid():N}";
    private bool _dragging;
    private bool _placed;
    private bool _closed;
    private int _edges;
    private uint _pointer;
    private const int ToolbarHeight = 92;

    private ScreenRegionSelector(IReadOnlyList<RecordingDisplay> displays, ScreenRegion reset,
        bool freeform, Action<ScreenRegion> changed)
    {
        int x = displays.Min(d => d.Bounds.X), y = displays.Min(d => d.Bounds.Y);
        _desktop = new(x, y, checked((int)(displays.Max(d => d.Bounds.Right) - x)),
            checked((int)(displays.Max(d => d.Bounds.Bottom) - y)));
        _reset = reset;
        _freeform = freeform;
        _changed = changed;
        _procedure = ProcessMessage;
    }

    public Task<ScreenRegion?> Completion => _completion.Task;
    public bool IsClosed => _closed;
    public Action<Exception>? Failed { get; set; }

    public static ScreenRegionSelector BeginSelection(IReadOnlyList<RecordingDisplay> displays,
        ScreenRegion reset, bool freeform, Action<ScreenRegion> changed)
    {
        if (displays.Count == 0) throw new InvalidOperationException("No connected displays.");
        var selector = new ScreenRegionSelector(displays, reset, freeform, changed);
        Active.Add(selector);
        try { selector.Open(); }
        catch { Active.Remove(selector); throw; }
        return selector;
    }

    private void Open()
    {
        var definition = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandle(null),
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), ClassName = _className,
            Cursor = LoadCursor(0, 32515),
        };
        _class = RegisterClassEx(ref definition);
        if (_class == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _visual = CreateWindowEx(0x000800A8, _className, "InkMotion capture boundary", 0x80000000,
            _desktop.X, _desktop.Y, _desktop.Width, _desktop.Height, 0, 0, definition.Instance, 0);
        _window = CreateWindowEx(0x00080088, _className, "InkMotion capture area", 0x80000000,
            _desktop.X, _desktop.Y, _desktop.Width, _desktop.Height, 0, 0, definition.Instance, 0);
        if (_window == 0 || _visual == 0)
        {
            Finish(null, new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }
        // A nearly invisible input surface keeps clear holes from clicking through to other apps.
        if (!SetLayeredWindowAttributes(_visual, 0, 170, 3) ||
            !SetLayeredWindowAttributes(_window, 0, 1, 2))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            Finish(null, error);
            return;
        }
        ShowWindow(_visual, 4);
        ShowWindow(_window, 5);
        SetForegroundWindow(_window);
        SetFocus(_window);
    }

    private nint ProcessMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case 0x000F:
                    Paint(window);
                    return 0;
                case 0x0014: return 1;
                case 0x0010: Finish(null); return 0;
                case 0x0100:
                    if (!_placed && wParam == 27) Finish(null);
                    else if (!_placed && wParam == 13 && !_dragging && ValidSelection()) Place();
                    else if (!_placed && wParam == 82 && !_dragging) Reset();
                    return 0;
                case 0x0201:
                    // Pen messages are handled through WM_POINTER rather than promoted mouse events.
                    if (((long)GetMessageExtraInfo() & 0xFFFFFF00L) != 0xFF515700L)
                        Begin(LocalPoint(lParam));
                    return 0;
                case 0x0200:
                    if (_dragging && _pointer == 0) Move(LocalPoint(lParam));
                    return 0;
                case 0x0202:
                    if (_dragging && _pointer == 0) End(LocalPoint(lParam));
                    return 0;
                case 0x0246:
                case 0x0245:
                case 0x0247:
                    uint id = (uint)(wParam & 0xFFFF);
                    if (!GetPointerInfo(id, out PointerInfo info))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (info.Type != 3) break; // PT_PEN
                    var point = new ScreenPoint(info.Pixel.X, info.Pixel.Y);
                    if (message == 0x0246)
                    {
                        _pointer = id;
                        Begin(point);
                    }
                    else if (_pointer == id && _dragging)
                    {
                        if ((info.Flags & 0x8000) != 0) CancelDrag();
                        else if (message == 0x0247) End(point);
                        else Move(point);
                    }
                    return 0;
                case 0x0215:
                case 0x024C:
                    if (_dragging) CancelDrag();
                    return 0;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException or OverflowException)
        {
            Finish(null, ex);
            return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private ScreenPoint LocalPoint(nint value) => new(
        _desktop.X + (short)((long)value & 0xFFFF), _desktop.Y + (short)(((long)value >> 16) & 0xFFFF));

    private void Begin(ScreenPoint point)
    {
        if (_dragging) return;
        int toolbarX = _reset.X - _desktop.X + 12, toolbarY = _reset.Y - _desktop.Y + 12;
        int x = point.X - _desktop.X - toolbarX, y = point.Y - _desktop.Y - toolbarY;
        if (!_placed && y is >= 0 and < ToolbarHeight && x is >= 0 and < 700)
        {
            if (y >= 52)
            {
                if (x < 160 && ValidSelection()) Place();
                else if (x is >= 180 and < 380) Reset();
                else if (x >= 400) Finish(null);
            }
            _pointer = 0;
            return;
        }
        _anchor = point;
        _beforeDrag = _selection;
        _edges = _selection is not null && (_placed || !_freeform) ? HitEdges(_selection, point) : 0;
        if (_placed && _edges == 0) _edges = 16;
        _dragging = true;
        if (_pointer == 0) SetCapture(_window);
        if (_edges == 0)
        {
            _points.Clear();
            _points.Add(point);
            _selection = new(point.X, point.Y, 0, 0);
        }
        Redraw();
    }

    private static int HitEdges(ScreenRegion region, ScreenPoint point)
    {
        const int grip = 12;
        if (point.X < region.X - grip || point.X > region.Right + grip ||
            point.Y < region.Y - grip || point.Y > region.Bottom + grip) return 0;
        int edges = 0;
        if (Math.Abs((long)point.X - region.X) <= grip) edges |= 1;
        else if (Math.Abs(point.X - region.Right) <= grip) edges |= 2;
        if (Math.Abs((long)point.Y - region.Y) <= grip) edges |= 4;
        else if (Math.Abs(point.Y - region.Bottom) <= grip) edges |= 8;
        return edges;
    }

    private void Move(ScreenPoint point)
    {
        point = new(Math.Clamp(point.X, _desktop.X, (int)_desktop.Right),
            Math.Clamp(point.Y, _desktop.Y, (int)_desktop.Bottom));
        if (_edges == 16 && _beforeDrag is { } moving)
        {
            int dx = Math.Clamp(point.X - _anchor.X, _desktop.X - moving.X, (int)(_desktop.Right - moving.Right));
            int dy = Math.Clamp(point.Y - _anchor.Y, _desktop.Y - moving.Y, (int)(_desktop.Bottom - moving.Bottom));
            _selection = moving.ResizeTo(new(moving.X + dx, moving.Y + dy, moving.Width, moving.Height));
        }
        else if (_freeform && _edges == 0)
        {
            ScreenPoint previous = _points[^1];
            if (Math.Abs(point.X - previous.X) + Math.Abs(point.Y - previous.Y) >= 2)
            {
                if (_points.Count >= 16383)
                    throw new ArgumentException("This outline is too detailed. Try drawing a simpler region.");
                _points.Add(point);
            }
            if (_points.Count >= 3) _selection = ScreenRegion.Freeform(_points);
        }
        else if (_edges != 0 && _beforeDrag is { } original)
        {
            int left = (_edges & 1) != 0 ? point.X : original.X;
            int right = (_edges & 2) != 0 ? point.X : (int)original.Right;
            int top = (_edges & 4) != 0 ? point.Y : original.Y;
            int bottom = (_edges & 8) != 0 ? point.Y : (int)original.Bottom;
            ScreenRegion bounds = Rectangle(left, top, right, bottom);
            if (bounds.Width >= 2 && bounds.Height >= 2) _selection = original.ResizeTo(bounds);
        }
        else _selection = Rectangle(_anchor.X, _anchor.Y, point.X, point.Y);
        if (_selection is { } selection) _changed(selection);
        Redraw();
    }

    private static ScreenRegion Rectangle(int x, int y, int right, int bottom) =>
        new(Math.Min(x, right), Math.Min(y, bottom), Math.Abs(right - x), Math.Abs(bottom - y));

    private void End(ScreenPoint point)
    {
        Move(point);
        _dragging = false;
        if (_pointer == 0 && GetCapture() == _window) ReleaseCapture();
        _pointer = 0;
        if (ValidSelection()) Place();
        else
        {
            _selection = _beforeDrag;
            if (_selection is { } selection) _changed(selection);
        }
        Redraw();
    }

    private void CancelDrag()
    {
        _dragging = false;
        if (_pointer == 0 && GetCapture() == _window) ReleaseCapture();
        _pointer = 0;
        _selection = _beforeDrag;
        if (_selection is { } selection) _changed(selection);
        Redraw();
    }

    private void Reset()
    {
        _selection = _reset;
        _points.Clear();
        _changed(_reset);
        Place();
        Redraw();
    }

    private bool ValidSelection() => _selection is { Width: >= 2, Height: >= 2 };
    private void Place()
    {
        _placed = true;
        UpdateInputRegion();
        Redraw();
        _completion.TrySetResult(_selection);
    }

    private void Redraw()
    {
        if (_placed && !_dragging) UpdateInputRegion();
        InvalidateRect(_visual, 0, false);
    }

    // Only the boundary/grips receive input once placed. All other desktop pixels belong to the apps below.
    private void UpdateInputRegion()
    {
        if (_selection is not { } region || _window == 0) return;
        int left = region.X - _desktop.X, top = region.Y - _desktop.Y;
        int right = (int)(region.Right - _desktop.X), bottom = (int)(region.Bottom - _desktop.Y);
        nint outer = CreateRectRgn(left - 12, top - 12, right + 13, bottom + 13);
        nint inner = CreateRectRgn(left + 13, top + 13, right - 12, bottom - 12);
        if (outer == 0 || inner == 0)
        {
            if (outer != 0) DeleteObject(outer);
            if (inner != 0) DeleteObject(inner);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            if (region.Width > 25 && region.Height > 25 && CombineRgn(outer, outer, inner, 4) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (SetWindowRgn(_window, outer, true) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            outer = 0; // The window owns the successful SetWindowRgn region.
        }
        finally { if (outer != 0) DeleteObject(outer); DeleteObject(inner); }
    }

    private void Paint(nint window)
    {
        nint dc = BeginPaint(window, out PaintInfo paint);
        if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var bounds = new NativeRect { Right = _desktop.Width, Bottom = _desktop.Height };
            if (window == _window)
            {
                Fill(dc, bounds, 0);
                return;
            }
            Fill(dc, bounds, _placed ? 0u : 0x00909090);
            if (_selection is { Width: > 0, Height: > 0 } region)
            {
                nint area;
                if (region.Polygon is { } polygon)
                {
                    NativePoint[] vertices = polygon.Select(p => new NativePoint { X = p.X - _desktop.X, Y = p.Y - _desktop.Y }).ToArray();
                    area = CreatePolygonRgn(vertices, vertices.Length, 1);
                }
                else area = CreateRectRgn(region.X - _desktop.X, region.Y - _desktop.Y,
                    (int)(region.Right - _desktop.X), (int)(region.Bottom - _desktop.Y));
                if (area == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                nint clear = CreateSolidBrush(0), border = CreateSolidBrush(0x00FFFF00);
                try
                {
                    FillRgn(dc, area, clear);
                    FrameRgn(dc, area, border, 3, 3);
                }
                finally { DeleteObject(clear); DeleteObject(border); DeleteObject(area); }
                if (region.Polygon is null || _placed)
                {
                    int left = region.X - _desktop.X, top = region.Y - _desktop.Y;
                    int right = (int)(region.Right - _desktop.X), bottom = (int)(region.Bottom - _desktop.Y);
                    if (_placed && region.Polygon is not null)
                    {
                        nint box = CreateRectRgn(left, top, right, bottom);
                        nint brush = CreateSolidBrush(0x00B0B000);
                        try { FrameRgn(dc, box, brush, 1, 1); }
                        finally { DeleteObject(box); DeleteObject(brush); }
                    }
                    foreach ((int x, int y) in new[] { (left, top), ((left + right) / 2, top), (right, top),
                        (left, (top + bottom) / 2), (right, (top + bottom) / 2),
                        (left, bottom), ((left + right) / 2, bottom), (right, bottom) })
                        Fill(dc, new() { Left = x - 5, Top = y - 5, Right = x + 6, Bottom = y + 6 }, 0x00FFFFFF);
                }
                if (_placed) return;
            }
            int tx = _reset.X - _desktop.X + 12, ty = _reset.Y - _desktop.Y + 12;
            Fill(dc, new() { Left = tx, Top = ty, Right = tx + 700, Bottom = ty + ToolbarHeight }, 0x00202020);
            SetBkMode(dc, 1);
            SetTextColor(dc, 0x00FFFFFF);
            string help = _freeform ? "Draw with mouse or pen; release to place the closed outline." :
                "Drag with mouse or pen; release to place an adjustable outline.";
            TextOut(dc, tx + 10, ty + 8, help, help.Length);
            string coordinates = _selection is { } s ? $"Left {s.X}   Top {s.Y}   Width {s.Width}   Height {s.Height}" : "Select an area across one or more screens.";
            TextOut(dc, tx + 10, ty + 30, coordinates, coordinates.Length);
            Label(dc, tx + 10, ty + 62, "Use selection [Enter]");
            Label(dc, tx + 190, ty + 62, "Reset entire screen [R]");
            Label(dc, tx + 410, ty + 62, "Cancel [Esc]");
        }
        finally { EndPaint(window, ref paint); }
    }

    private static void Label(nint dc, int x, int y, string text) => TextOut(dc, x, y, text, text.Length);
    private static void Fill(nint dc, NativeRect rectangle, uint colour)
    {
        nint brush = CreateSolidBrush(colour);
        try { FillRect(dc, ref rectangle, brush); }
        finally { DeleteObject(brush); }
    }

    private void Finish(ScreenRegion? selection, Exception? error = null)
    {
        if (_closed) return;
        _closed = true;
        _dragging = false;
        if (_pointer == 0 && GetCapture() == _window) ReleaseCapture();
        if (_window != 0) DestroyWindow(_window);
        if (_visual != 0) DestroyWindow(_visual);
        _window = 0;
        _visual = 0;
        if (_class != 0) UnregisterClass(_className, GetModuleHandle(null));
        _class = 0;
        bool completed = _completion.Task.IsCompleted;
        if (error is null) _completion.TrySetResult(selection);
        else if (!completed) _completion.TrySetException(error);
        GC.KeepAlive(_procedure);
        Active.Remove(this);
        if (completed && error is not null) Failed?.Invoke(error);
    }

    public void Close() => Finish(null);

    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PaintInfo
    {
        public nint Dc;
        public int Erase;
        public NativeRect Paint;
        public int Restore, Update;
        public long Reserved1, Reserved2, Reserved3, Reserved4;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public uint Type, Id, Frame, Flags;
        public nint Device, Window;
        public NativePoint Pixel, Himetric, RawPixel, RawHimetric;
        public uint Time, History;
        public int Input;
        public uint Keys;
        public ulong Performance;
        public uint ButtonChange;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, int name);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint window, out PaintInfo paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint window, ref PaintInfo paint);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref NativeRect rect, nint brush);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetPointerInfo(uint id, out PointerInfo info);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint colour);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern nint CreatePolygonRgn(NativePoint[] points, int count, int mode);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("gdi32.dll")] private static extern bool FillRgn(nint dc, nint region, nint brush);
    [DllImport("gdi32.dll")] private static extern bool FrameRgn(nint dc, nint region, nint brush, int width, int height);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint colour);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOut(nint dc, int x, int y, string text, int count);
}
