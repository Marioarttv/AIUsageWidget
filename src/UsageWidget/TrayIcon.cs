using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace UsageWidget;

// Minimal notification-area icon over Shell_NotifyIcon, so the widget does not load the Windows Forms runtime.
public sealed class TrayIcon : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int Size; public IntPtr Window; public int Id; public int Flags; public int CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags; public Guid Item; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[] small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const int CallbackMessage = 0x8001, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, NIN_BALLOONUSERCLICK = 0x0405;
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly HwndSource window;
    private readonly IntPtr icon;
    private NOTIFYICONDATA data;
    public event Action? DoubleClick, BalloonClick, MenuRequested;

    public TrayIcon(string tip)
    {
        // A hidden top-level window (not message-only) also receives TaskbarCreated when Explorer restarts.
        window = new HwndSource(new HwndSourceParameters("AIUsageWidgetTray") { WindowStyle = 0, Width = 0, Height = 0 });
        window.AddHook(Hook);
        var small = new IntPtr[1];
        ExtractIconEx(Environment.ProcessPath!, 0, null, small, 1);
        icon = small[0];
        data = new NOTIFYICONDATA { Size = Marshal.SizeOf<NOTIFYICONDATA>(), Window = window.Handle, Id = 1, Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            CallbackMessage = CallbackMessage, Icon = icon, Tip = tip, Info = "", InfoTitle = "" };
        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    // Menus opened from the tray only dismiss correctly when their owner is in the foreground.
    public void PrepareMenu() => SetForegroundWindow(window.Handle);

    public void ShowBalloon(string title, string text, bool warning)
    {
        var info = data;
        info.Flags = NIF_INFO; info.InfoTitle = title.Length > 63 ? title[..63] : title; info.Info = text.Length > 255 ? text[..255] : text;
        info.InfoFlags = warning ? 2 : 1; // NIIF_WARNING : NIIF_INFO
        Shell_NotifyIcon(NIM_MODIFY, ref info);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONDBLCLK: DoubleClick?.Invoke(); break;
                case WM_RBUTTONUP: MenuRequested?.Invoke(); break;
                case NIN_BALLOONUSERCLICK: BalloonClick?.Invoke(); break;
            }
            handled = true;
        }
        else if (message == TaskbarCreated) Shell_NotifyIcon(NIM_ADD, ref data);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Shell_NotifyIcon(NIM_DELETE, ref data);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        window.Dispose();
    }
}
