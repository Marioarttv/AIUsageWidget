using System.Globalization;

namespace UsageWidget;

// Stored as numbers in reset-alerts.json: append new providers, never reorder.
public enum UsageProvider { Claude, Codex, Antigravity }

public sealed record TrayReading(string Number, string Tooltip)
{
    public static string Name(UsageProvider provider) => provider switch {
        UsageProvider.Claude => "Claude", UsageProvider.Codex => "ChatGPT / Codex", _ => "Antigravity (Gemini)" };

    public static TrayReading Create(UsageProvider provider, UsageSnapshot? snapshot, DateTimeOffset now)
    {
        string name = Name(provider);
        var primary = UsageFormatting.Primary(snapshot);
        var value = UsageFormatting.Headline(snapshot, now);
        var blocker = primary is null ? null : snapshot!.Blocker(primary, now);
        string number = value is double v ? UsageFormatting.Percent(v) : "—";
        string detail = primary is null ? "Waiting for a reading" : $"{primary.Label} · " +
            (value is null ? "awaiting update" : $"{number}% remaining");
        string reset = primary is null ? "" : "\n" + (blocker is not null ? UsageFormatting.Blocked(blocker, now) : UsageFormatting.Countdown(primary.ResetsAt, now));
        return new TrayReading(number, $"{name}\n{detail}{reset}");
    }

    // Several ChatGPT accounts share one number: the average of what each has left, which is the share of their combined
    // allowance left when the plans are the same size. An account under a used-up weekly limit counts as 0%.
    public static TrayReading Merge(UsageProvider provider, IReadOnlyList<(string Name, UsageSnapshot? Snapshot)> accounts, DateTimeOffset now)
    {
        if (accounts.Count == 1) return Create(provider, accounts[0].Snapshot, now);
        var values = accounts.Select(a => UsageFormatting.Headline(a.Snapshot, now)).ToList();
        var known = values.OfType<double>().ToList();
        string number = known.Count == 0 ? "—" : UsageFormatting.Percent(known.Average());
        var lines = accounts.Select((a, i) => $"{a.Name}: " + (values[i] is double v ? UsageFormatting.Percent(v) + "%" : "—"));
        string average = number == "—" ? "no readings yet" : $"{number}% remaining on average";
        return new TrayReading(number, $"{Name(provider)} · {accounts.Count} accounts\n{average}\n" + string.Join("\n", lines));
    }
}
