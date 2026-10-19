using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Zytrus.Native;

/// <summary>
/// Windows counterpart of the macOS event tap: a low-level mouse hook (WH_MOUSE_LL) that notices
/// "left button held + moved + trigger key down" anywhere on the desktop and asks the app to show the drop zone.
/// Needs no special permission. Limitation (UIPI): drags that start in an elevated (admin) window are invisible
/// to a non-elevated hook.
/// </summary>
internal sealed class GlobalDragWatcher : IDisposable
{
    const int DragThresholdPx = 10;

    readonly Dispatcher _dispatcher;
    readonly Win32.LowLevelMouseProc _proc; // keep a reference so the GC never collects the callback
    IntPtr _hook;

    bool _buttonDown;
    bool _fired;
    Win32.POINT _downPoint;
    IntPtr _downWindow;

    /// <summary>Raised on the UI thread with the cursor position (physical pixels).</summary>
    public event Action<Win32.POINT>? ShiftDragStarted;

    /// <summary>Raised on the UI thread when the mouse button of a reported drag is released.</summary>
    public event Action? DragEnded;

    public GlobalDragWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = HookCallback;
    }

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _proc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Write("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    // Runs on the UI thread for every mouse event system-wide: keep it tiny.
    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == Win32.WM_LBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                _buttonDown = true;
                _fired = false;
                _downPoint = info.pt;
                _downWindow = Win32.WindowFromPoint(info.pt);
            }
            else if (msg == Win32.WM_MOUSEMOVE && _buttonDown && !_fired)
            {
                var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                int dx = info.pt.X - _downPoint.X, dy = info.pt.Y - _downPoint.Y;
                if (dx * dx + dy * dy >= DragThresholdPx * DragThresholdPx && TriggerHeld())
                {
                    _fired = true;
                    var pt = info.pt;
                    var hwnd = _downWindow;
                    _dispatcher.InvokeAsync(() => OnCandidate(pt, hwnd));
                }
            }
            else if (msg == Win32.WM_LBUTTONUP && _buttonDown)
            {
                _buttonDown = false;
                if (_fired) _dispatcher.InvokeAsync(() => DragEnded?.Invoke());
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>The key combination chosen in the settings (Shift, Ctrl or both).</summary>
    static bool TriggerHeld() => SettingsStore.Current.Trigger switch
    {
        DragTrigger.Ctrl => Win32.IsKeyDown(Win32.VK_CONTROL) && !Win32.IsKeyDown(Win32.VK_SHIFT),
        DragTrigger.ShiftCtrl => Win32.IsKeyDown(Win32.VK_CONTROL) && Win32.IsKeyDown(Win32.VK_SHIFT),
        _ => Win32.IsKeyDown(Win32.VK_SHIFT) && !Win32.IsKeyDown(Win32.VK_CONTROL)
    };

    void OnCandidate(Win32.POINT pt, IntPtr hwnd)
    {
        if (!_buttonDown) return; // released before we got here
        if (!IsAllowedSource(hwnd)) return;
        ShiftDragStarted?.Invoke(pt);
    }

    static bool IsAllowedSource(IntPtr hwnd)
    {
        try
        {
            var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
            Win32.GetWindowThreadProcessId(root == IntPtr.Zero ? hwnd : root, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;

            var allowed = SettingsStore.Current.DragSourceApps;
            if (SettingsStore.Current.DragFromAllApps || allowed.Any(a => a == "*")) return true;

            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            return allowed.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => Stop();
}
