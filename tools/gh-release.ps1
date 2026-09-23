<#
.SYNOPSIS
    Create a GitHub release for this repository and attach the distribution ZIP.

.DESCRIPTION
    Uses the GitHub REST API with the token from the Windows credential store
    (see tools\gh-api.ps1), so it works without the gh CLI.

    The release notes are read from a UTF-8 markdown file. Existing releases for the
    same tag are only replaced when -Force is given; re-uploading an asset with the
    same file name replaces the previous asset.

.PARAMETER Tag
    Release tag, e.g. v1.1.0. Created on -Target if it does not exist yet.

.PARAMETER Name
    Release title (defaults to the tag).

.PARAMETER BodyFile
    UTF-8 markdown file with the release notes.

.PARAMETER Asset
    One or more files to upload as release assets.

.PARAMETER Target
    Branch or commit the tag is created on (default main). Ignored if the tag exists.

.PARAMETER Draft / -Prerelease
    Mark the release accordingly.

.PARAMETER Force
    Delete an existing release with the same tag and recreate it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\gh-release.ps1 `
        -Tag v1.1.0 -BodyFile artifacts\release-notes-v1.1.0.md `
        -Asset publish\TaskbarLyrics-win-x64.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$Name,
    [string]$BodyFile,
    [string[]]$Asset,
    [string]$Target = 'main',
    [switch]$Draft,
    [switch]$Prerelease,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'gh-api.ps1')

$repo = 'DoingStone/DesktopMusic'
$who = Invoke-GhApi GET 'https://api.github.com/user'
Write-Host "authenticated as: $($who.login)   scopes: $(Get-GhScopes)"

$existing = $null
try { $existing = Invoke-GhApi GET "https://api.github.com/repos/$repo/releases/tags/$Tag" } catch { $existing = $null }

if ($existing) {
    if (-not $Force) { throw "release $Tag already exists ($($existing.html_url)); pass -Force to replace it" }
    Write-Host "deleting existing release $Tag (id $($existing.id))"
    Invoke-GhApi DELETE "https://api.github.com/repos/$repo/releases/$($existing.id)" | Out-Null
}

$body = ''
if ($BodyFile) {
    $body = [System.IO.File]::ReadAllText((Resolve-Path $BodyFile).Path, [System.Text.Encoding]::UTF8)
}

$payload = @{
    tag_name   = $Tag
    name       = if ($Name) { $Name } else { $Tag }
    body       = $body
    draft      = [bool]$Draft
    prerelease = [bool]$Prerelease
}
if (-not $existing) { $payload.target_commitish = $Target }

$rel = Invoke-GhApi POST "https://api.github.com/repos/$repo/releases" $payload
Write-Host "release : $($rel.html_url)"
Write-Host "tag     : $($rel.tag_name)  ->  $($rel.target_commitish)"

$fresh = Invoke-GhApi GET "https://api.github.com/repos/$repo/releases/$($rel.id)"
foreach ($a in $Asset) {
    $path = (Resolve-Path $a).Path
    $fileName = Split-Path $path -Leaf
    foreach ($ra in @($fresh.assets)) {
        if ($ra.name -eq $fileName) {
            Write-Host "  removing previous asset $($ra.name) (id $($ra.id))"
            Invoke-GhApi DELETE "https://api.github.com/repos/$repo/releases/assets/$($ra.id)" | Out-Null
        }
    }
    $sizeMb = [math]::Round((Get-Item -LiteralPath $path).Length / 1MB, 2)
    Write-Host ("uploading {0} ({1} MB)..." -f $fileName, $sizeMb)
    # -InFile, never -Body <byte[]>: Windows PowerShell 5.1 stringifies a byte[] body and
    # uploads a corrupted asset (7.56 MB arrived as 27.23 MB).
    $resp = Invoke-GhApi POST "https://uploads.github.com/repos/$repo/releases/$($rel.id)/assets?name=$fileName" -InFile $path -ContentType 'application/zip' -Raw
    $asset = $resp.Content | ConvertFrom-Json
    Write-Host ("  asset  : {0}  {1} MB  state={2}" -f $asset.name, [math]::Round($asset.size / 1MB, 2), $asset.state)
    Write-Host ("  url    : {0}" -f $asset.browser_download_url)
}

$final = Invoke-GhApi GET "https://api.github.com/repos/$repo/releases/$($rel.id)"
Write-Host ''
Write-Host ("release {0} now carries {1} asset(s): {2}" -f $final.tag_name, @($final.assets).Count, (($final.assets | ForEach-Object { $_.name }) -join ', '))
Write-Host ("published: {0}   draft: {1}   prerelease: {2}" -f $final.published_at, $final.draft, $final.prerelease)
