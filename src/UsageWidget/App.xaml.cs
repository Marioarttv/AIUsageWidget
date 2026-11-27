using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace UsageWidget;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Screenshots from built-in sample data: no widget data, network or credentials.
        if (Option(e.Args, "--render-demo") is string demo)
        {
            int code = 0;
            try { foreach (var path in DemoRender.Run(Path.GetFullPath(demo))) Console.WriteLine(path); }
            catch (Exception ex) { code = 1; File.WriteAllText(Path.Combine(Path.GetFullPath(demo), "render-error.txt"), ex.ToString()); }
            Shutdown(code);
            return;
        }
        DataStore.EnsureDirectory();
        DispatcherUnhandledException += (_, args) => {
            DataStore.Log("UI error: " + args.Exception.GetType().Name);
            args.Handled = true;
        };
        if (e.Args.Contains("--check"))
        {
            _ = RunCheckAsync();
            return;
        }
        if (Option(e.Args, "--check-antigravity") is string cli)
        {
            _ = Task.Run(() => ProbeAntigravity(cli, e.Args.SkipWhile(a => a != "--check-antigravity").Skip(2).FirstOrDefault()))
                .ContinueWith(task => Dispatcher.Invoke(() => Shutdown(task.IsFaulted ? 1 : task.Result)));
            return;
        }
        if (RelaunchOutsidePackage()) { Shutdown(); return; }
        instance = new Mutex(true, "Local\\AIUsageWidget.v1", out bool created);
        if (!created) {
            DataStore.Write("request.json", new { action = e.Args.Contains("--desktop") ? "desktop" : e.Args.Contains("--show") ? "reveal" : "show", id = Guid.NewGuid().ToString("N") });
            Shutdown(); return;
        }
        // The small widget uses software rendering rather than a hardware rendering target.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        MainWindow = new MainWindow();
        MainWindow.Show();
        DataStore.Log("Widget started: visible=" + MainWindow.IsVisible);
    }

    private static string? Option(string[] args, string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() is { Length: > 0 } value && !value.StartsWith("--") ? value : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    // Apps started by a packaged app (for example the Codex desktop app) inherit its file and registry virtualization, which hides
    // settings, the Claude status-line feed, and Start with Windows from the rest of the system. Explorer starts a clean copy instead.
    private static bool RelaunchOutsidePackage()
    {
        int length = 0;
        if (GetCurrentPackageFullName(ref length, null) == 15700) return false; // APPMODEL_ERROR_NO_PACKAGE
        var marker = Path.Combine(DataStore.DirectoryPath, "relaunch.txt");
        try
        {
            // A relaunch that is still packaged would loop; the marker is only visible inside the same package.
            if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromMinutes(1)) return false;
            File.WriteAllText(marker, "");
            Process.Start(new ProcessStartInfo("explorer.exe", '"' + Environment.ProcessPath + '"') { UseShellExecute = false });
            DataStore.Log("Relaunching outside the parent app's package");
            return true;
        }
        catch (Exception ex) { DataStore.Log("Relaunch outside package: " + ex.GetType().Name); return false; }
    }

    // A live connection check of every tracked service, without opening the widget. Results: check-result.json; exit code 1 on failure.
    private async Task RunCheckAsync()
    {
        string result = Path.Combine(DataStore.DirectoryPath, "check-result.json");
        int code = 1;
        try
        {
            var settings = DataStore.Read<WidgetSettings>("settings.json") ?? new();
            var accounts = CodexAccounts.Tracked(CodexAccounts.Discover(settings.CodexFolders), settings.CodexAccountsOn, settings.CodexAccountsOff);
            var claude = new ClaudeService();
            var claudeTask = claude.RefreshAsync(true);
            var codex = new List<CodexCheck>();
            bool ok = true;
            foreach (var account in accounts)
            {
                var service = new CodexService(account.Home, account.Source.SnapshotName);
                await service.RefreshAsync();
                var snapshot = DataStore.ReadSnapshot(service.SnapshotName);
                ok &= service.Status is null && snapshot?.Windows.Count > 0;
                codex.Add(new(account.Email ?? account.Name, snapshot?.Plan ?? account.Plan, account.Origin.ToString(), snapshot?.Windows.Count ?? 0, account.PaidUntil, service.Status));
            }
            var antigravity = new AntigravityService();
            var cli = AntigravityService.FindExecutable();
            bool readAntigravity = cli is not null && settings.ShowAntigravity;
            if (readAntigravity) await antigravity.RefreshAsync(true);
            var gemini = readAntigravity ? DataStore.ReadSnapshot(AntigravityService.SnapshotName) : null;
            if (readAntigravity) ok &= antigravity.Status is null && gemini?.Windows.Count > 0;
            await claudeTask;
            var claudeState = DataStore.ReadSnapshot(ClaudeService.SnapshotName);
            ok &= claude.Status is null && claudeState?.Windows.Count > 0;
            var main = DataStore.ReadSnapshot("codex.json");
            var report = new {
                ok,
                // The main ChatGPT sign-in, as earlier versions reported it.
                windows = main?.Windows, error = codex.FirstOrDefault()?.Error,
                claude = new { windows = claudeState?.Windows, error = claude.Status },
                codexAccounts = codex,
                antigravity = new { installed = cli is not null, shown = settings.ShowAntigravity, path = cli?.Path, managedByT3 = cli?.ManagedByT3,
                    windows = gemini?.Windows, error = readAntigravity ? antigravity.Status : null },
            };
            File.WriteAllText(result, JsonSerializer.Serialize(report, DataStore.JsonOptions));
            code = ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(result, JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
        finally { Shutdown(code); }
    }

    // `--check-antigravity <cli> [result.json]`: runs one Antigravity reading with the given CLI in a fresh scratch folder and
    // records how it ended, how long it took and how many processes ran in its job. Used by CI against the real CLI, signed out.
    private static int ProbeAntigravity(string cli, string? output)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "aiusage-agy-" + Guid.NewGuid().ToString("N"));
        var watch = Stopwatch.StartNew();
        object report;
        int code;
        try
        {
            int processes = -1;
            var outcome = AntigravityService.Read(Path.GetFullPath(cli), Path.Combine(scratch, "workspace"), Path.Combine(scratch, "path"), TimeSpan.FromSeconds(35), total => processes = total);
            report = new { outcome = outcome.Kind.ToString(), message = outcome.Message, windows = outcome.Snapshot?.Windows.Count, elapsedMs = watch.ElapsedMilliseconds,
                jobProcesses = processes, pathEntries = Directory.Exists(Path.Combine(scratch, "path")) ? Directory.GetFileSystemEntries(Path.Combine(scratch, "path")).Length : -1 };
            code = 0;
        }
        catch (Exception ex) { report = new { outcome = "Exception", message = ex.GetType().Name + ": " + ex.Message, elapsedMs = watch.ElapsedMilliseconds }; code = 1; }
        finally { try { Directory.Delete(scratch, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        File.WriteAllText(output ?? Path.Combine(DataStore.DirectoryPath, "check-antigravity.json"), JsonSerializer.Serialize(report, DataStore.JsonOptions));
        return code;
    }

    private sealed record CodexCheck(string Account, string? Plan, string Origin, int WindowCount, DateTimeOffset? PaidUntil, string? Error);

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
