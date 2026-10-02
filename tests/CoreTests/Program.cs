using System.Text.Json;
using UsageWidget;

if (args.Contains("--inspect-taskbar")) { TrayTests.InspectTaskbar(); return; }

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static UsageSnapshot Parse(string json) { using var doc = JsonDocument.Parse(json); return CodexService.Parse(doc.RootElement, DateTimeOffset.Parse("2026-09-30T12:00:00Z")); }

var weekly = Parse("""{"rateLimits":{"primary":{"usedPercent":14,"windowDurationMins":10080,"resetsAt":1791311823},"secondary":null,"planType":"pro"}}""");
Check(weekly.Windows.Count == 1 && weekly.Windows[0].Label == "Weekly", "Window duration must determine the label, rather than primary/secondary position.");
Check(weekly.Windows[0].UsedPercent == 14 && weekly.Plan == "pro", "Usage and plan must survive parsing.");
var multi = Parse("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":null},"secondary":null},"other":{"limitName":"Other models","primary":{"usedPercent":100,"windowDurationMins":10080,"resetsAt":1791311823}}}}""");
Check(multi.Windows.Count == 2 && multi.Windows[0].UsedPercent == 0 && multi.Windows[1].UsedPercent == 100, "All buckets and zero usage must be retained.");
Check(multi.Windows[1].Label.Contains("Other models"), "Multiple buckets must be distinguishable.");
var missing = Parse("""{"rateLimits":{"primary":{"usedPercent":null},"secondary":null}}""");
Check(missing.Windows.Count == 0, "Missing quota data must remain unknown.");
var now = DateTimeOffset.FromUnixTimeSeconds(1791311823);
Check(UsageFormatting.Expired(weekly.Windows[0], now), "Reached reset times must be treated as expired.");
Check(UsageFormatting.Countdown(1791311823, now).Contains("awaiting update"), "Resetting must not invent a fresh allowance.");
static UsageSnapshot ParseClaude(string json) { using var doc = JsonDocument.Parse(json); return ClaudeService.Parse(doc.RootElement, DateTimeOffset.Parse("2026-09-30T05:00:00Z"), "pro"); }
var claude = ParseClaude("""{"five_hour":{"utilization":52.0,"resets_at":"2026-09-30T07:50:00.103876+00:00"},"seven_day":{"utilization":0,"resets_at":"2026-10-03T07:00:00+00:00"},"seven_day_opus":null,"seven_day_sonnet":null,"limits":[{"kind":"session","percent":52,"resets_at":"2026-09-30T07:50:00+00:00"},{"kind":"weekly_scoped","percent":2,"resets_at":"2026-10-03T07:00:00+00:00","scope":{"model":{"display_name":"Fable"}}},{"kind":"weekly_scoped","percent":1,"scope":null},{"kind":"unknown_future_limit","percent":9}]}""");
Check(claude.Windows.Select(w => w.Label).SequenceEqual(new[] { "5-hour session", "Weekly", "Weekly · Fable" }), "Claude session, weekly, and model-scoped weekly limits must be read without duplicates.");
Check(claude.Windows[0].UsedPercent == 52 && claude.Windows[0].ResetsAt == DateTimeOffset.Parse("2026-09-30T07:50:00Z").ToUnixTimeSeconds() && claude.Windows[1].UsedPercent == 0, "Claude utilization, zero usage, and ISO reset times must survive parsing.");
Check(claude.Plan == "pro", "The Claude plan must be retained.");
Check(ParseClaude("""{"five_hour":{"utilization":null,"resets_at":null},"seven_day":null}""").Windows.Count == 0, "Missing Claude utilization must remain unknown.");
var fallback = ParseClaude("""{"limits":[{"kind":"session","percent":40,"resets_at":null},{"kind":"weekly_all","percent":7,"resets_at":"2026-10-03T07:00:00+00:00"}]}""");
Check(fallback.Windows.Count == 2 && fallback.Windows[0].Key == "five_hour" && fallback.Windows[0].ResetsAt is null && fallback.Windows[1].UsedPercent == 7, "The limits list must be used when named fields are absent.");
var line = new UsageSnapshot(claude.UpdatedAt.AddMinutes(1), "Claude Code status line", [new UsageWindow("five_hour", "5-hour session", 60, 1791311823, 300)]);
var merged = ClaudeService.Merge(claude, line)!;
Check(merged.Windows.Count == 3 && merged.Windows[0].UsedPercent == 60 && merged.UpdatedAt == line.UpdatedAt, "A newer status-line reading must replace the matching account window only.");
Check(ClaudeService.Merge(claude, line with { UpdatedAt = claude.UpdatedAt.AddMinutes(-1) }) == claude, "An older status-line reading must not override the account reading.");
Check(ClaudeService.Merge(null, line) == line && ClaudeService.Merge(claude, null) == claude, "Either Claude source alone must be shown.");
var trayState = new UsageSnapshot(now, "test", [
    new("weekly", "Weekly", 98, now.AddDays(1).ToUnixTimeSeconds(), 10080),
    new("session", "5-hour session", 32, now.AddHours(2).ToUnixTimeSeconds(), 300)]);
var trayReading = TrayReading.Create(UsageProvider.Claude, trayState, now);
Check(trayReading.Number == "68" && trayReading.Tooltip.Contains("5-hour session · 68% remaining") && trayReading.Tooltip.Contains("Resets in 2h"),
    "Tray must show the shortest window's remaining allowance, not the lowest allowance or time to reset.");
Check(TrayReading.Create(UsageProvider.Codex, null, now).Number == "—", "Missing tray usage must show a dash.");
Check(TrayReading.Create(UsageProvider.Codex, trayState with { Windows = [] }, now).Number == "—", "An empty reading must remain unknown.");
Check(TrayReading.Create(UsageProvider.Codex, trayState, now.AddHours(2)).Number == "—", "An expired session must show a dash, not switch to the weekly allowance.");
Check(TrayReading.Create(UsageProvider.Codex, trayState with { Windows = [trayState.Windows[0]] }, now).Number == "2", "Weekly-only plans must use their actual shortest window.");
Check(TrayReading.Create(UsageProvider.Codex, trayState, now).Tooltip.StartsWith("ChatGPT / Codex"), "ChatGPT marks must identify the Codex allowance in their tooltip.");
foreach (var (used, expected) in new[] { (0.0, "100"), (100.0, "0"), (-4.0, "100"), (105.0, "0"), (32.6, "67"), (double.NaN, "—"), (double.PositiveInfinity, "—") })
{
    var state = trayState with { Windows = [new("session", "5-hour session", used, null, 300)] };
    Check(TrayReading.Create(UsageProvider.Claude, state, now).Number == expected, "Tray must clamp and round percentages like the widget, and reject invalid usage.");
}
var durations = trayState with { Windows = [new("unknown", "Unknown", 2, null), new("invalid", "Invalid", 3, null, 0), trayState.Windows[1]] };
Check(UsageFormatting.Primary(durations)?.Key == "session", "Known, positive durations must precede unknown or invalid durations.");
Check(TrayReading.Create(UsageProvider.Claude, merged, claude.UpdatedAt).Number == "40", "Tray must use the same merged Claude feed as the widget.");
TrayTests.Run(args.Contains("--tray-preview"));
Console.WriteLine("Passed: usage parsing/merging and edge cases, native tray lifecycle, taskbar strip readings/clicks/layout, DPI scaling, collision fallback, fullscreen/auto-hide placement, and native resource cleanup.");
