# Shared GitHub REST helper for this repo's tooling.
#
# The token is read from the Windows credential store (the entry the Git Credential
# Manager created for https://github.com); nothing is hardcoded here. Dot-source this
# file to get Get-GhToken / Invoke-GhApi, or run it to print who the token belongs to.
#
#   . .\tools\gh-api.ps1
#   Invoke-GhApi GET 'https://api.github.com/repos/DoingStone/DesktopMusic/releases'
#
# Bodies are always sent from a temporary file via -InFile. Windows PowerShell 5.1
# mangles a byte[] -Body: a 7.56 MB ZIP uploaded as 27.23 MB of garbage (the array got
# stringified), and the same path made the create-release call come back 422
# ("For 'links/0/schema', 123 is not an object"). -InFile streams the file as-is.

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

# Invoke-GhApi <method> <uri> [-Body <object|byte[]>] [-InFile <path>] [-Raw]
# JSON bodies are serialised as UTF-8 without a BOM; -InFile streams a file verbatim
# (release asset uploads). -Raw returns the WebResponse so callers can read upload
# progress / headers.
function Invoke-GhApi {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [Parameter()]$Body,
        [Parameter()][string]$InFile,
        [Parameter()][switch]$Raw,
        [Parameter()][string]$ContentType = 'application/json; charset=utf-8',
        [Parameter()][int]$TimeoutSec = 600
    )

    $headers = Get-GhHeaders
    # .NET honours the WinINET system proxy by default. On this machine that points at a
    # local proxy port which cannot reach github.com, so the REST calls must go direct:
    # api.github.com answers fine without it (curl works, the system proxy does not).
    [System.Net.WebRequest]::DefaultWebProxy = New-Object System.Net.WebProxy

    $params = @{ Method = $Method; Uri = $Uri; Headers = $headers; UseBasicParsing = $true; TimeoutSec = $TimeoutSec }
    $tempFile = $null
    if ($InFile) {
        $headers['Content-Type'] = $ContentType
        $params.InFile = $InFile
    } elseif ($null -ne $Body) {
        $headers['Content-Type'] = $ContentType
        $tempFile = [System.IO.Path]::GetTempFileName()
        if ($Body -is [byte[]]) {
            [System.IO.File]::WriteAllBytes($tempFile, $Body)
        } else {
            $json = ($Body | ConvertTo-Json -Depth 12 -Compress)
            [System.IO.File]::WriteAllText($tempFile, $json, (New-Object System.Text.UTF8Encoding($false)))
        }
        $params.InFile = $tempFile
    }

    try {
        $resp = Invoke-WebRequest @params
    } finally {
        if ($tempFile) { Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue }
    }

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
