# Requires PowerShell 7+: these scripts read the helper stdout with
# ReadLineAsync, which never completes against a redirected pipe on the
# Windows PowerShell 5.1 host and would otherwise stall until the timeout.
#Requires -Version 7.0

param(
    [ValidateRange(10, 5000)]
    [int]$RefreshesPerBatch = 400,
    [ValidateRange(0, 64)]
    [int]$MaximumHandleGrowth = 8,
    [ValidateRange(0, 64)]
    [double]$MaximumPrivateGrowthMB = 8
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $root 'com.davedev.clef.sdPlugin\helper\ClefBridge.exe'
$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $helper
$startInfo.Arguments = '--stdio'
$startInfo.WorkingDirectory = Split-Path -Parent $helper
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
if (-not $process.Start()) { throw 'Could not start ClefBridge.' }
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()

function Get-Snapshot([string]$label) {
    $process.Refresh()
    [pscustomobject]@{
        Label = $label
        PrivateMB = [math]::Round($process.PrivateMemorySize64 / 1MB, 3)
        WorkingSetMB = [math]::Round($process.WorkingSet64 / 1MB, 3)
        Handles = $process.HandleCount
        Threads = $process.Threads.Count
        CpuSeconds = [math]::Round($process.TotalProcessorTime.TotalSeconds, 3)
    }
}

function Send-RefreshBatch([int]$firstId, [int]$count) {
    for ($offset = 0; $offset -lt $count; $offset++) {
        $id = $firstId + $offset
        $process.StandardInput.WriteLine("{`"type`":`"command`",`"id`":$id,`"name`":`"refresh`"}")
    }
    $process.StandardInput.Flush()
}

function Wait-ForQuiescence {
    $stable = 0
    $previousCpu = $process.TotalProcessorTime.TotalSeconds
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Seconds 1
        $process.Refresh()
        $currentCpu = $process.TotalProcessorTime.TotalSeconds
        if ($currentCpu - $previousCpu -lt 0.025) { $stable++ } else { $stable = 0 }
        $previousCpu = $currentCpu
        if ($stable -ge 3) { return }
    }
    throw 'ClefBridge did not become idle within 30 seconds.'
}

try {
    Start-Sleep -Seconds 2
    Send-RefreshBatch 1 25
    Wait-ForQuiescence
    $baseline = Get-Snapshot 'warmed'

    Send-RefreshBatch 1000 $RefreshesPerBatch
    Wait-ForQuiescence
    $batchOne = Get-Snapshot 'batch-one'

    Send-RefreshBatch 10000 $RefreshesPerBatch
    Wait-ForQuiescence
    $batchTwo = Get-Snapshot 'batch-two'

    $process.StandardInput.Close()
    if (-not $process.WaitForExit(10000)) { throw 'ClefBridge did not exit after stdin closed.' }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $acknowledgements = ([regex]::Matches($stdout, '"type":"ack"')).Count
    $expectedAcknowledgements = 25 + (2 * $RefreshesPerBatch)
    if ($acknowledgements -ne $expectedAcknowledgements) {
        throw "Expected $expectedAcknowledgements acknowledgements, received $acknowledgements."
    }

    $privateGrowth = $batchTwo.PrivateMB - $batchOne.PrivateMB
    $handleGrowth = $batchTwo.Handles - $batchOne.Handles
    if ($privateGrowth -gt $MaximumPrivateGrowthMB) {
        throw "Private memory grew by $([math]::Round($privateGrowth, 3)) MB in the repeated batch."
    }
    if ($handleGrowth -gt $MaximumHandleGrowth) {
        throw "Handle count grew by $handleGrowth in the repeated batch."
    }

    $baseline
    $batchOne
    $batchTwo
    [pscustomobject]@{
        Label = 'batch-two-delta'
        PrivateMB = [math]::Round($batchTwo.PrivateMB - $batchOne.PrivateMB, 3)
        WorkingSetMB = [math]::Round($batchTwo.WorkingSetMB - $batchOne.WorkingSetMB, 3)
        Handles = $batchTwo.Handles - $batchOne.Handles
        Threads = $batchTwo.Threads - $batchOne.Threads
        CpuSeconds = [math]::Round($batchTwo.CpuSeconds - $batchOne.CpuSeconds, 3)
    }
    [pscustomobject]@{
        Acknowledgements = $acknowledgements
        ExpectedAcknowledgements = $expectedAcknowledgements
        Stderr = $stderr.Trim()
    }
}
finally {
    if (-not $process.HasExited) {
        try { $process.StandardInput.Close() } catch { }
        if (-not $process.WaitForExit(3000)) { $process.Kill() }
    }
    $process.Dispose()
}
