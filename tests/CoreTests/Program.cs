using System.Text.Json;
using UsageWidget;

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
Console.WriteLine("Passed: dynamic window labels, multiple buckets, null/zero usage, and expired resets.");
