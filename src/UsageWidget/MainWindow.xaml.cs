using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace UsageWidget;

public partial class MainWindow : Window
{
    private readonly CodexService codex = new();
    private readonly WidgetSettings settings = DataStore.Read<WidgetSettings>("settings.json") ?? new();
    private readonly DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(90) };
    private readonly Forms.NotifyIcon tray;
    private DateTimeOffset lastManualRefresh = DateTimeOffset.MinValue;
    private NativeDesktop.POINT? dragOrigin;
    private NativeDesktop.RECT dragRect;
    private bool desktopAttached;
    private bool quitting;

    public MainWindow()
    {
        InitializeComponent();
        bool reveal = Environment.GetCommandLineArgs().Contains("--show");
        if (reveal) { settings.Mode = "topmost"; settings.X = null; settings.Y = null; }
        Left = SystemParameters.WorkArea.Right - Width - 28;
        Top = SystemParameters.WorkArea.Top + 60;
        tray = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "AI Usage Widget", Visible = true };
        tray.ContextMenuStrip = BuildTrayMenu();
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { Show(); SetMode("topmost"); });
        Loaded += async (_, _) => {
            DataStore.Log("Widget loaded: mode=" + settings.Mode);
            ApplyMode();
            Render();
            if (reveal) _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { NativeDesktop.CenterOnPrimary(this); SavePosition(); Activate(); }));
            displayTimer.Tick += (_, _) => { ProcessRequest(); Render(); };
            refreshTimer.Tick += async (_, _) => await Refresh();
            displayTimer.Start(); refreshTimer.Start();
            await Refresh();
        };
        Closing += (_, e) => {
            if (!quitting) { e.Cancel = true; Hide(); }
        };
    }

    private static System.Drawing.Icon MakeIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.Clear(System.Drawing.Color.Transparent);
        using var green = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(135, 187, 167));
        using var orange = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(217, 151, 118));
        g.FillRectangle(orange, 5, 13, 8, 14); g.FillRectangle(green, 18, 5, 8, 22);
        var handle = bitmap.GetHicon();
        try { using var borrowed = System.Drawing.Icon.FromHandle(handle); return (System.Drawing.Icon)borrowed.Clone(); }
        finally { NativeDesktop.DestroyIcon(handle); }
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show widget", null, (_, _) => Dispatcher.Invoke(() => { Show(); SetMode("topmost"); Activate(); }));
        menu.Items.Add("On desktop", null, (_, _) => Dispatcher.Invoke(() => SetMode("desktop")));
        menu.Items.Add("Always on top", null, (_, _) => Dispatcher.Invoke(() => SetMode("topmost")));
        menu.Items.Add("Refresh Codex", null, async (_, _) => await Dispatcher.InvokeAsync(async () => await Refresh()));
        var login = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = StartupEnabled() };
        login.CheckedChanged += (_, _) => Dispatcher.Invoke(() => SetStartup(login.Checked));
        menu.Items.Add(login);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open Claude usage", null, (_, _) => OpenUrl("https://claude.ai/settings/usage"));
        menu.Items.Add("Open usage data folder", null, (_, _) => Process.Start(new ProcessStartInfo(DataStore.DirectoryPath) { UseShellExecute = true }));
        menu.Items.Add("Hide widget", null, (_, _) => Dispatcher.Invoke(Hide));
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Quit));
        return menu;
    }

    private void OpenMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        // Local text style keeps the standard Windows menu readable on its light background.
        var menuText = new Style(typeof(TextBlock));
        menuText.Setters.Add(new Setter(TextBlock.ForegroundProperty, Color("#18212F")));
        menuText.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI")));
        menu.Resources[typeof(TextBlock)] = menuText;
        void Add(string label, RoutedEventHandler handler, bool check = false, bool selected = false) {
            var item = new MenuItem { Header = label, IsCheckable = check, IsChecked = selected }; item.Click += handler; menu.Items.Add(item);
        }
        Add("On desktop", (_, _) => SetMode("desktop"), true, settings.Mode == "desktop");
        Add("Always on top", (_, _) => SetMode("topmost"), true, settings.Mode == "topmost");
        Add("Start with Windows", (_, _) => SetStartup(!StartupEnabled()), true, StartupEnabled());
        menu.Items.Add(new Separator());
        Add("Refresh Codex", async (_, _) => await Refresh());
        Add("Open Claude usage", (_, _) => OpenUrl("https://claude.ai/settings/usage"));
        Add("Hide widget", (_, _) => Hide());
        Add("Quit", (_, _) => Quit());
        menu.PlacementTarget = (Button)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Refresh();
    private void PinClick(object sender, RoutedEventArgs e) => SetMode(settings.Mode == "desktop" ? "topmost" : "desktop");
    private async Task Refresh()
    {
        if (codex.Refreshing || DateTimeOffset.UtcNow - lastManualRefresh < TimeSpan.FromSeconds(10)) return;
        lastManualRefresh = DateTimeOffset.UtcNow;
        RefreshButton.IsEnabled = false;
        CodexStatus.Text = "Refreshing…";
        await codex.RefreshAsync();
        RefreshButton.IsEnabled = true;
        Render();
    }

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        var claudeState = DataStore.ReadSnapshot("claude.json");
        var codexState = DataStore.ReadSnapshot("codex.json");
        DrawRows(ClaudeRows, claudeState, "#D99776", now, true);
        DrawRows(CodexRows, codexState, "#87BBA7", now, false);
        ClaudeStatus.Text = claudeState?.Windows.Count > 0 ? "Last Claude Code reading · " + UsageFormatting.Age(claudeState.UpdatedAt, now) : "Waiting for Claude Code · updates after a response";
        ClaudeStatus.ToolTip = claudeState is null ? "Use your existing Claude Code session or start one. No messages are generated by this widget." : "Subscription usage from Claude Code. Other Claude activity may not appear until the next Claude Code response.\nLast reading: " + claudeState.UpdatedAt.ToLocalTime().ToString("f");
        bool stale = codexState is not null && now - codexState.UpdatedAt > TimeSpan.FromMinutes(3);
        CodexStatus.Text = codex.Refreshing ? "Refreshing…" : codex.Status ?? (codexState is null ? "Connecting to Codex…" : (stale ? "Last reading · " : "Updated · ") + UsageFormatting.Age(codexState.UpdatedAt, now));
        CodexStatus.ToolTip = codexState?.UpdatedAt.ToLocalTime().ToString("f");
        CodexPlan.Text = codexState?.Plan?.ToUpperInvariant() ?? "SUBSCRIPTION";
        ModeLabel.Text = settings.Mode == "topmost" ? "Always on top" : desktopAttached ? "On your desktop" : "Desktop · behind windows";
        PinButton.Content = settings.Mode == "topmost" ? "↙" : "↗";
    }

    private static Brush Color(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    private static TextBlock Text(string value, double size, string color = "#F0F3F7") => new() { Text = value, FontSize = size, Foreground = Color(color) };
    private static void DrawRows(StackPanel container, UsageSnapshot? state, string accent, DateTimeOffset now, bool claude)
    {
        container.Children.Clear();
        if (state is null || state.Windows.Count == 0)
        {
            foreach (string label in claude ? new[] { "5-hour session", "Weekly" } : new[] { "Subscription allowance" }) {
                var holder = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                var title = new Grid(); title.Children.Add(Text(label, 11, "#AAB5C5"));
                var unknown = Text("—", 14, "#6B798C"); unknown.HorizontalAlignment = HorizontalAlignment.Right; title.Children.Add(unknown);
                holder.Children.Add(title);
                holder.Children.Add(new Border { Height = 5, Background = Color("#2A3545"), CornerRadius = new CornerRadius(2.5), Margin = new Thickness(0, 7, 0, 3) });
                container.Children.Add(holder);
            }
            return;
        }
        foreach (var window in state.Windows)
        {
            bool expired = UsageFormatting.Expired(window, now);
            double remaining = Math.Clamp(100 - window.UsedPercent, 0, 100);
            var holder = new StackPanel { Margin = new Thickness(0, 0, 0, 11) };
            var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Text(window.Label, 11, "#CAD3DF"); label.VerticalAlignment = VerticalAlignment.Center; label.TextTrimming = TextTrimming.CharacterEllipsis; label.ToolTip = window.Label; heading.Children.Add(label);
            var percent = Text(expired ? "—" : $"{remaining:0}% left", 15, expired ? "#6B798C" : remaining <= 10 ? "#F08989" : "#F0F3F7"); percent.FontWeight = FontWeights.SemiBold; Grid.SetColumn(percent, 1); heading.Children.Add(percent);
            holder.Children.Add(heading);
            var bar = new Grid { Height = 5, Margin = new Thickness(0, 8, 0, 6), ClipToBounds = true };
            bar.Children.Add(new Border { Background = Color("#2A3545"), CornerRadius = new CornerRadius(2.5) });
            double value = expired ? 0 : remaining;
            var fill = new Grid(); fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(value, GridUnitType.Star) }); fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - value, GridUnitType.Star) });
            fill.Children.Add(new Border { Background = Color(remaining <= 10 ? "#F08989" : remaining <= 25 ? "#E4BF7D" : accent), CornerRadius = new CornerRadius(2.5), Visibility = value == 0 ? Visibility.Collapsed : Visibility.Visible });
            bar.Children.Add(fill); holder.Children.Add(bar);
            var countdown = Text(UsageFormatting.Countdown(window.ResetsAt, now), 10, "#9AA6B7");
            if (window.ResetsAt is long reset) countdown.ToolTip = DateTimeOffset.FromUnixTimeSeconds(reset).ToLocalTime().ToString("dddd, d MMMM yyyy 'at' HH:mm");
            holder.Children.Add(countdown); container.Children.Add(holder);
        }
    }

    private void SetMode(string mode)
    {
        settings.Mode = mode;
        SavePosition(); ApplyMode(); Render();
    }
    private void ApplyMode()
    {
        desktopAttached = NativeDesktop.Apply(this, settings.Mode == "desktop");
        if (settings.X is int x && settings.Y is int y) NativeDesktop.Move(this, x, y, true);
        NativeDesktop.GetWindowRect(NativeDesktop.Handle(this), out var rect);
        DataStore.Log($"Placement: attached={desktopAttached}, rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
    }
    private void DragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindButton(source)) return;
        NativeDesktop.GetCursorPos(out var cursor);
        NativeDesktop.GetWindowRect(NativeDesktop.Handle(this), out dragRect);
        dragOrigin = cursor; DragHandle.CaptureMouse(); e.Handled = true;
    }
    private static bool FindButton(DependencyObject source) {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node)) if (node is Button) return true;
        return false;
    }
    private void DragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (dragOrigin is not NativeDesktop.POINT origin) return;
        NativeDesktop.GetCursorPos(out var cursor);
        NativeDesktop.Move(this, dragRect.Left + cursor.X - origin.X, dragRect.Top + cursor.Y - origin.Y, false);
    }
    private void DragEnd(object sender, MouseButtonEventArgs e) { dragOrigin = null; DragHandle.ReleaseMouseCapture(); SavePosition(); }
    private void DragLost(object sender, System.Windows.Input.MouseEventArgs e) { if (dragOrigin is not null) { dragOrigin = null; SavePosition(); } }
    private void SavePosition() {
        NativeDesktop.GetWindowRect(NativeDesktop.Handle(this), out var rect);
        settings.X = rect.Left; settings.Y = rect.Top; DataStore.Write("settings.json", settings);
    }
    private void ProcessRequest() {
        var request = DataStore.Read<System.Text.Json.Nodes.JsonObject>("request.json");
        if (request is null) return;
        try { File.Delete(Path.Combine(DataStore.DirectoryPath, "request.json")); } catch { }
        string? action = request["action"]?.GetValue<string>();
        Show(); SetMode(action == "desktop" ? "desktop" : "topmost");
        if (action == "reveal") { NativeDesktop.CenterOnPrimary(this); SavePosition(); }
        if (action != "desktop") Activate();
    }
    private static bool StartupEnabled() {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("AIUsageWidget") is string;
    }
    private void SetStartup(bool enabled) {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("AIUsageWidget", '"' + Environment.ProcessPath! + '"'); else key.DeleteValue("AIUsageWidget", false);
        settings.StartAtLogin = enabled; DataStore.Write("settings.json", settings);
        if (tray.ContextMenuStrip?.Items[4] is Forms.ToolStripMenuItem item) item.Checked = enabled;
    }
    private void Quit() {
        quitting = true; SavePosition(); displayTimer.Stop(); refreshTimer.Stop();
        tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose();
        Close(); System.Windows.Application.Current.Shutdown();
    }
}
