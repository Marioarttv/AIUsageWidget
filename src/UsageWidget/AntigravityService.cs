using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UsageWidget;

// Runs the Antigravity CLI's `/usage` command once per refresh: no shell, model turn, browser, update check or log file.
public sealed class AntigravityService
{
    public const string SnapshotName = "antigravity.json";
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;
    // When the CLI last asked for a new login. It isn't started again for 30 minutes unless ↻ is pressed, so it doesn't sit
    // waiting for a login on every refresh; the browser guard makes a retry harmless.
    private DateTimeOffset? lapsedSince;
    public string? Status { get; private set; }
    public bool Refreshing { get; private set; }
    public AntigravityExecutable? Executable { get; private set; }
    public DateTimeOffset? LapsedSince => lapsedSince;
    internal static string Workspace => Path.Combine(DataStore.DirectoryPath, "antigravity-workspace");
    internal static string EmptyPath => Path.Combine(DataStore.DirectoryPath, "antigravity-path");

    public static AntigravityExecutable? FindExecutable() =>
        AntigravityCli.Find(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("PATH"));

    public async Task RefreshAsync(bool manual = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (manual || (lapsedSince is DateTimeOffset since && now - since > TimeSpan.FromMinutes(30))) lapsedSince = null;
        if (now - lastAttempt < (manual ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2.5))) return;
        if (!await refreshLock.WaitAsync(0)) return;
        Refreshing = true;
        try
        {
            lastAttempt = now;
            var cli = Executable = await Task.Run(FindExecutable);
            if (cli is null) { Status = "Install the Antigravity CLI to connect"; return; }
            string how = cli.ManagedByT3 ? "Open Antigravity in T3 Code" : "Run " + Path.GetFileNameWithoutExtension(cli.Path) + " in a terminal";
            if (lapsedSince is not null) { Status = "Sign-in expired · " + how; return; }
            var outcome = await Task.Run(() => Read(cli.Path, Workspace, EmptyPath, TimeSpan.FromSeconds(35)));
            switch (outcome.Kind)
            {
                case AntigravityResult.Reading:
                    DataStore.Write(SnapshotName, outcome.Snapshot!);
                    Status = null;
                    break;
                case AntigravityResult.SignIn:
                    lapsedSince = DateTimeOffset.UtcNow;
                    Status = "Sign-in expired · " + how;
                    break;
                default: Status = outcome.Message; break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = "Antigravity refresh failed · showing last reading";
            DataStore.Log("Antigravity refresh: " + ex.GetType().Name);
        }
        finally
        {
            Refreshing = false;
            refreshLock.Release();
        }
    }

    // Runs the CLI and returns as soon as it answers, asks for a login, or the deadline passes.
    // processes: receives how many processes ran in the CLI's job, for checks; 1 means the CLI started nothing else.
    internal static AntigravityOutcome Read(string executable, string workspace, string emptyPath, TimeSpan deadline, Action<int>? processes = null)
    {
        // Empty private folders: the working directory, so the CLI has nothing to look at, and PATH, so it finds no programs.
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(emptyPath);
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = workspace,
        };
        foreach (var argument in AntigravityCli.Arguments) start.ArgumentList.Add(argument);
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) inherited[key] = value;
        start.Environment.Clear();
        foreach (var (key, value) in AntigravityCli.Environment(inherited, emptyPath)) start.Environment[key] = value;

        using var job = ProcessJob.Create();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Antigravity did not start");
        try
        {
            job?.Assign(process);
            process.StandardInput.Close();
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var outDone = Pump(process.StandardOutput, stdout);
            var errDone = Pump(process.StandardError, stderr);
            var until = DateTime.UtcNow + deadline;
            while (!(outDone.IsCompleted && errDone.IsCompleted))
            {
                lock (stderr) if (AntigravityCli.AsksForLogin(stderr.ToString())) return new(AntigravityResult.SignIn);
                lock (stdout) lock (stderr) if (stdout.Length + stderr.Length > 2_000_000) return new(AntigravityResult.Failure, Message: "Antigravity returned an invalid response");
                if (DateTime.UtcNow > until) return new(AntigravityResult.Failure, Message: "Refresh timed out · retrying automatically");
                Task.WaitAny([outDone, errDone], 200);
            }
            lock (stderr) if (AntigravityCli.AsksForLogin(stderr.ToString())) return new(AntigravityResult.SignIn);
            lock (stdout) return AntigravityCli.Outcome(stdout.ToString(), DateTimeOffset.UtcNow);
        }
        finally
        {
            if (job is not null) processes?.Invoke(job.TotalProcesses());
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private static Task Pump(StreamReader reader, StringBuilder into) => Task.Run(async () => {
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            lock (into) { into.Append(buffer, 0, read); if (into.Length > 2_100_000) return; }
        }
    });
}

// A Windows job object for the CLI: closing it ends the CLI and anything it started, and it may not start any other process,
// such as a browser for a sign-in.
internal sealed class ProcessJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting { public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime; public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out Accounting info, int length, IntPtr returned);
    private const uint KillOnJobClose = 0x2000, DieOnUnhandledException = 0x400, ActiveProcess = 0x8;
    // One process: the CLI itself.
    internal const bool BlockChildProcesses = true;
    private readonly SafeFileHandle handle;
    private ProcessJob(SafeFileHandle handle) => this.handle = handle;

    internal static ProcessJob? Create()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        var limits = new ExtendedLimits { Basic = new() { LimitFlags = KillOnJobClose | DieOnUnhandledException | (BlockChildProcesses ? ActiveProcess : 0), ActiveProcessLimit = 1 } };
        if (!SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<ExtendedLimits>())) { handle.Dispose(); return null; }
        return new(handle);
    }

    internal bool Assign(Process process) => AssignProcessToJobObject(handle, process.Handle);
    // Every process that has run in the job; -1 when unknown.
    internal int TotalProcesses() => QueryInformationJobObject(handle, 1, out var info, Marshal.SizeOf<Accounting>(), IntPtr.Zero) ? (int)info.TotalProcesses : -1;
    public void Dispose() => handle.Dispose();
}
