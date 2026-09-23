# Verifies the cluster modes the machine's live settings never reach, plus the settings dialog
# itself:
#
#   * pinned open (HoverRevealControls off): the cluster never leaves, so the words must never
#     be left underneath it — they stay carried aside with no hover involved at all
#   * every track-info toggle off: the cluster collapses to nothing, so the words must stay
#     centred instead of being nudged aside by an empty box
#   * the settings dialog opens and loads without an unhandled fault: its three new toggles are
#     wired in XAML and code, and only a real load proves the names line up at run time
#
# Each case launches the app against a throwaway APPDATA, so the machine's own settings.json is
# never read or written, and reads back what the app logged about its own layout.
#
# Usage: & tools\verify-cluster-modes.ps1      (build Debug first)
# Exit code 0 = pass, 1 = fail.

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'src\TaskbarLyrics.App\bin\Debug\net8.0-windows10.0.19041.0\TaskbarLyrics.exe'
$log = Join-Path $env:TEMP 'taskbar-lyrics-diag.log'

if (-not (Test-Path $exe)) { Write-Host "FAIL: not built: $exe" -ForegroundColor Red; exit 1 }

$code = @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Modes {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@

Add-Type -TypeDefinition $code

# Same reason as verify-hover-shift.ps1: an unaware process gets virtualised rectangles, and the
# pointer would be sent to the wrong place on a scaled display.
[void][Modes]::SetProcessDPIAware()

$script:found = @()

function Get-AppWindows([int]$procId) {
  $script:found = @()
  [void][Modes]::EnumWindows({ param($h, $l)
    $owner = 0
    [void][Modes]::GetWindowThreadProcessId($h, [ref]$owner)
    if ($owner -eq $procId) {
      $title = New-Object Text.StringBuilder 512
      [void][Modes]::GetWindowText($h, $title, 512)
      $script:found += [pscustomobject]@{ Handle = $h; Title = $title.ToString() }
    }
    return $true
  }, [IntPtr]::Zero)
  return $script:found
}

# Every field UpdateLyricShift writes, so the invariants below can be checked on real numbers.
$insetPattern = 'lyric inset=(-?[\d.]+) cluster=(-?[\d.]+) info=(-?[\d.]+) ' +
                'title=(-?[\d.]+) artist=(-?[\d.]+) trans=(-?[\d.]+) strip=(-?[\d.]+) ' +
                'line=(-?[\d.]+) occupied=(\w+) hovered=(\w+)'

function Read-Shifts {
  if (-not (Test-Path $log)) { return @() }
  $out = @()
  foreach ($line in (Get-Content $log)) {
    $m = [regex]::Match($line, $insetPattern)
    if (-not $m.Success) { continue }
    $out += [pscustomobject]@{
      Inset    = [double]$m.Groups[1].Value
      Cluster  = [double]$m.Groups[2].Value
      Info     = [double]$m.Groups[3].Value
      Title    = [double]$m.Groups[4].Value
      Artist   = [double]$m.Groups[5].Value
      Trans    = [double]$m.Groups[6].Value
      Strip    = [double]$m.Groups[7].Value
      Line     = [double]$m.Groups[8].Value
      Occupied = $m.Groups[9].Value
      Hovered  = $m.Groups[10].Value
    }
  }
  return $out
}

function Invoke-Case([string]$name, [hashtable]$settings, [string[]]$extraArgs) {
  Get-Process TaskbarLyrics -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 600
  Remove-Item $log -ErrorAction SilentlyContinue

  $sandbox = Join-Path $env:TEMP ('tbl-modes-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
  New-Item -ItemType Directory -Path $sandbox -Force | Out-Null

  # Locked=false is the only setting every case needs: without it the strip is click-through
  # and no hover is tracked at all.
  $json = @{ Locked = $false }
  foreach ($key in $settings.Keys) { $json[$key] = $settings[$key] }
  ($json | ConvertTo-Json) | Set-Content -Path (Join-Path $sandbox 'settings.json') -Encoding utf8

  # TBL_SETTINGS is what keeps this off the machine's own settings.json: overriding APPDATA is
  # not enough, because the app resolves the folder through the shell rather than the variable.
  $oldSettings = $env:TBL_SETTINGS
  $oldDiag = $env:TBL_DIAG
  $env:TBL_SETTINGS = Join-Path $sandbox 'settings.json'
  $env:TBL_DIAG = '1'
  try {
    # An empty -ArgumentList is rejected outright, so it is only passed when there is one.
    $proc = if ($extraArgs -and $extraArgs.Count -gt 0) {
      Start-Process -FilePath $exe -ArgumentList $extraArgs -PassThru
    } else {
      Start-Process -FilePath $exe -PassThru
    }
  } finally {
    $env:TBL_SETTINGS = $oldSettings
    $env:TBL_DIAG = $oldDiag
  }

  Start-Sleep -Seconds 9

  $windows = @(Get-AppWindows $proc.Id)
  $overlay = $windows | Where-Object { $_.Title -like '*Overlay*' } | Select-Object -First 1
  if ($overlay) {
    $rect = New-Object Modes+RECT
    [void][Modes]::GetWindowRect($overlay.Handle, [ref]$rect)
    [void][Modes]::SetCursorPos([int](($rect.L + $rect.R) / 2), [int](($rect.T + $rect.B) / 2))
    Start-Sleep -Milliseconds 1600
  }

  $result = [pscustomobject]@{
    Name     = $name
    Alive    = -not $proc.HasExited
    Windows  = $windows
    Shifts   = @(Read-Shifts)
    Log      = @(if (Test-Path $log) { Get-Content $log } else { @() })
    Sandbox  = $sandbox
    Proc     = $proc
  }

  # Park the pointer back in the middle of the screen before the next case starts.
  [void][Modes]::SetCursorPos([int]([Modes]::GetSystemMetrics(0) / 2), [int]([Modes]::GetSystemMetrics(1) / 3))
  return $result
}

$fail = 0
function Check([string]$what, [bool]$ok, [string]$detail) {
  if ($ok) {
    Write-Host ("  PASS {0}{1}" -f $what, $(if ($detail) { "  ($detail)" } else { '' })) -ForegroundColor Green
  } else {
    Write-Host ("  FAIL {0}{1}" -f $what, $(if ($detail) { "  ($detail)" } else { '' })) -ForegroundColor Red
    $script:fail++
  }
}

function Assert-Invariants($shifts, [string]$label) {
  # The inset is the room the cluster takes off the left of the lines, so it can never eat the
  # whole strip: the two 8 DIP content margins plus a readable remainder have to survive.
  $bad = @($shifts | Where-Object { $_.Inset -gt $_.Strip - 16 })
  Check "$label the inset never eats the whole strip" ($bad.Count -eq 0) ("{0} line(s)" -f $shifts.Count)

  $bad = @($shifts | Where-Object { -not $_.Occupied -and $_.Inset -gt 0.5 })
  Check "$label nothing is inset while the cluster is away" ($bad.Count -eq 0)

  $bad = @($shifts | Where-Object { $_.Inset -gt 0.5 -and [Math]::Abs($_.Inset - ($_.Cluster + 6)) -gt 0.6 })
  Check "$label inset is the cluster plus its gap" ($bad.Count -eq 0)
}

# ---- 1. cluster pinned open -----------------------------------------------------------------
Write-Host ''
Write-Host '=== pinned open (HoverRevealControls=false) ==='
$pinned = Invoke-Case 'pinned' @{ HoverRevealControls = $false } @()
$pinned.Shifts | ForEach-Object { Write-Host ("  inset={0:F1} cluster={1:F1} strip={2:F1} occupied={3} hovered={4}" -f $_.Inset, $_.Cluster, $_.Strip, $_.Occupied, $_.Hovered) }
Check 'app stayed up' $pinned.Alive
Check 'reveal mode is the always-visible one' (@($pinned.Log | Select-String -SimpleMatch 'controls always visible').Count -gt 0)
Check 'hover is not tracked at all' (@($pinned.Log | Select-String -SimpleMatch 'hovered=True').Count -eq 0)
Check 'the cluster was measured' (($pinned.Shifts | Measure-Object -Property Cluster -Maximum).Maximum -gt 0)
Check 'the words are given the room beside it without any hover' (@($pinned.Shifts | Where-Object { $_.Occupied -and $_.Inset -gt 1 }).Count -gt 0)
Assert-Invariants $pinned.Shifts 'pinned:'

# ---- 2. every track-info toggle off ---------------------------------------------------------
Write-Host ''
Write-Host '=== track info switched off (title, artist, cover) ==='
$infoOff = Invoke-Case 'info-off' @{ ShowSongTitle = $false; ShowSongArtist = $false; ShowCoverArt = $false } @()
$infoOff.Shifts | ForEach-Object { Write-Host ("  inset={0:F1} cluster={1:F1} info={2:F1} trans={3:F1} occupied={4} hovered={5}" -f $_.Inset, $_.Cluster, $_.Info, $_.Trans, $_.Occupied, $_.Hovered) }
Check 'app stayed up' $infoOff.Alive
Check 'the strip was hovered' (@($infoOff.Shifts | Where-Object { $_.Hovered -eq 'True' }).Count -gt 0)
# The cluster's own width is what the shift is measured from, so that is what has to shrink when
# the info panel collapses — the panel's own ActualWidth is only a hint, since a collapsed
# element keeps reporting the size it last had.
Check 'the transport controls still take room' (($infoOff.Shifts | Measure-Object -Property Trans -Maximum).Maximum -gt 0)
Check 'the info panel takes none' (($infoOff.Shifts | Measure-Object -Property Cluster -Maximum).Maximum -le (($infoOff.Shifts | Measure-Object -Property Trans -Maximum).Maximum + 0.5))
Assert-Invariants $infoOff.Shifts 'info-off:'

# ---- 3. the settings dialog -----------------------------------------------------------------
Write-Host ''
Write-Host '=== settings dialog ==='
$dialog = Invoke-Case 'dialog' @{} @('--settings')
$dialog.Windows | ForEach-Object { Write-Host ("  window '{0}'" -f $_.Title) }

# Windows the app owns that are not its UI (IME, GDI, composition plumbing). The two that are
# are matched by exclusion rather than by name, so no non-ASCII literal has to survive whatever
# encoding this script is read back with.
$systemWindows = '^(WISPTIS|Hidden Window|GDI\+ Window|SystemResource|MediaContext|CiceroUIWndFrame|Default IME|MSCTFIME UI)'
$titled = @($dialog.Windows | Where-Object { $_.Title -and $_.Title -notmatch $systemWindows })
Check 'the overlay window is up' (@($titled | Where-Object { $_.Title -like '*Overlay*' }).Count -ge 1)
Check 'the settings window is up beside it' (@($titled | Where-Object { $_.Title -notlike '*Overlay*' }).Count -ge 1)
Check 'app stayed up' $dialog.Alive
$faults = @($dialog.Log | Select-String -SimpleMatch 'UNHANDLED')
Check 'loading it raised no unhandled fault' ($faults.Count -eq 0) ("{0} fault line(s)" -f $faults.Count)
Check 'the overlay is still drawing underneath' (@($dialog.Log | Select-String -SimpleMatch '[overlay] Render pos=').Count -gt 0)

foreach ($case in @($pinned, $infoOff, $dialog)) {
  if (-not $case.Proc.HasExited) { $case.Proc | Stop-Process -Force }
  Remove-Item $case.Sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($fail -eq 0) { Write-Host 'all cluster-mode checks passed' -ForegroundColor Green; exit 0 }
Write-Host ("{0} cluster-mode check(s) failed" -f $fail) -ForegroundColor Red
exit 1
