using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace UsageWidget;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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

    private async Task RunCheckAsync()
    {
        var service = new CodexService();
        var claude = new ClaudeService();
        try
        {
            await Task.WhenAll(service.RefreshAsync(), claude.RefreshAsync(true));
            var state = DataStore.ReadSnapshot("codex.json");
            var claudeState = DataStore.ReadSnapshot(ClaudeService.SnapshotName);
            var report = new { ok = state?.Windows.Count > 0 && claudeState?.Windows.Count > 0, windows = state?.Windows, error = service.Status,
                claude = new { windows = claudeState?.Windows, error = claude.Status } };
            File.WriteAllText(Path.Combine(DataStore.DirectoryPath, "check-result.json"), System.Text.Json.JsonSerializer.Serialize(report, DataStore.JsonOptions));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(DataStore.DirectoryPath, "check-result.json"), System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
        finally { Shutdown(); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
