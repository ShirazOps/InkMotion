using System.ComponentModel;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class ScreenRecorderWindow
{
    private const int StopShortcutId = 0x4B51;
    private ShortcutProcedure? _shortcutProcedure;
    private bool _shortcutRegistered;

    private void RegisterStopShortcut()
    {
        nint window = WindowNative.GetWindowHandle(this);
        if (_shortcutProcedure is null)
        {
            ShortcutProcedure procedure = ShortcutMessage;
            if (!SetWindowSubclass(window, procedure, StopShortcutId, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not prepare the recording stop shortcut.");
            _shortcutProcedure = procedure;
        }
        // Control + Shift, with key-repeat suppressed.
        if (!RegisterHotKey(window, StopShortcutId, 0x4006, 0x79))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Ctrl+Shift+F10 is unavailable or used by another application. Turn off the recording shortcut or free it and try again.");
        _shortcutRegistered = true;
    }

    private void UnregisterStopShortcut()
    {
        if (!_shortcutRegistered) return;
        if (!UnregisterHotKey(WindowNative.GetWindowHandle(this), StopShortcutId))
        {
            string error = $"Could not release Ctrl+Shift+F10: {new Win32Exception(Marshal.GetLastWin32Error()).Message}. Close the recorder to release it.";
            System.Diagnostics.Trace.TraceWarning(error);
            _status.Text += $"\n{error}";
            return;
        }
        _shortcutRegistered = false;
    }

    private nint ShortcutMessage(nint window, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint reference)
    {
        if (message == 0x0312 && wParam == StopShortcutId && _shortcutRegistered)
        {
            if (_busy) RequestStop();
            return 0;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint ShortcutProcedure(nint window, uint message, nuint wParam, nint lParam,
        nuint subclassId, nuint reference);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, ShortcutProcedure procedure, nuint id, nuint reference);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
