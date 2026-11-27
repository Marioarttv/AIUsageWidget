using System.IO;
using System.Text;
using System.Text.Json;

namespace UsageWidget;

// One tracked reading: a provider and, for additional sign-ins, which account.
public sealed record UsageSource(UsageProvider Provider, string? Account = null)
{
    public static readonly UsageSource Claude = new(UsageProvider.Claude), Codex = new(UsageProvider.Codex), Antigravity = new(UsageProvider.Antigravity);
    // Default sign-ins keep the bare provider name, so stored alert history still matches.
    public string Id => Provider.ToString().ToLowerInvariant() + (Account is null ? "" : "@" + Account);
    // The prefix of reset and low-alert history entries: "Claude", "Codex", "Codex@<account>", "Antigravity".
    public string AlertKey => Provider + (Account is null ? "" : "@" + Account);
    public string SnapshotName => Provider switch {
        UsageProvider.Claude => ClaudeService.SnapshotName,
        UsageProvider.Antigravity => "antigravity.json",
        _ => Account is null ? "codex.json" : "codex-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Account)))[..16].ToLowerInvariant() + ".json",
    };
}

public enum CodexOrigin
{
    // Codex's own default sign-in (CODEX_HOME or %USERPROFILE%\.codex).
    Main,
    // An account slot in Codex Subscription Router.
    Router,
    // A Codex home folder added from the menu.
    Folder,
}

// A ChatGPT sign-in that Codex can read subscription limits for.
// Key: null for the main sign-in, otherwise the ChatGPT account ID (or the folder path when it is unknown).
public sealed record CodexAccount(string? Key, string? Home, string? Email, string? Plan, string? AccountId, CodexOrigin Origin, DateTimeOffset? PaidUntil = null)
{
    public UsageSource Source => new(UsageProvider.Codex, Key);
    public bool SignedIn => Email is not null || AccountId is not null;
    // New additional sign-ins are tracked unless they're on a free plan or signed out.
    public bool TrackedByDefault => Origin is CodexOrigin.Main or CodexOrigin.Folder || (SignedIn && !string.Equals(Plan, "free", StringComparison.OrdinalIgnoreCase));
    public string Name => Email ?? Home ?? "Codex sign-in";
}

public static class CodexAccounts
{
    public sealed record Identity(string? Email, string? Plan, string? AccountId, DateTimeOffset? PaidUntil = null);

    // Reads only the identity claims of a sign-in. Tokens are never returned or kept.
    public static Identity? ReadIdentity(string home)
    {
        try
        {
            var file = new FileInfo(Path.Combine(home, "auth.json"));
            if (!file.Exists || file.Length >= 256_000) return null;
            return ParseIdentity(File.ReadAllText(file.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public static Identity? ParseIdentity(string authJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(authJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object) return null;
            JsonElement claims = default, auth = default;
            JsonDocument? payload = null;
            try
            {
                if (Text(tokens, "id_token") is string token && token.Split('.') is { Length: >= 2 } parts && Base64Url(parts[1]) is byte[] bytes)
                {
                    payload = JsonDocument.Parse(bytes);
                    if (payload.RootElement.ValueKind == JsonValueKind.Object) claims = payload.RootElement;
                    if (claims.ValueKind == JsonValueKind.Object && claims.TryGetProperty("https://api.openai.com/auth", out var a) && a.ValueKind == JsonValueKind.Object) auth = a;
                }
                var identity = new Identity(Text(claims, "email"), Text(auth, "chatgpt_plan_type"),
                    Text(auth, "chatgpt_account_id") ?? Text(tokens, "account_id"),
                    Text(auth, "chatgpt_subscription_active_until") is string until && DateTimeOffset.TryParse(until, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var end) ? end : null);
                return identity.Email is null && identity.AccountId is null ? null : identity;
            }
            finally { payload?.Dispose(); }
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } text ? text : null;

    private static byte[]? Base64Url(string text)
    {
        var value = text.Replace('-', '+').Replace('_', '/');
        value += new string('=', (4 - value.Length % 4) % 4);
        try { return Convert.FromBase64String(value); } catch (FormatException) { return null; }
    }

    // Codex homes listed in Codex Subscription Router's account state.
    public static List<string> RouterHomes(string stateJson)
    {
        var homes = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array) return homes;
            foreach (var account in accounts.EnumerateArray())
                if (Text(account, "codexHome") is string home) homes.Add(home);
        }
        catch (JsonException) { }
        return homes;
    }

    private static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string DefaultMainHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } configured ? configured : Path.Combine(UserHome, ".codex");
    public static string DefaultRouterState => Path.Combine(UserHome, ".codex-mux", "state.json");

    // A comparable full path: "~" expanded, separators and trailing separators normalized.
    public static string Normalize(string path, string? home = null)
    {
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\")) path = Path.Combine(home ?? UserHome, path.Length > 2 ? path[2..] : "");
        try { path = Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return path.Length > 3 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
    }

    // The main sign-in, then added folders, then signed-in router accounts, each ChatGPT account listed once.
    public static List<CodexAccount> Discover(IEnumerable<string> folders, string? mainHome = null, string? routerState = null, string? userHome = null)
    {
        mainHome ??= DefaultMainHome;
        routerState ??= DefaultRouterState;
        var main = ReadIdentity(mainHome);
        var accounts = new List<CodexAccount> { new(null, null, main?.Email, main?.Plan, main?.AccountId, CodexOrigin.Main, main?.PaidUntil) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Normalize(mainHome, userHome) };
        void Add(string path, CodexOrigin origin)
        {
            string home = Normalize(path, userHome);
            if (!seen.Add(home)) return;
            var found = ReadIdentity(home);
            // Router slots without a sign-in are placeholders; an added folder stays listed so it can be removed.
            if (found is null && origin != CodexOrigin.Folder) return;
            if (found?.AccountId is string id && accounts.Any(a => a.AccountId == id)) return;
            accounts.Add(new(found?.AccountId ?? home, home, found?.Email, found?.Plan, found?.AccountId, origin, found?.PaidUntil));
        }
        foreach (var folder in folders) Add(folder, CodexOrigin.Folder);
        try
        {
            if (File.Exists(routerState) && new FileInfo(routerState).Length < 1_000_000)
                foreach (var home in RouterHomes(File.ReadAllText(routerState))) Add(home, CodexOrigin.Router);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return accounts;
    }

    // Accounts to read, given the user's explicit choices; the main sign-in is always tracked.
    public static List<CodexAccount> Tracked(IEnumerable<CodexAccount> accounts, ICollection<string> on, ICollection<string> off) =>
        accounts.Where(account => account.Key is not string key || on.Contains(key) || (!off.Contains(key) && account.TrackedByDefault)).ToList();
}
