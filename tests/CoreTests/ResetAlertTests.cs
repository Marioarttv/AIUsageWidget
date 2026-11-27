using System.Text.Json;
using UsageWidget;

internal static class ResetAlertTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
    private static UsageSnapshot Snapshot(double used, int? reset, int observed = 0, int minutes = 300, string key = "session") =>
        new(Now.AddSeconds(observed), "test", [new(key, minutes == 10080 ? "Weekly" : "5-hour session", used,
            reset is int at ? Now.AddSeconds(at).ToUnixTimeSeconds() : null, minutes)]);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static ResetAlertLedger RoundTrip(ResetAlertLedger ledger) => JsonSerializer.Deserialize<ResetAlertLedger>(JsonSerializer.Serialize(ledger, DataStore.JsonOptions), DataStore.JsonOptions)!;
    private static void Observe(ResetAlertLedger ledger, UsageSnapshot snapshot, bool enabled = true, UsageProvider provider = UsageProvider.Claude) =>
        ledger.Observe(provider, snapshot, enabled, snapshot.UpdatedAt);

    internal static void Run()
    {
        Check(JsonSerializer.Deserialize<WidgetSettings>("{}", DataStore.JsonOptions)!.ResetNotifications,
            "Existing installations must enable reset notifications by default.");
        Check(!JsonSerializer.Deserialize<WidgetSettings>("{\"resetNotifications\":false}", DataStore.JsonOptions)!.ResetNotifications,
            "The user's reset preference must survive loading.");

        foreach (int minutes in new[] { 300, 10080 })
        foreach (var provider in Enum.GetValues<UsageProvider>())
        {
            var ledger = new ResetAlertLedger();
            Observe(ledger, Snapshot(20, 5, minutes: minutes), provider: provider);
            Check(ledger.Pending.Count == 0, "A first observation is a baseline, not a reset.");
            var next = Snapshot(40, minutes * 60, 10, minutes);
            Observe(ledger, next, provider: provider);
            Check(ledger.Pending is [{ Remaining: 60 }] && ledger.Pending[0].Provider == provider,
                "An advanced boundary must confirm session/weekly resets even when usage has already increased again.");
            ledger = RoundTrip(ledger);
            Check(ledger.Pending.Count == 1, "Unsent reset alerts must survive a restart.");
            Observe(ledger, next, provider: provider);
            Check(ledger.Pending.Count == 1, "Display ticks must not duplicate pending resets.");
            ledger.MarkDelivered(ledger.Pending[0]);
            ledger = RoundTrip(ledger);
            Observe(ledger, next with { UpdatedAt = Now.AddSeconds(20) }, provider: provider);
            // Also tolerate a fresh but regressed backend response, followed by the already-reported rollover.
            Observe(ledger, Snapshot(20, 5, 30, minutes), provider: provider);
            Observe(ledger, Snapshot(40, minutes * 60, 40, minutes), provider: provider);
            Check(ledger.Pending.Count == 0, "Delivered resets must not repeat after refreshes, restart, or a regressed response.");
        }

        var expired = new ResetAlertLedger();
        var initial = Snapshot(80, 5);
        Observe(expired, initial);
        expired.Observe(UsageProvider.Claude, initial, true, Now.AddSeconds(10));
        Observe(expired, Snapshot(80, 5, 10));
        Check(expired.Pending.Count == 0, "Countdown expiry or unchanged expired data cannot confirm a reset.");

        var jitter = new ResetAlertLedger();
        Observe(jitter, Snapshot(1, 5)); Observe(jitter, Snapshot(0, 6, 5));
        Check(jitter.Pending.Count == 0, "A small timestamp adjustment must not look like a rollover.");
        var correction = new ResetAlertLedger();
        Observe(correction, Snapshot(80, 500)); Observe(correction, Snapshot(78, 500, 10));
        Observe(correction, Snapshot(0, 500, 20));
        Check(correction.Pending.Count == 0, "A usage correction without a moved boundary must not trigger a reset.");
        var early = new ResetAlertLedger();
        Observe(early, Snapshot(80, 500)); Observe(early, Snapshot(0, 18000, 10));
        Check(early.Pending.Count == 1, "An early reset needs both zero usage and an advanced boundary.");
        var earlyUsed = new ResetAlertLedger();
        Observe(earlyUsed, Snapshot(80, 500)); Observe(earlyUsed, Snapshot(50, 18000, 10));
        Check(earlyUsed.Pending.Count == 0, "An early partial correction must not count as a reset.");

        var idle = new ResetAlertLedger();
        Observe(idle, Snapshot(80, 5)); Observe(idle, Snapshot(0, null, 10));
        Check(idle.Pending.Count == 1, "Zero usage with no new boundary can confirm an expired idle window's reset.");
        var unknown = new ResetAlertLedger();
        Observe(unknown, Snapshot(80, null)); Observe(unknown, Snapshot(0, null, 10));
        Check(unknown.Pending.Count == 0, "Without a previous reset boundary a drop alone is ambiguous.");
        var partial = new ResetAlertLedger();
        Observe(partial, Snapshot(80, 5)); Observe(partial, Snapshot(70, null, 10));
        Check(partial.Pending.Count == 0, "An expired boundary plus partial recovery without a new boundary is ambiguous.");

        var stale = new ResetAlertLedger();
        stale.Observe(UsageProvider.Codex, Snapshot(80, 5, -3600), true, Now);
        stale.Observe(UsageProvider.Codex, Snapshot(80, 5, 3600), true, Now);
        Check(stale.Observations.Count == 0, "Stale and clock-skewed readings must not seed or replace observations.");
        Observe(stale, Snapshot(80, 5), provider: UsageProvider.Codex);
        stale.Observe(UsageProvider.Codex, Snapshot(0, 18000, -1), true, Now.AddSeconds(10));
        Check(stale.Pending.Count == 0, "Out-of-order readings must be ignored.");
        Observe(stale, Snapshot(0, 18000, 10), provider: UsageProvider.Codex);
        Check(stale.Pending.Count == 1, "Ignored old readings must not spoil the valid reset baseline.");
        Check(stale.Prune(true, Now.AddMinutes(31)) && stale.Pending.Count == 0, "Queued alerts must expire rather than notify hours later.");

        var disabled = new ResetAlertLedger();
        Observe(disabled, Snapshot(80, 5), false); Observe(disabled, Snapshot(0, 18000, 10), false);
        Observe(disabled, Snapshot(1, 18000, 20));
        Check(disabled.Pending.Count == 0, "Re-enabling must not notify for resets already seen while disabled.");
        Observe(disabled, Snapshot(0, 36000, 18001));
        Check(disabled.Pending.Count == 1, "The next reset after re-enabling must still notify.");
        Check(disabled.Prune(false, Now.AddSeconds(18002)) && disabled.Pending.Count == 0, "Disabling must cancel queued reset notifications.");

        var missing = new ResetAlertLedger();
        Observe(missing, Snapshot(80, 5)); Observe(missing, new(Now.AddSeconds(3), "test", []));
        Observe(missing, Snapshot(0, 18000, 10));
        Check(missing.Pending.Count == 1, "Temporary partial responses must retain the previous window observation.");

        var sources = new ResetAlertLedger();
        var model = Snapshot(80, 5, 2, 10080, "model-weekly");
        Observe(sources, model);
        sources.ObserveClaude(Snapshot(0, 604800, 1, 10080, "model-weekly"), Snapshot(10, 18000, 10), true, Now.AddSeconds(10));
        Check(sources.Pending.Count == 0 && sources.Observations["Claude:model-weekly"].UpdatedAt == model.UpdatedAt,
            "A fresh status line must not make stale model-specific API windows look newly observed.");
        sources.ObserveClaude(null, Snapshot(0, 604800, 11, 10080, "model-weekly"), true, Now.AddSeconds(11));
        Check(sources.Pending.Count == 1, "A genuinely newer Claude feed can confirm the same window's reset.");

        var simultaneous = new ResetAlertLedger();
        var all = new UsageSnapshot(Now, "test", [Snapshot(80, 5).Windows[0], Snapshot(40, 5, minutes: 10080, key: "weekly").Windows[0]]);
        foreach (var provider in Enum.GetValues<UsageProvider>()) Observe(simultaneous, all, provider: provider);
        var rolled = all with { UpdatedAt = Now.AddSeconds(10), Windows = all.Windows.Select(w => w with { UsedPercent = 0, ResetsAt = Now.AddDays(7).ToUnixTimeSeconds() }).ToList() };
        foreach (var provider in Enum.GetValues<UsageProvider>()) Observe(simultaneous, rolled, provider: provider);
        int expected = Enum.GetValues<UsageProvider>().Length * 2;
        Check(simultaneous.Pending.Count == expected, "Each provider and window must retain its own simultaneous reset notification.");
        simultaneous.MarkDelivered(simultaneous.Pending[0]);
        Check(simultaneous.Pending.Count == expected - 1, "Sending one notification must leave the others pending.");
        simultaneous = RoundTrip(simultaneous);
        Check(simultaneous.Pending.Count == expected - 1, "Remaining notifications must survive restart during delivery.");
    }
}
