# Takes the README screenshots: each menu of the running tray in its default orange, on a plain backdrop.
#   powershell -ExecutionPolicy Bypass -File tools\screenshots.ps1 [-OutDir docs\screenshots]
# The tray must be running. Only komorebi-tray's own windows
# are captured; keys are posted to those windows only, nothing is typed globally. "Match Windows accent" is
# switched off for the run so the menus use the default orange, and put back afterwards.
param([string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\screenshots'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text; using System.Collections.Generic;
public static class Shot {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(P f, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, P f, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  // The biggest visible window of a process (the volume mixer has no title)
  public static IntPtr Biggest(uint pid) {
    IntPtr best = IntPtr.Zero; long area = 0;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p != pid || !IsWindowVisible(h)) return true;
      RECT r; GetWindowRect(h, out r); long a = (long)(r.R - r.L) * (r.B - r.T); if (a > area && a < 4000000) { area = a; best = h; } return true; }, IntPtr.Zero);
    return best;
  }
  // Keys go to the window's text box when it has one (where focus sits), else its first child
  public static IntPtr KeyTarget(IntPtr top) {
    IntPtr first = IntPtr.Zero, edit = IntPtr.Zero;
    EnumChildWindows(top, (h, l) => { if (first == IntPtr.Zero) first = h; var c = new StringBuilder(256); GetClassName(h, c, 256);
      if (edit == IntPtr.Zero && c.ToString().ToUpperInvariant().Contains("EDIT")) edit = h; return true; }, IntPtr.Zero);
    return edit != IntPtr.Zero ? edit : first != IntPtr.Zero ? first : top;
  }
}
'@

$tray = Get-Process komorebi-tray -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $tray) { throw 'Start komorebi-tray first.' }
$exe = $tray.Path
$prefsPath = Join-Path $env:USERPROFILE '.config\komorebi\komorebi-tray.json'
if ($env:KOMOREBI_CONFIG_HOME) { $prefsPath = Join-Path $env:KOMOREBI_CONFIG_HOME 'komorebi-tray.json' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$Margin = 36
$dot = [char]0x00B7   # the titles' middle dot, spelled out so this file reads the same in any code page

function Find([string]$title) {
    for ($i = 0; $i -lt 40; $i++) { $h = [Shot]::FindWindow([NullString]::Value, $title); if ($h -ne [IntPtr]::Zero) { return $h }; Start-Sleep -Milliseconds 100 }
    throw "window not found: $title"
}
function Key([IntPtr]$h, [int]$vk, [int]$times = 1) {
    $t = [Shot]::KeyTarget($h)
    for ($i = 0; $i -lt $times; $i++) {
        [Shot]::PostMessage($t, 0x0100, [IntPtr]$vk, [IntPtr]1) | Out-Null
        [Shot]::PostMessage($t, 0x0101, [IntPtr]$vk, [IntPtr]0xC0000001) | Out-Null
        Start-Sleep -Milliseconds 120
    }
}
function TypeText([IntPtr]$h, [string]$text) {
    $t = [Shot]::KeyTarget($h)
    foreach ($c in $text.ToCharArray()) { [Shot]::PostMessage($t, 0x0102, [IntPtr][int]$c, [IntPtr]1) | Out-Null; Start-Sleep -Milliseconds 40 }
    Start-Sleep -Milliseconds 300
}
function Grab([IntPtr]$h, [string]$name) {
    Start-Sleep -Milliseconds 450   # the intro animation
    $r = New-Object Shot+RECT
    if ([Shot]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [Shot]::GetWindowRect($h, [ref]$r) | Out-Null }
    $x = $r.L - $Margin; $y = $r.T - $Margin; $w = $r.R - $r.L + 2 * $Margin; $hh = $r.B - $r.T + 2 * $Margin
    $bmp = New-Object Drawing.Bitmap $w, $hh
    $g = [Drawing.Graphics]::FromImage($bmp)
    # Margin that falls off the screen (the volume mixer sits at its top edge) gets the backdrop colour
    $g.Clear([Drawing.Color]::FromArgb(30, 31, 35))
    $vs = [Windows.Forms.SystemInformation]::VirtualScreen
    $sx = [Math]::Max($x, $vs.Left); $sy = [Math]::Max($y, $vs.Top)
    $sw = [Math]::Min($x + $w, $vs.Right) - $sx; $sh = [Math]::Min($y + $hh, $vs.Bottom) - $sy
    if ($sw -gt 0 -and $sh -gt 0) { $g.CopyFromScreen($sx, $sy, $sx - $x, $sy - $y, (New-Object Drawing.Size $sw, $sh)) }
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir $name), [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  $name  ($w x $hh)"
}
function Open([string]$flag, [string]$title) { Start-Process $exe -ArgumentList $flag; Find $title }
function Close([IntPtr]$h) { Key $h 0x1B; Start-Sleep -Milliseconds 400 }

# Default orange for the run
# Sets (or, with $null, removes) one top-level key of komorebi-tray.json and returns its previous JSON text
function SetPref([string]$key, $json) {
    $t = if (Test-Path $prefsPath) { [IO.File]::ReadAllText($prefsPath) } else { '{}' }
    $pattern = '"' + [regex]::Escape($key) + '"\s*:\s*("[^"]*"|true|false|-?\d+)'
    $m = [regex]::Match($t, $pattern)
    $old = if ($m.Success) { $m.Groups[1].Value } else { $null }
    if ($json -eq $null) { if ($m.Success) { $t = [regex]::Replace($t, ',?\s*' + $pattern, '') } }
    elseif ($m.Success) { $t = [regex]::Replace($t, $pattern, ('"' + $key + '": ' + $json)) }
    else { $t = $t.TrimEnd() -replace '\}$', ((if2 ($t -match '\{\s*\}$') '' ',') + "`n  `"$key`": $json`n}") }
    [IO.File]::WriteAllText($prefsPath, $t, (New-Object Text.UTF8Encoding $false))
    return $old
}
function if2($c, $a, $b) { if ($c) { $a } else { $b } }

# The defaults for the run: orange menus
$restore = @{}

$backdrop = $null
try {
    $restore['border_follows_accent'] = SetPref 'border_follows_accent' 'false'

    # A plain backdrop behind the menus (same gradient as the earlier screenshots)
    $backdrop = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', @'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$f = New-Object Windows.Forms.Form
$f.FormBorderStyle = 'None'; $f.ShowInTaskbar = $false; $f.TopMost = $true; $f.StartPosition = 'Manual'
$f.Bounds = [Windows.Forms.SystemInformation]::VirtualScreen
$f.Add_Paint({ param($s, $e) $b = New-Object Drawing.Drawing2D.LinearGradientBrush($f.ClientRectangle, [Drawing.Color]::FromArgb(30, 31, 35), [Drawing.Color]::FromArgb(21, 22, 25), 45); $e.Graphics.FillRectangle($b, $f.ClientRectangle); $b.Dispose() })
[Windows.Forms.Application]::Run($f)
'@
    Start-Sleep 1.5

    Write-Host 'keybinds'
    $h = Open '--shortcuts' "komorebi $dot Keybindings"; Grab $h 'keybinds.png'
    TypeText $h 'focus'; Grab $h 'keybinds-search.png'; Close $h

    Write-Host 'launcher'
    $h = Open '--apps' "komorebi $dot Apps"; TypeText $h 'windows'; Start-Sleep 0.6; Grab $h 'launcher.png'; Close $h

    Write-Host 'settings'
    $h = Open '--settings' "komorebi $dot Settings"
    Key $h 0x22 1; Grab $h 'settings-borders.png'
    Key $h 0x22 2; Grab $h 'settings-animation.png'
    Key $h 0x28 3; Key $h 0x0D; Grab $h 'settings-picker.png'; Key $h 0x1B; Close $h

    Write-Host 'volume mixer'
    Start-Process $exe -ArgumentList '--volume'; Start-Sleep 1
    $h = [Shot]::Biggest([uint32]$tray.Id); Grab $h 'volume-mixer.png'
    Start-Process $exe -ArgumentList '--volume'; Start-Sleep 0.6

    $setup = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\komorebi-tray-setup.exe'
    if (Test-Path $setup) {
        Write-Host 'installer'
        # The installer's test mode installs into a scratch folder (no tasks, nothing started, its own
        # "-test" uninstall entry), so both screens can be shown without touching the real install
        $root = Join-Path $env:TEMP ('komorebi-tray-shots-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Force $root | Out-Null
        try {
            $p = Start-Process $setup -PassThru -ArgumentList "/root:$root"
            $h = Find 'komorebi-tray setup'; Start-Sleep 0.8; Grab $h 'installer.png'
            Key $h 0x0D; Start-Sleep 4; Grab $h 'installer-done.png'
            $p.CloseMainWindow() | Out-Null; Start-Sleep 0.5; if (-not $p.HasExited) { $p.Kill() }
            Start-Process $setup -Wait -ArgumentList '/uninstall', '/S', "/root:$root"
        }
        finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
finally {
    if ($backdrop -and -not $backdrop.HasExited) { $backdrop.Kill() }
    # Put the user's own values back, key by key (the tray may have written other keys meanwhile)
    foreach ($k in $restore.Keys) { SetPref $k $restore[$k] | Out-Null }
}
