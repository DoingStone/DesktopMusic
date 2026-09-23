# Shared GitHub REST helper for this repo's tooling.
#
# The token is read from the Windows credential store (the entry the Git Credential
# Manager created for https://github.com); nothing is hardcoded here. Dot-source this
# file to get Get-GhToken / Invoke-GhApi, or run it to print who the token belongs to.
#
#   . .\tools\gh-api.ps1
#   Invoke-GhApi GET 'https://api.github.com/repos/DoingStone/DesktopMusic/releases'

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

$script:GhToken = $null
$script:GhScopes = $null

function Get-GhToken {
    if ($script:GhToken) { return $script:GhToken }
    $raw = [Cred2]::Read('git:https://github.com')
    if (-not $raw) { throw 'no GitHub credential in the Windows credential store (git:https://github.com)' }
    # GCM stores the token alone as UTF-16LE; older helpers store "user:token".
    $secret = if ($raw -match "`n") { ($raw -split "`n")[-1] } elseif ($raw -match ':') { ($raw -split ':', 2)[1] } else { $raw }
    $script:GhToken = $secret.Trim()
    return $script:GhToken
}

function Get-GhHeaders {
    return @{
        Authorization  = "Bearer $(Get-GhToken)"
        'User-Agent'   = 'dsh'
        Accept         = 'application/vnd.github+json'
    }
}

# Invoke-GhApi <method> <uri> [-Body <object>] [-Raw]
# JSON bodies are serialised as UTF-8; -Raw returns the WebResponse so callers can
# read upload progress / headers (used by the release asset upload).
function Invoke-GhApi {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [Parameter()]$Body,
        [Parameter()][switch]$Raw,
        [Parameter()][string]$ContentType = 'application/json; charset=utf-8',
        [Parameter()][int]$TimeoutSec = 600
    )

    $headers = Get-GhHeaders
    if ($null -ne $Body) {
        $headers['Content-Type'] = $ContentType
        $bytes = if ($Body -is [byte[]]) { $Body } else { [System.Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 12 -Compress)) }
    }

    $params = @{ Method = $Method; Uri = $Uri; Headers = $headers; UseBasicParsing = $true; TimeoutSec = $TimeoutSec }
    if ($null -ne $Body) { $params.Body = $bytes }
    $resp = Invoke-WebRequest @params

    $script:GhScopes = $resp.Headers['x-oauth-scopes']
    if ($Raw) { return $resp }
    if ([string]::IsNullOrWhiteSpace($resp.Content)) { return $null }
    return ($resp.Content | ConvertFrom-Json)
}

function Get-GhScopes { return $script:GhScopes }

if ($MyInvocation.InvocationName -ne '.') {
    $who = Invoke-GhApi GET 'https://api.github.com/user'
    Write-Host "authenticated as: $($who.login)"
    Write-Host "token scopes    : $(Get-GhScopes)"
    Write-Host "token length    : $((Get-GhToken).Length)"
}
