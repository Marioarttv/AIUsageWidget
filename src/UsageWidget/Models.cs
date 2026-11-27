using System.IO;
using System.Text.Json;

namespace UsageWidget;

// Pool: the quota a window shares with others, such as one Codex limit bucket or one Antigravity model group.
// null is the whole account, so such a window limits every other window.
public record UsageWindow(string Key, string Label, double UsedPercent, long? ResetsAt, int? WindowMinutes = null, string? Pool = null);
public record UsageSnapshot(DateTimeOffset UpdatedAt, string Source, List<UsageWindow> Windows, string? Plan = null)
{
    // The used-up limit that leaves nothing in `window` either: a longer window of the same quota (a spent weekly limit
    // also ends the 5-hour session), or an account-wide window for a model-specific one. When several apply, the one that
    // resets last decides. Expired windows await a new reading and limit nothing.
    public UsageWindow? Blocker(UsageWindow window, DateTimeOffset now)
    {
        if (window.WindowMinutes is not int minutes) return null;
        return Windows.Where(other => other.Key != window.Key && UsageFormatting.UsedUp(other) && !UsageFormatting.Expired(other, now)
                && other.WindowMinutes is int length && (other.Pool is null || other.Pool == window.Pool)
                && (length > minutes || (length == minutes && other.Pool is null && window.Pool is not null)))
            .OrderBy(other => other.ResetsAt ?? long.MaxValue).LastOrDefault();
    }
    // What can actually be used of `window`: 0 while a longer limit is used up.
    public double Available(UsageWindow window, DateTimeOffset now) => Blocker(window, now) is null ? UsageFormatting.Left(window) : 0;
}

public sealed class WidgetSettings
{
    public string Mode { get; set; } = "topmost";
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool StartAtLogin { get; set; }
    public bool LowUsageAlerts { get; set; } = true;
    public bool ResetNotifications { get; set; } = true;
    public bool TaskbarStrip { get; set; } = true;
    public bool ShowAntigravity { get; set; } = true;
    // "dark" (the default, also when unset), "light", or "system" to follow Windows' app theme.
    public string? Theme { get; set; }
    public List<string> AlertedWindows { get; set; } = new();
    // Additional ChatGPT sign-ins the user turned on or off; the others follow CodexAccount.TrackedByDefault.
    public List<string> CodexAccountsOn { get; set; } = new();
    public List<string> CodexAccountsOff { get; set; } = new();
    // Codex home folders added from the menu.
    public List<string> CodexFolders { get; set; } = new();
    // Renewal or end dates entered by hand, keyed by UsageSource.Id.
    public Dictionary<string, ManualPlanDate> PlanDates { get; set; } = new();
}

public static class DataStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageWidget");
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static void EnsureDirectory() => Directory.CreateDirectory(DirectoryPath);
    public static T? Read<T>(string name)
    {
        string path = Path.Combine(DirectoryPath, name);
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return default; }
    }
    public static UsageSnapshot? ReadSnapshot(string name) => Read<UsageSnapshot>(name);
    public static void Write<T>(string name, T value)
    {
        EnsureDirectory();
        string dest = Path.Combine(DirectoryPath, name), temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temp, dest, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Log(string message)
    {
        try {
            var path = Path.Combine(DirectoryPath, "widget.log");
            if (File.Exists(path) && new FileInfo(path).Length > 100_000) File.WriteAllText(path, "");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}\n");
        } catch { }
    }
}

public static class UsageFormatting
{
    public static UsageWindow? Primary(UsageSnapshot? snapshot) => snapshot?.Windows
        .OrderBy(w => w.WindowMinutes is > 0 ? w.WindowMinutes.Value : int.MaxValue).FirstOrDefault();
    public static double? Remaining(UsageWindow? window, DateTimeOffset now) =>
        window is null || Expired(window, now) || !double.IsFinite(window.UsedPercent)
            ? null : Math.Clamp(100 - window.UsedPercent, 0, 100);
    public static double Left(UsageWindow window) => double.IsFinite(window.UsedPercent) ? Math.Clamp(100 - window.UsedPercent, 0, 100) : 0;
    // Shown as 0% in whole numbers.
    public static bool UsedUp(UsageWindow window) => double.IsFinite(window.UsedPercent) && Left(window) < 1;
    // What can be used now of the shortest window: 0 under a used-up longer limit (even once the window itself has expired),
    // otherwise null when unknown or expired.
    public static double? Headline(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        var primary = Primary(snapshot);
        if (snapshot is null || primary is null) return null;
        if (snapshot.Blocker(primary, now) is not null) return 0;
        return Remaining(primary, now);
    }
    // Whole percentages are rounded down, so 0% always means used up.
    public static string Percent(double value) => Math.Floor(Math.Clamp(value, 0, 100)).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    // Why a window shows 0%, short enough for one line: "Weekly used up · 3d 4h".
    public static string Blocked(UsageWindow blocker, DateTimeOffset now) => WindowLabel(blocker.WindowMinutes, "Longer limit") + " used up · " + ShortCountdown(blocker.ResetsAt, now);
    public static string ShortCountdown(long? reset, DateTimeOffset now) => reset is null ? "—" : reset <= now.ToUnixTimeSeconds() ? "awaiting update" : Short(reset, now);
    // "17 Nov", with the year only when it isn't the current one.
    public static string Day(DateOnly day, DateOnly today, IFormatProvider? culture = null) =>
        day.ToString(day.Year == today.Year ? "d MMM" : "d MMM yyyy", culture ?? System.Globalization.CultureInfo.CurrentCulture);
    // The end of a paid ChatGPT period. The sign-in doesn't say whether the plan will renew or was cancelled.
    public static string PaidPeriod(DateTimeOffset end, DateTimeOffset now, TimeZoneInfo? zone = null, IFormatProvider? culture = null)
    {
        zone ??= TimeZoneInfo.Local;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end, zone).DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return (end > now ? "Renews or ends " : "Paid until ") + Day(day, today, culture);
    }

    public static string WindowLabel(int? minutes, string fallback) => minutes switch {
        10080 => "Weekly", 300 => "5-hour session", 1440 => "Daily", 60 => "Hourly",
        > 0 when minutes % 1440 == 0 => $"{minutes / 1440}-day window",
        > 0 when minutes % 60 == 0 => $"{minutes / 60}-hour window",
        > 0 => $"{minutes}-minute window", _ => fallback
    };
    public static bool Expired(UsageWindow value, DateTimeOffset now) => value.ResetsAt is long reset && reset <= now.ToUnixTimeSeconds();
    public static string Countdown(long? reset, DateTimeOffset now)
    {
        if (reset is null) return "Reset time unavailable";
        var remaining = DateTimeOffset.FromUnixTimeSeconds(reset.Value) - now;
        if (remaining.TotalSeconds <= 0) return "Reset reached · awaiting update";
        if (remaining.TotalDays >= 1) return $"Resets in {(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1) return $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"Resets in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }
    public static string Short(long? reset, DateTimeOffset now)
    {
        if (reset is null) return "—";
        var remaining = DateTimeOffset.FromUnixTimeSeconds(reset.Value) - now;
        if (remaining.TotalSeconds <= 0) return "now";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }
    public static string Age(DateTimeOffset updated, DateTimeOffset now)
    {
        var age = now - updated;
        if (age.TotalSeconds < 60) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
