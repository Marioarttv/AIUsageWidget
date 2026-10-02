using System.Globalization;

namespace UsageWidget;

public enum UsageProvider { Claude, Codex }

public sealed record TrayReading(string Number, string Tooltip)
{
    public static TrayReading Create(UsageProvider provider, UsageSnapshot? snapshot, DateTimeOffset now)
    {
        string name = provider == UsageProvider.Claude ? "Claude" : "ChatGPT / Codex";
        var primary = UsageFormatting.Primary(snapshot);
        var remaining = UsageFormatting.Remaining(primary, now);
        string number = remaining is double value ? value.ToString("0", CultureInfo.InvariantCulture) : "—";
        string detail = primary is null ? "Waiting for a reading" : $"{primary.Label} · " +
            (remaining is null ? "awaiting update" : $"{number}% remaining");
        string reset = primary is null ? "" : "\n" + UsageFormatting.Countdown(primary.ResetsAt, now);
        return new TrayReading(number, $"{name}\n{detail}{reset}");
    }
}
