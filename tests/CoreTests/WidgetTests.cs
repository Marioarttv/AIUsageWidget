using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UsageWidget;

// The card and calendar builders, laid out offscreen as the widget and the screenshot demo use them.
internal static class WidgetTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        if (root is TextBlock text) yield return string.Concat(text.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) is { Length: > 0 } runs ? runs : text.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var value in Texts(VisualTreeHelper.GetChild(root, i))) yield return value;
    }

    private static T Layout<T>(T element, double width) where T : FrameworkElement
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(new Size(width, element.DesiredSize.Height)));
        element.UpdateLayout();
        return element;
    }

    internal static void Run()
    {
        // The widget's own Application, for its styles; OnStartup only runs from Run(), so nothing else starts.
        if (System.Windows.Application.Current is null) new App().InitializeComponent();
        var now = DateTimeOffset.UtcNow;
        long In(TimeSpan span) => now.Add(span).ToUnixTimeSeconds();
        var spent = new UsageSnapshot(now, "test", [new("s", "5-hour session", 20, In(TimeSpan.FromHours(3)), 300), new("w", "Weekly", 100, In(new TimeSpan(4, 5, 0, 0)), 10080)], "plus");
        foreach (var palette in new[] { Palette.Dark, Palette.Light })
        {
            palette.Apply(System.Windows.Application.Current!.Resources);
            var card = Layout(WidgetView.Card(new(new UsageSource(UsageProvider.Codex, "a"), "Codex", "someone.with.a.long.address@example.com", "plus", spent, false, null, "", new("Paid until 20 Nov", "", true), null), palette, now, null), WidgetView.Width - 28);
            var texts = Texts(card).ToList();
            Check(texts.Contains("0% left") && texts.Any(t => t.StartsWith("Weekly used up · 4d")) && texts.Contains("Paid until 20 Nov") && texts.Any(t => t.StartsWith("Updated")),
                "A spent week must show 0% with the reason, and the footer the renewal date and age: " + string.Join(" | ", texts));
            Check(texts.Any(t => t.EndsWith("@example.com") && t.Contains('…')), "Long emails keep their domain.");
            Check(card.ActualHeight < 130, $"A card with one further limit must stay compact ({card.ActualHeight:0} px).");

            var groups = new UsageSnapshot(now, "test", [
                new("g5", "Gemini · 5-hour session", 3, In(TimeSpan.FromHours(4)), 300, "Gemini Models"), new("gw", "Gemini · Weekly", 85, In(TimeSpan.FromDays(5)), 10080, "Gemini Models"),
                new("c5", "Claude and GPT · 5-hour session", 5, In(TimeSpan.FromHours(3)), 300, "Claude and GPT models"), new("cw", "Claude and GPT · Weekly", 30, In(TimeSpan.FromDays(6)), 10080, "Claude and GPT models")]);
            var gemini = Layout(WidgetView.Card(new(UsageSource.Antigravity, "Antigravity", null, null, groups, false, null, "", null, "Add"), palette, now, null), WidgetView.Width - 28);
            texts = Texts(gemini).ToList();
            Check(texts.Contains("97% left") && texts.Contains("Gemini · Weekly") && texts.Contains("Claude and GPT") && texts.Any(t => t.Contains("5h 95%") && t.Contains("wk 70%")) && texts.Contains("Renewal date"),
                "Antigravity: Gemini 5-hour large, Gemini weekly, the Claude and GPT group on one row, and a renewal-date chip: " + string.Join(" | ", texts));

            ManualPlanDate? saved = null; bool cancelled = false;
            var editor = new PlanDateEditor(palette, palette.Claude, new(new DateOnly(2026, 1, 31), PlanCycle.Monthly), new DateOnly(2026, 12, 14), new DateOnly(2026, 11, 27));
            editor.Saved += entry => saved = entry; editor.Cancelled += () => cancelled = true;
            Layout(editor, editor.Width);
            texts = Texts(editor).ToList();
            Check(texts.Contains("Monthly") && texts.Contains("Yearly") && texts.Contains("Ends") && texts.Contains("Save") && texts.Contains("Use ChatGPT's Date") && texts.Contains("30")
                && texts.Any(t => t.StartsWith("Renews on the 30th")), "The calendar shows the cycle choice, the month and the actions: " + string.Join(" | ", texts));
            foreach (var button in Buttons(editor).Where(b => Texts(b).Contains("Save"))) button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(saved == new ManualPlanDate(new DateOnly(2026, 11, 30), PlanCycle.Monthly), "Save must return the selected day and cycle: " + saved);
            editor.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new FakeSource(), 0, System.Windows.Input.Key.Escape) { RoutedEvent = UIElement.KeyDownEvent });
            Check(cancelled, "Escape must close the calendar.");
        }
        Check(WidgetView.MiddleTrim("alice.example.longname@email.org", 24) == "alice.example…@email.org" && WidgetView.MiddleTrim("you@example.com", 24) == "you@example.com",
            "Email trimming keeps the domain.");
    }

    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var value in Buttons(VisualTreeHelper.GetChild(root, i))) yield return value;
    }

    private sealed class FakeSource : PresentationSource
    {
        protected override CompositionTarget GetCompositionTargetCore() => null!;
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
    }
}
