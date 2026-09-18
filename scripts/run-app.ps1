<#
.SYNOPSIS
    Build and/or launch the TaskbarLyrics overlay.

.DESCRIPTION
    Convenience wrapper so the app can be started without remembering the long
    bin path. Builds on demand, stops any running instance first (the executable
    is file-locked while running), then optionally starts it.

.PARAMETER NoBuild
    Skip the build and launch the existing binary.

.PARAMETER StopOnly
    Stop a running instance and exit, releasing the executable lock.

.PARAMETER Diag
    Launch with file logging enabled at %TEMP%\taskbar-lyrics-diag.log.

.PARAMETER Composite
    Pin the transparency mode: "alpha" (per-pixel, best quality) or "colorkey"
    (opaque window + colour key, for systems where alpha misbehaves).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\run-app.ps1
    powershell -ExecutionPolicy Bypass -File scripts\run-app.ps1 -Diag
    powershell -ExecutionPolicy Bypass -File scripts\run-app.ps1 -StopOnly
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$StopOnly,
    [switch]$Diag,
    [ValidateSet('alpha', 'colorkey')]
    [string]$Composite
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\TaskbarLyrics.App\bin\Release\net8.0-windows10.0.19041.0\TaskbarLyrics.exe'
$log = Join-Path $env:TEMP 'taskbar-lyrics-diag.log'

function Stop-Running {
    $procs = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue
    if ($procs) {
        Write-Host "Stopping $($procs.Count) running instance(s)..."
        $procs | Stop-Process -Force
        # The exe stays locked briefly after termination.
        for ($i = 0; $i -lt 20; $i++) {
            Start-Sleep -Milliseconds 250
            if (-not (Get-Process TaskbarLyrics -ErrorAction SilentlyContinue)) { break }
        }
    }
}

Stop-Running

if ($StopOnly) {
    Write-Host 'Stopped.'
    return
}

if (-not $NoBuild) {
    Write-Host 'Building solution (Release)...'
    & dotnet build (Join-Path $root 'TaskbarLyrics.sln') -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

if (-not (Test-Path $exe)) { throw "Executable not found: $exe`nRun without -NoBuild first." }

if ($Diag) {
    Remove-Item $log -ErrorAction SilentlyContinue
    $env:TBL_DIAG = '1'
    Write-Host "Diagnostics will be written to $log"
}

if ($Composite) {
    $env:TBL_COMPOSITE = $Composite
    Write-Host "Compositing mode pinned to '$Composite'"
}

Write-Host "Starting $exe"
Start-Process -FilePath $exe
Start-Sleep -Seconds 2

$p = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue
if ($p) {
    Write-Host "Running (pid $($p.Id)). Look for lyrics in the taskbar, left of the clock."
    Write-Host 'Tray icon: left-click toggles visibility; right-click opens the menu.'
} else {
    Write-Warning 'The process exited immediately. Re-run with -Diag and inspect the log.'
}

# Do not leak the diagnostic switches into the user's shell session.
$env:TBL_DIAG = $null
$env:TBL_COMPOSITE = $null
