using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace UsageWidget;

internal sealed class TaskbarStrip : Window, IDisposable
{
    private readonly Border surface;
    private readonly Button claudeButton, codexButton;
    private readonly TextBlock claudeNumber, codexNumber;
    private readonly Path claudeMark, codexMark;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Func<ContextMenu> buildMenu;
    private readonly Action reveal;
    private HwndSource? source;
    private TaskbarState? lastState;
    private IReadOnlyList<Rect>? buttons;
    private DateTimeOffset buttonsReadAt;
    private bool probing, disposed, enabled;
    private TrayAppearance? appearance;
    private Rect? lastPlacement;
    internal event Action? PlacementChanged;

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(IntPtr window, int index, nint value);

    internal TaskbarStrip(Action reveal, Func<ContextMenu> buildMenu)
    {
        this.reveal = reveal; this.buildMenu = buildMenu;
        Title = "AI Usage taskbar strip";
        Width = 150; Height = 30; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false;
        Topmost = true; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        (claudeButton, claudeMark, claudeNumber) = ProviderButton(UsageProvider.Claude);
        (codexButton, codexMark, codexNumber) = ProviderButton(UsageProvider.Codex);
        row.Children.Add(claudeButton); row.Children.Add(codexButton);
        surface = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(3, 0, 3, 0), Child = row };
        Content = surface;
        PreviewMouseRightButtonUp += (_, e) => { OpenMenu(); e.Handled = true; };
        SourceInitialized += (_, _) => {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x08000000 | 0x00000080); // NOACTIVATE | TOOLWINDOW
            source = HwndSource.FromHwnd(handle); source.AddHook(Hook);
        };
        timer.Tick += (_, _) => Reposition();
    }

    private (Button, Path, TextBlock) ProviderButton(UsageProvider provider)
    {
        var mark = new Path { Data = BrandMarks.For(provider), Width = 17, Height = 17, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        var number = new TextBlock { Text = "—", FontSize = 13, FontWeight = FontWeights.SemiBold, MinWidth = 33, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(mark); row.Children.Add(number);
        var button = new Button { Content = row, Padding = new Thickness(5, 0, 5, 0), Height = 30, Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(button, provider == UsageProvider.Claude ? "Claude usage" : "ChatGPT / Codex usage");
        button.Click += (_, _) => reveal();
        ToolTipService.SetInitialShowDelay(button, 350);
        return (button, mark, number);
    }

    internal void SetEnabled(bool value)
    {
        if (disposed) return;
        enabled = value;
        if (value) { timer.Start(); Reposition(); }
        else { timer.Stop(); Hide(); }
    }

    internal void Update(UsageSnapshot? claude, UsageSnapshot? codex, DateTimeOffset now)
    {
        UpdateProvider(claudeButton, claudeNumber, TrayReading.Create(UsageProvider.Claude, claude, now));
        UpdateProvider(codexButton, codexNumber, TrayReading.Create(UsageProvider.Codex, codex, now));
    }

    private static void UpdateProvider(Button button, TextBlock number, TrayReading reading)
    {
        number.Text = reading.Number == "—" ? "—" : reading.Number + "%";
        button.ToolTip = reading.Tooltip;
        AutomationProperties.SetName(button, reading.Tooltip.Replace('\n', ' '));
    }

    internal void RefreshEnvironment()
    {
        appearance = null; buttonsReadAt = DateTimeOffset.MinValue;
        if (enabled) Reposition();
    }

    internal void ApplyAppearance(TrayAppearance current)
    {
        if (appearance == current) return;
        appearance = current;
        surface.Background = current.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(current.Light ? Color.FromRgb(229, 229, 235) : Color.FromRgb(48, 48, 55));
        var claude = current.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(current.Light ? Color.FromRgb(155, 72, 37) : Color.FromRgb(232, 162, 128));
        var codex = current.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(current.Light ? Color.FromRgb(23, 101, 74) : Color.FromRgb(155, 220, 191));
        claudeMark.Fill = claude; claudeNumber.Foreground = claude;
        codexMark.Fill = codex; codexNumber.Foreground = codex;
    }

    private void Reposition()
    {
        if (!enabled || disposed) return;
        var state = TaskbarProbe.Read();
        if (state is null) { lastState = null; buttons = null; lastPlacement = null; Hide(); return; }
        if (lastState?.Handle != state.Handle || lastState.Bounds != state.Bounds || lastState.Tray != state.Tray)
        { buttons = null; buttonsReadAt = DateTimeOffset.MinValue; }
        lastState = state;
        var placement = TaskbarPlacement.Calculate(state, buttons);
        if (placement is null) { lastPlacement = null; Hide(); return; }
        if (!probing && DateTimeOffset.UtcNow - buttonsReadAt >= TimeSpan.FromSeconds(3)) _ = ProbeButtons(state);
        if (appearance is null) ApplyAppearance(TrayAppearance.Read());
        bool changed = lastPlacement != placement || !IsVisible;
        if (!IsVisible) Show();
        var rect = placement.Value;
        var handle = new WindowInteropHelper(this).Handle;
        // Keep above Explorer without taking focus from the user's current app.
        SetWindowPos(handle, new IntPtr(-1), (int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height), 0x0010);
        lastPlacement = rect;
        if (changed) PlacementChanged?.Invoke();
    }

    private async Task ProbeButtons(TaskbarState state)
    {
        probing = true;
        try
        {
            var found = await Task.Run(() => TaskbarProbe.ReadButtons(state.Handle));
            if (disposed || lastState?.Handle != state.Handle || lastState.Bounds != state.Bounds || lastState.Tray != state.Tray) return;
            buttons = found; buttonsReadAt = DateTimeOffset.UtcNow;
        }
        finally { probing = false; }
        if (enabled && !disposed) Reposition();
    }

    private void OpenMenu()
    {
        var menu = buildMenu();
        menu.PlacementTarget = this;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE, but still deliver clicks.
        if (message is 0x001A or 0x007E or 0x02E0 or 0x031A)
            _ = Dispatcher.BeginInvoke(new Action(RefreshEnvironment));
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop();
        source?.RemoveHook(Hook);
        Close();
    }
}
