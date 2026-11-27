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
            try { WidgetTests.Run(); CheckArtwork(preview); CheckTrayLifecycle(); CheckPlacement(); CheckStrip(preview); CheckStripPopupOrder(); CheckTaskbarLayoutWatch(); }
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
        Guid[] ids = [Guid.NewGuid()];
        int submissions = 0;
        bool accept = false;
        using (var tray = new TrayIcon(ids[0], () => { submissions++; return accept; }))
        {
            if (!ids.All(Registered)) throw new Exception("Windows did not register the fallback tray entry.");
            int clicks = 0, menus = 0, balloons = 0;
            tray.Click += () => clicks++;
            tray.MenuRequested += () => menus++;
            tray.BalloonClick += () => balloons++;
            for (int id = 1; id <= 1; id++)
            foreach (int message in new[] { 0x0400, 0x0401, 0x007B, 0x0405 })
                SendMessage(tray.WindowHandle, TrayIcon.CallbackMessage, IntPtr.Zero, new IntPtr((id << 16) | message));
            if (clicks != 2 || menus != 1 || balloons != 1) throw new Exception("Tray mouse, keyboard, context-menu, or balloon callbacks failed.");
            if (tray.TryShowBalloon("Test", "Rejected", false)) throw new Exception("Rejected notifications must remain pending.");
            accept = true;
            if (!tray.TryShowBalloon("Test", "Accepted", false) || tray.TryShowBalloon("Test", "Wait", true) || submissions != 2)
                throw new Exception("An open notification must prevent a second notification from replacing it.");
            foreach (int completed in new[] { 0x0403, 0x0404, 0x0405 })
            {
                SendMessage(tray.WindowHandle, TrayIcon.CallbackMessage, IntPtr.Zero, new IntPtr((1 << 16) | completed));
                if (!tray.TryShowBalloon("Test", "Next pending notification", false))
                    throw new Exception("Closing a notification must allow the next one to be submitted.");
            }

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

    private static void CheckPlacement()
    {
        var state = new TaskbarState(new IntPtr(1), new(0, 1032, 1920, 48), new(0, 0, 1920, 1080), new Rect(1620, 1032, 300, 48), 1, true, false);
        var rect = TaskbarPlacement.Calculate(state, [new Rect(600, 1032, 650, 48)]);
        if (rect != new Rect(1464, 1041, 150, 30)) throw new Exception("The strip must be centered immediately left of the tray.");
        if (TaskbarPlacement.Calculate(state, [new Rect(600, 1032, 650, 48)], TaskbarPlacement.WideWidth) != new Rect(1400, 1041, 214, 30))
            throw new Exception("With Antigravity shown, the wider strip must still end just left of the tray.");
        var collision = TaskbarPlacement.Calculate(state, [new Rect(1400, 1032, 180, 48)]);
        if (collision is not Rect safe || safe.Bottom >= state.Bounds.Top) throw new Exception("A crowded taskbar must place the strip above it.");
        if (TaskbarPlacement.Calculate(state, null) != collision) throw new Exception("Unknown button geometry must use the safe adjacent position.");
        if (TaskbarPlacement.Calculate(state with { Fullscreen = true }, []) is not null || TaskbarPlacement.Calculate(state with { Visible = false }, []) is not null)
            throw new Exception("Fullscreen and hidden taskbars must hide the strip.");
        if (TaskbarPlacement.Calculate(state with { Bounds = new(0, 1078, 1920, 48) }, []) is not null)
            throw new Exception("An auto-hidden taskbar's activation edge must not keep the strip visible.");
        var scaled = state with { Bounds = new(-2560, 1368, 2560, 72), Monitor = new(-2560, 0, 2560, 1440), Tray = new Rect(-450, 1368, 450, 72), Scale = 1.5 };
        var scaledRect = TaskbarPlacement.Calculate(scaled, []);
        if (scaledRect != new Rect(-684, 1381.5, 225, 45)) throw new Exception("DPI scaling and negative monitor origins must use screen pixels.");
        var top = state with { Bounds = new(0, 0, 1920, 48), Tray = new Rect(1620, 0, 300, 48) };
        if (TaskbarPlacement.Calculate(top, null) is not Rect below || below.Top <= top.Bounds.Bottom) throw new Exception("A crowded top taskbar needs the strip below it.");
        var vertical = state with { Bounds = new(1872, 0, 48, 1080), Tray = new Rect(1872, 800, 48, 280) };
        if (TaskbarPlacement.Calculate(vertical, []) is not Rect left || left.Right >= vertical.Bounds.Left) throw new Exception("Vertical taskbars need an adjacent horizontal strip.");
    }

    private static void CheckStrip(bool preview)
    {
        int clicked = 0;
        using var strip = new TaskbarStrip(() => clicked++, () => new System.Windows.Controls.ContextMenu());
        var now = DateTimeOffset.UtcNow;
        strip.Update(new(now, "test", [new("session", "5-hour session", 0, now.AddHours(1).ToUnixTimeSeconds(), 300)]),
            new(now, "test", [new("weekly", "Weekly", 100, now.AddDays(1).ToUnixTimeSeconds(), 10080)]), now);
        var content = (System.Windows.Controls.Border)strip.Content;
        strip.Content = null; // A hidden parent Window suppresses WPF drawing; render the actual content independently.
        content.Measure(new Size(150, 30)); content.Arrange(new Rect(0, 0, 150, 30)); content.UpdateLayout();
        var row = (System.Windows.Controls.StackPanel)content.Child;
        var providerButtons = row.Children.Cast<System.Windows.Controls.Button>().Where(b => b.Visibility == Visibility.Visible).ToArray();
        if (providerButtons.Length != 2 || row.DesiredSize.Width > 144 || row.DesiredSize.Width < 100) throw new Exception("Both provider pairs must fit inside the strip, including 100%.");
        if (!System.Windows.Automation.AutomationProperties.GetName(providerButtons[0]).Contains("100% remaining") ||
            !System.Windows.Automation.AutomationProperties.GetName(providerButtons[1]).Contains("0% remaining"))
            throw new Exception("The strip must expose both provider readings to accessibility tools.");
        foreach (var button in providerButtons) button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (clicked != 2) throw new Exception("Either provider must reveal the widget.");
        var now3 = DateTimeOffset.UtcNow;
        var full = new UsageSnapshot(now3, "test", [new("session", "5-hour session", 0, now3.AddHours(1).ToUnixTimeSeconds(), 300)]);
        strip.Update(TrayReading.Create(UsageProvider.Claude, full, now3), TrayReading.Merge(UsageProvider.Codex, [("a@example.com", full), ("b@example.com", full)], now3),
            TrayReading.Create(UsageProvider.Antigravity, full, now3));
        content.Measure(new Size(TaskbarPlacement.WideWidth, 30)); content.Arrange(new Rect(0, 0, TaskbarPlacement.WideWidth, 30)); content.UpdateLayout();
        if (providerButtons[0].Parent is not System.Windows.Controls.StackPanel || row.Children.Count != 3 || row.Children[2].Visibility != Visibility.Visible
            || row.DesiredSize.Width > TaskbarPlacement.WideWidth - 6 || !System.Windows.Automation.AutomationProperties.GetName((System.Windows.Controls.Button)row.Children[2]).Contains("Antigravity"))
            throw new Exception($"All three providers at 100% must fit inside the wide strip ({row.DesiredSize.Width:0} px).");
        strip.Update(TrayReading.Create(UsageProvider.Claude, null, now3), TrayReading.Create(UsageProvider.Codex, null, now3), null);
        content.Measure(new Size(150, 30)); content.UpdateLayout();
        if (row.Children[2].Visibility != Visibility.Collapsed) throw new Exception("Without Antigravity the Gemini mark must be hidden.");
        if (preview)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                int y = 0;
                foreach (bool light in new[] { false, true })
                {
                    strip.ApplyAppearance(new(24, light, false, Colors.White));
                    content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(300, 60, 192, 192, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    drawing.DrawImage(bitmap, new Rect(0, y, 300, 60));
                    y += 70;
                }
            }
            var image = new RenderTargetBitmap(300, 140, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string path = Path.Combine(AppContext.BaseDirectory, "strip-preview.png");
            using var file = File.Create(path); encoder.Save(file);
            Console.WriteLine("Taskbar strip preview: " + path);
        }
        strip.Update(null, null, now);
        if (providerButtons.Any(b => ((System.Windows.Controls.TextBlock)((System.Windows.Controls.StackPanel)b.Content).Children[1]).Text != "—"))
            throw new Exception("Missing readings must clear both percentages.");
        strip.SetEnabled(false);
        if (strip.IsVisible || strip.ShowInTaskbar || strip.ShowActivated) throw new Exception("The disabled strip must remain hidden without a taskbar button or focus stealing.");
    }

    internal static void InspectTaskbar()
    {
        var state = TaskbarProbe.Read();
        if (state is null) throw new Exception("No taskbar was found.");
        var buttons = TaskbarProbe.ReadButtons(state.Handle);
        Console.WriteLine($"Taskbar: {state.Bounds}; monitor: {state.Monitor}; tray: {state.Tray}; scale: {state.Scale}; visible: {state.Visible}; fullscreen: {state.Fullscreen}");
        Console.WriteLine("Buttons: " + (buttons is null ? "unknown" : string.Join("; ", buttons)));
        Console.WriteLine("Strip: " + TaskbarPlacement.Calculate(state, buttons));
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);

    private static bool IsAbove(IntPtr higher, IntPtr lower)
    {
        for (var window = GetWindow(lower, 3); window != IntPtr.Zero; window = GetWindow(window, 3)) // GW_HWNDPREV
            if (window == higher) return true;
        return false;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void CheckStripPopupOrder()
    {
        using var strip = new TaskbarStrip(() => { }, () => new System.Windows.Controls.ContextMenu());
        strip.Update(null, null, DateTimeOffset.UtcNow);
        strip.SetEnabled(true);
        Pump(TimeSpan.FromSeconds(1)); // Let Explorer's initial geometry query settle.
        if (!strip.IsVisible) throw new Exception("Popup stacking check needs a visible taskbar, outside fullscreen mode.");
        var row = (System.Windows.Controls.StackPanel)((System.Windows.Controls.Border)strip.Content).Child;
        var button = (System.Windows.Controls.Button)row.Children[0];
        var tooltip = new System.Windows.Controls.ToolTip {
            Content = "Claude · 22% remaining", PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top, StaysOpen = true
        };
        button.ToolTip = tooltip;
        tooltip.IsOpen = true;
        try
        {
            Pump(TimeSpan.FromMilliseconds(100));
            var popup = ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(tooltip)).Handle;
            var owner = new System.Windows.Interop.WindowInteropHelper(strip).Handle;
            if (!IsAbove(popup, owner)) throw new Exception("Tooltip did not initially open above the strip.");
            Pump(TimeSpan.FromSeconds(4)); // Cross several placement ticks and an asynchronous button refresh.
            if (!tooltip.IsOpen || !IsAbove(popup, owner))
                throw new Exception("Periodic taskbar positioning raised the strip above its open tooltip.");
        }
        finally { tooltip.IsOpen = false; }
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = button };
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Show widget" });
        menu.IsOpen = true;
        try
        {
            Pump(TimeSpan.FromMilliseconds(100));
            var popup = ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(menu)).Handle;
            var owner = new System.Windows.Interop.WindowInteropHelper(strip).Handle;
            Pump(TimeSpan.FromSeconds(4));
            if (!menu.IsOpen || !IsAbove(popup, owner))
                throw new Exception("Periodic taskbar positioning raised the strip above its open context menu.");
        }
        finally { menu.IsOpen = false; strip.SetEnabled(false); }
    }

    private static void CheckTaskbarLayoutWatch()
    {
        var taskbar = TaskbarProbe.Read() ?? throw new Exception("Taskbar watch check needs Explorer.");
        using var watch = new TaskbarLayoutWatch();
        watch.Attach(taskbar.Handle);
        if (!watch.Active) throw new Exception("Explorer's layout event hook could not be installed.");
        long revision = watch.Revision;
        watch.Attach(taskbar.Handle);
        if (watch.Revision != revision) throw new Exception("Reusing the same taskbar must retain its geometry cache.");
        var window = new Window { Title = "AI Usage layout check", Width = 100, Height = 60, Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            window.Show();
            Pump(TimeSpan.FromSeconds(2));
            if (watch.Revision == revision) throw new Exception("Adding a taskbar button must invalidate the cached overlap geometry.");
            revision = watch.Revision;
            window.Close();
            Pump(TimeSpan.FromSeconds(2));
            if (watch.Revision == revision) throw new Exception("Removing a taskbar button must invalidate the cached overlap geometry.");
        }
        finally { window.Close(); }
        watch.Attach(IntPtr.Zero);
        if (watch.Active) throw new Exception("Hiding the strip must release the Explorer event hook.");
        watch.Attach(taskbar.Handle);
        if (!watch.Active) throw new Exception("The layout watch must reconnect after taskbar recreation or re-enabling.");
        watch.Dispose();
        if (watch.Active) throw new Exception("Disposing the layout watch must release its native hook.");
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
