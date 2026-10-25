using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace InkMotion.Tests;

public sealed class ControlledScreenCaptureFactAttribute : FactAttribute
{
    public ControlledScreenCaptureFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("INKMOTION_TEST_CONTROLLED_SCREEN") != "1")
            Skip = "Opt-in desktop test: set INKMOTION_TEST_CONTROLLED_SCREEN=1 to capture only a generated test window.";
    }
}

public sealed class ControlledScreenCaptureTests
{
    [ControlledScreenCaptureFact]
    public void DesktopCapture_RecordsOnlyControlledWindowWithCroppedRectangleAndFreeform()
    {
        using var dpi = new PhysicalPixels();
        using var window = new SyntheticWindow();
        ScreenRegion rectangle = window.Bounds;
        ScreenRegion triangle = ScreenRegion.Freeform([
            new(rectangle.X, rectangle.Y), new((int)rectangle.Right, rectangle.Y),
            new(rectangle.X, (int)rectangle.Bottom)]);
        foreach (ScreenRegion region in new[] { rectangle, triangle })
        {
            string directory = Path.Combine(Path.GetTempPath(), $"inkmotion-controlled-screen-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var plan = ScreenCapturePlan.Create([region],
                    DesktopRecordingFrames.Displays().Select(d => d.Bounds).ToArray(), 1920, 30);
                var manifest = new ScreenRecordingManifest { Plan = plan };
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var clock = Stopwatch.StartNew();
                ScreenRecordingEncoder.Record(directory, manifest, clock,
                    () => new GuardedFrames(plan, window, stop), () => ScreenRecordingStore.Save(directory, manifest), stop.Token);
                clock.Stop();
                var restored = ScreenRecordingStore.Read(directory);
                ScreenRecordingMedia media = Assert.Single(restored.Media, m => m.Finalised);
                Assert.All(restored.Media, m => Assert.Equal("Screen", m.Kind));
                Assert.InRange(media.DurationSeconds + media.OffsetSeconds, 2.8, clock.Elapsed.TotalSeconds + 0.2);
                using var reader = new MediaFoundationVideoReader(Path.Combine(directory, media.FileName));
                Assert.Equal(320, reader.Width);
                Assert.Equal(240, reader.Height);
                Assert.True(reader.TryGetFrame(0.5));
                AssertPixel(reader.Pixels, reader.Width, 40, 40, red: true);
                AssertPixel(reader.Pixels, reader.Width, 280, 200, red: region.Polygon is null);
                Assert.Single(ScreenRecordingStore.BuildProject(directory, restored).VideoTracks[0].Clips);
                Assert.Empty(ScreenRecordingStore.BuildProject(directory, restored).AudioTracks);
            }
            finally { Directory.Delete(directory, true); }
        }
    }

    private static void AssertPixel(byte[] pixels, int width, int x, int y, bool red)
    {
        int index = (y * width + x) * 4;
        Assert.InRange(pixels[index], 0, 20);
        Assert.InRange(pixels[index + 1], 0, 20);
        Assert.InRange(pixels[index + 2], red ? 220 : 0, red ? 255 : 20);
    }

    private sealed class GuardedFrames(ScreenCapturePlan plan, SyntheticWindow window,
        CancellationTokenSource stop) : IScreenRecordingFrames
    {
        private readonly DesktopRecordingFrames _frames = new(plan, false);
        private readonly Stopwatch _time = Stopwatch.StartNew();

        public byte[] Capture()
        {
            window.CheckVisible();
            byte[] pixels = _frames.Capture();
            window.CheckVisible();
            // Fail before encoding any unexpected desktop pixels, including transient occlusion.
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i] > 2 || pixels[i + 1] > 2)
                    throw new InvalidOperationException("The controlled window was obscured. No unexpected pixels were saved.");
            if (_time.Elapsed.TotalSeconds >= 3) stop.Cancel();
            return pixels;
        }

        public void Dispose() => _frames.Dispose();
    }

    private sealed class PhysicalPixels : IDisposable
    {
        private readonly nint _previous = SetThreadDpiAwarenessContext(-4);
        public PhysicalPixels()
        {
            if (_previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => SetThreadDpiAwarenessContext(_previous);
    }

    private sealed class SyntheticWindow : IDisposable
    {
        private readonly string _name = $"InkMotionControlledCapture{Guid.NewGuid():N}";
        private readonly Procedure _procedure = DefWindowProc;
        private readonly nint _brush;
        private nint _window;
        public ScreenRegion Bounds { get; }

        public SyntheticWindow()
        {
            ScreenRegion display = DesktopRecordingFrames.Displays().First(d => d.Bounds.Width >= 400 && d.Bounds.Height >= 320).Bounds;
            Bounds = new(display.X + 40, display.Y + 40, 320, 240);
            _brush = CreateSolidBrush(0x000000FF);
            var definition = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = GetModuleHandle(null),
                Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), Background = _brush, ClassName = _name,
            };
            if (RegisterClassEx(ref definition) == 0)
            {
                DeleteObject(_brush);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            _window = CreateWindowEx(0x08000088, _name, "InkMotion synthetic capture test", 0x80000000,
                Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, 0, 0, definition.Instance, 0);
            if (_window == 0) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            ShowWindow(_window, 4);
            UpdateWindow(_window);
        }

        public void CheckVisible()
        {
            if (!IsWindowVisible(_window)) throw new InvalidOperationException("The synthetic window is not visible.");
            // Enumerate top-to-bottom: refuse any visible overlapping window above the test surface.
            bool obscured = false;
            EnumProcedure check = (handle, _) =>
            {
                if (handle == _window) return false;
                if (IsWindowVisible(handle) && GetWindowRect(handle, out NativeRect rect) &&
                    rect.Right > Bounds.X && rect.Left < Bounds.Right &&
                    rect.Bottom > Bounds.Y && rect.Top < Bounds.Bottom) obscured = true;
                return !obscured;
            };
            EnumWindows(check, 0);
            if (obscured) throw new InvalidOperationException("Move overlapping windows away from the controlled test surface before retrying.");
        }

        public void Dispose()
        {
            if (_window != 0) DestroyWindow(_window);
            _window = 0;
            UnregisterClass(_name, GetModuleHandle(null));
            DeleteObject(_brush);
            GC.KeepAlive(_procedure);
        }
    }

    private delegate nint Procedure(nint window, uint message, nuint wParam, nint lParam);
    private delegate bool EnumProcedure(nint window, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProcedure callback, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint colour);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
}
