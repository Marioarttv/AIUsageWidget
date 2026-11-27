using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace UsageWidget;

// The renewal line under a subscription: entered by hand, otherwise ChatGPT's paid-period end.
internal sealed record PlanDateLine(string Text, string Help, bool Lapsed);

// Everything one card shows. Built by MainWindow from live readings, or by the demo from sample data.
internal sealed record CardState(UsageSource Source, string Title, string? Account, string? Plan, UsageSnapshot? Snapshot, bool Refreshing,
    string? Status, string About, PlanDateLine? PlanDate, string? AddDateHelp)
{
    // Changes whenever something visible changes; countdowns and ages change at most once a minute.
    public string Key(DateTimeOffset now) => string.Join("|", Source.Id, Title, Account, Plan, Refreshing, Status, PlanDate, AddDateHelp,
        Snapshot?.UpdatedAt.ToUnixTimeSeconds(), Snapshot is null ? "" : UsageFormatting.Age(Snapshot.UpdatedAt, now),
        string.Join(";", (Snapshot?.Windows ?? []).Select(w => $"{w.Key}:{w.Label}:{w.UsedPercent}:{UsageFormatting.Short(w.ResetsAt, now)}:{Snapshot!.Blocker(w, now)?.Key}")));
}

// Builds the widget's visuals from plain state, with no file or network access, so the window and the demo share them.
internal static class WidgetView
{
    public const double Width = 300;
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private static TextBlock Text(string value, double size, Brush color) => new() { Text = value, FontSize = size, Foreground = color, VerticalAlignment = VerticalAlignment.Center };
    private static Brush Severity(double remaining, Brush accent, Palette p) => remaining <= 10 ? p.Danger : remaining <= 25 ? p.Warn : accent;
    internal static string ResetTime(long reset) => DateTimeOffset.FromUnixTimeSeconds(reset).ToLocalTime().ToString("dddd, d MMMM 'at' HH:mm");

    // Emails keep their start and their domain when they don't fit: "mario.examp…@example.com".
    internal static string MiddleTrim(string text, int max)
    {
        if (text.Length <= max || max < 5) return text;
        int at = text.IndexOf('@');
        int tail = at > 0 && text.Length - at <= max - 4 ? text.Length - at : (max - 1) / 2;
        return text[..(max - 1 - tail)] + "…" + text[^tail..];
    }

    // The window frame: title bar with refresh, pin and settings, and a scrolling slot for the cards.
    public static Border Shell(Palette p, out Grid dragHandle, out Button refresh, out Button pin, out Button menu, out StackPanel cards, out ScrollViewer scroller, out Border fade)
    {
        var root = new Border { Background = p.Background, BorderBrush = p.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 8, 14, 12) };
        var stack = new StackPanel();
        dragHandle = new Grid { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, -8, 6) };
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            dragHandle.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var title = Text("AI USAGE", 9.5, p.Muted); title.FontWeight = FontWeights.Bold;
        dragHandle.Children.Add(title);
        Button Icon(string glyph, string name, int column)
        {
            var button = new Button { Content = glyph, Style = (Style)Application.Current.FindResource("IconButton") };
            AutomationProperties.SetName(button, name);
            Grid.SetColumn(button, column);
            return button;
        }
        refresh = Icon("", "Refresh usage", 1); refresh.ToolTip = "Refresh usage";
        pin = Icon("", "Toggle always on top", 2);
        menu = Icon("", "Widget settings", 3); menu.ToolTip = "Widget settings";
        dragHandle.Children.Add(refresh); dragHandle.Children.Add(pin); dragHandle.Children.Add(menu);
        stack.Children.Add(dragHandle);
        cards = new StackPanel();
        // macOS-style: no visible scroll bar; the wheel scrolls, and a fade at the bottom shows that more follows.
        scroller = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        fade = new Border { Height = 18, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Background = new LinearGradientBrush(Colors.Transparent, ((SolidColorBrush)p.Background).Color with { A = 255 }, 90) };
        var slot = new Grid();
        slot.Children.Add(scroller); slot.Children.Add(fade);
        stack.Children.Add(slot);
        root.Child = stack;
        return root;
    }

    public static FrameworkElement Divider(Palette p) => new Border { Height = 1, Background = p.Divider, Margin = new Thickness(0, 11, 0, 10) };

    private static Grid Bar(double remaining, Brush color, double height, Palette p)
    {
        var bar = new Grid { Height = height, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(new Border { Background = p.Track, CornerRadius = new CornerRadius(height / 2) });
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(remaining, GridUnitType.Star) });
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - remaining, GridUnitType.Star) });
        fill.Children.Add(new Border { Background = color, CornerRadius = new CornerRadius(height / 2), Visibility = remaining <= 0 ? Visibility.Collapsed : Visibility.Visible });
        bar.Children.Add(fill);
        return bar;
    }

    private static string ShortName(UsageWindow window) => window.WindowMinutes switch {
        300 => "5h", 10080 => "wk", 1440 => "day", 60 => "1h", _ => window.Label.Split(" · ")[^1] };

    // One card: the shortest window large, further windows one line each, and a footer with the renewal date and the reading's age.
    public static FrameworkElement Card(CardState s, Palette p, DateTimeOffset now, Action<FrameworkElement>? editDate)
    {
        var panel = new StackPanel();
        AutomationProperties.SetName(panel, s.Account is null ? s.Title + " usage" : $"{s.Title} usage for {s.Account}");
        var snapshot = s.Snapshot;
        var windows = snapshot?.Windows ?? [];
        var primary = UsageFormatting.Primary(snapshot);
        var accent = p.Accent(s.Source.Provider);
        UsageWindow? Blocker(UsageWindow w) => snapshot!.Blocker(w, now);
        bool Unknown(UsageWindow w) => Blocker(w) is null && UsageFormatting.Expired(w, now);
        double Available(UsageWindow w) => snapshot!.Available(w, now);
        string Percent(UsageWindow w) => Unknown(w) ? "—" : UsageFormatting.Percent(Available(w)) + "%";
        string Help(UsageWindow w)
        {
            string text = w.ResetsAt is long at ? "Resets " + ResetTime(at) : "The provider has not supplied a reset time.";
            if (Blocker(w) is UsageWindow b)
                text = $"{UsageFormatting.Percent(UsageFormatting.Left(w))}% of this window is left, but the {b.Label} limit is used up, so none of it can be used until that resets"
                    + (b.ResetsAt is long r ? " " + ResetTime(r) : "") + ".\n" + text;
            return text;
        }

        // Header: dot, name, account and plan, and the headline percentage.
        var head = new Grid();
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = accent, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = Text(s.Title, 13, p.Ink); name.FontWeight = FontWeights.SemiBold; name.ToolTip = s.About; name.Background = Brushes.Transparent;
        Grid.SetColumn(name, 1);
        head.Children.Add(dot); head.Children.Add(name);
        if (s.Account is not null)
        {
            var account = Text(MiddleTrim(s.Account, 24), 10, p.Muted);
            account.Margin = new Thickness(6, 1, 0, 0); account.TextTrimming = TextTrimming.CharacterEllipsis; account.ToolTip = s.Account;
            Grid.SetColumn(account, 3); head.Children.Add(account);
        }
        if (!string.IsNullOrWhiteSpace(s.Plan))
        {
            // Beside the name, before the account, so it reads "Codex PRO you@example.com".
            var badge = Text(s.Plan.ToUpperInvariant(), 8.5, p.Faint); badge.Margin = new Thickness(6, 2, 0, 0);
            Grid.SetColumn(badge, 2); head.Children.Add(badge);
        }
        double? headline = UsageFormatting.Headline(snapshot, now);
        var blocker = primary is null ? null : Blocker(primary);
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 0) };
        Brush valueColor = headline is null ? p.Unknown : blocker is not null ? p.Blocked : headline <= 25 ? Severity(headline.Value, accent, p) : p.Ink;
        value.Inlines.Add(new Run(headline is double h ? UsageFormatting.Percent(h) + "%" : "—") { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = valueColor });
        if (headline is not null) value.Inlines.Add(new Run(" left") { FontSize = 10, Foreground = p.Faint });
        Grid.SetColumn(value, 4); head.Children.Add(value);
        panel.Children.Add(head);

        var hero = Bar(headline ?? 0, blocker is not null ? p.Blocked : Severity(headline ?? 100, accent, p), 5, p); hero.Margin = new Thickness(0, 4, 0, 5);
        panel.Children.Add(hero);

        // Caption: the headline window and when it resets, or why it shows 0%.
        var caption = new Grid();
        caption.ColumnDefinitions.Add(new ColumnDefinition()); caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Text(primary?.Label ?? "5-hour session", 10, p.Muted); label.TextTrimming = TextTrimming.CharacterEllipsis;
        caption.Children.Add(label);
        string when = primary is null ? "waiting for a reading" : blocker is not null ? UsageFormatting.Blocked(blocker, now)
            : UsageFormatting.Expired(primary, now) ? "awaiting update" : primary.ResetsAt is null ? "" : "resets in " + UsageFormatting.Short(primary.ResetsAt, now);
        var right = Text(when, 10, blocker is not null ? p.Blocked : p.Muted); right.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(right, 1); caption.Children.Add(right);
        if (primary is not null) caption.ToolTip = Help(primary);
        caption.Background = Brushes.Transparent;
        panel.Children.Add(caption);

        // Further windows, one line each; a quota pool other than the headline's (an Antigravity model group) shares one line.
        var rows = new List<List<UsageWindow>>();
        foreach (var window in windows.Where(w => w != primary))
        {
            if (window.Pool is string pool && pool != primary?.Pool && rows.FirstOrDefault(r => r[0].Pool == pool) is { } existing) existing.Add(window);
            else rows.Add([window]);
        }
        if (rows.Count > 0)
        {
            // One grid keeps the secondary bars aligned regardless of label length.
            var list = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 118 });
            list.ColumnDefinitions.Add(new ColumnDefinition());
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(17) });
                var tightest = row.OrderBy(w => Unknown(w) ? 101 : Available(w)).First();
                bool blocked = row.Any(w => Blocker(w) is not null);
                string rowLabel = row.Count == 1 ? row[0].Label : row[0].Label.Split(" · ")[0];
                var cells = new List<FrameworkElement> { Text(rowLabel, 10, p.Label), Bar(Unknown(tightest) ? 0 : Available(tightest), blocked ? p.Blocked : Severity(Available(tightest), accent, p), 3, p) };
                ((TextBlock)cells[0]).TextTrimming = TextTrimming.CharacterEllipsis;
                cells[1].Margin = new Thickness(10, 0, 0, 0);
                if (row.Count == 1)
                {
                    var w = row[0];
                    double left = Available(w);
                    var percent = Text(Percent(w), 10, Unknown(w) ? p.Unknown : Blocker(w) is not null ? p.Blocked : left <= 25 ? Severity(left, accent, p) : p.Value);
                    percent.Margin = new Thickness(8, 0, 0, 0); percent.MinWidth = 28; percent.TextAlignment = TextAlignment.Right; percent.FontWeight = FontWeights.SemiBold;
                    var reset = Text(Blocker(w) is UsageWindow b ? UsageFormatting.WindowLabel(b.WindowMinutes, "limit").ToLowerInvariant() + " used up"
                        : Unknown(w) ? "now" : UsageFormatting.Short(w.ResetsAt, now), 9.5, Blocker(w) is not null ? p.Blocked : p.Faint);
                    reset.Margin = new Thickness(6, 0, 0, 0); reset.MinWidth = 36; reset.TextAlignment = TextAlignment.Right;
                    cells.Add(percent); cells.Add(reset);
                }
                else
                {
                    // "5h 95% · wk 70%"
                    var values = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextAlignment = TextAlignment.Right, FontSize = 10 };
                    for (int k = 0; k < row.Count; k++)
                    {
                        if (k > 0) values.Inlines.Add(new Run(" · ") { Foreground = p.Faint });
                        values.Inlines.Add(new Run(ShortName(row[k]) + " ") { Foreground = p.Faint, FontSize = 9.5 });
                        double left = Available(row[k]);
                        values.Inlines.Add(new Run(Percent(row[k])) { FontWeight = FontWeights.SemiBold,
                            Foreground = Unknown(row[k]) ? p.Unknown : Blocker(row[k]) is not null ? p.Blocked : left <= 25 ? Severity(left, accent, p) : p.Value });
                    }
                    Grid.SetColumnSpan(values, 2);
                    cells.Add(values);
                }
                string tip = string.Join("\n\n", row.Select(w => $"{w.Label} · {(Unknown(w) ? "awaiting update" : Percent(w) + " left")}\n{Help(w)}"));
                for (int c = 0; c < cells.Count; c++) { cells[c].ToolTip = tip; Grid.SetRow(cells[c], i); Grid.SetColumn(cells[c], c); list.Children.Add(cells[c]); }
            }
            panel.Children.Add(list);
        }

        if (s.Status is not null)
        {
            var notice = Text(s.Status, 10, p.Warn); notice.TextWrapping = TextWrapping.Wrap; notice.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(notice);
        }

        // Footer: the renewal date shares the line with the reading's age, so it adds little height.
        var footer = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new ColumnDefinition());
        if (s.PlanDate is not null || s.AddDateHelp is not null)
        {
            var chip = Chip(s.PlanDate, s.AddDateHelp, p);
            if (editDate is not null) chip.Click += (_, _) => editDate(chip);
            footer.Children.Add(chip);
        }
        var age = Text(s.Refreshing ? "Updating…" : snapshot is null ? "Connecting…" : "Updated " + UsageFormatting.Age(snapshot.UpdatedAt, now), 9.5, p.Faint);
        age.HorizontalAlignment = HorizontalAlignment.Right; age.Margin = new Thickness(8, 0, 0, 0);
        age.ToolTip = snapshot is null ? s.About : "Last reading: " + snapshot.UpdatedAt.ToLocalTime().ToString("f");
        Grid.SetColumn(age, 1); footer.Children.Add(age);
        panel.Children.Add(footer);
        return panel;
    }

    // The renewal-date chip: "Renews 26 Nov", orange "Paid until 28 Oct" once passed, or a faint "Renewal date" to add one.
    private static Button Chip(PlanDateLine? line, string? addHelp, Palette p)
    {
        var icon = new TextBlock { Text = line is null ? "" : "", FontFamily = new FontFamily(IconFont), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 4, 0) };
        var text = new TextBlock { Text = line?.Text ?? "Renewal date", FontSize = 9.5, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon); content.Children.Add(text);
        var back = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 7, 2), Background = p.ChipBack, Child = content };
        Brush ink = line is null ? p.Faint : line.Lapsed ? p.Blocked : p.Muted;
        content.SetValue(TextElement.ForegroundProperty, ink);
        if (line is null) back.Opacity = 0.8;
        var button = new Button { Style = (Style)Application.Current.FindResource("BareButton"), Content = back, ToolTip = line?.Help ?? addHelp, Margin = new Thickness(-6, 0, 0, 0) };
        AutomationProperties.SetName(button, line?.Text ?? "Add the renewal date");
        AutomationProperties.SetHelpText(button, "Edit the renewal date");
        button.MouseEnter += (_, _) => { back.Background = p.ChipHover; back.Opacity = 1; };
        button.MouseLeave += (_, _) => { back.Background = p.ChipBack; back.Opacity = line is null ? 0.8 : 1; };
        return button;
    }
}

// A compact month calendar with the renewal cycle, shown in a floating card under the date chip.
internal sealed class PlanDateEditor : Border
{
    private readonly Palette p;
    private readonly Brush accent;
    private readonly ManualPlanDate? existing;
    private readonly DateOnly today;
    private readonly bool reported;
    private DateOnly day;
    private int year, month;
    private PlanCycle cycle;
    private readonly StackPanel body = new();
    public event Action<ManualPlanDate?>? Saved;
    public event Action? Cancelled;

    // existing: the date entered earlier, if any. reported: ChatGPT's paid-period end, which removing the entry falls back to.
    public PlanDateEditor(Palette palette, Brush accent, ManualPlanDate? existing, DateOnly? reported, DateOnly today)
    {
        p = palette; this.accent = accent; this.existing = existing; this.today = today; this.reported = reported is not null;
        day = existing?.Upcoming(today) ?? reported ?? today;
        year = day.Year; month = day.Month;
        cycle = existing?.Cycle ?? PlanCycle.Monthly;
        Width = 252; Padding = new Thickness(12); CornerRadius = new CornerRadius(12); BorderThickness = new Thickness(1);
        Background = p.IsLight ? Palette.Brush("#FFFFFF") : Palette.Brush("#161B23"); BorderBrush = p.Border;
        TextElement.SetFontFamily(this, new FontFamily("Segoe UI"));
        TextElement.SetForeground(this, p.Ink);
        Focusable = true;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Cancelled?.Invoke(); e.Handled = true; } };
        Child = body;
        Build();
    }

    private Button Plain(object content, string? tip = null)
    {
        var button = new Button { Content = content, Padding = new Thickness(6, 3, 6, 3), FontSize = 11, ToolTip = tip };
        return button;
    }

    private void Build()
    {
        body.Children.Clear();
        // Monthly / Yearly / Ends
        var segments = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        var segmentBack = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Background = p.IsLight ? Palette.Brush("#EEF1F5") : Palette.Brush("#40000000"), Child = segments };
        foreach (var option in Enum.GetValues<PlanCycle>())
        {
            segments.ColumnDefinitions.Add(new ColumnDefinition());
            bool selected = option == cycle;
            var label = new TextBlock { Text = ManualPlanDate.Title(option), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, Foreground = selected ? p.Ink : p.Muted };
            var back = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 4, 0, 4), Background = selected ? (p.IsLight ? Palette.Brush("#FFFFFF") : Palette.Brush("#24FFFFFF")) : Brushes.Transparent, Child = label };
            var button = new Button { Style = (Style)Application.Current.FindResource("BareButton"), Content = back, ToolTip = ManualPlanDate.Explanation(option) };
            AutomationProperties.SetName(button, ManualPlanDate.Title(option) + (selected ? " (selected)" : ""));
            var chosen = option;
            button.Click += (_, _) => { cycle = chosen; Build(); };
            Grid.SetColumn(button, segments.ColumnDefinitions.Count - 1);
            segments.Children.Add(button);
        }
        body.Children.Add(segmentBack);

        // Month header
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var previous = new Button { Content = "", Style = (Style)Application.Current.FindResource("IconButton"), FontSize = 9 };
        var next = new Button { Content = "", Style = (Style)Application.Current.FindResource("IconButton"), FontSize = 9 };
        AutomationProperties.SetName(previous, "Previous month"); AutomationProperties.SetName(next, "Next month");
        previous.Click += (_, _) => Step(-1); next.Click += (_, _) => Step(1);
        var title = Plain(new TextBlock { Text = new DateTime(year, month, 1).ToString("MMMM yyyy"), FontSize = 11, FontWeight = FontWeights.SemiBold }, "Go to the selected day");
        title.Padding = new Thickness(6, 1, 6, 1);
        title.Click += (_, _) => { year = day.Year; month = day.Month; Build(); };
        title.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(title, 1); Grid.SetColumn(next, 2);
        header.Children.Add(previous); header.Children.Add(title); header.Children.Add(next);
        body.Children.Add(header);

        // Week grid: six rows, so every month takes the same space.
        var first = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
        foreach (var symbol in MonthGrid.WeekdaySymbols(first))
            weekdays.Children.Add(new TextBlock { Text = symbol, FontSize = 9, FontWeight = FontWeights.Medium, Foreground = p.Faint, HorizontalAlignment = HorizontalAlignment.Center });
        body.Children.Add(weekdays);
        var grid = new UniformGrid { Columns = 7 };
        var repeat = MonthGrid.Repeat(day, cycle, year, month);
        foreach (var cell in MonthGrid.Days(year, month, first))
        {
            if (cell is not DateOnly date) { grid.Children.Add(new Border { Height = 26 }); continue; }
            bool selected = date == day;
            var number = new TextBlock { Text = date.Day.ToString(), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = selected ? FontWeights.Bold : FontWeights.Normal, Foreground = selected ? p.OnAccent : date < today ? p.Faint : p.Ink };
            var circle = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = selected ? accent : Brushes.Transparent,
                BorderThickness = new Thickness(date == today && !selected ? 1 : 0), BorderBrush = p.Muted, Child = number };
            var cellGrid = new Grid { Height = 26, Background = Brushes.Transparent };
            cellGrid.Children.Add(circle);
            if (date == repeat) cellGrid.Children.Add(new Border { Width = 3, Height = 3, CornerRadius = new CornerRadius(1.5), Background = accent, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });
            var button = new Button { Style = (Style)Application.Current.FindResource("BareButton"), Content = cellGrid,
                ToolTip = date.ToString("D") + (date == repeat ? " · renews" : "") };
            AutomationProperties.SetName(button, date.ToString("D") + (selected ? " (selected)" : ""));
            var chosen = date;
            button.Click += (_, _) => { day = chosen; Build(); };
            grid.Children.Add(button);
        }
        body.Children.Add(grid);

        body.Children.Add(new TextBlock { Text = new ManualPlanDate(day, cycle).Caption(today), FontSize = 9, Foreground = p.Muted, Margin = new Thickness(0, 8, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis });

        // Remove / Use ChatGPT's Date, Cancel, Save
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (existing is not null)
        {
            var remove = Plain(reported ? "Use ChatGPT's Date" : "Remove", reported ? "Show the date ChatGPT reports again" : "Stop showing a renewal date");
            remove.Foreground = p.Muted; remove.Margin = new Thickness(-6, 0, 0, 0);
            remove.Click += (_, _) => Saved?.Invoke(null);
            actions.Children.Add(remove);
        }
        var cancel = Plain("Cancel"); cancel.Foreground = p.Muted; Grid.SetColumn(cancel, 2);
        cancel.Click += (_, _) => Cancelled?.Invoke();
        var saveText = new TextBlock { Text = "Save", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = p.OnAccent };
        var save = new Button { Style = (Style)Application.Current.FindResource("BareButton"), Margin = new Thickness(6, 0, 0, 0),
            Content = new Border { CornerRadius = new CornerRadius(10), Background = accent, Padding = new Thickness(12, 3, 12, 4), Child = saveText } };
        AutomationProperties.SetName(save, "Save");
        save.Click += (_, _) => Saved?.Invoke(new ManualPlanDate(day, cycle));
        Grid.SetColumn(save, 3);
        actions.Children.Add(cancel); actions.Children.Add(save);
        body.Children.Add(actions);
    }

    private void Step(int months)
    {
        var shown = new DateOnly(year, month, 1).AddMonths(months);
        year = shown.Year; month = shown.Month;
        Build();
    }
}
