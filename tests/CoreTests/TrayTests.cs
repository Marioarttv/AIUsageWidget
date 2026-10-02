using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageWidget;

internal static class TrayTests
{
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flag);

    internal static void Run(bool preview)
    {
        Exception? failure = null;
        var thread = new Thread(() => {
            try { CheckArtwork(preview); CheckTrayLifecycle(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Tray artwork verification failed", failure);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier { public int Size; public IntPtr Window; public int Id; public Guid Item; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public int Size; public IntPtr Window; public int Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public int InfoFlags; public Guid Item; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out NativeRect rect);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NotifyData data);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private static bool Registered(Guid id)
    {
        var identifier = new IconIdentifier { Size = Marshal.SizeOf<IconIdentifier>(), Item = id };
        return Shell_NotifyIconGetRect(ref identifier, out _) == 0;
    }

    private static void CheckTrayLifecycle()
    {
        Guid[] ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        using (var tray = new TrayIcon(ids))
        {
            if (!ids.All(Registered)) throw new Exception("Windows did not register all four tray entries.");
            int clicks = 0, menus = 0, balloons = 0;
            tray.Click += () => clicks++;
            tray.MenuRequested += () => menus++;
            tray.BalloonClick += () => balloons++;
            for (int id = 1; id <= 4; id++)
            foreach (int message in new[] { 0x0400, 0x0401, 0x007B, 0x0405 })
                SendMessage(tray.WindowHandle, TrayIcon.CallbackMessage, IntPtr.Zero, new IntPtr((id << 16) | message));
            if (clicks != 8 || menus != 4 || balloons != 4) throw new Exception("Tray mouse, keyboard, context-menu, or balloon callbacks failed.");

            var now = DateTimeOffset.UtcNow;
            for (int used = 0; used <= 100; used++)
            {
                var state = new UsageSnapshot(now, "test", [new("session", "5-hour session", used, now.AddHours(1).ToUnixTimeSeconds(), 300)]);
                tray.Update(state, state, now);
            }
            tray.Update(null, null, now); // Clear both readings without removing either provider.
            if (!ids.All(Registered)) throw new Exception("Usage updates lost a tray entry.");

            // Simulate Explorer losing this app's entries, then notifying its window only.
            foreach (var id in ids)
            {
                var data = new NotifyData { Size = Marshal.SizeOf<NotifyData>(), Flags = 0x20, Item = id, Tip = "", Info = "", Title = "" };
                if (!Shell_NotifyIcon(2, ref data)) throw new Exception("Could not remove a test tray entry.");
            }
            SendMessage(tray.WindowHandle, TrayIcon.TaskbarCreated, IntPtr.Zero, IntPtr.Zero);
            if (!ids.All(Registered)) throw new Exception("Tray entries were not restored after Explorer's restart notification.");
            SendMessage(tray.WindowHandle, 0x031A, IntPtr.Zero, IntPtr.Zero);
            if (!ids.All(Registered)) throw new Exception("Theme changes lost a tray entry.");
            tray.Dispose(); // Disposing twice must be harmless.
        }
        if (ids.Any(Registered)) throw new Exception("Disposed tray entries were left in the shell.");
    }

    private static void CheckArtwork(bool preview)
    {
        foreach (int size in new[] { 16, 20, 24, 32, 48 })
        foreach (var provider in Enum.GetValues<UsageProvider>())
        foreach (bool light in new[] { false, true })
        foreach (string? number in new[] { null, "0", "9", "68", "100", "—" })
        {
            var appearance = new TrayAppearance(size, light, false, Colors.White);
            var bitmap = TrayArtwork.Render(provider, number, appearance);
            var pixels = new byte[size * size * 4];
            bitmap.CopyPixels(pixels, size * 4, 0);
            if (bitmap.PixelWidth != size || bitmap.PixelHeight != size || !Enumerable.Range(0, size * size).Any(i => pixels[i * 4 + 3] > 0))
                throw new Exception($"Empty or incorrectly scaled {provider} {number} icon at {size}px.");
            using var icon = TrayArtwork.Create(provider, number, appearance);
            if (icon.IsInvalid) throw new Exception("Native HICON creation failed.");
        }
        var contrast = TrayArtwork.Render(UsageProvider.Claude, "68", new(16, false, true, Colors.White));
        var contrastPixels = new byte[16 * 16 * 4];
        contrast.CopyPixels(contrastPixels, 16 * 4, 0);
        for (int i = 0; i < contrastPixels.Length; i += 4)
            if (contrastPixels[i] != contrastPixels[i + 1] || contrastPixels[i] != contrastPixels[i + 2])
                throw new Exception("High-contrast icons must use the system text color.");

        // Warmed up above: repeated icon changes must not retain GDI bitmaps or USER icon handles.
        using var process = Process.GetCurrentProcess();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int gdi = GetGuiResources(process.Handle, 0), user = GetGuiResources(process.Handle, 1);
        for (int i = 0; i < 250; i++)
        {
            using var icon = TrayArtwork.Create(UsageProvider.Codex, (i % 101).ToString(), new(24, false, false, Colors.White));
        }
        // WPF bitmap wrappers release their own backing resources through finalizers.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int gdiAfter = GetGuiResources(process.Handle, 0), userAfter = GetGuiResources(process.Handle, 1);
        if (gdiAfter > gdi + 2 || userAfter > user + 2)
            throw new Exception($"Changing tray numbers leaked native graphics resources: GDI {gdi} -> {gdiAfter}, USER {user} -> {userAfter}.");
        if (preview) WritePreview();
    }

    private static void WritePreview()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            int row = 0;
            foreach (bool light in new[] { false, true })
            foreach (int size in new[] { 16, 20, 24, 32 })
            {
                drawing.DrawRectangle(light ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(0, row * 52, 400, 52));
                int x = 12;
                foreach (var provider in Enum.GetValues<UsageProvider>())
                foreach (string? number in new[] { null, "68", "100", "0", "—" })
                {
                    drawing.DrawImage(TrayArtwork.Render(provider, number, new(size, light, false, Colors.White)), new Rect(x, row * 52 + (52 - size) / 2, size, size));
                    x += size + 6;
                }
                row++;
            }
        }
        var image = new RenderTargetBitmap(400, 416, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        string path = Path.Combine(AppContext.BaseDirectory, "tray-preview.png");
        using var file = File.Create(path); encoder.Save(file);
        Console.WriteLine("Tray artwork preview: " + path);
    }
}
