using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace UsageWidget;

public partial class MainWindow : Window
{
    private readonly CodexService codex = new();
    private readonly ClaudeService claude = new();
    private readonly WidgetSettings settings = DataStore.Read<WidgetSettings>("settings.json") ?? new();
    private readonly DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(90) };
    private readonly Forms.NotifyIcon tray;
    private Forms.ToolStripMenuItem? trayStartup, trayAlerts;
    private DateTimeOffset lastRefresh = DateTimeOffset.MinValue;
    private NativeDesktop.POINT? dragOrigin;
    private NativeDesktop.RECT dragRect;
    private bool quitting;

    public MainWindow()
    {
        InitializeComponent();
        bool reveal = Environment.GetCommandLineArgs().Contains("--show");
        if (reveal) { settings.Mode = "topmost"; settings.X = null; settings.Y = null; }
        else if (settings.Mode != "topmost") settings.Mode = "desktop";
        Left = SystemParameters.WorkArea.Right - Width - 28;
        Top = SystemParameters.WorkArea.Top + 60;
        tray = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "AI Usage Widget", Visible = true };
        tray.ContextMenuStrip = BuildTrayMenu();
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { Show(); SetMode("topmost"); });
        tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() => { Show(); SetMode("topmost"); Activate(); });
        Loaded += async (_, _) => {
            DataStore.Log("Widget loaded: mode=" + settings.Mode);
            NativeDesktop.KeepAtBottom(this, () => settings.Mode == "desktop");
            ApplyMode();
            Render();
            if (reveal) _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { NativeDesktop.CenterOnPrimary(this); SavePosition(); Activate(); }));
            displayTimer.Tick += (_, _) => { ProcessRequest(); Render(); };
            refreshTimer.Tick += async (_, _) => await Refresh(false);
            displayTimer.Start(); refreshTimer.Start();
            await Refresh(false);
        };
        Closing += (_, e) => {
            if (!quitting) { e.Cancel = true; Hide(); }
        };
    }

    private static System.Drawing.Icon MakeIcon()
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/widget.ico")).Stream;
        using var icon = new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
        return (System.Drawing.Icon)icon.Clone();
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show widget", null, (_, _) => Dispatcher.Invoke(() => { Show(); SetMode("topmost"); Activate(); }));
        menu.Items.Add("Always on top", null, (_, _) => Dispatcher.Invoke(() => SetMode("topmost")));
        menu.Items.Add("On desktop (below other windows)", null, (_, _) => Dispatcher.Invoke(() => SetMode("desktop")));
        menu.Items.Add("Refresh now", null, (_, _) => Dispatcher.InvokeAsync(() => Refresh(true)));
        trayStartup = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = StartupEnabled() };
        trayStartup.CheckedChanged += (_, _) => Dispatcher.Invoke(() => SetStartup(trayStartup.Checked));
        menu.Items.Add(trayStartup);
        trayAlerts = new Forms.ToolStripMenuItem("Low 5-hour limit alerts") { CheckOnClick = true, Checked = settings.LowUsageAlerts };
        trayAlerts.CheckedChanged += (_, _) => Dispatcher.Invoke(() => SetAlerts(trayAlerts.Checked));
        menu.Items.Add(trayAlerts);
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
        void Add(string label, RoutedEventHandler handler, bool check = false, bool selected = false) {
            var item = new MenuItem { Header = label, IsCheckable = check, IsChecked = selected }; item.Click += handler; menu.Items.Add(item);
        }
        Add("Always on top", (_, _) => SetMode("topmost"), true, settings.Mode == "topmost");
        Add("On desktop (below other windows)", (_, _) => SetMode("desktop"), true, settings.Mode == "desktop");
        Add("Start with Windows", (_, _) => SetStartup(!StartupEnabled()), true, StartupEnabled());
        Add("Low 5-hour limit alerts", (_, _) => SetAlerts(!settings.LowUsageAlerts), true, settings.LowUsageAlerts);
        menu.Items.Add(new Separator());
        Add("Refresh now", async (_, _) => await Refresh(true));
        Add("Open Claude usage", (_, _) => OpenUrl("https://claude.ai/settings/usage"));
        Add("Hide widget", (_, _) => Hide());
        Add("Quit", (_, _) => Quit());
        menu.PlacementTarget = (Button)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Refresh(true);
    private void PinClick(object sender, RoutedEventArgs e) => SetMode(settings.Mode == "topmost" ? "desktop" : "topmost");
    private async Task Refresh(bool manual)
    {
        if (codex.Refreshing || claude.Refreshing || DateTimeOffset.UtcNow - lastRefresh < TimeSpan.FromSeconds(10)) return;
        lastRefresh = DateTimeOffset.UtcNow;
        RefreshButton.IsEnabled = false;
        var codexTask = codex.RefreshAsync();
        var claudeTask = claude.RefreshAsync(manual);
        Render();
        try { await Task.WhenAll(codexTask, claudeTask); }
        finally { RefreshButton.IsEnabled = true; Render(); }
    }

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        var claudeAccount = DataStore.ReadSnapshot(ClaudeService.SnapshotName);
        var claudeState = ClaudeService.Merge(claudeAccount, DataStore.ReadSnapshot("claude.json"));
        var codexState = DataStore.ReadSnapshot("codex.json");
        DrawService(ClaudePanel, "Claude", "#D99776", claude.Plan ?? claudeAccount?.Plan, claudeState, claude.Refreshing, claude.Status, now,
            "Claude subscription usage across claude.ai, Claude Code, and SDK tools, read with your Claude Code sign-in.");
        DrawService(CodexPanel, "Codex", "#87BBA7", codexState?.Plan, codexState, codex.Refreshing, codex.Status, now,
            "Codex subscription usage from the Codex CLI.");
        CheckAlerts("Claude", claudeState, now);
        CheckAlerts("Codex", codexState, now);
        bool pinned = settings.Mode == "topmost";
        // Filled, highlighted pin while always on top; muted crossed-out pin while on the desktop.
        PinButton.Content = pinned ? "\uE841" : "\uE77A";
        if (pinned) { PinButton.Foreground = Color("#F0F3F7"); PinButton.Background = Color("#2A3648"); }
        else { PinButton.ClearValue(ForegroundProperty); PinButton.ClearValue(BackgroundProperty); }
        PinButton.ToolTip = pinned ? "Always on top · click to place it on the desktop below other windows" : "On the desktop below other windows · click to keep it on top";
    }

    private static Brush Color(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    private static TextBlock Text(string value, double size, string color = "#F0F3F7") => new() { Text = value, FontSize = size, Foreground = Color(color), VerticalAlignment = VerticalAlignment.Center };
    private static string Severity(double remaining, string accent) => remaining <= 10 ? "#F08989" : remaining <= 25 ? "#E4BF7D" : accent;
    private static string ResetTime(long reset) => DateTimeOffset.FromUnixTimeSeconds(reset).ToLocalTime().ToString("dddd, d MMMM 'at' HH:mm");

    private static Grid Bar(double remaining, string color, double height)
    {
        var bar = new Grid { Height = height, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(new Border { Background = Color("#263140"), CornerRadius = new CornerRadius(height / 2) });
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(remaining, GridUnitType.Star) });
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - remaining, GridUnitType.Star) });
        fill.Children.Add(new Border { Background = Color(color), CornerRadius = new CornerRadius(height / 2), Visibility = remaining <= 0 ? Visibility.Collapsed : Visibility.Visible });
        bar.Children.Add(fill);
        return bar;
    }

    // The shortest window (the 5-hour session when present) is the headline; longer windows are listed compactly below it.
    private static void DrawService(StackPanel panel, string name, string accent, string? plan, UsageSnapshot? state, bool refreshing, string? problem, DateTimeOffset now, string about)
    {
        panel.Children.Clear();
        var windows = state?.Windows ?? new List<UsageWindow>();
        var primary = windows.OrderBy(w => w.WindowMinutes ?? int.MaxValue).FirstOrDefault();
        double? remaining = primary is null || UsageFormatting.Expired(primary, now) ? null : Math.Clamp(100 - primary.UsedPercent, 0, 100);

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, ToolTip = about };
        title.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = Color(accent), Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        var label = Text(name, 13); label.FontWeight = FontWeights.SemiBold; title.Children.Add(label);
        if (!string.IsNullOrWhiteSpace(plan)) { var badge = Text(plan.ToUpperInvariant(), 8.5, "#7F8B9C"); badge.Margin = new Thickness(6, 2, 0, 0); title.Children.Add(badge); }
        head.Children.Add(title);
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Bottom };
        value.Inlines.Add(new Run(remaining is double r ? $"{r:0}%" : "—") { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Color(remaining is double low && low <= 25 ? Severity(low, accent) : remaining is null ? "#6B798C" : "#F0F3F7") });
        if (remaining is not null) value.Inlines.Add(new Run(" left") { FontSize = 10, Foreground = Color("#7F8B9C") });
        Grid.SetColumn(value, 1); head.Children.Add(value);
        panel.Children.Add(head);

        var hero = Bar(remaining ?? 0, Severity(remaining ?? 100, accent), 5); hero.Margin = new Thickness(0, 4, 0, 5);
        panel.Children.Add(hero);

        var caption = new Grid();
        caption.ColumnDefinitions.Add(new ColumnDefinition()); caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string when = primary is null ? "waiting for a reading" : UsageFormatting.Expired(primary, now) ? "awaiting update" : primary.ResetsAt is null ? "" : "resets in " + UsageFormatting.Short(primary.ResetsAt, now);
        var detail = Text(primary is null ? "5-hour session · " + when : when.Length == 0 ? primary.Label : primary.Label + " · " + when, 10, "#9AA6B7");
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        if (primary?.ResetsAt is long reset) detail.ToolTip = "Resets " + ResetTime(reset);
        caption.Children.Add(detail);
        var age = Text(refreshing ? "updating…" : state is null ? "connecting…" : UsageFormatting.Age(state.UpdatedAt, now), 9.5, "#6F7C8E");
        age.Margin = new Thickness(8, 0, 0, 0);
        age.ToolTip = state is null ? about : "Last reading: " + state.UpdatedAt.ToLocalTime().ToString("f");
        Grid.SetColumn(age, 1); caption.Children.Add(age);
        panel.Children.Add(caption);

        var others = windows.Where(w => w != primary).ToList();
        if (others.Count > 0)
        {
            // One grid keeps the secondary bars aligned regardless of label length.
            var list = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 110 });
            list.ColumnDefinitions.Add(new ColumnDefinition());
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < others.Count; i++)
            {
                var window = others[i];
                bool expired = UsageFormatting.Expired(window, now);
                double left = Math.Clamp(100 - window.UsedPercent, 0, 100);
                list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(17) });
                string tip = window.Label + (expired ? " · awaiting update" : $" · {left:0}% left") + (window.ResetsAt is long at ? "\nResets " + ResetTime(at) : "");
                var cells = new FrameworkElement[] {
                    Text(window.Label, 10, "#AAB5C5"),
                    Bar(expired ? 0 : left, Severity(left, accent), 3),
                    Text(expired ? "—" : $"{left:0}%", 10, expired ? "#6B798C" : left <= 25 ? Severity(left, accent) : "#CAD3DF"),
                    Text(expired ? "now" : UsageFormatting.Short(window.ResetsAt, now), 9.5, "#6F7C8E"),
                };
                ((TextBlock)cells[0]).TextTrimming = TextTrimming.CharacterEllipsis;
                cells[1].Margin = new Thickness(10, 0, 0, 0);
                cells[2].Margin = new Thickness(8, 0, 0, 0); cells[2].MinWidth = 28; ((TextBlock)cells[2]).TextAlignment = TextAlignment.Right; ((TextBlock)cells[2]).FontWeight = FontWeights.SemiBold;
                cells[3].Margin = new Thickness(6, 0, 0, 0); cells[3].MinWidth = 36; ((TextBlock)cells[3]).TextAlignment = TextAlignment.Right;
                for (int c = 0; c < cells.Length; c++) { cells[c].ToolTip = tip; Grid.SetRow(cells[c], i); Grid.SetColumn(cells[c], c); list.Children.Add(cells[c]); }
            }
            panel.Children.Add(list);
        }

        if (problem is not null)
        {
            var notice = Text(problem, 10, "#E4BF7D"); notice.TextWrapping = TextWrapping.Wrap; notice.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(notice);
        }
    }

    // Notifies once per 5-hour window when less than 10% remains; the entry is dropped once the window resets or recovers.
    private void CheckAlerts(string service, UsageSnapshot? state, DateTimeOffset now)
    {
        if (state is null) return;
        bool changed = settings.AlertedWindows.RemoveAll(entry => entry.StartsWith(service + "|") && entry.Split('|') is [_, _, var reset] && long.TryParse(reset, out long at) && at <= now.ToUnixTimeSeconds()) > 0;
        foreach (var window in state.Windows.Where(w => w.WindowMinutes == 300))
        {
            string id = $"{service}|{window.Key}|{window.ResetsAt}";
            // Sources can report the same reset a few seconds apart; treat those as one window.
            bool Same(string entry) => entry == id || entry.Split('|') is [var s, var k, var r] && s == service && k == window.Key
                && long.TryParse(r, out long at) && window.ResetsAt is long reset && Math.Abs(at - reset) < 900;
            double left = Math.Clamp(100 - window.UsedPercent, 0, 100);
            if (UsageFormatting.Expired(window, now) || left >= 10) { changed |= settings.AlertedWindows.RemoveAll(Same) > 0; continue; }
            if (settings.AlertedWindows.Any(Same)) continue;
            settings.AlertedWindows.Add(id); changed = true;
            if (!settings.LowUsageAlerts) continue;
            string reset = window.ResetsAt is long at ? $" · resets in {UsageFormatting.Short(at, now)} ({DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime():HH:mm})" : "";
            tray.ShowBalloonTip(10_000, $"{service} 5-hour limit is low", $"{left:0}% left{reset}", Forms.ToolTipIcon.Warning);
            DataStore.Log($"Low usage alert: {service} {window.Key}");
        }
        if (changed) DataStore.Write("settings.json", settings);
    }

    private void SetMode(string mode)
    {
        settings.Mode = mode;
        SavePosition(); ApplyMode(); Render();
    }
    private void ApplyMode()
    {
        NativeDesktop.Apply(this, settings.Mode == "desktop");
        if (settings.X is int x && settings.Y is int y) NativeDesktop.Move(this, x, y, true);
        NativeDesktop.GetWindowRect(NativeDesktop.Handle(this), out var rect);
        DataStore.Log($"Placement: mode={settings.Mode}, rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
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
        if (trayStartup is not null) trayStartup.Checked = enabled;
    }
    private void SetAlerts(bool enabled) {
        settings.LowUsageAlerts = enabled; DataStore.Write("settings.json", settings);
        if (trayAlerts is not null) trayAlerts.Checked = enabled;
    }
    private void Quit() {
        quitting = true; SavePosition(); displayTimer.Stop(); refreshTimer.Stop();
        tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose();
        Close(); System.Windows.Application.Current.Shutdown();
    }
}
