using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace UsageWidget;

// Windows gives each notification icon a square slot. A mark + number pair keeps both readable.
// One hidden window owns all four entries; no Windows Forms runtime or extra refresh loop is needed.
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
    private sealed class Entry(UsageProvider provider, bool number, int id, Guid guid)
    {
        internal readonly UsageProvider Provider = provider;
        internal readonly bool IsNumber = number;
        internal NOTIFYICONDATA Data = new() { Size = Marshal.SizeOf<NOTIFYICONDATA>(), Id = id, Item = guid,
            CallbackMessage = CallbackMessage, Tip = "", Info = "", InfoTitle = "" };
        internal TrayIconHandle? Icon;
        internal string? Number;
        internal TrayAppearance? Appearance;
        internal bool Registered;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;
    internal const int CallbackMessage = 0x8001;
    internal static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly HwndSource window;
    private readonly Entry[] entries = [
        new(UsageProvider.Claude, false, 1, new Guid("2e6bac65-0a81-41dd-ac61-d09a0d3890bc")),
        new(UsageProvider.Claude, true, 2, new Guid("508fed50-bfdf-45f9-b219-5f23214a5e58")),
        new(UsageProvider.Codex, false, 3, new Guid("c3a3e8e7-4726-4c85-ae61-e1b46230464e")),
        new(UsageProvider.Codex, true, 4, new Guid("a01b25ac-0a2e-46b4-9031-c86bd4660d66"))
    ];
    private bool disposed;
    private TrayAppearance? appearance;
    private UsageSnapshot? claude, codex;
    public event Action? Click, BalloonClick, MenuRequested;
    internal IntPtr WindowHandle => window.Handle;

    public TrayIcon() : this(null) { }

    // Integration checks use temporary GUIDs so they cannot claim the installed app's persisted tray identities.
    internal TrayIcon(Guid[]? identities)
    {
        if (identities is not null && identities.Length != entries.Length) throw new ArgumentException("Four tray identities are required.", nameof(identities));
        // A top-level window also receives TaskbarCreated when Explorer restarts.
        window = new HwndSource(new HwndSourceParameters("AIUsageWidgetTray") { WindowStyle = 0, Width = 0, Height = 0 });
        window.AddHook(Hook);
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i].Data.Window = window.Handle;
            if (identities is not null) entries[i].Data.Item = identities[i];
        }
        try { Update(null, null, DateTimeOffset.UtcNow); }
        catch { Dispose(); throw; }
    }

    public void Update(UsageSnapshot? claude, UsageSnapshot? codex, DateTimeOffset now)
    {
        if (disposed) return;
        this.claude = claude; this.codex = codex;
        appearance ??= TrayAppearance.Read();
        var claudeReading = TrayReading.Create(UsageProvider.Claude, claude, now);
        var codexReading = TrayReading.Create(UsageProvider.Codex, codex, now);
        foreach (var entry in entries)
        {
            var reading = entry.Provider == UsageProvider.Claude ? claudeReading : codexReading;
            string? number = entry.IsNumber ? reading.Number : null;
            string tip = reading.Tooltip + (entry.IsNumber ? "\nRemaining %" : "");
            if (tip.Length > 127) tip = tip[..127];
            bool imageChanged = entry.Icon is null || entry.Number != number || entry.Appearance != appearance;
            bool tipChanged = entry.Data.Tip != tip;
            if (imageChanged)
            {
                var icon = TrayArtwork.Create(entry.Provider, number, appearance.Value);
                var previous = entry.Icon;
                entry.Icon = icon; entry.Data.Icon = icon.DangerousGetHandle();
                entry.Number = number; entry.Appearance = appearance;
                previous?.Dispose(); // The shell keeps its own copy of the previous icon.
            }
            entry.Data.Tip = tip;
            if (!entry.Registered) Register(entry);
            else if (imageChanged || tipChanged)
            {
                entry.Data.Flags = NIF_GUID | (imageChanged ? NIF_ICON : 0) | (tipChanged ? NIF_TIP | NIF_SHOWTIP : 0);
                if (!Shell_NotifyIcon(NIM_MODIFY, ref entry.Data)) { entry.Registered = false; Register(entry); }
            }
        }
    }

    private static void Register(Entry entry)
    {
        entry.Data.Flags = NIF_GUID | NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        entry.Registered = Shell_NotifyIcon(NIM_ADD, ref entry.Data);
        if (entry.Registered)
        {
            entry.Data.Version = 4; // Keyboard selection and context-menu notifications.
            Shell_NotifyIcon(NIM_SETVERSION, ref entry.Data);
        }
    }

    // Menus opened from the tray only dismiss correctly when their owner is in the foreground.
    public void PrepareMenu() => SetForegroundWindow(window.Handle);

    public void ShowBalloon(UsageProvider provider, string title, string text, bool warning)
    {
        if (disposed) return;
        var info = entries.First(e => e.Provider == provider && !e.IsNumber).Data;
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
            foreach (var entry in entries) entry.Registered = false;
            appearance = null;
            Update(claude, codex, DateTimeOffset.UtcNow);
        }
        else if (message is 0x001A or 0x007E or 0x02E0 or 0x031A) // Settings, display, DPI, theme.
        {
            appearance = null;
            Update(claude, codex, DateTimeOffset.UtcNow);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var entry in entries)
        {
            entry.Data.Flags = NIF_GUID;
            Shell_NotifyIcon(NIM_DELETE, ref entry.Data);
            entry.Icon?.Dispose();
        }
        window.RemoveHook(Hook);
        window.Dispose();
    }
}
