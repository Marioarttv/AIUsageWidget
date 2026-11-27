using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UsageWidget;

public static class NativeDesktop
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWPOS { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOSIZE = 0x01, SWP_NOMOVE = 0x02, SWP_NOZORDER = 0x04, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    public static IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;

    // Work area of the monitor nearest to the rectangle; the origin always lies on the primary monitor.
    private static RECT WorkArea(RECT rect, uint flags = 2 /* MONITOR_DEFAULTTONEAREST */) {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromRect(ref rect, flags), ref info);
        return info.Work;
    }

    // The work area of the monitor the window is on, in device-independent pixels.
    public static Rect WorkAreaDips(Window window)
    {
        GetWindowRect(Handle(window), out var rect);
        var area = WorkArea(rect);
        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new Point(area.Left, area.Top));
        var bottomRight = transform.Transform(new Point(area.Right, area.Bottom));
        var result = new Rect(topLeft, bottomRight);
        return result.IsEmpty || result.Height < 1 ? SystemParameters.WorkArea : result;
    }

    public static void CenterOnPrimary(Window window) {
        GetWindowRect(Handle(window), out var rect);
        var area = WorkArea(new RECT(), 1 /* MONITOR_DEFAULTTOPRIMARY */);
        Move(window, area.Left + (area.Right - area.Left - (rect.Right - rect.Left)) / 2,
            area.Top + (area.Bottom - area.Top - (rect.Bottom - rect.Top)) / 2, true);
    }

    // Desktop mode is an ordinary window kept at the bottom of the z-order, so it keeps its taskbar button and never covers other apps.
    public static void Apply(Window window, bool desktop)
    {
        window.Topmost = !desktop;
        if (desktop) SetWindowPos(Handle(window), HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    // Redirects every attempt to raise the window (activation, clicks, Alt+Tab) to the bottom of the z-order while desktop mode is on.
    public static void KeepAtBottom(Window window, Func<bool> enabled)
    {
        HwndSource.FromHwnd(Handle(window)).AddHook((IntPtr _, int message, IntPtr _, IntPtr data, ref bool _) => {
            if (message == WM_WINDOWPOSCHANGING && enabled()) {
                var position = Marshal.PtrToStructure<WINDOWPOS>(data);
                if ((position.Flags & SWP_NOZORDER) == 0) { position.InsertAfter = HWND_BOTTOM; Marshal.StructureToPtr(position, data, false); }
            }
            return IntPtr.Zero;
        });
    }

    // The widget idles for minutes between refreshes; hand memory it is not using back to Windows.
    public static void TrimMemory()
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    public static void Move(Window window, int x, int y, bool constrain)
    {
        var handle = Handle(window);
        GetWindowRect(handle, out var rect);
        if (constrain)
        {
            int width = Math.Max(1, rect.Right - rect.Left), height = Math.Max(1, rect.Bottom - rect.Top);
            var area = WorkArea(new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height });
            x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
            y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height));
        }
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
