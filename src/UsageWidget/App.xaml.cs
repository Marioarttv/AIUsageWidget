using System.IO;
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
        instance = new Mutex(true, "Local\\AIUsageWidget.Mario.v1", out bool created);
        if (!created) {
            DataStore.Write("request.json", new { action = e.Args.Contains("--desktop") ? "desktop" : e.Args.Contains("--show") ? "reveal" : "show", id = Guid.NewGuid().ToString("N") });
            Shutdown(); return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
        DataStore.Log("Widget started: visible=" + MainWindow.IsVisible);
    }

    private async Task RunCheckAsync()
    {
        var service = new CodexService();
        try
        {
            await service.RefreshAsync();
            var state = DataStore.ReadSnapshot("codex.json");
            var report = new { ok = state?.Windows.Count > 0, windows = state?.Windows, error = service.Status };
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
