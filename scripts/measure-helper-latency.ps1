# Requires PowerShell 7+: these scripts read the helper stdout with
# ReadLineAsync, which never completes against a redirected pipe on the
# Windows PowerShell 5.1 host and would otherwise stall until the timeout.
#Requires -Version 7.0

param(
    [ValidateRange(1000, 30000)]
    [int]$TimeoutMilliseconds = 10000
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
$stderrTask = $process.StandardError.ReadToEndAsync()

function Read-Message([string]$stage = 'helper output') {
    $readTask = $process.StandardOutput.ReadLineAsync()
    if (-not $readTask.Wait($TimeoutMilliseconds)) { throw "Timed out waiting for $stage." }
    $line = $readTask.GetAwaiter().GetResult()
    if ($null -eq $line) { throw 'Helper output closed before the latency test completed.' }
    return $line | ConvertFrom-Json
}

function Send-Command([int]$id, [string]$name, [Nullable[double]]$amount = $null) {
    $command = [ordered]@{ type = 'command'; id = $id; name = $name }
    if ($null -ne $amount) { $command.amount = [double]$amount }
    $process.StandardInput.WriteLine(($command | ConvertTo-Json -Compress))
    $process.StandardInput.Flush()
}

function Wait-ForCommand([int]$id, [scriptblock]$statePredicate) {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $ackMs = $null
    $stateMs = $null
    $state = $null
    $observedVolumes = [System.Collections.Generic.List[string]]::new()
    while ($null -eq $ackMs -or $null -eq $stateMs) {
        try {
            $message = Read-Message "command $id feedback"
        }
        catch {
            $ackDescription = if ($null -eq $ackMs) { 'not seen' } else { "$([math]::Round($ackMs, 1)) ms" }
            $volumes = if ($observedVolumes.Count -eq 0) { 'none' } else { $observedVolumes -join ', ' }
            throw "Command $id timed out; ack: $ackDescription; observed audio states: $volumes."
        }
        if ($message.type -eq 'ack' -and $message.id -eq $id) {
            if (-not $message.ok) { throw "Command $id failed: $($message.error)" }
            $ackMs = $timer.Elapsed.TotalMilliseconds
        }
        if ($message.type -eq 'state') {
            $observedVolumes.Add("$($message.audio.volumePercent)%@r$($message.revision)")
            if (& $statePredicate $message) {
                $stateMs = $timer.Elapsed.TotalMilliseconds
                $state = $message
            }
        }
    }
    return [pscustomobject]@{ AckMs = $ackMs; StateMs = $stateMs; State = $state }
}

try {
    $hello = Read-Message 'helper hello'
    if ($hello.type -ne 'hello' -or $hello.protocol -ne 1) { throw 'Invalid helper hello.' }

    Send-Command 1 'refresh'
    $initialResult = Wait-ForCommand 1 { param($message) $message.audio.available }
    $initialVolume = [int]$initialResult.State.audio.volumePercent
    $delta = if ($initialVolume -le 97) { 2.0 } else { -2.0 }
    Send-Command 2 'volume' $delta
    $changed = if ($delta -gt 0) {
        Wait-ForCommand 2 { param($message) [int]$message.audio.volumePercent -gt $initialVolume }
    } else {
        Wait-ForCommand 2 { param($message) [int]$message.audio.volumePercent -lt $initialVolume }
    }

    Send-Command 3 'volume' (-$delta)
    $restored = Wait-ForCommand 3 { param($message) [math]::Abs([int]$message.audio.volumePercent - $initialVolume) -le 1 }

    $process.StandardInput.Close()
    if (-not $process.WaitForExit($TimeoutMilliseconds)) { throw 'Helper did not exit after stdin closed.' }
    $stderr = $stderrTask.GetAwaiter().GetResult().Trim()
    if ($process.ExitCode -ne 0) { throw "Helper exited with code $($process.ExitCode): $stderr" }

    [pscustomobject]@{
        Version = $hello.version
        InitialVolume = $initialVolume
        ChangedVolume = [int]$changed.State.audio.volumePercent
        ChangeAckMs = [math]::Round($changed.AckMs, 1)
        ChangeStateMs = [math]::Round($changed.StateMs, 1)
        RestoredVolume = [int]$restored.State.audio.volumePercent
        RestoreAckMs = [math]::Round($restored.AckMs, 1)
        RestoreStateMs = [math]::Round($restored.StateMs, 1)
        Stderr = $stderr
    }
}
finally {
    if (-not $process.HasExited) {
        try { $process.StandardInput.Close() } catch { }
        if (-not $process.WaitForExit(3000)) { $process.Kill() }
    }
    $process.Dispose()
}
