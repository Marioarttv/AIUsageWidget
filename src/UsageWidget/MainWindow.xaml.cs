using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace UsageWidget;

public partial class MainWindow : Window
{
    private readonly ClaudeService claude = new();
    private readonly AntigravityService antigravity = new();
    // One reader per tracked ChatGPT sign-in, keyed by UsageSource.Id.
    private Dictionary<string, CodexService> codexServices = new();
    private List<CodexAccount> codexAccounts = new();
    // Claude, then each tracked ChatGPT sign-in, then Antigravity, in display order.
    private List<UsageSource> sources = [UsageSource.Claude, UsageSource.Codex];
    private bool antigravityInstalled;
    private readonly WidgetSettings settings = DataStore.Read<WidgetSettings>("settings.json") ?? new();
    private readonly ResetAlertLedger resetAlerts = DataStore.Read<ResetAlertLedger>("reset-alerts.json") ?? new();
    private readonly DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMinutes(3) };
    private readonly TrayIcon tray;
    private readonly TaskbarStrip taskbarStrip;
    private readonly Dictionary<string, (DateTime Written, UsageSnapshot? Value)> snapshots = new();
    private readonly Dictionary<string, (string Key, FrameworkElement Card)> drawn = new();
    private string drawnOrder = "";
    private Palette palette;
    private Grid DragHandle = null!;
    private Button RefreshButton = null!, PinButton = null!;
    private StackPanel CardsPanel = null!;
    private ScrollViewer Scroller = null!;
    private Border Fade = null!;
    private Popup? datePopup;
    private (string Source, DateTime At) dateClosed;
    private DateTimeOffset lastRefresh = DateTimeOffset.MinValue;
    private bool refreshing, refreshAgain;
    private NativeDesktop.POINT? dragOrigin;
    private NativeDesktop.RECT dragRect;
    private bool quitting;

    public MainWindow()
    {
        InitializeComponent();
        palette = Palette.For(settings.Theme);
        palette.Apply(System.Windows.Application.Current.Resources);
        BuildShell();
        bool reveal = Environment.GetCommandLineArgs().Contains("--show");
        if (reveal) { settings.Mode = "topmost"; settings.X = null; settings.Y = null; }
        else if (settings.Mode != "topmost") settings.Mode = "desktop";
        Left = SystemParameters.WorkArea.Right - Width - 28;
        Top = SystemParameters.WorkArea.Top + 60;
        antigravityInstalled = AntigravityService.FindExecutable() is not null;
        ApplyAccounts(CodexAccounts.Discover(settings.CodexFolders));
        tray = new TrayIcon();
        tray.Click += Reveal;
        tray.BalloonClick += Reveal;
        tray.MenuRequested += () => { tray.PrepareMenu(); var menu = BuildMenu(true); menu.Placement = PlacementMode.MousePoint; menu.IsOpen = true; };
        taskbarStrip = new TaskbarStrip(Reveal, () => { tray.PrepareMenu(); return BuildMenu(true); });
        taskbarStrip.PlacementChanged += () => {
            NativeDesktop.GetWindowRect(NativeDesktop.Handle(taskbarStrip), out var bounds);
            DataStore.Log($"Taskbar strip: rect={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}");
        };
        tray.EnvironmentChanged += () => { taskbarStrip.RefreshEnvironment(); if (settings.Theme == "system") ApplyTheme(); };
        Loaded += async (_, _) => {
            DataStore.Log("Widget loaded: mode=" + settings.Mode);
            NativeDesktop.KeepAtBottom(this, () => settings.Mode == "desktop");
            ApplyMode();
            Render();
            taskbarStrip.SetEnabled(settings.TaskbarStrip);
            if (reveal) _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { NativeDesktop.CenterOnPrimary(this); SavePosition(); Activate(); }));
            displayTimer.Tick += (_, _) => { ProcessRequest(); Render(); };
            refreshTimer.Tick += async (_, _) => await Refresh(false);
            displayTimer.Start(); refreshTimer.Start();
            await Refresh(false);
        };
        // A taller widget (more accounts) must stay on its display.
        SizeChanged += (_, e) => { if (e.HeightChanged && IsLoaded && dragOrigin is null) KeepOnScreen(); };
        LocationChanged += (_, _) => CloseDate();
        Deactivated += (_, _) => CloseDate();
        Closing += (_, e) => {
            if (!quitting) { e.Cancel = true; Hide(); }
        };
    }

    private void BuildShell()
    {
        Content = WidgetView.Shell(palette, out DragHandle, out RefreshButton, out PinButton, out var menuButton, out CardsPanel, out Scroller, out Fade);
        DragHandle.MouseLeftButtonDown += DragStart; DragHandle.MouseMove += DragMove; DragHandle.MouseLeftButtonUp += DragEnd; DragHandle.LostMouseCapture += DragLost;
        RefreshButton.Click += async (_, _) => await Refresh(true);
        PinButton.Click += (_, _) => SetMode(settings.Mode == "topmost" ? "desktop" : "topmost");
        menuButton.Click += (sender, e) => { var menu = BuildMenu(false); menu.PlacementTarget = (Button)sender; menu.IsOpen = true; e.Handled = true; };
        Scroller.ScrollChanged += (_, _) => UpdateFade();
        drawn.Clear(); drawnOrder = "";
    }

    private void ApplyTheme()
    {
        var next = Palette.For(settings.Theme);
        if (next == palette) return;
        CloseDate();
        palette = next;
        palette.Apply(System.Windows.Application.Current.Resources);
        BuildShell();
        Render();
    }

    private void Reveal() { Show(); SetMode("topmost"); Activate(); }

    // Shows tracked accounts and keeps one reader per account, reusing readers whose home is unchanged.
    private void ApplyAccounts(List<CodexAccount> accounts)
    {
        codexAccounts = accounts;
        var tracked = CodexAccounts.Tracked(accounts, settings.CodexAccountsOn, settings.CodexAccountsOff);
        var services = new Dictionary<string, CodexService>();
        foreach (var account in tracked)
        {
            var source = account.Source;
            if (codexServices.TryGetValue(source.Id, out var existing) && existing.Home == account.Home) { services[source.Id] = existing; continue; }
            string signIn = account.Origin switch {
                CodexOrigin.Main => "Sign in to Codex CLI to connect",
                CodexOrigin.Router => "Sign in to this account again in Codex Subscription Router",
                _ => "Sign in to Codex again with this folder as CODEX_HOME",
            };
            services[source.Id] = new CodexService(account.Home, source.SnapshotName, signIn);
        }
        codexServices = services;
        sources = [UsageSource.Claude, .. tracked.Select(a => a.Source), .. settings.ShowAntigravity && antigravityInstalled ? [UsageSource.Antigravity] : Array.Empty<UsageSource>()];
    }

    private CodexAccount? Account(UsageSource source) => source.Provider == UsageProvider.Codex ? codexAccounts.FirstOrDefault(a => a.Source == source) : null;
    // The account's email, shown for ChatGPT/Codex sign-ins.
    private string? AccountName(UsageSource source) => Account(source) is CodexAccount account ? account.Origin == CodexOrigin.Main ? account.Email : account.Name : null;
    private bool MultipleCodexAccounts => sources.Count(s => s.Provider == UsageProvider.Codex) > 1;
    private string Display(UsageSource source) => source.Provider switch {
        UsageProvider.Claude => "Claude",
        UsageProvider.Antigravity => "Antigravity",
        _ => MultipleCodexAccounts && AccountName(source) is string name ? "ChatGPT / Codex · " + name : "ChatGPT / Codex",
    };

    // One menu serves the settings button, the tray icon and the taskbar strip.
    private ContextMenu BuildMenu(bool fromTray)
    {
        var menu = new ContextMenu();
        MenuItem Item(string label, RoutedEventHandler? handler, bool check = false, bool selected = false, ItemsControl? parent = null, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsCheckable = check, IsChecked = selected, IsEnabled = enabled };
            if (handler is not null) item.Click += handler;
            (parent ?? menu).Items.Add(item);
            return item;
        }
        if (fromTray) { Item("Show widget", (_, _) => Reveal()); menu.Items.Add(new Separator()); }
        Item("Always on top", (_, _) => SetMode("topmost"), true, settings.Mode == "topmost");
        Item("On desktop (below other windows)", (_, _) => SetMode("desktop"), true, settings.Mode == "desktop");
        Item("Start with Windows", (_, _) => SetStartup(!StartupEnabled()), true, StartupEnabled());
        Item("Low 5-hour limit alerts", (_, _) => SetAlerts(!settings.LowUsageAlerts), true, settings.LowUsageAlerts);
        Item("Reset notifications", (_, _) => {
            settings.ResetNotifications = !settings.ResetNotifications;
            SaveSettings();
            if (resetAlerts.Prune(settings.ResetNotifications, DateTimeOffset.UtcNow)) DataStore.Write("reset-alerts.json", resetAlerts);
        }, true, settings.ResetNotifications);
        Item("Show taskbar usage strip", (_, _) => {
            settings.TaskbarStrip = !settings.TaskbarStrip;
            SaveSettings();
            taskbarStrip.SetEnabled(settings.TaskbarStrip);
        }, true, settings.TaskbarStrip);
        var theme = Item("Theme", null);
        foreach (var (label, value) in new[] { ("Dark", (string?)null), ("Light", "light"), ("Match Windows", "system") })
            Item(label, (_, _) => { settings.Theme = value; SaveSettings(); ApplyTheme(); }, true, settings.Theme == value || (value is null && settings.Theme is not ("light" or "system")), theme);
        menu.Items.Add(new Separator());
        var accounts = Item("ChatGPT accounts", null);
        foreach (var account in codexAccounts)
        {
            var current = account;
            Item(AccountTitle(account), (_, _) => SetTracked(current, !sources.Contains(current.Source)), true, sources.Contains(account.Source), accounts, account.Key is not null);
        }
        accounts.Items.Add(new Separator());
        Item("Add Codex sign-in folder…", (_, _) => AddCodexFolder(), parent: accounts);
        Item("Codex Subscription Router accounts appear automatically", null, parent: accounts, enabled: false);
        Item(antigravityInstalled ? "Show Antigravity" : "Show Antigravity (CLI not found)", (_, _) => {
            settings.ShowAntigravity = !settings.ShowAntigravity;
            SaveSettings();
            ApplyAccounts(codexAccounts);
            Render();
            if (settings.ShowAntigravity) _ = Refresh(true, force: true);
        }, true, settings.ShowAntigravity && antigravityInstalled, enabled: antigravityInstalled);
        menu.Items.Add(new Separator());
        Item("Refresh now", async (_, _) => await Refresh(true));
        Item("Open Claude usage", (_, _) => OpenUrl("https://claude.ai/settings/usage"));
        Item("Open usage data folder", (_, _) => OpenUrl(DataStore.DirectoryPath));
        Item("Hide widget", (_, _) => Hide());
        Item("Quit", (_, _) => Quit());
        return menu;
    }

    private static string AccountTitle(CodexAccount account)
    {
        var parts = new List<string> { account.Name };
        if (account.Plan is string plan) parts.Add(System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan));
        if (account.Origin == CodexOrigin.Main) parts.Add("Main sign-in");
        else if (account.Origin == CodexOrigin.Router) parts.Add("Subscription Router");
        else if (!account.SignedIn) parts.Add("Signed out");
        return string.Join(" · ", parts);
    }

    // Turning off an added folder removes it from the list.
    private void SetTracked(CodexAccount account, bool on)
    {
        if (account.Key is not string key) return;
        if (!on && account.Origin == CodexOrigin.Folder && account.Home is string home)
            settings.CodexFolders.RemoveAll(folder => string.Equals(CodexAccounts.Normalize(folder), home, StringComparison.OrdinalIgnoreCase));
        settings.CodexAccountsOn.Remove(key); settings.CodexAccountsOff.Remove(key);
        (on ? settings.CodexAccountsOn : settings.CodexAccountsOff).Add(key);
        SaveSettings();
        ApplyAccounts(CodexAccounts.Discover(settings.CodexFolders));
        Render();
        if (on) _ = Refresh(true, force: true);
    }

    // Adds a Codex home folder holding another ChatGPT sign-in, such as one made with `set CODEX_HOME=… & codex login`.
    private void AddCodexFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a Codex sign-in folder (it contains auth.json)", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (dialog.ShowDialog() != true) return;
        string folder = CodexAccounts.Normalize(dialog.FolderName);
        var identity = CodexAccounts.ReadIdentity(folder);
        if (identity is null) { System.Windows.MessageBox.Show("No ChatGPT sign-in was found in that folder.", "AI Usage Widget", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (codexAccounts.FirstOrDefault(a => CodexAccounts.SameAccount(a, identity)) is CodexAccount existing)
        {
            if (existing.Key is not null && !sources.Contains(existing.Source)) SetTracked(existing, true);
            System.Windows.MessageBox.Show(existing.Name + " is already listed.", "AI Usage Widget", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!settings.CodexFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) settings.CodexFolders.Add(folder);
        settings.CodexAccountsOff.Remove(CodexAccounts.Key(identity) ?? folder);
        SaveSettings();
        ApplyAccounts(CodexAccounts.Discover(settings.CodexFolders));
        Render();
        _ = Refresh(true, force: true);
    }

    private void SaveSettings() => DataStore.Write("settings.json", settings);
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private async Task Refresh(bool manual, bool force = false)
    {
        // A newly tracked account is read as soon as the refresh in progress finishes.
        if (refreshing) { if (force) refreshAgain = true; return; }
        if (!force && DateTimeOffset.UtcNow - lastRefresh < TimeSpan.FromSeconds(10)) return;
        lastRefresh = DateTimeOffset.UtcNow;
        refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            // Picks up accounts signed in or out of Codex Subscription Router, and an installed or removed Antigravity CLI.
            var folders = settings.CodexFolders.ToList();
            var (accounts, installed) = await Task.Run(() => (CodexAccounts.Discover(folders), AntigravityService.FindExecutable() is not null));
            antigravityInstalled = installed;
            ApplyAccounts(accounts);
            var claudeTask = claude.RefreshAsync(manual);
            Render();
            // The CLIs run one at a time, so several accounts never start several Codex processes at once.
            async Task ReadClis()
            {
                foreach (var source in sources.ToList())
                {
                    // One failing reader must not stop the others.
                    try
                    {
                        if (codexServices.TryGetValue(source.Id, out var service)) await service.RefreshAsync();
                        else if (source == UsageSource.Antigravity) await antigravity.RefreshAsync(manual);
                        else continue;
                    }
                    catch (Exception ex) { DataStore.Log($"Refresh {source.Provider}: {ex.GetType().Name}"); }
                    Render();
                }
            }
            await Task.WhenAll(claudeTask, ReadClis());
        }
        finally { refreshing = false; RefreshButton.IsEnabled = true; Render(); }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(NativeDesktop.TrimMemory));
        if (refreshAgain) { refreshAgain = false; await Refresh(true, force: true); }
    }

    private bool Refreshing(UsageSource source) => source.Provider switch {
        UsageProvider.Claude => claude.Refreshing,
        UsageProvider.Antigravity => antigravity.Refreshing,
        _ => codexServices.TryGetValue(source.Id, out var service) && service.Refreshing,
    };
    private string? Status(UsageSource source) => source.Provider switch {
        UsageProvider.Claude => claude.Status,
        UsageProvider.Antigravity => antigravity.Status,
        _ => codexServices.TryGetValue(source.Id, out var service) ? service.Status : null,
    };

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        var claudeAccount = Snapshot(ClaudeService.SnapshotName);
        var claudeLine = Snapshot("claude.json");
        var states = new Dictionary<string, UsageSnapshot?>();
        foreach (var source in sources)
            states[source.Id] = source.Provider == UsageProvider.Claude ? ClaudeService.Merge(claudeAccount, claudeLine) : Snapshot(source.SnapshotName);
        var codex = sources.Where(s => s.Provider == UsageProvider.Codex).Select(s => (AccountName(s) ?? "Main sign-in", states[s.Id])).ToList();
        var claudeReading = TrayReading.Create(UsageProvider.Claude, states[UsageSource.Claude.Id], now);
        var codexReading = TrayReading.Merge(UsageProvider.Codex, codex, now);
        var geminiReading = sources.Contains(UsageSource.Antigravity) ? TrayReading.Create(UsageProvider.Antigravity, states[UsageSource.Antigravity.Id], now) : null;
        tray.Update(claudeReading, codexReading, geminiReading);
        taskbarStrip.Update(claudeReading, codexReading, geminiReading);
        bool changed = settings.AlertedWindows.RemoveAll(entry => entry.Split('|') is [_, _, var reset] && long.TryParse(reset, out long at) && at <= now.ToUnixTimeSeconds()) > 0;
        foreach (var source in sources) changed |= CheckAlerts(source, states[source.Id], now);
        if (changed) SaveSettings();
        CheckResets(claudeAccount, claudeLine, states, now);
        // Hidden/minimized cards need no layout work; readings and notifications above stay active.
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        DrawCards(states, now);
        bool pinned = settings.Mode == "topmost";
        // Filled, highlighted pin while always on top; muted crossed-out pin while on the desktop.
        PinButton.Content = pinned ? "\uE841" : "\uE77A";
        if (pinned) { PinButton.Foreground = palette.Ink; PinButton.Background = palette.PinBack; }
        else { PinButton.ClearValue(ForegroundProperty); PinButton.ClearValue(BackgroundProperty); }
        PinButton.ToolTip = pinned ? "Always on top · click to place it on the desktop below other windows" : "On the desktop below other windows · click to keep it on top";
    }

    private CardState Card(UsageSource source, UsageSnapshot? state)
    {
        var account = Account(source);
        string? addHelp = source.Provider switch {
            UsageProvider.Claude => "Claude's sign-in doesn't report when the subscription renews. Add the date from claude.ai → Settings → Billing.",
            UsageProvider.Antigravity => "Antigravity doesn't report when your Google AI plan renews. Add the date from Google One → Manage plan.",
            _ => string.Equals(account?.Plan, "free", StringComparison.OrdinalIgnoreCase) ? null : "Add when this subscription renews or ends.",
        };
        return source.Provider switch {
            UsageProvider.Claude => new(source, "Claude", null, claude.Plan ?? state?.Plan, state, claude.Refreshing, claude.Status,
                "Claude subscription usage across claude.ai, Claude Code, and SDK tools, read with your Claude Code sign-in.", PlanDate(source), addHelp),
            UsageProvider.Antigravity => new(source, "Antigravity", null, null, state, antigravity.Refreshing, antigravity.Status,
                "Antigravity (Google) quota from the Antigravity CLI's /usage command. Within each group, models share a weekly and a 5-hour limit.", PlanDate(source), addHelp),
            _ => new(source, "Codex", AccountName(source), state?.Plan ?? account?.Plan, state, Refreshing(source), Status(source),
                "ChatGPT subscription usage from the Codex CLI" + (account?.Home is string home ? ", signed in at " + home + "." : "."), PlanDate(source), addHelp),
        };
    }

    // The renewal line under a subscription: entered by hand, otherwise ChatGPT's paid-period end.
    private PlanDateLine? PlanDate(UsageSource source)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (settings.PlanDates.TryGetValue(source.Id, out var entry))
        {
            string next = entry.Upcoming(today).ToString("D");
            string help = entry.Cycle == PlanCycle.Ends ? $"Entered by hand: the plan ends on {next}. Click to change."
                : $"Entered by hand: renews {(entry.Cycle == PlanCycle.Monthly ? "monthly" : "yearly")} on {next}. Click to change.";
            return new(entry.Label(today), help, entry.Lapsed(today));
        }
        if (Account(source)?.PaidUntil is not DateTimeOffset end) return null;
        var now = DateTimeOffset.UtcNow;
        string through = end.ToLocalTime().ToString("f");
        string reported = end > now
            ? $"ChatGPT reports this plan paid through {through}. It renews then unless the subscription was cancelled."
            : $"ChatGPT last reported this plan paid through {through} and hasn't recorded a renewal yet. The date updates when Codex renews the sign-in.";
        return new(UsageFormatting.PaidPeriod(end, now), reported + " Click to set whether it renews or ends.", end <= now);
    }

    private void DrawCards(Dictionary<string, UsageSnapshot?> states, DateTimeOffset now)
    {
        // The open calendar is anchored to its card's date chip; keep the cards as they are until it closes.
        if (datePopup is not null) return;
        string order = string.Join(",", sources.Select(s => s.Id));
        bool rebuilt = order != drawnOrder;
        foreach (var source in sources)
        {
            var card = Card(source, states[source.Id]);
            string key = card.Key(now);
            if (drawn.TryGetValue(source.Id, out var last) && last.Key == key) continue;
            var current = source;
            drawn[source.Id] = (key, WidgetView.Card(card, palette, now, chip => EditDate(current, chip)));
            rebuilt = true;
        }
        if (!rebuilt) return;
        foreach (var id in drawn.Keys.Except(sources.Select(s => s.Id)).ToList()) drawn.Remove(id);
        drawnOrder = order;
        CardsPanel.Children.Clear();
        foreach (var source in sources)
        {
            if (CardsPanel.Children.Count > 0) CardsPanel.Children.Add(WidgetView.Divider(palette));
            CardsPanel.Children.Add(drawn[source.Id].Card);
        }
        UpdateScrollLimit();
    }

    // The cards scroll when they are taller than the display the widget is on.
    private void UpdateScrollLimit()
    {
        var area = NativeDesktop.WorkAreaDips(this);
        double limit = Math.Max(240, area.Height - 64);
        if (Math.Abs(Scroller.MaxHeight - limit) > 0.5) Scroller.MaxHeight = limit;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateFade));
    }

    private void UpdateFade() =>
        Fade.Visibility = Scroller.ExtentHeight > Scroller.ViewportHeight + 1 && Scroller.VerticalOffset < Scroller.ScrollableHeight - 1 ? Visibility.Visible : Visibility.Collapsed;

    private void KeepOnScreen()
    {
        NativeDesktop.GetWindowRect(NativeDesktop.Handle(this), out var rect);
        NativeDesktop.Move(this, rect.Left, rect.Top, true);
        UpdateScrollLimit();
    }

    private void EditDate(UsageSource source, FrameworkElement chip)
    {
        // A click on the chip of the open calendar closes it (the popup closes on that click before the chip sees it).
        if (dateClosed.Source == source.Id && DateTime.UtcNow - dateClosed.At < TimeSpan.FromMilliseconds(400)) return;
        CloseDate();
        var today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly? reported = Account(source)?.PaidUntil is DateTimeOffset end ? DateOnly.FromDateTime(end.ToLocalTime().DateTime) : null;
        var editor = new PlanDateEditor(palette, palette.Accent(source.Provider), settings.PlanDates.GetValueOrDefault(source.Id), reported, today);
        var popup = new Popup { Child = editor, PlacementTarget = chip, Placement = PlacementMode.Bottom, HorizontalOffset = -6, VerticalOffset = 6,
            AllowsTransparency = true, StaysOpen = false, PopupAnimation = PopupAnimation.None };
        editor.Saved += entry => {
            if (entry is null) settings.PlanDates.Remove(source.Id); else settings.PlanDates[source.Id] = entry;
            SaveSettings();
            CloseDate();
        };
        editor.Cancelled += CloseDate;
        popup.Opened += (_, _) => editor.Focus();
        popup.Closed += (_, _) => {
            if (datePopup != popup) return;
            datePopup = null; dateClosed = (source.Id, DateTime.UtcNow);
            drawn.Clear(); Render();
        };
        datePopup = popup;
        popup.IsOpen = true;
    }

    private void CloseDate() { if (datePopup is not null) datePopup.IsOpen = false; }

    // Snapshot files are parsed again only after they change.
    private UsageSnapshot? Snapshot(string name)
    {
        var written = File.GetLastWriteTimeUtc(Path.Combine(DataStore.DirectoryPath, name));
        if (snapshots.TryGetValue(name, out var cached) && cached.Written == written) return cached.Value;
        var value = DataStore.ReadSnapshot(name);
        snapshots[name] = (written, value);
        return value;
    }

    // Notifies once per 5-hour window when less than 10% remains; the entry is dropped once the window resets or recovers.
    // Not while a used-up weekly limit already holds the window at 0%.
    private bool CheckAlerts(UsageSource source, UsageSnapshot? state, DateTimeOffset now)
    {
        if (state is null) return false;
        string service = source.AlertKey;
        bool changed = false;
        foreach (var window in state.Windows.Where(w => w.WindowMinutes == 300))
        {
            string id = $"{service}|{window.Key}|{window.ResetsAt}";
            // Sources can report the same reset a few seconds apart; treat those as one window.
            bool Same(string entry) => entry == id || entry.Split('|') is [var s, var k, var r] && s == service && k == window.Key
                && long.TryParse(r, out long at) && window.ResetsAt is long reset && Math.Abs(at - reset) < 900;
            double left = Math.Clamp(100 - window.UsedPercent, 0, 100);
            if (UsageFormatting.Expired(window, now) || left >= 10) { changed |= settings.AlertedWindows.RemoveAll(Same) > 0; continue; }
            if (settings.AlertedWindows.Any(Same) || state.Blocker(window, now) is not null) continue;
            string reset = window.ResetsAt is long at ? $" · resets in {UsageFormatting.Short(at, now)} ({DateTimeOffset.FromUnixTimeSeconds(at).ToLocalTime():HH:mm})" : "";
            string label = source.Provider == UsageProvider.Antigravity ? window.Label.Split(" · ")[0] + " 5-hour limit" : "5-hour limit";
            if (settings.LowUsageAlerts && !tray.TryShowBalloon($"{Display(source)} {label} is low", $"{UsageFormatting.Percent(left)}% left{reset}", true)) continue;
            settings.AlertedWindows.Add(id); changed = true;
            if (settings.LowUsageAlerts) DataStore.Log($"Low usage alert: {source.Provider} {window.Key}");
        }
        return changed;
    }

    private void CheckResets(UsageSnapshot? claudeAccount, UsageSnapshot? claudeLine, Dictionary<string, UsageSnapshot?> states, DateTimeOffset now)
    {
        bool enabled = settings.ResetNotifications;
        bool changed = resetAlerts.Prune(enabled, now);
        changed |= resetAlerts.ObserveClaude(claudeAccount, claudeLine, enabled, now);
        foreach (var source in sources.Where(s => s.Provider != UsageProvider.Claude))
            changed |= resetAlerts.Observe(source.Provider, states[source.Id], enabled, now, source.AlertKey,
                source.Provider == UsageProvider.Codex && MultipleCodexAccounts ? AccountName(source) : null);
        // Persist observations and pending alerts before asking Windows to display anything.
        if (changed) DataStore.Write("reset-alerts.json", resetAlerts);
        if (!enabled || resetAlerts.Pending.FirstOrDefault() is not ResetAlert alert) return;
        string provider = alert.Provider switch { UsageProvider.Claude => "Claude", UsageProvider.Antigravity => "Antigravity", _ => "ChatGPT / Codex" }
            + (alert.Account is string name ? " · " + name : "");
        if (!tray.TryShowBalloon($"{provider} usage reset", $"{alert.Label} reset · {UsageFormatting.Percent(alert.Remaining)}% remaining. Click to open the widget.", false)) return;
        resetAlerts.MarkDelivered(alert);
        DataStore.Write("reset-alerts.json", resetAlerts);
        DataStore.Log($"Reset notification: {alert.Provider} {alert.Label}");
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
        CloseDate();
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
    private void DragEnd(object sender, MouseButtonEventArgs e) { dragOrigin = null; DragHandle.ReleaseMouseCapture(); SavePosition(); UpdateScrollLimit(); }
    private void DragLost(object sender, System.Windows.Input.MouseEventArgs e) { if (dragOrigin is not null) { dragOrigin = null; SavePosition(); UpdateScrollLimit(); } }
    private void SavePosition() {
        NativeDesktop.GetWindowRect(NativeDesktop.Handle(this), out var rect);
        settings.X = rect.Left; settings.Y = rect.Top; SaveSettings();
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
        settings.StartAtLogin = enabled; SaveSettings();
    }
    private void SetAlerts(bool enabled) {
        settings.LowUsageAlerts = enabled; SaveSettings();
    }
    private void Quit() {
        quitting = true; CloseDate(); SavePosition(); displayTimer.Stop(); refreshTimer.Stop();
        taskbarStrip.Dispose(); tray.Dispose();
        Close(); System.Windows.Application.Current.Shutdown();
    }
}
