using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace UsageWidget;

internal sealed record TaskbarState(IntPtr Handle, Rect Bounds, Rect Monitor, Rect? Tray, double Scale, bool Visible, bool Fullscreen);

internal static class TaskbarPlacement
{
    // All placement arithmetic uses physical screen pixels, including monitors with negative origins.
    // Strip widths in device-independent pixels: Claude and ChatGPT, and with Antigravity's Gemini mark.
    internal const double NarrowWidth = 150, WideWidth = 214;

    internal static Rect? Calculate(TaskbarState state, IReadOnlyList<Rect>? buttons, double stripWidth = NarrowWidth)
    {
        var bar = state.Bounds;
        var screen = state.Monitor;
        double scale = state.Scale, width = stripWidth * scale, height = 30 * scale, gap = 6 * scale;
        if (!state.Visible || state.Fullscreen || bar.IsEmpty || screen.IsEmpty || screen.Width < width || screen.Height < height)
            return null;
        var visible = Rect.Intersect(bar, screen);
        // An auto-hidden taskbar can still report IsWindowVisible while only its thin activation edge remains.
        if (visible.Width < Math.Min(bar.Width, 16 * scale) || visible.Height < Math.Min(bar.Height, 16 * scale)) return null;
        bool horizontal = bar.Width >= bar.Height;
        double x, y;
        if (horizontal)
        {
            double edge = state.Tray?.Left ?? bar.Right;
            x = edge - gap - width;
            y = bar.Top + (bar.Height - height) / 2;
            var inline = new Rect(x, y, width, height);
            // Use a taskbar slot only after checking the actual buttons. Unknown layouts get a safe adjacent position.
            if (state.Tray is not null && buttons is not null && bar.Contains(inline) &&
                !buttons.Any(button => Rect.Intersect(button, inline) is var hit && !hit.IsEmpty && hit.Width > 0 && hit.Height > 0))
                return inline;
            bool bottom = bar.Top >= screen.Top + screen.Height / 2;
            x = (state.Tray?.Right ?? bar.Right) - width - gap;
            y = bottom ? bar.Top - height - gap : bar.Bottom + gap;
        }
        else
        {
            // A vertical taskbar cannot contain the horizontal strip; keep it beside the tray instead.
            bool right = bar.Left >= screen.Left + screen.Width / 2;
            x = right ? bar.Left - width - gap : bar.Right + gap;
            y = (state.Tray?.Bottom ?? bar.Bottom) - height - gap;
        }
        return new Rect(Math.Clamp(x, screen.Left, screen.Right - width), Math.Clamp(y, screen.Top, screen.Bottom - height), width, height);
    }
}

internal static class TaskbarProbe
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; public readonly Rect Rect => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top)); }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    internal static TaskbarState? Read()
    {
        // UI Automation returns physical pixels even when a diagnostic host is DPI-unaware.
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { return ReadPhysical(); }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    private static TaskbarState? ReadPhysical()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var bounds)) return null;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(taskbar, 2), ref monitor)) return null;
        var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        Rect? trayBounds = tray != IntPtr.Zero && GetWindowRect(tray, out var trayRect) && !trayRect.Rect.IsEmpty && trayRect.Rect.Width > 0
            ? trayRect.Rect : null;
        uint dpi = GetDpiForWindow(taskbar);
        bool fullscreen = false;
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != taskbar && GetWindowRect(foreground, out var foregroundRect) && foregroundRect.Rect.Contains(monitor.Monitor.Rect))
        {
            var name = new StringBuilder(256); GetClassName(foreground, name, name.Capacity);
            fullscreen = name.ToString() is not ("Progman" or "WorkerW");
        }
        return new(taskbar, bounds.Rect, monitor.Monitor.Rect, trayBounds, (dpi == 0 ? 96 : dpi) / 96.0, IsWindowVisible(taskbar), fullscreen);
    }

    // Run off the UI thread: Explorer may take time to answer accessibility queries. Only geometry is read.
    internal static IReadOnlyList<Rect>? ReadButtons(IntPtr taskbar)
    {
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
            // We only need cached rectangles; full element references retain Explorer's provider objects.
            var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            using (cache.Activate())
            {
                var elements = root.FindAll(TreeScope.Descendants, condition);
                var buttons = new List<Rect>();
                foreach (AutomationElement element in elements)
                {
                    var rect = element.Cached.BoundingRectangle;
                    if (!element.Cached.IsOffscreen && !rect.IsEmpty && rect.Width > 0 && rect.Height > 0) buttons.Add(rect);
                }
                return buttons.Count > 0 ? buttons : null;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or COMException or UnauthorizedAccessException)
        { return null; }
    }
}
