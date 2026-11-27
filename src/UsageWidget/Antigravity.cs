using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace UsageWidget;

public sealed record AntigravityExecutable(string Path, bool ManagedByT3);

public enum AntigravityResult { Reading, SignIn, Failure }
public sealed record AntigravityOutcome(AntigravityResult Kind, UsageSnapshot? Snapshot = null, string? Message = null);

// Reading Antigravity's quota through its own CLI: `agy -p /usage --output-format json` runs no model turn, and the CLI owns
// the Google sign-in and renews it itself. The widget never reads that sign-in.
public static class AntigravityCli
{
    public static readonly string[] Arguments = ["-p", "/usage", "--output-format", "json", "--print-timeout", "25s", "--log-file", "NUL"];
    // Kept from the inherited environment: what Windows programs need to run, find the user's profile and reach the network.
    private static readonly string[] Kept = ["SystemRoot", "SystemDrive", "windir", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "ProgramData", "TEMP", "TMP", "USERNAME", "USERDOMAIN", "COMPUTERNAME", "LANG", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY"];

    // A minimal environment. PATH is an empty folder, so the CLI cannot find `rundll32` to open a browser for a sign-in: a lapsed
    // sign-in must never start a login from a background refresh. Auto-update is off.
    public static Dictionary<string, string> Environment(IReadOnlyDictionary<string, string> inherited, string emptyFolder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in inherited)
            if (Kept.Contains(key, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value)) result[key] = value;
        result["PATH"] = emptyFolder;
        result["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1";
        return result;
    }

    public static string OSArchitecture => RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    // The CLI on PATH or in ~\.local\bin, then the copy T3 Code installs for its Antigravity sessions
    // (%USERPROFILE%\.t3\tools\antigravity-acp\win32-<arch>). Only a native CLI binary qualifies, never the Antigravity editor.
    public static AntigravityExecutable? Find(string home, string? path, string? architecture = null)
    {
        var folders = (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(f => f.Trim('"'))
            .Append(System.IO.Path.Combine(home, ".local", "bin"));
        foreach (var folder in folders)
            foreach (var name in new[] { "antigravity.exe", "agy.exe" })
            {
                string candidate;
                try { candidate = System.IO.Path.Combine(folder, name); } catch (ArgumentException) { continue; }
                if (NativeBinary(candidate)) return new(candidate, false);
            }
        var arch = architecture ?? OSArchitecture;
        foreach (var platform in new[] { "win32-" + arch, arch == "arm64" ? "win32-x64" : "win32-arm64" })
            if (FindManaged(System.IO.Path.Combine(home, ".t3", "tools", "antigravity-acp", platform)) is string managed) return new(managed, true);
        return null;
    }

    private static string? FindManaged(string tools)
    {
        string? release = ReadString(System.IO.Path.Combine(tools, "active.json"), "releaseId", null);
        if (release is null || !SafeName(release)) return null;
        string version = System.IO.Path.Combine(tools, "versions", release);
        // Only a finished install: T3 writes this record last.
        string record = System.IO.Path.Combine(version, ".install-complete.json");
        if (!File.Exists(record)) return null;
        string name = ReadString(record, "executable", "name") ?? "agy.exe";
        if (!SafeName(name)) return null;
        string candidate = System.IO.Path.Combine(version, name);
        return NativeBinary(candidate) ? candidate : null;
    }

    private static bool SafeName(string name) => name.Length > 0 && !name.StartsWith('.') && name.IndexOfAny(['/', '\\', ':']) < 0;

    private static string? ReadString(string file, string property, string? nested)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 64_000) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(property, out var value)) return null;
            if (nested is not null && (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(nested, out value))) return null;
            return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // A Windows executable (MZ header) that isn't the Antigravity editor, an Electron app with its resources beside it.
    internal static bool NativeBinary(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            string? folder = System.IO.Path.GetDirectoryName(path);
            if (folder is null || File.Exists(System.IO.Path.Combine(folder, "resources.pak")) || Directory.Exists(System.IO.Path.Combine(folder, "resources", "app"))
                || File.Exists(System.IO.Path.Combine(folder, "resources", "app.asar"))) return false;
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // The CLI's prompt to sign in on stderr. It then waits for a browser login that will never come.
    public static bool AsksForLogin(string stderr)
    {
        var text = (stderr.Length > 16_384 ? stderr[^16_384..] : stderr).ToLowerInvariant();
        return text.Contains("authentication required") || text.Contains("visit the url to log in") || text.Contains("not logged into antigravity");
    }

    public static AntigravityOutcome Outcome(string stdout, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(AntigravityResult.Failure, Message: "Antigravity returned an invalid response");
            if (Text(root, "status") == "SUCCESS")
            {
                var snapshot = Parse(root, now);
                return snapshot.Windows.Count == 0 ? new(AntigravityResult.Failure, Message: "No Antigravity quota returned for this account") : new(AntigravityResult.Reading, snapshot);
            }
            // "authentication failed or timed out" also follows a slow or offline renewal, so it is retried like any failure;
            // only the CLI's own login prompt (AsksForLogin) means a new sign-in is needed.
            var error = (Text(root, "error") ?? "").ToLowerInvariant();
            if (error.Contains("auth") || error.Contains("timed out")) return new(AntigravityResult.Failure, Message: "Antigravity didn't answer · retrying automatically");
            return new(AntigravityResult.Failure, Message: "Antigravity refresh failed · showing last reading");
        }
        catch (JsonException) { return new(AntigravityResult.Failure, Message: "Antigravity returned an invalid response"); }
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } text ? text : null;
    private static double? Number(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    // The `/usage` command's JSON: model groups, each with a 5-hour and a weekly bucket.
    public static UsageSnapshot Parse(JsonElement root, DateTimeOffset updated)
    {
        var groups = new List<JsonElement>();
        if (root.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.Object && command.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("groups", out var list) && list.ValueKind == JsonValueKind.Array)
            groups.AddRange(list.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Object));
        // Gemini first, so its 5-hour limit leads the card and the taskbar whatever order the CLI uses. OrderBy is stable.
        groups = groups.OrderBy(g => (Text(g, "name") ?? "").StartsWith("gemini models", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        // The plain-text table the CLI prints alongside: "Group\tBucket\t97%\tReset".
        var printed = new Dictionary<string, double>();
        foreach (var line in (Text(root, "response") ?? "").Split('\n'))
        {
            var cells = line.Split('\t').Select(c => c.Trim()).ToArray();
            if (cells.Length >= 3 && cells[2].EndsWith('%') && double.TryParse(cells[2][..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                printed[cells[0] + "\t" + cells[1]] = value;
        }
        var windows = new List<UsageWindow>();
        for (int index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            string groupName = Text(group, "name") ?? $"Group {index + 1}";
            string shortName = ShortGroupName(groupName);
            var buckets = group.TryGetProperty("buckets", out var b) && b.ValueKind == JsonValueKind.Array ? b.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList() : new();
            // Shortest window first within each group, so a group reads "5-hour session", then "Weekly".
            foreach (var bucket in buckets.OrderBy(x => Minutes(Text(x, "window")) ?? int.MaxValue))
            {
                if (bucket.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True) continue;
                int? minutes = Minutes(Text(bucket, "window"));
                string? name = Text(bucket, "name");
                string id = Text(bucket, "id") ?? groupName + ":" + (Text(bucket, "window") ?? name ?? windows.Count.ToString(CultureInfo.InvariantCulture));
                if (windows.Any(w => w.Key == id)) continue;
                // The CLI leaves out zero values, so a bucket without a fraction is used up, as its printed table confirms.
                // Without either, or a reset time, the bucket is unknown and left out.
                long? reset = Text(bucket, "reset_time") is string r && DateTimeOffset.TryParse(r, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    && at.ToUnixTimeSeconds() > 86_400 ? at.ToUnixTimeSeconds() : null; // Unused windows report 1970-01-01: no reset time.
                double? fraction = Number(bucket, "remaining_fraction")
                    ?? (name is not null && printed.TryGetValue(groupName + "\t" + name, out var p) ? p / 100 : (double?)null)
                    ?? (reset is not null ? 0 : (double?)null);
                if (fraction is not double f) continue;
                windows.Add(new(id, shortName + " · " + UsageFormatting.WindowLabel(minutes, name ?? "Limit"), (1 - Math.Clamp(f, 0, 1)) * 100, reset, minutes, groupName));
            }
        }
        return new(updated, "Antigravity CLI", windows);
    }

    // "Gemini Models" → "Gemini", "Claude and GPT models" → "Claude and GPT".
    public static string ShortGroupName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(" models", StringComparison.OrdinalIgnoreCase) ? trimmed[..^" models".Length] : trimmed;
    }

    // "5h", "weekly", "daily", "30m", "2d", "1w".
    public static int? Minutes(string? window)
    {
        var text = window?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text)) return null;
        switch (text) { case "weekly" or "week": return 10080; case "daily" or "day": return 1440; case "hourly" or "hour": return 60; }
        if (!int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count <= 0 || count >= 100_000) return null;
        return text[^1] switch { 'm' => count, 'h' => count * 60, 'd' => count * 1440, 'w' => count * 10080, _ => null };
    }
}
