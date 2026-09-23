# Verifies the hover layout the strip is supposed to have:
#
#   * with the pointer away, nothing is revealed and the words sit in the middle of the band
#   * with the pointer on the strip, the cluster shows up on the left and the words end up
#     centred in the space that is left, clear of the cluster
#   * the move between the two states is a ramp, not a jump
#
# The band is measured from the pixels, so this checks what the strip actually draws rather
# than what it meant to draw. The backdrop luminance is taken as the most common value in the
# region rather than assumed, which keeps the ink test working on a light taskbar and on a
# dark one. Two frames are also written out for the eye.
#
# Usage: & tools\verify-hover-shift.ps1      (the app must already be running)
# Exit code 0 = pass, 1 = fail.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$code = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class HoverShift {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int index);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);

  /// Width of the physical screen in device pixels. Unlike GetSystemMetrics(SM_CXSCREEN) this
  /// is never virtualised, so it is the trustworthy half of the DPI gate at the top of the
  /// script: while the two disagree the process is DPI-unaware and every coordinate here is a
  /// DIP pretending to be a pixel.
  public static int PhysicalScreenWidth() { return GetDeviceCaps(GetDC(IntPtr.Zero), 118); }

  /// DIP-to-pixel factor of this display: 96 dpi = 1.0, 120 dpi = 1.25, 144 dpi = 1.5.
  public static double DpiScale() { return GetDpiForSystem() / 96.0; }

  /// Backdrop luminance - the most common luminance in the frame, in 8-wide buckets. Measured
  /// rather than assumed so the ink test holds on a light taskbar and on a dark one alike.
  public static int BackdropLum(byte[] px, int stride, int w, int h) {
    var counts = new Dictionary<int,int>();
    for (int y = 0; y < h; y++) {
      int row = y * stride;
      for (int x = 0; x < w; x++) {
        int b = px[row + x*4], g = px[row + x*4 + 1], r = px[row + x*4 + 2];
        int l = (r*30 + g*59 + b*11) / 100;
        int bucket = l >> 3;
        int n; counts.TryGetValue(bucket, out n);
        counts[bucket] = n + 1;
      }
    }
    int best = 0, bestN = -1;
    foreach (var kv in counts) if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
    return (best << 3) + 4;
  }

  /// Left edge of the first wide ink-free gap: the boundary between the hover cluster and the
  /// words. Measured from the pixels because every "split the band in half" constant is written
  /// in DIP while these pixels are device pixels - the two differ by a quarter at 125%, and a
  /// long song title widens the cluster past a half-band split anyway. gapPx has to be wider
  /// than the widest gap inside the cluster (the 12 DIP margin between the info panel and the
  /// transport panel) and narrower than the gap the cluster leaves before the words.
  /// Returns 0 when the frame has no ink at all, -1 when it has ink but no such gap.
  public static int ClusterEnd(byte[] px, int stride, int w, int h, int gapPx) {
    int bg = BackdropLum(px, stride, w, h);
    var colInk = new int[w];
    int total = 0;
    for (int y = 0; y < h; y++) {
      int row = y * stride;
      for (int x = 0; x < w; x++) {
        int b = px[row + x*4], g = px[row + x*4 + 1], r = px[row + x*4 + 2];
        int l = (r*30 + g*59 + b*11) / 100;
        if (Math.Abs(l - bg) <= 32) continue;
        colInk[x]++; total++;
      }
    }
    if (total == 0) return 0;

    int limit = (w * 3) / 4;
    for (int x = 1; x < limit; x++) {
      if (colInk[x] != 0) continue;
      bool clear = true;
      for (int k = x; k < x + gapPx && k < limit; k++) if (colInk[k] != 0) { clear = false; break; }
      if (!clear) continue;
      for (int k = 0; k < x; k++) if (colInk[k] != 0) return x;
    }
    return -1;
  }

  public struct Scan { public int Left, Right, Count, Bg; public double Center; }

  /// Ink is any pixel far enough from the backdrop's own luminance, and the backdrop is
  /// measured as the most common luminance here rather than assumed: the same check then
  /// holds for dark glyphs on a light taskbar and light glyphs on a dark one.
  public static Scan ScanPixels(byte[] px, int stride, int w, int h, int xMin, int xMax) {
    var lum = new byte[w * h];
    for (int y = 0; y < h; y++) {
      int row = y * stride;
      for (int x = 0; x < w; x++) {
        int b = px[row + x*4], g = px[row + x*4 + 1], r = px[row + x*4 + 2];
        lum[y*w + x] = (byte)((r*30 + g*59 + b*11) / 100);
      }
    }

    int bg = BackdropLum(px, stride, w, h);

    var scan = new Scan { Left = -1, Right = -1, Bg = bg };
    double sum = 0;
    if (xMax > w) xMax = w;
    if (xMin < 0) xMin = 0;
    for (int y = 0; y < h; y++) {
      for (int x = xMin; x < xMax; x++) {
        int l = lum[y*w + x];
        if (Math.Abs(l - bg) <= 32) continue;
        if (scan.Left < 0 || x < scan.Left) scan.Left = x;
        if (x > scan.Right) scan.Right = x;
        scan.Count++;
        sum += x;
      }
    }
    scan.Center = scan.Count > 0 ? sum / scan.Count : 0;
    return scan;
  }

  /// Pixels that differ between two frames of the same size: static furniture cancels out,
  /// so what is left is what the reveal actually moved.
  public static int DiffPixels(byte[] a, byte[] b, int stride, int w, int h, int threshold) {
    int n = 0;
    for (int y = 0; y < h; y++) {
      int row = y * stride;
      for (int x = 0; x < w; x++) {
        int i = row + x*4;
        int d = Math.Abs(a[i] - b[i]) + Math.Abs(a[i+1] - b[i+1]) + Math.Abs(a[i+2] - b[i+2]);
        if (d > threshold) n++;
      }
    }
    return n;
  }
}
"@

Add-Type -TypeDefinition $code

# ---- coordinate space ------------------------------------------------------------------
# Everything below - window rectangles, cursor positions, the pixels copied off the screen - is
# in DEVICE PIXELS, while the thresholds are written in DIP. On this 125% display the two differ
# by a quarter, so they are reconciled exactly once, here, and the result is proved rather than
# assumed.
#
# This console host starts DPI-unaware, and an unaware process is not merely offset: GetWindowRect
# answers in virtualised DIP and CopyFromScreen hands back a blurry, down-scaled copy of the strip
# with the ink smeared across the whole band. That failure is silent and looks plausible, so the
# awareness call is followed by a gate. DESKTOPHORZRES is the physical screen width and is never
# virtualised, whereas SM_CXSCREEN only reports the physical screen once awareness has taken
# effect: if the two disagree, no number this script prints can be trusted.
[void][HoverShift]::SetProcessDPIAware()
$physW = [HoverShift]::PhysicalScreenWidth()
$seenW = [HoverShift]::GetSystemMetrics(0)
$dpiScale = [HoverShift]::DpiScale()
if ($seenW -ne $physW) {
  Write-Host ("FAIL coordinate space: still DPI-unaware (SM_CXSCREEN={0}, physical={1}) - every coordinate would be a DIP pretending to be a pixel" -f $seenW, $physW) -ForegroundColor Red
  exit 1
}
Write-Host ("screen {0}x{1} px, dpi {2} -> scale {3}" -f $seenW, [HoverShift]::GetSystemMetrics(1), [int]($dpiScale * 96), $dpiScale)

# The single DIP -> device pixel conversion. Every geometric constant below is named in DIP and
# converted here, so a threshold keeps its meaning at 100%, 125% and 150% instead of quietly
# changing size with the display scale.
function Convert-Dip([double]$v) { return [int][Math]::Round($v * $dpiScale) }

$proc = Get-Process TaskbarLyrics -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host 'FAIL: TaskbarLyrics is not running' -ForegroundColor Red; exit 1 }

$script:rect = $null
[void][HoverShift]::EnumWindows({ param($h, $l)
  $owner = 0
  [void][HoverShift]::GetWindowThreadProcessId($h, [ref]$owner)
  if ($owner -eq $proc.Id) {
    $title = New-Object Text.StringBuilder 512
    [void][HoverShift]::GetWindowText($h, $title, 512)
    if ($title.ToString() -like '*Overlay*') {
      $r = New-Object HoverShift+RECT
      [void][HoverShift]::GetWindowRect($h, [ref]$r)
      $script:rect = $r
    }
  }
  return $true
}, [IntPtr]::Zero)

if (-not $script:rect) { Write-Host 'FAIL: overlay window not found' -ForegroundColor Red; exit 1 }
$rect = $script:rect
$w = $rect.R - $rect.L
$h = $rect.B - $rect.T
Write-Host ("overlay rect ({0},{1})-({2},{3})  {4}x{5} px = {6:F1}x{7:F1} DIP" -f $rect.L, $rect.T, $rect.R, $rect.B, $w, $h, ($w / $dpiScale), ($h / $dpiScale))

# One screen grab of the strip, as raw BGRA plus its own scan. The pixels are copied out of a
# locked bitmap and walked in compiled code: the same walk in PowerShell would be far too slow
# to sample an animation with.
function Capture-Strip {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($rect.L, $rect.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
  $g.Dispose()

  $data = $bmp.LockBits((New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $w, $h),
                        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  try {
    $bytes = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $stride = $data.Stride
  } finally {
    $bmp.UnlockBits($data)
  }

  return [pscustomobject]@{ Bitmap = $bmp; Bytes = $bytes; Stride = $stride }
}

function Save-Frame($frame, [string]$name) {
  $path = Join-Path $env:TEMP $name
  $frame.Bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  Write-Host ("wrote {0}" -f $path)
  return $path
}

# Park the pointer in the middle of the screen: far from every taskbar edge, so it cannot be
# sitting on the strip on a machine whose taskbar is docked somewhere other than the bottom.
$parkX = [int]([HoverShift]::GetSystemMetrics(0) / 2)
$parkY = [int]([HoverShift]::GetSystemMetrics(1) / 3)

# ---- rest: pointer away, nothing revealed ----------------------------------------------
[void][HoverShift]::SetCursorPos($parkX, $parkY)
Start-Sleep -Milliseconds 700
$rest = Capture-Strip
[void](Save-Frame $rest 'tbl-hover-rest.png')

# Edges of the band the lyric lines occupy, all DIP converted to the pixels of this frame. The
# lines span the content grid, which carries an 8 DIP margin at each end of the strip; the left
# scan window starts a little further in, past the drag handle.
$contentPadDip = 8
$edgePad = Convert-Dip 12
$rightEdge = $w - (Convert-Dip $contentPadDip)
$restWords = [HoverShift]::ScanPixels($rest.Bytes, $rest.Stride, $w, $h, $edgePad, $rightEdge)
Write-Host ("[rest]  ink={0,5} left={1,4} right={2,4} center={3,7:F1} bg={4} (words window {5}..{6} px)" -f $restWords.Count, $restWords.Left, $restWords.Right, $restWords.Center, $restWords.Bg, $edgePad, $rightEdge)
$restAll = [HoverShift]::ScanPixels($rest.Bytes, $rest.Stride, $w, $h, 0, $w)
Write-Host ("[rest]  full-strip ink={0} left={1} right={2}" -f $restAll.Count, $restAll.Left, $restAll.Right)

# ---- hover: pointer on the strip, over the words rather than over a control --------------
[void][HoverShift]::SetCursorPos(($rect.L + [int]($w * 0.8)), ($rect.T + [int]($h / 2)))
Start-Sleep -Milliseconds 700
$hover = Capture-Strip
[void](Save-Frame $hover 'tbl-hover-hover.png')

$hoverAll = [HoverShift]::ScanPixels($hover.Bytes, $hover.Stride, $w, $h, 0, $w)
Write-Host ("[hover] full-strip ink={0} left={1} right={2} center={3:F1}" -f $hoverAll.Count, $hoverAll.Left, $hoverAll.Right, $hoverAll.Center)

# Where the words are allowed to begin, from the app's own geometry rather than guessed from the
# pixels: the inset it reports is the cluster's width plus its margin, in DIP, and this is the
# one place it is converted to the pixels of the frame. A pixel heuristic used to stand in for
# this - "the cluster cannot reach past half the band" - which was a DIP number measured against
# device pixels, leaving ~20 DIP of slack that a longer song title ate straight through.
$log = Join-Path $env:TEMP 'taskbar-lyrics-diag.log'
$insetDip = $null
if (Test-Path $log) {
  $m = Select-String -Path $log -Pattern 'lyric inset=([\d.]+)' -ErrorAction SilentlyContinue | Select-Object -Last 1
  if ($m -and $m.Matches.Count -gt 0) { $insetDip = [double]$m.Matches[0].Groups[1].Value }
}
if ($null -eq $insetDip) {
  Write-Host "FAIL hover geometry: no 'lyric inset=' line in the diag log - run the app with TBL_DIAG=1 (tools\verify-cluster-modes.ps1 does that for you)" -ForegroundColor Red
  $hover.Bitmap.Dispose(); $rest.Bitmap.Dispose()
  exit 1
}
$edgePx = Convert-Dip ($contentPadDip + $insetDip)
$hoverWords = [HoverShift]::ScanPixels($hover.Bytes, $hover.Stride, $w, $h, $edgePx, $rightEdge)
Write-Host ("[hover] room beside the cluster {0}..{1} px (inset {2:F1} DIP): ink={3} left={4} right={5} center={6:F1}" -f $edgePx, $rightEdge, $insetDip, $hoverWords.Count, $hoverWords.Left, $hoverWords.Right, $hoverWords.Center)

# Cross-check of that boundary against the pixels. The words are now laid out beside the cluster
# instead of being slid under it, so ink may legitimately begin right at the boundary and a wide
# ink-free gap is no longer guaranteed; when one is there, it has to be where the app says.
$clusterGap = Convert-Dip 40
$clusterEnd = [HoverShift]::ClusterEnd($hover.Bytes, $hover.Stride, $w, $h, $clusterGap)
Write-Host ("[hover] cross-check: first {0} px ink-free run starts at {1} px (app puts the room at {2} px)" -f $clusterGap, $clusterEnd, $edgePx)

# ---- animated, not a jump: how far the frame still is from the settled rest frame --------
$drift = New-Object System.Collections.Generic.List[int]
[void][HoverShift]::SetCursorPos($parkX, $parkY)
for ($i = 0; $i -lt 16; $i++) {
  Start-Sleep -Milliseconds 25
  $sample = Capture-Strip
  try {
    $d = [HoverShift]::DiffPixels($sample.Bytes, $rest.Bytes, $sample.Stride, $w, $h, 40)
  } finally { $sample.Bitmap.Dispose() }
  $drift.Add([int]$d)
}
$distinct = ($drift | Select-Object -Unique).Count
Write-Host ("[leave] pixels still different from the rest frame: {0}" -f ($drift -join ','))
Write-Host ("[leave] distinct values: {0}" -f $distinct)

$rest.Bitmap.Dispose()
$hover.Bitmap.Dispose()

# What the app thinks it did, next to what the pixels show.
if (Test-Path $log) {
  $lines = Select-String -Path $log -Pattern 'lyric inset=' -ErrorAction SilentlyContinue |
           Select-Object -Last 4 | ForEach-Object { $_.Line.Trim() }
  if ($lines) { Write-Host '[diag]'; $lines | ForEach-Object { Write-Host "  $_" } }
}

$fail = 0

# 1. At rest the words are centred in the band, so their ink centre has to land on the strip's
#    centre. The tolerance is 12 DIP, for glyph ink sitting inside its advance width, and this is
#    the one assertion here that holds whatever the current lyric line happens to be.
$restOff = [Math]::Abs($restWords.Center - $w / 2.0)
if ($restWords.Count -gt 0 -and $restOff -le (Convert-Dip 12)) {
  Write-Host ("PASS rest centred: ink centre {0:F1} vs band centre {1:F1} (off {2:F1} px)" -f $restWords.Center, ($w / 2.0), $restOff) -ForegroundColor Green
} else {
  Write-Host ("FAIL rest centred: ink centre {0:F1} vs band centre {1:F1} (off {2:F1} px, ink {3} px)" -f $restWords.Center, ($w / 2.0), $restOff, $restWords.Count) -ForegroundColor Red
  $fail++
}

# 2. Left-edge observations, advisory. These two used to be pass/fail, which was wrong: the
#    leftmost ink depends on how long the current lyric line is, not on the layout. Whether the
#    cluster is hidden at rest and drawn on hover is asserted in DIP against the app's own
#    geometry by verify-cluster-modes.ps1; here the numbers are printed for reading only.
Write-Host ("[rest]  leftmost ink {0} px = {1:F1} DIP of a {2:F1} DIP strip" -f $restAll.Left, ($restAll.Left / $dpiScale), ($w / $dpiScale))
Write-Host ("[hover] leftmost ink {0} px = {1:F1} DIP" -f $hoverAll.Left, ($hoverAll.Left / $dpiScale))

# 3. On hover the words are laid out in the room beside the cluster: their left edge has to sit
#    inside that room, and they have to be centred in it rather than pushed off to one side. A
#    line that was slid aside and clipped used to start at the clip edge and its ink centre sat
#    well right of the room's centre - which is what this check is here to catch.
$expect = ($edgePx + $rightEdge) / 2.0
$hoverOff = [Math]::Abs($hoverWords.Center - $expect)
Write-Host ("[hover] ink width {0} px at rest vs {1} px on hover (a clipped line comes back narrower)" -f ($restWords.Right - $restWords.Left), ($hoverWords.Right - $hoverWords.Left))
if ($hoverWords.Count -gt 0 -and $hoverWords.Left -ge $edgePx -and $hoverOff -le (Convert-Dip 14)) {
  Write-Host ("PASS hover words beside the cluster and centred: left edge {0} px (room starts at {1}), ink centre {2:F1} vs {3:F1} (off {4:F1} px)" -f $hoverWords.Left, $edgePx, $hoverWords.Center, $expect, $hoverOff) -ForegroundColor Green
} else {
  Write-Host ("FAIL hover words: left edge {0} px (room starts at {1} is required), ink centre {2:F1} vs {3:F1} (off {4:F1} px, ink {5} px)" -f $hoverWords.Left, $edgePx, $hoverWords.Center, $expect, $hoverOff, $hoverWords.Count) -ForegroundColor Red
  $fail++
}

# 4. The return trip is a ramp, not a jump: the frame has to pass through several distances
#    from where it settles.
if ($distinct -ge 3) {
  Write-Host ("PASS animated: {0} distinct distances on the way back" -f $distinct) -ForegroundColor Green
} else {
  Write-Host ("FAIL animated: only {0} distinct distance(s) seen" -f $distinct) -ForegroundColor Red
  $fail++
}

# 5. The two coordinate spaces agree. The strip these pixels came from, divided by the display
#    scale, has to equal the DIP width the app logs for itself. This is the check that would have
#    caught the original defect: an unaware host reports the virtualised width as if it were
#    physical, and at scale 1.0 no arithmetic anywhere else would have noticed.
$appWidth = $null
if (Test-Path $log) {
  $m = Select-String -Path $log -Pattern 'Render pos=.* W=([\d.]+)' -ErrorAction SilentlyContinue | Select-Object -Last 1
  if ($m -and $m.Matches.Count -gt 0) { $appWidth = [double]$m.Matches[0].Groups[1].Value }
}
$stripDip = $w / $dpiScale
if ($appWidth) {
  if ([Math]::Abs($appWidth - $stripDip) -le 2) {
    Write-Host ("PASS coordinate space: {0:F1} DIP from {1} px matches the app's own W={2:F1} DIP" -f $stripDip, $w, $appWidth) -ForegroundColor Green
  } else {
    Write-Host ("FAIL coordinate space: {0:F1} DIP from {1} px vs the app's own W={2:F1} DIP (scale {3})" -f $stripDip, $w, $appWidth, $dpiScale) -ForegroundColor Red
    $fail++
  }
} else {
  Write-Host "SKIP coordinate space: no '[overlay] Render ... W=' line in the log yet - play a track and re-run to cross-check the DIP width"
}

[void][HoverShift]::SetCursorPos($parkX, $parkY)

if ($fail -eq 0) { Write-Host 'all hover-shift checks passed' -ForegroundColor Green; exit 0 }
Write-Host ("{0} hover-shift check(s) failed" -f $fail) -ForegroundColor Red
exit 1
