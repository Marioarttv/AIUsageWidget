using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace UsageWidget;

// One native launcher/notification icon remains available when the taskbar strip is hidden.
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
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;
    internal const int CallbackMessage = 0x8001;
    internal static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly HwndSource window;
    private readonly TrayIconHandle icon;
    private NOTIFYICONDATA data;
    private bool disposed, registered;
    public event Action? Click, BalloonClick, MenuRequested, EnvironmentChanged;
    internal IntPtr WindowHandle => window.Handle;

    // Keep the former numeric entry's identity, preserving its user's visibility preference.
    public TrayIcon() : this(new Guid("a01b25ac-0a2e-46b4-9031-c86bd4660d66")) { }

    // Tests use a temporary identity without claiming the installed application's GUID.
    internal TrayIcon(Guid identity)
    {
        var small = new IntPtr[1];
        ExtractIconEx(Environment.ProcessPath!, 0, null, small, 1);
        icon = small[0] != IntPtr.Zero ? new TrayIconHandle(small[0]) : TrayArtwork.Create(UsageProvider.Codex, null, TrayAppearance.Read());
        window = new HwndSource(new HwndSourceParameters("AIUsageWidgetTray") { WindowStyle = 0, Width = 0, Height = 0 });
        window.AddHook(Hook);
        data = new NOTIFYICONDATA { Size = Marshal.SizeOf<NOTIFYICONDATA>(), Window = window.Handle, Id = 1, Item = identity,
            CallbackMessage = CallbackMessage, Icon = icon.DangerousGetHandle(), Tip = "AI Usage Widget", Info = "", InfoTitle = "" };
        Register();
    }

    public void Update(UsageSnapshot? claude, UsageSnapshot? codex, DateTimeOffset now)
    {
        if (disposed) return;
        var cl = TrayReading.Create(UsageProvider.Claude, claude, now);
        var cx = TrayReading.Create(UsageProvider.Codex, codex, now);
        static string Percent(string number) => number == "—" ? number : number + "%";
        string tip = $"AI Usage · Claude {Percent(cl.Number)} · ChatGPT/Codex {Percent(cx.Number)} remaining\nClick to open · Right-click for settings";
        if (tip.Length > 127) tip = tip[..127];
        bool changed = data.Tip != tip;
        data.Tip = tip;
        if (!registered) Register();
        else if (changed)
        {
            data.Flags = NIF_GUID | NIF_TIP | NIF_SHOWTIP;
            if (!Shell_NotifyIcon(NIM_MODIFY, ref data)) { registered = false; Register(); }
        }
    }

    private void Register()
    {
        data.Flags = NIF_GUID | NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        registered = Shell_NotifyIcon(NIM_ADD, ref data);
        if (registered)
        {
            data.Version = 4;
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }
    }

    public void PrepareMenu() => SetForegroundWindow(window.Handle);

    public void ShowBalloon(string title, string text, bool warning)
    {
        if (disposed) return;
        var info = data;
        info.Flags = NIF_GUID | NIF_INFO; info.InfoTitle = title.Length > 63 ? title[..63] : title;
        info.Info = text.Length > 255 ? text[..255] : text; info.InfoFlags = warning ? 2 : 1;
        Shell_NotifyIcon(NIM_MODIFY, ref info);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (disposed) return IntPtr.Zero;
        if (message == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case 0x0400: // NIN_SELECT
                case 0x0401: Click?.Invoke(); break; // NIN_KEYSELECT
                case 0x007B: MenuRequested?.Invoke(); break; // WM_CONTEXTMENU
                case 0x0405: BalloonClick?.Invoke(); break;
            }
            handled = true;
        }
        else if (message == TaskbarCreated)
        {
            Register();
            EnvironmentChanged?.Invoke();
        }
        else if (message is 0x001A or 0x007E or 0x02E0 or 0x031A) EnvironmentChanged?.Invoke();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        data.Flags = NIF_GUID;
        Shell_NotifyIcon(NIM_DELETE, ref data);
        icon.Dispose();
        window.RemoveHook(Hook);
        window.Dispose();
    }
}
