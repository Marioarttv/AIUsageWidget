using System.IO;
using System.Text.Json;

namespace UsageWidget;

public record UsageWindow(string Key, string Label, double UsedPercent, long? ResetsAt, int? WindowMinutes = null);
public record UsageSnapshot(DateTimeOffset UpdatedAt, string Source, List<UsageWindow> Windows, string? Plan = null);
public sealed class WidgetSettings
{
    public string Mode { get; set; } = "topmost";
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool StartAtLogin { get; set; }
    public bool LowUsageAlerts { get; set; } = true;
    public List<string> AlertedWindows { get; set; } = new();
}

public static class DataStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageWidget");
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static void EnsureDirectory() => Directory.CreateDirectory(DirectoryPath);
    public static T? Read<T>(string name)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(DirectoryPath, name)), JsonOptions); }
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
