<#
.SYNOPSIS
    Publish TaskbarLyrics as a distributable folder.

.DESCRIPTION
    Produces publish\TaskbarLyrics\ containing a runnable TaskbarLyrics.exe plus
    its resources, and a ZIP next to it.

.PARAMETER SelfContained
    Bundle the .NET runtime so the target machine needs no .NET install.
    Much larger (roughly 70 MB compressed); use when the target may lack .NET 8.

.PARAMETER NoZip
    Skip creating the ZIP archive.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'publish\TaskbarLyrics'
$project = Join-Path $root 'src\TaskbarLyrics.App\TaskbarLyrics.App.csproj'

# The exe is file-locked while the app runs.
Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

Write-Host "Publishing ($(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }))..."

$args = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $outDir,
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:SatelliteResourceLanguages=en',
    '--nologo',
    '-v', 'q'
)

if ($SelfContained) {
    $args += '--self-contained', 'true'
    $args += '-p:PublishSingleFile=true'
    $args += '-p:EnableCompressionInSingleFile=true'
} else {
    $args += '--self-contained', 'false'
}

& dotnet @args
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# The CLI is a developer diagnostic tool and duplicates a 24 MB WinRT interop
# assembly, so it is excluded from the shipped package. Build it from source
# (src\TaskbarLyrics.Cli) when needed.

# Ship the user docs.
Copy-Item (Join-Path $root 'README.md') $outDir -Force -ErrorAction SilentlyContinue

$exe = Join-Path $outDir 'TaskbarLyrics.exe'
if (-not (Test-Path $exe)) { throw "Expected executable missing: $exe" }

$files = Get-ChildItem $outDir -Recurse -File
$sizeMb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ""
Write-Host "Output : $outDir"
Write-Host "Files  : $($files.Count)"
Write-Host "Size   : $sizeMb MB"
Write-Host "Main   : TaskbarLyrics.exe"

if (-not $NoZip) {
    $zip = Join-Path $root 'publish\TaskbarLyrics-win-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip -CompressionLevel Optimal
    $zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "ZIP    : $zip ($zipMb MB)"
}
