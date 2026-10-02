# Read-only sampling; does not collect garbage, trim memory, or change the widget.
[CmdletBinding()]
param(
    [ValidateRange(1, 10000)][int]$Samples = 61,
    [ValidateRange(1, 3600)][int]$IntervalSeconds = 5,
    [int]$ProcessId = 0
)

$ErrorActionPreference = 'Stop'
if ($ProcessId) {
    $taskProcess = Get-Process -Id $ProcessId
} else {
    $taskProcesses = @(Get-Process AIUsageWidget)
    if ($taskProcesses.Count -ne 1) { throw 'Specify -ProcessId when multiple widget processes are running.' }
    $taskProcess = $taskProcesses[0]
}
$taskClock = [Diagnostics.Stopwatch]::StartNew()
$taskCpu = $taskProcess.TotalProcessorTime.TotalMilliseconds
$taskRows = for ($sample = 0; $sample -lt $Samples; $sample++) {
    $taskProcess.Refresh()
    if ($taskProcess.HasExited) { throw 'The sampled process exited; restart the measurement.' }
    $taskPerf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($taskProcess.Id)"
    if ($null -eq $taskPerf) { throw 'Windows did not return memory counters for the sampled process.' }
    [pscustomobject]@{
        ProcessId = $taskProcess.Id
        Seconds = [math]::Round($taskClock.Elapsed.TotalSeconds, 1)
        PrivateWorkingMiB = [math]::Round($taskPerf.WorkingSetPrivate / 1MB, 2)
        WorkingMiB = [math]::Round($taskPerf.WorkingSet / 1MB, 2)
        PrivateCommitMiB = [math]::Round($taskPerf.PrivateBytes / 1MB, 2)
        CpuMilliseconds = [math]::Round($taskProcess.TotalProcessorTime.TotalMilliseconds - $taskCpu, 1)
        Handles = $taskProcess.HandleCount
    }
    if ($sample -lt $Samples - 1) { Start-Sleep -Seconds $IntervalSeconds }
}
$taskRows | ConvertTo-Json
