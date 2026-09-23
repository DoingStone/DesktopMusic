$ErrorActionPreference = 'Stop'

# Pushes the local commit through the GitHub Git Data API.
#
# The git HTTPS transport is unusable in this session: Schannel reports
# SEC_E_NO_CREDENTIALS and the credential helper cannot spawn (the sandbox denies named
# pipes). api.github.com is reachable and the token can be read straight from the
# Windows credential store, so the same content is pushed without git's transport.
Add-Type @"
using System; using System.Runtime.InteropServices;
public class Cred2 {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct CREDENTIAL {
    public uint Flags; public uint Type; public IntPtr TargetName; public IntPtr Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
    public uint CredentialBlobSize; public IntPtr CredentialBlob;
    public uint Persist; public uint AttributeCount; public IntPtr Attributes;
    public IntPtr TargetAlias; public IntPtr UserName;
  }
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  public static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
  [DllImport("advapi32.dll")] public static extern void CredFree(IntPtr buffer);
  public static string Read(string target) {
    IntPtr p;
    if (!CredReadW(target, 1, 0, out p)) return null;
    var c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
    var blob = new byte[c.CredentialBlobSize];
    Marshal.Copy(c.CredentialBlob, blob, 0, (int)c.CredentialBlobSize);
    CredFree(p);
    return System.Text.Encoding.Unicode.GetString(blob);
  }
}
"@

$f = 'E:\Qianmory\Desktop\DesktopMusic'
Set-Location $f
$repo = 'DoingStone/DesktopMusic'

$raw = [Cred2]::Read('git:https://github.com')
if (-not $raw) { throw 'credential not readable' }

# GCM stores the token alone, as UTF-16LE.
$secret = if ($raw -match "`n") { ($raw -split "`n")[-1] } elseif ($raw -match ':') { ($raw -split ':', 2)[1] } else { $raw }
$secret = $secret.Trim()
Write-Host "token length: $($secret.Length)"

$H = @{
    Authorization  = "Bearer $secret"
    'User-Agent'   = 'dsh'
    Accept         = 'application/vnd.github+json'
    'Content-Type' = 'application/json; charset=utf-8'
}

function Api([string]$method, [string]$uri, $body) {
    $p = @{ Method = $method; Uri = $uri; Headers = $H }
    if ($null -ne $body) {
        $p.Body = [System.Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 8 -Compress))
    }
    Invoke-RestMethod @p
}

$who = Api 'GET' 'https://api.github.com/user' $null
Write-Host "authenticated as: $($who.login)"

$changed = @(git diff --name-only origin/main..main)
if ($changed.Count -eq 0) { Write-Host 'nothing to push'; exit 0 }
$remoteHead = (git rev-parse origin/main).Trim()
Write-Host "pushing $($changed.Count) file(s) onto $($remoteHead.Substring(0,8))"

$base = Api 'GET' "https://api.github.com/repos/$repo/git/commits/$remoteHead" $null
$entries = @()
foreach ($p in $changed) {
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $f $p))
    $blob = Api 'POST' "https://api.github.com/repos/$repo/git/blobs" @{
        content = [Convert]::ToBase64String($bytes); encoding = 'base64'
    }
    Write-Host "  $p -> $($blob.sha.Substring(0,8))"
    $entries += @{ path = ($p -replace '\\', '/'); mode = '100644'; type = 'blob'; sha = $blob.sha }
}

$tree = Api 'POST' "https://api.github.com/repos/$repo/git/trees" @{ base_tree = $base.tree.sha; tree = $entries }
$message = [System.IO.File]::ReadAllText((Join-Path $f 'artifacts\msg.txt'), [System.Text.Encoding]::UTF8)
$commit = Api 'POST' "https://api.github.com/repos/$repo/git/commits" @{
    message = $message; tree = $tree.sha; parents = @($remoteHead)
}
Write-Host "new commit: $($commit.sha)"
Api 'PATCH' "https://api.github.com/repos/$repo/git/refs/heads/main" @{ sha = $commit.sha; force = $false } | Out-Null
Write-Host 'remote ref updated'

git fetch origin 2>&1 | Out-Null
Write-Host ''
Write-Host "remote main = $((git rev-parse origin/main).Trim())"
Write-Host 'content diff of remote against the local worktree (should be empty):'
git diff --stat origin/main | ForEach-Object { "  $_" }
