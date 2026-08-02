param(
    [ValidateRange(1000, 30000)]
    [int]$TimeoutMilliseconds = 10000
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$helper = Join-Path $root 'com.davedev.apple-music.sdPlugin\helper\AppleMusicBridge.exe'
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
if (-not $process.Start()) { throw 'Could not start AppleMusicBridge.' }
$stderrTask = $process.StandardError.ReadToEndAsync()

function Read-Message {
    $readTask = $process.StandardOutput.ReadLineAsync()
    if (-not $readTask.Wait($TimeoutMilliseconds)) { throw 'Timed out waiting for helper output.' }
    $line = $readTask.GetAwaiter().GetResult()
    if ($null -eq $line) { throw 'Helper output closed before the smoke test completed.' }
    return $line | ConvertFrom-Json
}

try {
    $hello = Read-Message
    if ($hello.type -ne 'hello' -or $hello.protocol -ne 1) { throw 'Invalid helper hello.' }

    $process.StandardInput.WriteLine('{"type":"command","id":1,"name":"refresh"}')
    $process.StandardInput.Flush()
    $states = @()
    $acknowledged = $false
    while (-not $acknowledged -or $states.Count -lt 1) {
        $message = Read-Message
        if ($message.type -eq 'state') { $states += $message }
        if ($message.type -eq 'ack' -and $message.id -eq 1) {
            if (-not $message.ok) { throw "Refresh command failed: $($message.error)" }
            $acknowledged = $true
        }
    }

    $latest = $states[-1]
    $artworkLength = if ($null -ne $latest.media.artworkDataUri) { $latest.media.artworkDataUri.Length } else { 0 }
    if ($artworkLength -gt 40000) { throw "Artwork data URI exceeded its 40,000 character limit ($artworkLength)." }

    $process.StandardInput.Close()
    if (-not $process.WaitForExit($TimeoutMilliseconds)) { throw 'Helper did not exit after stdin closed.' }
    $stderr = $stderrTask.GetAwaiter().GetResult().Trim()
    if ($process.ExitCode -ne 0) { throw "Helper exited with code $($process.ExitCode): $stderr" }

    [pscustomobject]@{
        Version = $hello.version
        Protocol = $hello.protocol
        StateCount = $states.Count
        LatestRevision = $latest.revision
        MediaAvailable = $latest.media.available
        AudioAvailable = $latest.audio.available
        ArtworkDataUriLength = $artworkLength
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
