using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace UsageWidget;

public sealed class CodexService
{
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    public string? Status { get; private set; }
    public bool Refreshing { get; private set; }

    public async Task RefreshAsync()
    {
        if (!await refreshLock.WaitAsync(0)) return;
        Refreshing = true;
        Process? process = null;
        try
        {
            string? executable = FindCodex();
            if (executable is null) { Status = "Codex CLI not found"; return; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            process = new Process { StartInfo = new ProcessStartInfo(executable, "app-server --listen stdio://") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = DataStore.DirectoryPath
            } };
            process.Start();
            // Drain stderr without recording account data, credentials, or session contents.
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await Send(process, new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "ai_usage_widget", title = "AI Usage Widget", version = "1.0.0" } } });
            await ReadResponse(process, 1, timeout.Token);
            await Send(process, new { method = "initialized", @params = new { } });
            await Send(process, new { id = 2, method = "account/rateLimits/read", @params = new { } });
            var result = await ReadResponse(process, 2, timeout.Token);
            var snapshot = Parse(result, DateTimeOffset.UtcNow);
            if (snapshot.Windows.Count == 0) { Status = "No subscription windows returned"; return; }
            DataStore.Write("codex.json", snapshot);
            Status = null;
        }
        catch (OperationCanceledException) { Status = "Refresh timed out · retrying automatically"; }
        catch (Exception ex) { Status = ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ? "Sign in to Codex CLI to connect" : "Refresh failed · showing last reading"; DataStore.Log("Codex refresh: " + ex.GetType().Name); }
        finally
        {
            if (process is not null) {
                try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
            Refreshing = false;
            refreshLock.Release();
        }
    }

    private static async Task Send(Process process, object value) {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value));
        await process.StandardInput.FlushAsync();
    }
    private static async Task<JsonElement> ReadResponse(Process process, int id, CancellationToken token)
    {
        while (await process.StandardOutput.ReadLineAsync(token) is string line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var number) || number != id) continue;
            if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.TryGetProperty("message", out var message) ? message.GetString() : "Codex returned an error");
            if (root.TryGetProperty("result", out var result)) return result.Clone();
        }
        throw new IOException("Codex disconnected");
    }

    public static UsageSnapshot Parse(JsonElement result, DateTimeOffset updated)
    {
        var windows = new List<UsageWindow>();
        string? plan = null;
        void AddBucket(JsonElement bucket, string id, bool multiple)
        {
            if (bucket.ValueKind != JsonValueKind.Object) return;
            if (bucket.TryGetProperty("planType", out var p) && p.ValueKind == JsonValueKind.String) plan ??= p.GetString();
            string name = bucket.TryGetProperty("limitName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? id : id;
            foreach (string key in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percentage) || !double.IsFinite(percentage)) continue;
                int? minutes = w.TryGetProperty("windowDurationMins", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out int duration) ? duration : null;
                long? reset = w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out long at) && at is > 0 and < 253402300800 ? at : null;
                string label = UsageFormatting.WindowLabel(minutes, key == "primary" ? "Current window" : "Additional window");
                if (multiple) label = name + " · " + label;
                windows.Add(new UsageWindow(id + ":" + key, label, Math.Clamp(percentage, 0, 100), reset, minutes));
            }
        }
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object && buckets.EnumerateObject().Any()) {
            int count = buckets.EnumerateObject().Count();
            foreach (var item in buckets.EnumerateObject()) AddBucket(item.Value, item.Name, count > 1);
        }
        if (windows.Count == 0 && result.TryGetProperty("rateLimits", out var legacy)) AddBucket(legacy, "codex", false);
        return new(updated, "Codex account", windows, plan);
    }

    private static string? FindCodex()
    {
        string npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
        foreach (string relative in new[] { "node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe", "vendor/x86_64-pc-windows-msvc/codex/codex.exe", "vendor/x86_64-pc-windows-msvc/bin/codex.exe" }) {
            string candidate = Path.Combine(npm, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
            string candidate = Path.Combine(dir.Trim('"'), "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
