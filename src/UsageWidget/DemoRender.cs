using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UsageWidget;

// `AIUsageWidget.exe --render-demo <folder>`: renders the widget, its renewal calendar and the taskbar strip with built-in sample
// data to PNGs, in the dark and light themes. No network, credentials or widget data are touched.
internal static class DemoRender
{
    internal static List<string> Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var now = DateTimeOffset.UtcNow;
        var written = new List<string>();
        long In(TimeSpan span) => now.Add(span).ToUnixTimeSeconds();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var you = new UsageSource(UsageProvider.Codex, "demo-account-1");
        var work = new UsageSource(UsageProvider.Codex, "demo-account-2");
        var claude = new UsageSnapshot(now.AddMinutes(-2), "demo", [
            new("five_hour", "5-hour session", 32, In(new(2, 14, 0)), 300),
            new("seven_day", "Weekly", 58, In(new(3, 4, 10, 0)), 10080),
            new("seven_day_opus", "Weekly · Opus", 12, In(new(3, 4, 10, 0)), 10080, "seven_day_opus")], "max");
        var codexYou = new UsageSnapshot(now.AddMinutes(-2), "demo", [
            new("codex:primary", "5-hour session", 18, In(new(2, 40, 0)), 300),
            new("codex:secondary", "Weekly", 44, In(new(5, 2, 0, 0)), 10080)], "pro");
        // A used-up weekly limit: the session shows 0% too.
        var codexWork = new UsageSnapshot(now.AddMinutes(-3), "demo", [
            new("codex:primary", "5-hour session", 20, In(new(4, 10, 0)), 300),
            new("codex:secondary", "Weekly", 100, In(new(4, 5, 0, 0)), 10080)], "plus");
        var gemini = new UsageSnapshot(now.AddMinutes(-2), "demo", [
            new("gemini-5h", "Gemini · 5-hour session", 3, In(new(4, 21, 0)), 300, "Gemini Models"),
            new("gemini-weekly", "Gemini · Weekly", 85, In(new(5, 20, 0, 0)), 10080, "Gemini Models"),
            new("3p-5h", "Claude and GPT · 5-hour session", 5, In(new(3, 2, 0)), 300, "Claude and GPT models"),
            new("3p-weekly", "Claude and GPT · Weekly", 30, In(new(6, 1, 0, 0)), 10080, "Claude and GPT models"),
            new("argon-weekly", "Gemini 4 Argon · Weekly", 0, In(new(6, 1, 0, 0)), 10080, "Gemini 4 Argon")]);
        var claudeDate = new ManualPlanDate(today.AddDays(6).AddMonths(-1), PlanCycle.Monthly);
        var youPaid = now.AddDays(17);
        var workPaid = now.AddDays(-4);
        PlanDateLine Manual(ManualPlanDate entry) => new(entry.Label(today), "Entered by hand.", entry.Lapsed(today));
        var cards = new[] {
            new CardState(UsageSource.Claude, "Claude", null, "max", claude, false, null, "", Manual(claudeDate), null),
            new CardState(you, "Codex", "you@example.com", "pro", codexYou, false, null, "", new(UsageFormatting.PaidPeriod(youPaid, now), "", false), null),
            new CardState(work, "Codex", "work.team@example.com", "plus", codexWork, false, null, "", new(UsageFormatting.PaidPeriod(workPaid, now), "", true), null),
            new CardState(UsageSource.Antigravity, "Antigravity", null, null, gemini, false, null, "", null, "Add the date"),
        };

        foreach (var (name, palette) in new[] { ("dark", Palette.Dark), ("light", Palette.Light) })
        {
            palette.Apply(Application.Current.Resources);
            var shell = WidgetView.Shell(palette, out _, out _, out var pin, out _, out var panel, out _, out _);
            pin.Foreground = palette.Ink; pin.Background = palette.PinBack;
            foreach (var card in cards)
            {
                if (panel.Children.Count > 0) panel.Children.Add(WidgetView.Divider(palette));
                panel.Children.Add(WidgetView.Card(card, palette, now, null));
            }
            written.Add(Save(shell, WidgetView.Width, Path.Combine(folder, $"widget-{name}.png"), palette));
            var editor = new PlanDateEditor(palette, palette.Claude, claudeDate, null, today);
            written.Add(Save(editor, editor.Width, Path.Combine(folder, $"calendar-{name}.png"), palette));
        }

        using var strip = new TaskbarStrip(() => { }, () => new ContextMenu());
        var readings = (TrayReading.Create(UsageProvider.Claude, claude, now),
            TrayReading.Merge(UsageProvider.Codex, [("you@example.com", codexYou), ("work.team@example.com", codexWork)], now),
            TrayReading.Create(UsageProvider.Antigravity, gemini, now));
        strip.Update(readings.Item1, readings.Item2, readings.Item3);
        var content = (Border)strip.Content;
        strip.Content = null;
        foreach (bool light in new[] { false, true })
        {
            strip.ApplyAppearance(new(24, light, false, Colors.White));
            // A taskbar-coloured backdrop, as the strip sits on the taskbar.
            var backdrop = new Border { Background = Palette.Brush(light ? "#F3F3F3" : "#202020"), Padding = new Thickness(8, 5, 8, 5), Child = content };
            written.Add(Save(backdrop, TaskbarPlacement.WideWidth + 16, Path.Combine(folder, $"strip-{(light ? "light" : "dark")}.png"), null, 40));
            backdrop.Child = null;
        }
        strip.Content = content;
        return written;
    }

    // Lays the element out at its natural height and saves it at twice the size, as on a 200% display.
    private static string Save(FrameworkElement element, double width, string path, Palette? palette, double? height = null)
    {
        var host = new Border { Padding = new Thickness(12), Child = element, Background = palette is null ? Brushes.Transparent : Palette.Brush(palette.IsLight ? "#DDE3EA" : "#0B0E12") };
        if (palette is null) host.Padding = new Thickness(0);
        TextElementDefaults(host);
        host.Measure(new Size(width + host.Padding.Left + host.Padding.Right, height ?? double.PositiveInfinity));
        var size = new Size(width + host.Padding.Left + host.Padding.Right, height ?? host.DesiredSize.Height);
        host.Arrange(new Rect(size));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        if (host.Child is not null) host.Child = null;
        return path;
    }

    // The widget inherits its font and text colour from its window; outside one, set them on the host.
    private static void TextElementDefaults(Border host)
    {
        System.Windows.Documents.TextElement.SetFontFamily(host, new FontFamily("Segoe UI"));
        System.Windows.Documents.TextElement.SetForeground(host, (Brush)Application.Current.Resources["Ink"]);
    }
}
