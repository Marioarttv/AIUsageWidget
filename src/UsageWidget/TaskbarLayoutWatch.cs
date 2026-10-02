using System.Runtime.InteropServices;

namespace UsageWidget;

// Explorer's WinEvents invalidate cached button geometry without repeatedly walking its accessibility tree.
// Out-of-context hooks run on the WPF thread; no code is injected into Explorer.
internal sealed class TaskbarLayoutWatch : IDisposable
{
    private delegate void WinEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr window);
    private readonly WinEvent callback;
    private IntPtr taskbar, hook;
    internal long Revision { get; private set; }
    internal bool Active => hook != IntPtr.Zero;

    internal TaskbarLayoutWatch() => callback = OnEvent;

    internal void Attach(IntPtr window)
    {
        if (taskbar == window) return;
        Detach();
        taskbar = window; Revision++;
        if (window == IntPtr.Zero) return;
        GetWindowThreadProcessId(window, out uint process);
        if (process != 0) hook = SetWinEventHook(0x8000, 0x800B, IntPtr.Zero, callback, process, 0, 2); // OBJECT_CREATE..LOCATIONCHANGE, SKIPOWNPROCESS
    }

    private void OnEvent(IntPtr _, uint kind, IntPtr window, int objectId, int childId, uint threadId, uint time)
    {
        // Ignore focus/selection noise. Creation, removal, visibility, reordering and geometry can affect overlap.
        if (kind is not (0x8000 or 0x8001 or 0x8002 or 0x8003 or 0x8004 or 0x800B)) return;
        if (window == taskbar || IsChild(taskbar, window)) Revision++;
    }

    private void Detach()
    {
        if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
        taskbar = IntPtr.Zero;
    }

    public void Dispose() { Detach(); GC.KeepAlive(callback); }
}
