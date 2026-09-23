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

.PARAMETER IncludeFonts
    Keep the font files inside the ZIP. Off by default: the fonts have their own
    licence and are not redistributed with the release.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$NoZip,
    [switch]$IncludeFonts
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
    # Font files carry their own licence and are not redistributed with the release, so
    # the ZIP leaves them out by default. The local publish folder keeps them so the app
    # can still be run in place; pass -IncludeFonts for a private archive.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $skipped = @()
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in Get-ChildItem $outDir -Recurse -File) {
            $relative = $item.FullName.Substring($outDir.Length + 1)
            if (-not $IncludeFonts -and $relative -like 'Fonts\*') { $skipped += $relative; continue }
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $item.FullName, $relative.Replace('\', '/'),
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
    if ($skipped.Count -gt 0) {
        Write-Host "Fonts  : excluded from the ZIP -> $($skipped -join ', ')  (-IncludeFonts to keep)"
    }
    $zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "ZIP    : $zip ($zipMb MB)"
}
