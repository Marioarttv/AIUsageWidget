using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace UsageWidget;

public static class NativeDesktop
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref POINT point);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    public static IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;

    public static void CenterOnPrimary(Window window) {
        GetWindowRect(Handle(window), out var rect);
        var area = (Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0]).WorkingArea;
        Move(window, area.Left + (area.Width - (rect.Right - rect.Left)) / 2,
            area.Top + (area.Height - (rect.Bottom - rect.Top)) / 2, true);
    }

    public static bool Apply(Window window, bool desktop)
    {
        var handle = Handle(window);
        GetWindowRect(handle, out var rect);
        long style = GetWindowLongPtr(handle, -16).ToInt64();
        if (desktop)
        {
            IntPtr host = IntPtr.Zero;
            EnumWindows((candidate, _) => {
                if (FindWindowEx(candidate, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { host = candidate; return false; }
                return true;
            }, IntPtr.Zero);
            if (host != IntPtr.Zero)
            {
                window.Topmost = false;
                SetWindowLongPtr(handle, -16, new IntPtr((style | 0x40000000L) & ~0x80000000L));
                SetParent(handle, host);
                if (GetParent(handle) == host) {
                    Move(window, rect.Left, rect.Top, true);
                    SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x01 | 0x02 | 0x10 | 0x20 | 0x40);
                    return true;
                }
                DataStore.Log("Desktop attachment failed; using bottom window");
            }
            SetParent(handle, IntPtr.Zero);
            SetWindowLongPtr(handle, -16, new IntPtr((style | 0x80000000L) & ~0x40000000L));
            window.Topmost = false;
            SetWindowPos(handle, new IntPtr(1), rect.Left, rect.Top, 0, 0, 0x01 | 0x10 | 0x20 | 0x40);
            return false;
        }
        SetParent(handle, IntPtr.Zero);
        SetWindowLongPtr(handle, -16, new IntPtr((style | 0x80000000L) & ~0x40000000L));
        SetWindowPos(handle, new IntPtr(-1), rect.Left, rect.Top, 0, 0, 0x01 | 0x10 | 0x20 | 0x40);
        window.Topmost = true;
        return false;
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
        var point = new POINT { X = x, Y = y };
        var parent = GetParent(handle);
        bool child = (GetWindowLongPtr(handle, -16).ToInt64() & 0x40000000L) != 0;
        if (child && parent != IntPtr.Zero) ScreenToClient(parent, ref point);
        SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, 0, 0, 0x01 | 0x04 | 0x10);
    }
}
