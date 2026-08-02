param(
    [ValidateRange(3, 30)]
    [int]$ObservationSeconds = 10,
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
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$observations = [System.Collections.Generic.List[object]]::new()
$nextCommandId = 1

function Read-Message {
    $readTask = $process.StandardOutput.ReadLineAsync()
    if (-not $readTask.Wait($TimeoutMilliseconds)) { throw 'Timed out waiting for helper output.' }
    $line = $readTask.GetAwaiter().GetResult()
    if ($null -eq $line) { throw 'Helper output closed before the artwork test completed.' }
    return $line | ConvertFrom-Json
}

function Get-IdentityPrefix($media) {
    $parts = @($media.title, $media.artist, $media.album) | ForEach-Object {
        if ($null -eq $_) { '' } else { ([string]$_).Trim().ToUpperInvariant() }
    }
    $identity = $parts -join [char]0x1f
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($identity)) }
    finally { $sha.Dispose() }
    $hex = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    return $hex.Substring(0, 12)
}

function Record-State($message) {
    $prefix = Get-IdentityPrefix $message.media
    $artworkKey = [string]$message.media.artworkKey
    $valid = [string]::IsNullOrWhiteSpace($artworkKey) -or $artworkKey.StartsWith("$prefix-", [StringComparison]::Ordinal)
    $observations.Add([pscustomobject]@{
        ElapsedMs = [math]::Round($clock.Elapsed.TotalMilliseconds)
        Revision = $message.revision
        Title = [string]$message.media.title
        Artist = [string]$message.media.artist
        Album = [string]$message.media.album
        ArtworkKey = $artworkKey
        IdentityKeyValid = $valid
    })
    if (-not $valid) { throw "Artwork key '$artworkKey' did not match '$($message.media.title)'." }
}

function Send-Command([string]$name) {
    $id = $script:nextCommandId++
    $command = [ordered]@{ type = 'command'; id = $id; name = $name }
    $process.StandardInput.WriteLine(($command | ConvertTo-Json -Compress))
    $process.StandardInput.Flush()
    $latest = $null
    $acknowledged = $false
    while (-not $acknowledged) {
        $message = Read-Message
        if ($message.type -eq 'state') {
            Record-State $message
            $latest = $message
        }
        if ($message.type -eq 'ack' -and $message.id -eq $id) {
            if (-not $message.ok) { throw "Command '$name' failed: $($message.error)" }
            $acknowledged = $true
        }
    }
    return $latest
}

function Poll-Until([scriptblock]$predicate) {
    $deadline = [DateTimeOffset]::UtcNow + [TimeSpan]::FromSeconds($ObservationSeconds)
    $latest = $null
    do {
        Start-Sleep -Milliseconds 500
        $state = Send-Command 'refresh'
        if ($null -ne $state) { $latest = $state }
        if ($null -ne $latest -and (& $predicate $latest)) { return $latest }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    return $latest
}

try {
    $hello = Read-Message
    if ($hello.type -ne 'hello' -or $hello.protocol -ne 1) { throw 'Invalid helper hello.' }

    $initial = Send-Command 'refresh'
    if ($null -eq $initial -or [string]::IsNullOrWhiteSpace([string]$initial.media.title))
        { throw 'No current Apple Music track was available for the artwork test.' }
    $initialTitle = [string]$initial.media.title

    Send-Command 'next' | Out-Null
    $next = Poll-Until {
        param($state)
        -not [string]::Equals([string]$state.media.title, $initialTitle, [StringComparison]::Ordinal) -and
        -not [string]::IsNullOrWhiteSpace([string]$state.media.artworkKey)
    }

    Send-Command 'previous' | Out-Null
    $restored = Poll-Until {
        param($state)
        [string]::Equals([string]$state.media.title, $initialTitle, [StringComparison]::Ordinal) -and
        -not [string]::IsNullOrWhiteSpace([string]$state.media.artworkKey)
    }

    $process.StandardInput.Close()
    if (-not $process.WaitForExit($TimeoutMilliseconds)) { throw 'Helper did not exit after stdin closed.' }
    $stderr = $stderrTask.GetAwaiter().GetResult().Trim()
    if ($process.ExitCode -ne 0) { throw "Helper exited with code $($process.ExitCode): $stderr" }

    [pscustomobject]@{
        Version = $hello.version
        InitialTitle = $initialTitle
        NextTitle = [string]$next.media.title
        NextArtworkReady = -not [string]::IsNullOrWhiteSpace([string]$next.media.artworkKey)
        RestoredTitle = [string]$restored.media.title
        RestoredArtworkReady = -not [string]::IsNullOrWhiteSpace([string]$restored.media.artworkKey)
        InvalidIdentityKeys = @($observations | Where-Object { -not $_.IdentityKeyValid }).Count
        ObservationCount = $observations.Count
        Stderr = $stderr
    }
    $observations | Select-Object ElapsedMs,Revision,Title,Album,ArtworkKey,IdentityKeyValid
}
finally {
    if (-not $process.HasExited) {
        try { $process.StandardInput.Close() } catch { }
        if (-not $process.WaitForExit(3000)) { $process.Kill() }
    }
    $process.Dispose()
}
