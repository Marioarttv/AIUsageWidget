using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageWidget;

// Reads subscription usage with the existing Claude Code sign-in. The token is read at request time and never stored or refreshed here,
// because refreshing would rotate Claude Code's own credentials.
public sealed class ClaudeService
{
    public const string SnapshotName = "claude-api.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue, backoffUntil = DateTimeOffset.MinValue;
    public string? Status { get; private set; }
    public string? Plan { get; private set; }
    public bool Refreshing { get; private set; }

    public async Task RefreshAsync(bool manual = false)
    {
        var now = DateTimeOffset.UtcNow;
        // The usage endpoint is rate limited; poll gently and honour server back-off even for manual refreshes.
        if (now < backoffUntil || now - lastAttempt < (manual ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2.5))) return;
        if (!await refreshLock.WaitAsync(0)) return;
        Refreshing = true;
        try
        {
            lastAttempt = now;
            var credentials = ReadCredentials();
            if (credentials is null) { Status = "Sign in to Claude Code to connect"; return; }
            Plan = credentials.Value.Plan;
            if (credentials.Value.ExpiresAt is DateTimeOffset expires && expires <= now) { Status = "Claude sign-in expired · open Claude Code to renew"; return; }
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Value.Token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("AIUsageWidget/" + (typeof(ClaudeService).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
            using var response = await Http.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { Status = "Claude sign-in expired · open Claude Code to renew"; return; }
            if (response.StatusCode == HttpStatusCode.TooManyRequests) {
                backoffUntil = DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(10));
                Status = "Claude usage busy · retrying later"; return;
            }
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var snapshot = Parse(doc.RootElement, DateTimeOffset.UtcNow, Plan);
            if (snapshot.Windows.Count == 0) { Status = "No Claude subscription windows returned"; return; }
            DataStore.Write(SnapshotName, snapshot);
            Status = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            Status = "Claude refresh failed · showing last reading";
            DataStore.Log("Claude refresh: " + ex.GetType().Name);
        }
        finally
        {
            Refreshing = false;
            refreshLock.Release();
        }
    }

    private static (string Token, DateTimeOffset? ExpiresAt, string? Plan)? ReadCredentials()
    {
        string directory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, ".credentials.json")));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object) return null;
            if (!oauth.TryGetProperty("accessToken", out var token) || token.GetString() is not { Length: > 0 } value) return null;
            DateTimeOffset? expires = oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            string? plan = oauth.TryGetProperty("subscriptionType", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            return (value, expires, plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    public static UsageSnapshot Parse(JsonElement result, DateTimeOffset updated, string? plan = null)
    {
        var windows = new List<UsageWindow>();
        void Add(string key, string label, int minutes, double? used, JsonElement reset)
        {
            if (used is not double percentage || !double.IsFinite(percentage) || windows.Any(w => w.Key == key || w.Label == label)) return;
            long? resetsAt = reset.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(reset.GetString(), out var at) ? at.ToUnixTimeSeconds() : null;
            windows.Add(new UsageWindow(key, label, Math.Clamp(percentage, 0, 100), resetsAt, minutes));
        }
        static double? Number(JsonElement value, string name) => value.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out double d) ? d : null;
        static JsonElement Property(JsonElement value, string name) => value.TryGetProperty(name, out var p) ? p : default;

        foreach (var (field, label) in new[] { ("five_hour", "5-hour session"), ("seven_day", "Weekly"), ("seven_day_opus", "Weekly · Opus"), ("seven_day_sonnet", "Weekly · Sonnet") })
        {
            if (!result.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Object) continue;
            Add(field, label, field == "five_hour" ? 300 : 10080, Number(value, "utilization"), Property(value, "resets_at"));
        }
        // Newer accounts also report model-scoped weekly limits here; the named fields above take precedence.
        if (result.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (limit.ValueKind != JsonValueKind.Object || !limit.TryGetProperty("kind", out var kind)) continue;
                var reset = Property(limit, "resets_at");
                switch (kind.GetString())
                {
                    case "session": Add("five_hour", "5-hour session", 300, Number(limit, "percent"), reset); break;
                    case "weekly_all": Add("seven_day", "Weekly", 10080, Number(limit, "percent"), reset); break;
                    case "weekly_scoped":
                        string? model = limit.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                            && scope.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object
                            && m.TryGetProperty("display_name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(model)) Add("seven_day_" + model.ToLowerInvariant(), "Weekly · " + model, 10080, Number(limit, "percent"), reset);
                        break;
                }
            }
        }
        return new(updated, "Claude account", windows, plan);
    }

    // The status-line feed can be newer than the last poll; its readings replace matching account windows.
    public static UsageSnapshot? Merge(UsageSnapshot? account, UsageSnapshot? statusLine)
    {
        if (statusLine is null || statusLine.Windows.Count == 0) return account;
        if (account is null || account.Windows.Count == 0) return statusLine;
        if (statusLine.UpdatedAt <= account.UpdatedAt) return account;
        var windows = account.Windows.Select(w => statusLine.Windows.FirstOrDefault(s => s.Key == w.Key) ?? w)
            .Concat(statusLine.Windows.Where(s => account.Windows.All(w => w.Key != s.Key))).ToList();
        return account with { UpdatedAt = statusLine.UpdatedAt, Windows = windows };
    }
}
