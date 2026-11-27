namespace UsageWidget;

public sealed record ResetObservation(UsageWindow Window, DateTimeOffset UpdatedAt);
// Account: the account's display name, such as its email, when a provider has several.
public sealed record ResetAlert(string Id, UsageProvider Provider, string Label, double Remaining, DateTimeOffset ExpiresAt, string? Account = null);

// Only a newer, fresh provider observation can confirm a reset. Countdown ticks cannot generate alerts.
public sealed class ResetAlertLedger
{
    public Dictionary<string, ResetObservation> Observations { get; set; } = new();
    public List<ResetAlert> Pending { get; set; } = new();
    public List<string> Delivered { get; set; } = new();

    public bool Prune(bool enabled, DateTimeOffset now)
    {
        bool changed = Pending.RemoveAll(alert => !enabled || alert.ExpiresAt <= now) > 0;
        foreach (var key in Observations.Where(pair => now - pair.Value.UpdatedAt >= TimeSpan.FromDays(45)).Select(pair => pair.Key).ToArray())
            changed |= Observations.Remove(key);
        return changed;
    }

    // source: the reading's alert key ("Codex@<account>" for an additional sign-in); the provider's name for its default one.
    public bool Observe(UsageProvider provider, UsageSnapshot? snapshot, bool enabled, DateTimeOffset now, string? source = null, string? account = null)
    {
        if (snapshot is null || Math.Abs((snapshot.UpdatedAt - now).TotalSeconds) >= 120) return false;
        bool changed = false;
        foreach (var window in snapshot.Windows)
        {
            if (!double.IsFinite(window.UsedPercent)) continue;
            string key = $"{source ?? provider.ToString()}:{window.Key}";
            Observations.TryGetValue(key, out var previous);
            if (previous is not null && snapshot.UpdatedAt <= previous.UpdatedAt) continue;
            if (previous?.Window.ResetsAt is long oldReset)
            {
                var old = previous.Window;
                double tolerance = Math.Min(60, (old.WindowMinutes is > 0 ? old.WindowMinutes.Value : 300) * 6.0);
                bool advanced = window.ResetsAt is long next && next - (double)oldReset > tolerance;
                bool cleared = window.UsedPercent == 0 && old.UsedPercent > 0;
                bool boundaryPassed = oldReset <= snapshot.UpdatedAt.ToUnixTimeSeconds();
                bool didReset = boundaryPassed
                    ? window.ResetsAt is long reset ? advanced && reset > snapshot.UpdatedAt.ToUnixTimeSeconds() : cleared
                    : advanced && cleared;
                string id = $"{key}:reset:{oldReset}";
                // Only a window that was used can usefully reset (unused windows of some providers report a boundary that keeps
                // moving), and a window under a used-up longer limit stays at 0% after its own reset.
                bool useful = old.UsedPercent > 0 && snapshot.Blocker(window, now) is null;
                if (didReset && useful && enabled && !Delivered.Contains(id) && Pending.All(alert => alert.Id != id))
                    Pending.Add(new(id, provider, window.Label, Math.Clamp(100 - window.UsedPercent, 0, 100), now.AddMinutes(30), account));
            }
            // Disabled alerts still advance the baseline, without replaying those resets when re-enabled.
            Observations[key] = new(window, snapshot.UpdatedAt);
            changed = true;
        }
        return changed;
    }

    // Keep each source's original timestamp: merging a fresh Claude feed with an old API snapshot
    // would otherwise make untouched model-specific weekly readings appear fresh.
    public bool ObserveClaude(UsageSnapshot? account, UsageSnapshot? statusLine, bool enabled, DateTimeOffset now)
    {
        bool changed = false;
        foreach (var snapshot in new[] { account, statusLine }.OfType<UsageSnapshot>().OrderBy(value => value.UpdatedAt))
            changed |= Observe(UsageProvider.Claude, snapshot, enabled, now);
        return changed;
    }

    public void MarkDelivered(ResetAlert alert)
    {
        if (!Delivered.Contains(alert.Id)) Delivered.Add(alert.Id);
        if (Delivered.Count > 256) Delivered.RemoveRange(0, Delivered.Count - 256);
        Pending.RemoveAll(value => value.Id == alert.Id);
    }
}
