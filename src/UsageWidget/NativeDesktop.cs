using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace UsageWidget;

public static class NativeDesktop
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWPOS { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOSIZE = 0x01, SWP_NOMOVE = 0x02, SWP_NOZORDER = 0x04, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    public static IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;

    public static void CenterOnPrimary(Window window) {
        GetWindowRect(Handle(window), out var rect);
        var area = (Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0]).WorkingArea;
        Move(window, area.Left + (area.Width - (rect.Right - rect.Left)) / 2,
            area.Top + (area.Height - (rect.Bottom - rect.Top)) / 2, true);
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

    public static void Move(Window window, int x, int y, bool constrain)
    {
        var handle = Handle(window);
        GetWindowRect(handle, out var rect);
        if (constrain)
        {
            var desired = new System.Drawing.Rectangle(x, y, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
            var area = Forms.Screen.FromRectangle(desired).WorkingArea;
            x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - desired.Width));
            y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - desired.Height));
        }
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
