using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using UsageWidget;

// Pure logic shared by the Windows CoreTests and the cross-platform LogicTests: pools, accounts, plan dates, Antigravity.
internal static class LogicTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static UsageWindow W(string key, double used, int minutes, string? pool = null, int reset = 3600) =>
        new(key, key, used, Now.AddSeconds(reset).ToUnixTimeSeconds(), minutes, pool);

    internal static void Run()
    {
        Pools();
        Antigravity();
        AntigravityDiscovery();
        Accounts();
        PlanDates();
        Readings();
    }

    private static void Pools()
    {
        var session = W("session", 0, 300); var weekly = W("weekly", 100, 10080, reset: 86400);
        var spent = new UsageSnapshot(Now, "test", [session, weekly]);
        Check(spent.Blocker(session, Now) == weekly && spent.Available(session, Now) == 0 && spent.Available(weekly, Now) == 0,
            "A used-up weekly limit must end the 5-hour session.");
        Check(UsageFormatting.Blocked(weekly, Now) == "Weekly used up · 1d 0h", "The blocked caption must name the limit and when it resets.");
        var nearly = new UsageSnapshot(Now, "test", [W("session", 100, 300), W("weekly", 99.5, 10080)]);
        Check(nearly.Available(nearly.Windows[0], Now) == 0 && Math.Abs(nearly.Available(nearly.Windows[1], Now) - 0.5) < 0.001,
            "99.5% used shows as 0% and counts as used up; a spent session never limits the weekly window.");
        var spentSession = new UsageSnapshot(Now, "test", [W("session", 100, 300), W("weekly", 40, 10080)]);
        Check(spentSession.Available(spentSession.Windows[1], Now) == 60, "A spent session must not zero the weekly window.");
        var expired = new UsageSnapshot(Now, "test", [W("session", 10, 300), W("weekly", 100, 10080, reset: -5)]);
        Check(expired.Blocker(expired.Windows[0], Now) is null && expired.Available(expired.Windows[0], Now) == 90,
            "An expired weekly limit awaits a reading instead of blocking.");
        var groups = new UsageSnapshot(Now, "test", [W("g5", 0, 300, "Gemini"), W("gw", 100, 10080, "Gemini"), W("c5", 0, 300, "Claude"), W("cw", 20, 10080, "Claude")]);
        Check(groups.Available(groups.Windows[0], Now) == 0 && groups.Available(groups.Windows[2], Now) == 100, "Pools must limit only their own windows.");

        static UsageSnapshot Claude(string json) { using var doc = JsonDocument.Parse(json); return ClaudeService.Parse(doc.RootElement, Now); }
        var model = Claude("""{"five_hour":{"utilization":20},"seven_day":{"utilization":30},"seven_day_opus":{"utilization":100}}""");
        Check(model.Windows.Select(w => model.Available(w, Now)).SequenceEqual([80.0, 70.0, 0.0]), "A spent model week must not zero the session or the account week.");
        var account = Claude("""{"five_hour":{"utilization":20},"seven_day":{"utilization":100},"seven_day_opus":{"utilization":10}}""");
        Check(account.Windows.Select(w => account.Available(w, Now)).SequenceEqual([0.0, 0.0, 0.0]), "A spent Claude week must zero every window.");
        static UsageSnapshot Codex(string json) { using var doc = JsonDocument.Parse(json); return CodexService.Parse(doc.RootElement, Now); }
        var single = Codex("""{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":300},"secondary":{"usedPercent":100,"windowDurationMins":10080}}}""");
        Check(single.Available(UsageFormatting.Primary(single)!, Now) == 0, "A spent Codex week must zero its session.");
        var buckets = Codex("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":0,"windowDurationMins":300}},"other":{"primary":{"usedPercent":100,"windowDurationMins":10080}}}}""");
        Check(buckets.Available(UsageFormatting.Primary(buckets)!, Now) == 100, "Codex limit buckets must be separate pools.");
        Check(UsageFormatting.Headline(spent, Now) == 0 && UsageFormatting.Headline(spent with { Windows = [session with { ResetsAt = Now.AddSeconds(-5).ToUnixTimeSeconds() }, weekly] }, Now) == 0,
            "The headline must show 0 under a spent week, even once the session itself has expired.");
        Check(JsonSerializer.Deserialize<UsageSnapshot>("""{"updatedAt":"2026-11-20T10:00:00Z","source":"x","windows":[{"key":"a","label":"Weekly","usedPercent":5,"windowMinutes":10080}]}""", DataStore.JsonOptions)!.Windows[0].Pool is null,
            "Snapshots saved before pools must still load.");

        // Low and reset alerts skip blocked windows; reset alerts skip never-used windows.
        var ledger = new ResetAlertLedger();
        UsageSnapshot Reading(double used, int reset, int at) => new(Now.AddSeconds(at), "test", [
            new("s", "5-hour session", used, Now.AddSeconds(reset).ToUnixTimeSeconds(), 300), new("w", "Weekly", 100, Now.AddDays(1).ToUnixTimeSeconds(), 10080)]);
        ledger.Observe(UsageProvider.Codex, Reading(95, 5, 0), true, Now);
        ledger.Observe(UsageProvider.Codex, Reading(0, 18000, 10), true, Now.AddSeconds(10));
        Check(ledger.Pending.Count == 0, "No reset alert for a session the spent week still blocks.");
        var unused = new ResetAlertLedger();
        unused.Observe(UsageProvider.Antigravity, new(Now, "t", [new("s", "5-hour session", 0, Now.AddSeconds(5).ToUnixTimeSeconds(), 300)]), true, Now);
        unused.Observe(UsageProvider.Antigravity, new(Now.AddSeconds(10), "t", [new("s", "5-hour session", 0, Now.AddSeconds(18010).ToUnixTimeSeconds(), 300)]), true, Now.AddSeconds(10));
        Check(unused.Pending.Count == 0, "An unused window whose boundary moves on is no reset.");
        var accounts = new ResetAlertLedger();
        foreach (var source in new[] { "Codex", "Codex@acct-2" })
        {
            accounts.Observe(UsageProvider.Codex, new(Now, "t", [new("s", "5-hour session", 50, Now.AddSeconds(5).ToUnixTimeSeconds(), 300)]), true, Now, source, source);
            accounts.Observe(UsageProvider.Codex, new(Now.AddSeconds(10), "t", [new("s", "5-hour session", 0, Now.AddSeconds(18010).ToUnixTimeSeconds(), 300)]), true, Now.AddSeconds(10), source, source);
        }
        Check(accounts.Pending.Count == 2 && accounts.Pending[1].Account == "Codex@acct-2" && accounts.Observations.ContainsKey("Codex:s") && accounts.Observations.ContainsKey("Codex@acct-2:s"),
            "Each ChatGPT account keeps its own reset history; the main one keeps the original key.");
        Check(Enum.GetValues<UsageProvider>().Select(p => (int)p).SequenceEqual([0, 1, 2]) && (int)UsageProvider.Antigravity == 2,
            "Providers are stored as numbers: Antigravity must be appended.");
    }

    // `antigravity -p /usage --output-format json`, as CLI 1.5.13 prints it (descriptions shortened).
    internal const string AntigravitySample = """
    {"conversation_id":"","status":"SUCCESS","response":"Gemini Models\tWeekly Limit Remaining\t100%\t2026-11-27T16:14:33Z\n","duration_seconds":0,"num_turns":0,
     "usage":{"input_tokens":0,"output_tokens":0,"total_tokens":0},
     "command":{"name":"usage","data":{"description":"Within each group, models share a weekly limit and a 5-hour limit.","groups":[
      {"name":"Gemini Models","description":"Gemini 4 Neon, Gemini 4 Hydrogen","buckets":[
        {"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.9990500807762146,"reset_time":"2026-11-27T16:14:33Z"},
        {"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.9966751933097839,"reset_time":"2026-11-20T21:14:33Z"}]},
      {"name":"Claude and GPT models","buckets":[
        {"id":"3p-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":1,"reset_time":"2026-11-27T16:24:14Z"},
        {"id":"3p-5h","name":"Five Hour Limit Remaining","window":"5h","reset_time":"2026-11-20T21:24:14Z"}]},
      {"name":"Gemini 4 Argon","buckets":[
        {"id":"gemini-argon-wtu-weekly","name":"Gemini 4 Argon Weekly Limit","window":"weekly","remaining_fraction":1,"reset_time":"2026-11-27T16:24:14Z"},
        {"id":"off","name":"Disabled","window":"5h","remaining_fraction":1,"disabled":true}]}]}}}
    """;

    private static void Antigravity()
    {
        var now = DateTimeOffset.Parse("2026-11-20T16:30:00Z", CultureInfo.InvariantCulture);
        var outcome = AntigravityCli.Outcome(AntigravitySample, now);
        Check(outcome.Kind == AntigravityResult.Reading, "A successful /usage reply must be a reading.");
        var snapshot = outcome.Snapshot!;
        Check(snapshot.Windows.Select(w => w.Label).SequenceEqual(["Gemini · 5-hour session", "Gemini · Weekly", "Claude and GPT · 5-hour session", "Claude and GPT · Weekly", "Gemini 4 Argon · Weekly"]),
            "Groups must become labelled windows, shortest first, disabled buckets left out: " + string.Join(", ", snapshot.Windows.Select(w => w.Label)));
        Check(snapshot.Windows.Select(w => w.WindowMinutes).SequenceEqual(new int?[] { 300, 10080, 300, 10080, 10080 }), "Window names must become durations.");
        Check(snapshot.Windows.Select(w => w.Pool).SequenceEqual(["Gemini Models", "Gemini Models", "Claude and GPT models", "Claude and GPT models", "Gemini 4 Argon"]), "Each model group is a pool.");
        Check(UsageFormatting.Primary(snapshot)?.Key == "gemini-5h", "The Gemini 5-hour limit leads the card and the taskbar.");
        Check(Math.Abs(UsageFormatting.Left(snapshot.Windows[0]) - 99.66751933097839) < 0.0001 && snapshot.Windows[0].ResetsAt == DateTimeOffset.Parse("2026-11-20T21:14:33Z", CultureInfo.InvariantCulture).ToUnixTimeSeconds(),
            "Fractions and reset times must survive parsing.");
        Check(UsageFormatting.Left(snapshot.Windows[2]) == 0, "The CLI omits zero values: a bucket without a fraction is used up.");

        var reordered = AntigravityCli.Outcome("""
        {"status":"SUCCESS","response":"Claude and GPT models\tFive Hour Limit Remaining\t42%\t2026-11-20T21:24:14Z\n",
         "command":{"data":{"groups":[
          {"name":"Claude and GPT models","buckets":[{"id":"3p-5h","name":"Five Hour Limit Remaining","window":"5h","reset_time":"2026-11-20T21:24:14Z"},
                                                     {"id":"3p-x","name":"Mystery","window":"5h"}]},
          {"name":"Gemini Models","buckets":[{"id":"g-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.5,"reset_time":"2026-11-20T21:14:33Z"}]}]}}}
        """, now).Snapshot!;
        Check(reordered.Windows.Select(w => w.Key).SequenceEqual(["g-5h", "3p-5h"]) && Math.Abs(UsageFormatting.Left(reordered.Windows[1]) - 42) < 0.001,
            "Gemini first; the printed table fills in a missing fraction; unknown buckets are left out.");
        // Seen from CLI 1.5.14: windows nobody used report 1970-01-01 as their reset time.
        var unused = AntigravityCli.Outcome("""{"status":"SUCCESS","command":{"data":{"groups":[{"name":"Claude and GPT models","buckets":[{"id":"3p-5h","window":"5h","remaining_fraction":1,"reset_time":"1970-01-01T00:00:00Z"}]}]}}}""", now).Snapshot!;
        Check(unused.Windows is [{ ResetsAt: null }] && UsageFormatting.Remaining(unused.Windows[0], now) == 100, "A 1970 reset time means none, not an expired window.");

        Check(AntigravityCli.Minutes("5h") == 300 && AntigravityCli.Minutes("Weekly") == 10080 && AntigravityCli.Minutes("daily") == 1440
            && AntigravityCli.Minutes("30m") == 30 && AntigravityCli.Minutes("2d") == 2880 && AntigravityCli.Minutes("monthly") is null && AntigravityCli.Minutes(null) is null,
            "Antigravity window names must map to minutes.");
        Check(AntigravityCli.ShortGroupName("Gemini 4 Argon") == "Gemini 4 Argon" && AntigravityCli.ShortGroupName("Gemini Models") == "Gemini", "Group names drop a trailing 'models'.");
        var signedOut = AntigravityCli.Outcome("""{"conversation_id":"","status":"ERROR","response":"","error":"authentication failed or timed out"}""", now);
        Check(signedOut is { Kind: AntigravityResult.Failure, Message: "Antigravity didn't answer · retrying automatically" }, "An auth error alone is retried, not a sign-in.");
        Check(AntigravityCli.Outcome("""{"status":"SUCCESS","response":"no quota summary is available for this account"}""", now).Message == "No Antigravity quota returned for this account", "An empty quota must say so.");
        Check(AntigravityCli.Outcome("Fetching…", now).Message == "Antigravity returned an invalid response", "Non-JSON output must fail cleanly.");
        Check(AntigravityCli.AsksForLogin("Authentication required. Please visit the URL to log in:\n") && !AntigravityCli.AsksForLogin("Fetching available models..."), "The login prompt must be recognised.");

        var environment = AntigravityCli.Environment(new Dictionary<string, string> {
            ["USERPROFILE"] = @"C:\Users\you", ["Path"] = @"C:\Windows\System32", ["SECRET_TOKEN"] = "x", ["SystemRoot"] = @"C:\Windows", ["TEMP"] = @"C:\Temp",
            ["HTTPS_PROXY"] = "http://proxy:8080", ["OPENAI_API_KEY"] = "sk" }, @"C:\Data\empty");
        Check(environment.Count == 6 && environment["PATH"] == @"C:\Data\empty" && environment["AGY_CLI_DISABLE_AUTO_UPDATE"] == "1" && environment["SystemRoot"] == @"C:\Windows"
            && environment["HTTPS_PROXY"] == "http://proxy:8080" && !environment.ContainsKey("SECRET_TOKEN") && !environment.ContainsKey("OPENAI_API_KEY"),
            "The CLI gets a minimal environment with an empty PATH and no auto-update: " + string.Join(", ", environment.Keys));
        Check(AntigravityCli.Arguments.Contains("/usage") && AntigravityCli.Arguments[^1] == "NUL", "No log file per refresh.");
    }

    private static void AntigravityDiscovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "agy-" + Guid.NewGuid().ToString("N"));
        try
        {
            string home = Path.Combine(root, "home"), bin = Path.Combine(root, "bin");
            void Binary(string path, bool native = true) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, native ? [(byte)'M', (byte)'Z', 0x90, 0] : Encoding.ASCII.GetBytes("@echo off\r\n")); }
            Check(AntigravityCli.Find(home, bin, "x64") is null, "Nothing installed: no CLI.");
            // A script and the Antigravity editor (an Electron app) don't qualify.
            Binary(Path.Combine(bin, "agy.exe"), native: false);
            string editor = Path.Combine(root, "Programs", "Antigravity");
            Binary(Path.Combine(editor, "Antigravity.exe"));
            File.WriteAllText(Path.Combine(editor, "resources.pak"), "");
            Check(AntigravityCli.Find(home, bin + ";" + editor, "x64") is null, "Scripts and the editor's executable are not the CLI.");
            // T3 Code's managed copy, only once its install has finished.
            string tools = Path.Combine(home, ".t3", "tools", "antigravity-acp", "win32-x64");
            Directory.CreateDirectory(tools);
            File.WriteAllText(Path.Combine(tools, "active.json"), """{"releaseId":"abc"}""");
            Binary(Path.Combine(tools, "versions", "abc", "agy.exe"));
            Check(AntigravityCli.Find(home, bin, "x64") is null, "An install still in progress isn't run.");
            File.WriteAllText(Path.Combine(tools, "versions", "abc", ".install-complete.json"), """{"releaseId":"abc","version":"1.5.18","executable":{"name":"agy.exe"}}""");
            Check(AntigravityCli.Find(home, bin, "x64") is { ManagedByT3: true } managed && managed.Path == Path.Combine(tools, "versions", "abc", "agy.exe"), "T3 Code's finished install must be found.");
            Check(AntigravityCli.Find(home, bin, "arm64")?.ManagedByT3 == true, "An x64 T3 install also serves an ARM64 PC.");
            // One on PATH comes first, and quoted PATH entries work.
            string own = Path.Combine(root, "own");
            Binary(Path.Combine(own, "antigravity.exe"));
            Check(AntigravityCli.Find(home, bin + ";\"" + own + "\"", "x64") == new AntigravityExecutable(Path.Combine(own, "antigravity.exe"), false), "A CLI on PATH comes first.");
            File.WriteAllText(Path.Combine(tools, "active.json"), """{"releaseId":"..\\abc"}""");
            Check(AntigravityCli.Find(home, bin, "x64") is null, "A release id must not leave the versions folder.");
            File.WriteAllText(Path.Combine(tools, "active.json"), """{"releaseId":"abc"}""");
            File.WriteAllText(Path.Combine(tools, "versions", "abc", ".install-complete.json"), """{"executable":{"name":"..\\..\\evil.exe"}}""");
            Check(AntigravityCli.Find(home, bin, "x64") is null, "The executable name must not leave its version folder.");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static string Base64Url(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    // A Codex home whose auth.json carries an unsigned ID token with the given claims.
    private static string Home(string root, string name, string? email, string account, string plan = "plus", string? until = null)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var auth = new Dictionary<string, object> { ["chatgpt_plan_type"] = plan, ["chatgpt_account_id"] = account };
        if (until is not null) auth["chatgpt_subscription_active_until"] = until;
        var claims = new Dictionary<string, object> { ["https://api.openai.com/auth"] = auth };
        if (email is not null) claims["email"] = email;
        string token = "e30." + Base64Url(JsonSerializer.Serialize(claims)) + ".sig";
        File.WriteAllText(Path.Combine(directory, "auth.json"), JsonSerializer.Serialize(new { auth_mode = "chatgpt", tokens = new { id_token = token, access_token = "secret", refresh_token = "secret" } }));
        return directory;
    }

    private static void Accounts()
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-accounts-" + Guid.NewGuid().ToString("N"));
        try
        {
            string main = Home(root, "main", "main@example.com", "acct-main", until: "2026-11-17T10:25:07+00:00");
            var identity = CodexAccounts.ReadIdentity(main);
            Check(identity is { Email: "main@example.com", Plan: "plus", AccountId: "acct-main" } && identity.PaidUntil == DateTimeOffset.FromUnixTimeSeconds(1_794_911_107),
                "The sign-in's identity claims and paid-period end must be read.");
            Check(CodexAccounts.ParseIdentity("""{"OPENAI_API_KEY":"sk-test"}""") is null && CodexAccounts.ReadIdentity(Path.Combine(root, "missing")) is null && CodexAccounts.ParseIdentity("not json") is null,
                "API-key sign-ins and missing or broken files have no account.");
            Check(CodexAccounts.ParseIdentity("""{"tokens":{"id_token":"bad","account_id":"acct-x"}}""") is { AccountId: "acct-x", Email: null }, "The token's account id is a fallback.");

            string second = Home(root, "router-second", "second@example.com", "acct-second", until: "2026-10-28T05:23:28.123456+00:00");
            string free = Home(root, "router-free", "free@example.com", "acct-free", plan: "free");
            string duplicate = Home(root, "router-duplicate", "main@example.com", "acct-main");
            string empty = Path.Combine(root, "router-empty"); Directory.CreateDirectory(empty);
            string folder = Home(root, "folder", "folder@example.com", "acct-folder");
            string signedOutFolder = Path.Combine(root, "folder-signed-out"); Directory.CreateDirectory(signedOutFolder);
            string state = Path.Combine(root, "state.json");
            File.WriteAllText(state, JsonSerializer.Serialize(new { version = 1, accounts = new object[] {
                new { id = "slot0", codexHome = second, enabled = true }, new { id = "slot1", codexHome = free, enabled = false },
                new { id = "slot2", codexHome = duplicate, enabled = true }, new { id = "slot3", codexHome = empty, enabled = true },
                new { id = "slot4", codexHome = second.ToUpperInvariant() + Path.DirectorySeparatorChar, enabled = true },
                new { id = "slot5", codexHome = folder, enabled = true } }, threadOwner = new { } }));
            var accounts = CodexAccounts.Discover([folder, signedOutFolder], main, state, root);
            Check(accounts.Select(a => a.Origin).SequenceEqual([CodexOrigin.Main, CodexOrigin.Folder, CodexOrigin.Folder, CodexOrigin.Router, CodexOrigin.Router]),
                "Main, then added folders, then signed-in router accounts, each listed once: " + string.Join(", ", accounts.Select(a => a.Origin + " " + a.Name)));
            Check(accounts.Select(a => a.Key).SequenceEqual([null, "acct-folder", CodexAccounts.Normalize(signedOutFolder), "acct-second", "acct-free"]),
                "Account ids identify sign-ins; a signed-out folder is keyed by its path.");
            Check(accounts[3].PaidUntil == DateTimeOffset.Parse("2026-10-28T05:23:28.123456+00:00", CultureInfo.InvariantCulture) && accounts[3].Email == "second@example.com",
                "Each account keeps its own paid-period end and email.");
            var tracked = CodexAccounts.Tracked(accounts, new HashSet<string>(), new HashSet<string>());
            Check(tracked.Select(a => a.Key).SequenceEqual([null, "acct-folder", CodexAccounts.Normalize(signedOutFolder), "acct-second"]), "Free plans are listed but off by default.");
            tracked = CodexAccounts.Tracked(accounts, new HashSet<string> { "acct-free" }, new HashSet<string> { "acct-second" });
            Check(tracked.Select(a => a.Key).SequenceEqual([null, "acct-folder", CodexAccounts.Normalize(signedOutFolder), "acct-free"]), "The user's choices override the defaults.");
            Check(CodexAccounts.Tracked(accounts, new HashSet<string>(), new HashSet<string> { "x" }).First().Key is null, "The main sign-in is always tracked.");
            Check(CodexAccounts.Discover([], Path.Combine(root, "none"), Path.Combine(root, "missing.json"), root) is [{ Origin: CodexOrigin.Main, SignedIn: false }],
                "Without any sign-in the main account is still listed.");
            Check(new UsageSource(UsageProvider.Codex).SnapshotName == "codex.json" && new UsageSource(UsageProvider.Codex, "acct-second").SnapshotName.StartsWith("codex-")
                && new UsageSource(UsageProvider.Codex, "acct-second").Id == "codex@acct-second" && new UsageSource(UsageProvider.Codex, "acct-second").AlertKey == "Codex@acct-second"
                && UsageSource.Claude.SnapshotName == ClaudeService.SnapshotName && UsageSource.Antigravity.Id == "antigravity",
                "The main Codex reading keeps codex.json; other accounts get their own files.");
            Check(CodexAccounts.Normalize("~/x", root) == Path.Combine(root, "x"), "Router paths may start with ~.");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static void PlanDates()
    {
        var invariant = CultureInfo.InvariantCulture;
        var monthly = new ManualPlanDate(new DateOnly(2026, 1, 31), PlanCycle.Monthly);
        Check(monthly.Upcoming(new DateOnly(2026, 2, 10)) == new DateOnly(2026, 2, 28) && monthly.Upcoming(new DateOnly(2026, 3, 1)) == new DateOnly(2026, 3, 31)
            && monthly.Upcoming(new DateOnly(2026, 11, 5)) == new DateOnly(2026, 11, 30), "Short months fall back to their last day; later months keep the 31st.");
        Check(monthly.Upcoming(new DateOnly(2026, 3, 31)) == new DateOnly(2026, 3, 31) && monthly.Upcoming(new DateOnly(2025, 12, 1)) == new DateOnly(2026, 1, 31),
            "The renewal day itself, and a future date, stay as they are.");
        var yearly = new ManualPlanDate(new DateOnly(2024, 6, 26), PlanCycle.Yearly);
        Check(yearly.Upcoming(new DateOnly(2026, 11, 5)) == new DateOnly(2027, 6, 26) && yearly.Label(new DateOnly(2026, 11, 5), invariant) == "Renews 26 Jun 2027",
            "Yearly dates roll forward by years, with the year shown when it isn't this one.");
        var ends = new ManualPlanDate(new DateOnly(2026, 11, 26), PlanCycle.Ends);
        Check(ends.Upcoming(new DateOnly(2026, 12, 30)) == ends.Date && ends.Label(new DateOnly(2026, 11, 5), invariant) == "Ends 26 Nov"
            && ends.Label(new DateOnly(2026, 11, 27), invariant) == "Ended 26 Nov" && ends.Lapsed(new DateOnly(2026, 11, 27)) && !ends.Lapsed(new DateOnly(2026, 11, 26)),
            "A cancelled plan ends on its day and doesn't roll forward.");
        Check(new ManualPlanDate(new DateOnly(2026, 11, 26), PlanCycle.Monthly).Caption(new DateOnly(2026, 12, 1), invariant) == "Renews on the 26th of every month · next 26 Dec"
            && ManualPlanDate.Ordinal(1) == "1st" && ManualPlanDate.Ordinal(22) == "22nd" && ManualPlanDate.Ordinal(13) == "13th" && ManualPlanDate.Ordinal(3) == "3rd",
            "The calendar caption must say what saving will show.");
        var settings = new WidgetSettings { PlanDates = { ["claude"] = yearly, ["codex@acct"] = ends } };
        var loaded = JsonSerializer.Deserialize<WidgetSettings>(JsonSerializer.Serialize(settings, DataStore.JsonOptions), DataStore.JsonOptions)!;
        Check(loaded.PlanDates["claude"] == yearly && loaded.PlanDates["codex@acct"] == ends && JsonSerializer.Serialize(settings, DataStore.JsonOptions).Contains("\"ends\"", StringComparison.OrdinalIgnoreCase),
            "Saved dates must load back, with the cycle stored by name.");
        var old = JsonSerializer.Deserialize<WidgetSettings>("""{"mode":"desktop","lowUsageAlerts":false,"alertedWindows":["Claude|five_hour|1"]}""", DataStore.JsonOptions)!;
        Check(old.Mode == "desktop" && !old.LowUsageAlerts && old.ShowAntigravity && old.PlanDates.Count == 0 && old.CodexFolders.Count == 0 && old.AlertedWindows.Count == 1,
            "Settings saved by earlier versions must keep their values and get the new defaults.");

        var cells = MonthGrid.Days(2026, 11, DayOfWeek.Monday);
        Check(cells.Length == 42 && cells.Take(6).All(c => c is null) && cells[6] == new DateOnly(2026, 11, 1) && cells.Count(c => c is not null) == 30,
            "1 November 2026 is a Sunday: six blank cells before it in a Monday-first week.");
        Check(MonthGrid.Days(2026, 11, DayOfWeek.Sunday)[0] == new DateOnly(2026, 11, 1), "Sunday-first weeks start on the 1st.");
        Check(MonthGrid.WeekdaySymbols(DayOfWeek.Monday, invariant)[0] == "Mo" && MonthGrid.WeekdaySymbols(DayOfWeek.Sunday, invariant)[0] == "Su", "Weekday headers follow the first day.");
        Check(MonthGrid.Repeat(new DateOnly(2026, 1, 31), PlanCycle.Monthly, 2026, 2) == new DateOnly(2026, 2, 28) && MonthGrid.Repeat(new DateOnly(2026, 1, 31), PlanCycle.Yearly, 2026, 2) is null
            && MonthGrid.Repeat(new DateOnly(2026, 1, 31), PlanCycle.Yearly, 2027, 1) == new DateOnly(2027, 1, 31) && MonthGrid.Repeat(new DateOnly(2026, 1, 31), PlanCycle.Ends, 2026, 2) is null,
            "The calendar marks where the date repeats.");

        var utc = TimeZoneInfo.Utc;
        var end = DateTimeOffset.FromUnixTimeSeconds(1_794_911_107); // 17 Nov 2026
        Check(UsageFormatting.PaidPeriod(end, end.AddDays(-1), utc, invariant) == "Renews or ends 17 Nov" && UsageFormatting.PaidPeriod(end, end.AddDays(1), utc, invariant) == "Paid until 17 Nov"
            && UsageFormatting.PaidPeriod(end, end.AddDays(-365), utc, invariant).EndsWith("2026"), "ChatGPT's paid-period end says renews-or-ends, then paid-until.");
    }

    private static void Readings()
    {
        var full = new UsageSnapshot(Now, "t", [W("s", 0, 300)]);
        var spent = new UsageSnapshot(Now, "t", [W("s", 0, 300), W("w", 100, 10080)]);
        var merged = TrayReading.Merge(UsageProvider.Codex, [("you@example.com", full), ("work@example.com", spent), ("new@example.com", null)], Now);
        Check(merged.Number == "50" && merged.Tooltip.Contains("3 accounts") && merged.Tooltip.Contains("you@example.com: 100%") && merged.Tooltip.Contains("work@example.com: 0%")
            && merged.Tooltip.Contains("new@example.com: —"), "The taskbar's ChatGPT number is the average of the accounts with readings; the tooltip lists each.");
        Check(TrayReading.Merge(UsageProvider.Codex, [("a", null), ("b", null)], Now).Number == "—", "No readings: a dash.");
        Check(TrayReading.Merge(UsageProvider.Codex, [("a", full)], Now) == TrayReading.Create(UsageProvider.Codex, full, Now), "One account reads as before.");
        var blocked = TrayReading.Create(UsageProvider.Claude, spent, Now);
        Check(blocked.Number == "0" && blocked.Tooltip.Contains("Weekly used up"), "A spent week shows 0 with the reason.");
        Check(TrayReading.Create(UsageProvider.Antigravity, null, Now).Tooltip.StartsWith("Antigravity"), "Antigravity readings name the service.");
    }
}
