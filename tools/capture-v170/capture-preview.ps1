# QuotaMonitor v1.7.0 real-preview screen capture (offline, synthetic fixture data only)
#
# Launches the packaged app in `--preview-v170` mode (production WPF window driven by the
# app's synthetic preview fixture; no adapters, credentials or account data), records the
# app window region with ffmpeg gdigrab (lossless FFV1), scripts the documented preview
# hotkeys / real mouse hover, and writes per-take action timestamps for later GIF editing.
#
# Usage (from repository root, Windows, ffmpeg on PATH):
#   pwsh -File tools/capture-v170/capture-preview.ps1 `
#        -Exe "<repo>\publish\v1.7.0-stage\win-x64\QuotaMonitor.exe" `
#        -OutDir "<repo>\evidence\v1.7.0\visual\capture" `
#        -WorkDir "<repo>\evidence\v1.7.0\visual\preview"
#
# Output: <OutDir>/<take>.mkv, <OutDir>/<take>-final.png, <OutDir>/baseline.png,
#         <OutDir>/takes.json (window rect, DPI, fps, per-action milliseconds).
# The app is closed at the end. Only the app window rectangle is captured, never the desktop.

param(
    [Parameter(Mandatory = $true)] [string]$Exe,
    [Parameter(Mandatory = $true)] [string]$OutDir,
    [string]$WorkDir = "",
    [int]$Fps = 30,
    [switch]$KeepAppRunning
)

$ErrorActionPreference = 'Stop'
$Exe = [IO.Path]::GetFullPath($Exe)
$OutDir = [IO.Path]::GetFullPath($OutDir)
if ([string]::IsNullOrWhiteSpace($WorkDir)) { $WorkDir = Join-Path (Split-Path $OutDir -Parent) 'preview' }
$WorkDir = [IO.Path]::GetFullPath($WorkDir)
New-Item -ItemType Directory -Force -Path $OutDir, $WorkDir | Out-Null
if (-not (Test-Path -LiteralPath $Exe)) { throw "Preview executable not found: $Exe" }
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class PreviewWin32
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
'@

# Physical-pixel coordinates: make this PowerShell process per-monitor DPI aware before any
# Win32 window/rect/cursor call, so GetWindowRect and SetCursorPos use the same units as gdigrab.
try { [PreviewWin32]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch { }
try { [PreviewWin32]::SetProcessDPIAware() | Out-Null } catch { }

function Get-AppWindow([System.Diagnostics.Process]$Process) {
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) { throw "Preview process exited early (code $($Process.ExitCode))." }
        $handle = $Process.MainWindowHandle
        if ($handle -ne [IntPtr]::Zero) {
            $title = [Text.StringBuilder]::new(256)
            [PreviewWin32]::GetWindowText($handle, $title, $title.Capacity) | Out-Null
            if ($title.ToString() -like 'QuotaMonitor*') { return [IntPtr]$handle }
        }
        Start-Sleep -Milliseconds 120
    }
    throw 'Timed out waiting for the QuotaMonitor preview window.'
}

function Focus-Window([IntPtr]$Handle) {
    $foreground = [PreviewWin32]::GetForegroundWindow()
    $foregroundThread = [PreviewWin32]::GetWindowThreadProcessId($foreground, [IntPtr]::Zero)
    $targetThread = [PreviewWin32]::GetWindowThreadProcessId($Handle, [IntPtr]::Zero)
    $attached = $false
    if ($foregroundThread -ne 0 -and $targetThread -ne 0 -and $foregroundThread -ne $targetThread) {
        $attached = [PreviewWin32]::AttachThreadInput($targetThread, $foregroundThread, $true)
    }
    try {
        [PreviewWin32]::SetForegroundWindow($Handle) | Out-Null
        [PreviewWin32]::SetFocus($Handle) | Out-Null
    } finally {
        if ($attached) { [PreviewWin32]::AttachThreadInput($targetThread, $foregroundThread, $false) | Out-Null }
    }
    Start-Sleep -Milliseconds 200
}

# Sends a synthetic WM_KEYDOWN/WM_KEYUP for a supported preview hotkey.
function Send-Key([IntPtr]$Handle, [string]$Key) {
    $virtualKeys = @{ Escape = 0x1B; F5 = 0x74; F6 = 0x75; F7 = 0x76; F8 = 0x77; F10 = 0x79; F11 = 0x7A; F12 = 0x7B }
    if (-not $virtualKeys.ContainsKey($Key)) { throw "Unsupported preview key: $Key" }
    $virtual = [uint32]$virtualKeys[$Key]
    $scan = [PreviewWin32]::MapVirtualKey($virtual, 0)
    $down = [IntPtr]([int64](1 -bor ($scan -shl 16)))
    $up = [IntPtr]([int64](0xC0000001 -bor ($scan -shl 16)))
    [PreviewWin32]::PostMessage($Handle, 0x0100, [IntPtr]$virtual, $down) | Out-Null
    Start-Sleep -Milliseconds 35
    [PreviewWin32]::PostMessage($Handle, 0x0101, [IntPtr]$virtual, $up) | Out-Null
    Start-Sleep -Milliseconds 60
}

function Move-Cursor([int]$X, [int]$Y) {
    [PreviewWin32]::SetCursorPos($X, $Y) | Out-Null
}

function Invoke-StateProbe([IntPtr]$Handle, [string]$Expected, [string]$StateFile) {
    $before = if (Test-Path -LiteralPath $StateFile) { (Get-Item -LiteralPath $StateFile).LastWriteTimeUtc } else { [datetime]::MinValue }
    Send-Key $Handle 'F8'
    $deadline = (Get-Date).AddSeconds(3)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path -LiteralPath $StateFile) -and (Get-Item -LiteralPath $StateFile).LastWriteTimeUtc -gt $before) {
            $state = (Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json).Window.State
            if ($state -ne $Expected) { throw "Unexpected island state: expected '$Expected', probe reported '$state'." }
            return $state
        }
        Start-Sleep -Milliseconds 60
    }
    throw "State probe did not refresh: $StateFile"
}

function Get-Rect([IntPtr]$Handle) {
    $rect = New-Object PreviewWin32+RECT
    if (-not [PreviewWin32]::GetWindowRect($Handle, [ref]$rect)) { throw 'GetWindowRect failed.' }
    return @{ Left = $rect.Left; Top = $rect.Top; Width = $rect.Right - $rect.Left; Height = $rect.Bottom - $rect.Top }
}

function Start-Recorder([string]$Path, [hashtable]$Region, [int]$DurationMs) {
    $arguments = @(
        '-y', '-hide_banner', '-loglevel', 'error',
        '-f', 'gdigrab', '-framerate', "$Fps", '-draw_mouse', '1',
        '-video_size', "$($Region.Width)x$($Region.Height)",
        '-offset_x', "$($Region.Left)", '-offset_y', "$($Region.Top)",
        '-i', 'desktop',
        '-t', ('{0:0.###}' -f ($DurationMs / 1000.0)),
        '-an', '-sn', '-dn',
        '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '16', '-pix_fmt', 'yuv420p', '-threads', '0',
        $Path
    )
    return Start-Process -FilePath $ffmpeg -ArgumentList $arguments -PassThru -WindowStyle Hidden
}

function Save-Still([string]$Path, [hashtable]$Region) {
    $arguments = @(
        '-y', '-hide_banner', '-loglevel', 'error',
        '-f', 'gdigrab', '-framerate', '1', '-draw_mouse', '0',
        '-video_size', "$($Region.Width)x$($Region.Height)",
        '-offset_x', "$($Region.Left)", '-offset_y', "$($Region.Top)",
        '-i', 'desktop', '-frames:v', '1', $Path
    )
    & $ffmpeg @arguments
    if ($LASTEXITCODE -ne 0) { throw "Still capture failed: $Path" }
}

function Wait-Until([System.Diagnostics.Stopwatch]$Clock, [int]$Milliseconds) {
    $remaining = $Milliseconds - $Clock.ElapsedMilliseconds
    if ($remaining -gt 0) { Start-Sleep -Milliseconds $remaining }
}

function Wait-StableRect([IntPtr]$Handle) {
    $previous = $null
    for ($i = 0; $i -lt 60; $i++) {
        $rect = Get-Rect $Handle
        $key = "$($rect.Left),$($rect.Top),$($rect.Width),$($rect.Height)"
        if ($key -eq $previous) { return $rect }
        $previous = $key
        Start-Sleep -Milliseconds 150
    }
    throw 'The preview window rectangle did not settle.'
}

$process = $null
$recorder = $null
$takeRecords = @()
try {
    Write-Host "Launching preview: $Exe"
    $process = Start-Process -FilePath $Exe -ArgumentList '--preview-v170' -WorkingDirectory $WorkDir -PassThru
    $handle = Get-AppWindow $process

    $verification = Join-Path $WorkDir 'evidence/v1.7.0/desktop/verification.json'
    $stateFile = Join-Path $WorkDir 'evidence/v1.7.0/desktop/desktop-state.json'
    $deadline = (Get-Date).AddSeconds(45)
    while (-not (Test-Path -LiteralPath $verification)) {
        if ((Get-Date) -gt $deadline) { throw "Preview fixture did not become ready: $verification" }
        Start-Sleep -Milliseconds 200
    }
    Start-Sleep -Milliseconds 700
    Focus-Window $handle
    Invoke-StateProbe $handle 'Expanded' $stateFile | Out-Null
    Start-Sleep -Milliseconds 500
    # Read the rectangle only after the preview has positioned the window and grown it to
    # the expanded height; both happen asynchronously after the first window handle appears.
    $region = Wait-StableRect $handle
    $dpi = [PreviewWin32]::GetDpiForWindow($handle) / 96.0
    Write-Host ("Window: {0}x{1} at {2},{3} · DPI {4:0.##}x" -f $region.Width, $region.Height, $region.Left, $region.Top, $dpi)
    Move-Cursor 120 1400
    Start-Sleep -Milliseconds 300
    Save-Still (Join-Path $OutDir 'baseline.png') $region

    # Take 1: real Minimal -> Collapsed -> Expanded -> Minimal island states.
    $take = 'morph'
    $duration = 9500
    $actions = @()
    $path = Join-Path $OutDir "$take.mkv"
    $recorder = Start-Recorder $path $region $duration
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    Wait-Until $clock 900
    Send-Key $handle 'Escape'; $actions += @{ atMs = 900; action = 'Escape (Minimal)' }
    Wait-Until $clock 2300
    Invoke-StateProbe $handle 'Minimal' $stateFile | Out-Null
    Send-Key $handle 'F6'; $actions += @{ atMs = 2400; action = 'F6 (Collapsed, immediate switch)' }
    Wait-Until $clock 3600
    Invoke-StateProbe $handle 'Collapsed' $stateFile | Out-Null
    Send-Key $handle 'F7'; $actions += @{ atMs = 3700; action = 'F7 (Expanded morph)' }
    Wait-Until $clock 5600
    Invoke-StateProbe $handle 'Expanded' $stateFile | Out-Null
    Send-Key $handle 'Escape'; $actions += @{ atMs = 5700; action = 'Escape (Minimal morph)' }
    Wait-Until $clock 7200
    Invoke-StateProbe $handle 'Minimal' $stateFile | Out-Null
    Wait-Until $clock $duration
    if (-not $recorder.WaitForExit(20000)) { throw 'ffmpeg recorder did not exit.' }
    if ($recorder.ExitCode -ne 0) { throw "ffmpeg recorder failed with code $($recorder.ExitCode)." }
    Save-Still (Join-Path $OutDir "$take-final.png") $region
    $takeRecords += [ordered]@{ name = $take; file = "$take.mkv"; durationMs = $duration; actions = $actions }

    # Take 2: four-provider quota overview, collapsed limiting values, low-quota alert capsule.
    # Starts in Minimal (the morph take ends there), so the alert capsule is the opening frame.
    $take = 'quota'
    $duration = 9000
    $actions = @()
    $path = Join-Path $OutDir "$take.mkv"
    $recorder = Start-Recorder $path $region $duration
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    Wait-Until $clock 700
    Send-Key $handle 'F7'; $actions += @{ atMs = 700; action = 'F7 (Expanded morph, four-provider quota)' }
    Wait-Until $clock 3000
    Invoke-StateProbe $handle 'Expanded' $stateFile | Out-Null
    Send-Key $handle 'F6'; $actions += @{ atMs = 3000; action = 'F6 (Collapsed, limiting values)' }
    Wait-Until $clock 4800
    Invoke-StateProbe $handle 'Collapsed' $stateFile | Out-Null
    Send-Key $handle 'Escape'; $actions += @{ atMs = 4800; action = 'Escape (Alert Minimal capsule)' }
    Wait-Until $clock 7000
    Invoke-StateProbe $handle 'Minimal' $stateFile | Out-Null
    Send-Key $handle 'F7'; $actions += @{ atMs = 7000; action = 'F7 (Expanded again)' }
    Wait-Until $clock $duration
    if (-not $recorder.WaitForExit(20000)) { throw 'ffmpeg recorder did not exit.' }
    if ($recorder.ExitCode -ne 0) { throw "ffmpeg recorder failed with code $($recorder.ExitCode)." }
    Save-Still (Join-Path $OutDir "$take-final.png") $region
    $takeRecords += [ordered]@{ name = $take; file = "$take.mkv"; durationMs = $duration; actions = $actions }

    # Take 3: heatmap hover, real tooltip positions, huge-value edge state, legend focus, mask reflow.
    # Tile/legend coordinates are derived from the identical production layout captured offline
    # in evidence/v1.7.0/visual/showcase/geometry-expanded-2x.json (same window size and DPI).
    Invoke-StateProbe $handle 'Expanded' $stateFile | Out-Null
    $geometryFile = Join-Path (Split-Path $OutDir -Parent) 'showcase/geometry-expanded-2x.json'
    if (-not (Test-Path -LiteralPath $geometryFile)) { throw "Missing layout geometry: $geometryFile" }
    $geometry = Get-Content -LiteralPath $geometryFile -Raw | ConvertFrom-Json
    $scale = $dpi
    function ToScreen([double]$x, [double]$y) { return @{ X = [int]($region.Left + $x * $scale); Y = [int]($region.Top + $y * $scale) } }
    $heatmap = $geometry.Heatmap
    $pitch = ($heatmap.W - 26) / 26.0
    function Tile-Center([int]$column, [int]$row) {
        $x = $heatmap.X + 26 + $column * $pitch + ($pitch - 18) / 2.0 + 9
        $y = $heatmap.Y + 20 + $row * 24 + (24 - 18) / 2.0 + 9
        return (ToScreen $x $y)
    }
    $legend = $geometry.Legend
    $take = 'heatmap'
    $duration = 15000
    $actions = @()
    $path = Join-Path $OutDir "$take.mkv"
    $recorder = Start-Recorder $path $region $duration
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $hoverColumns = @(@{ c = 19; r = 2 }, @{ c = 21; r = 4 }, @{ c = 23; r = 1 }, @{ c = 24; r = 5 })
    $at = 1000
    foreach ($hover in $hoverColumns) {
        Wait-Until $clock $at
        $point = Tile-Center $hover.c $hover.r
        Move-Cursor $point.X $point.Y
        $actions += @{ atMs = $at; action = "hover heatmap column $($hover.c) row $($hover.r)" }
        $at += 1000
    }
    Wait-Until $clock $at
    Send-Key $handle 'F11'; $actions += @{ atMs = $at; action = 'F11 (heatmap hover, rightmost week)' }
    $at += 1100
    Wait-Until $clock $at
    Send-Key $handle 'F12'; $actions += @{ atMs = $at; action = 'F12 (large synthetic values + today hover)' }
    $at += 1600
    Wait-Until $clock $at
    $legendPoint = ToScreen ($legend.X + 34) ($legend.Y + 13)
    Move-Cursor $legendPoint.X $legendPoint.Y
    $actions += @{ atMs = $at; action = 'hover Codex legend chip (single-platform focus)' }
    $at += 2200
    Wait-Until $clock $at
    $point = Tile-Center 20 4
    Move-Cursor $point.X $point.Y
    $actions += @{ atMs = $at; action = 'hover heatmap date (focus crossfades back)' }
    $at += 1900
    Wait-Until $clock $at
    Send-Key $handle 'F10'; $actions += @{ atMs = $at; action = 'F10 (provider mask 13, 3-provider reflow)' }
    Start-Sleep -Milliseconds 700
    $point = Tile-Center 22 3
    Move-Cursor $point.X $point.Y
    $actions += @{ atMs = $at + 700; action = 'hover heatmap after reflow' }
    $at += 2400
    Wait-Until $clock $at
    Move-Cursor 120 1400
    $actions += @{ atMs = $at; action = 'cursor leaves island' }
    Wait-Until $clock $duration
    if (-not $recorder.WaitForExit(20000)) { throw 'ffmpeg recorder did not exit.' }
    if ($recorder.ExitCode -ne 0) { throw "ffmpeg recorder failed with code $($recorder.ExitCode)." }
    Save-Still (Join-Path $OutDir "$take-final.png") $region
    $takeRecords += [ordered]@{ name = $take; file = "$take.mkv"; durationMs = $duration; actions = $actions; note = 'Tile/legend positions computed from offline Showcase geometry of the identical production layout.' }

    [ordered]@{
        Tool = 'tools/capture-v170/capture-preview.ps1'
        Method = 'Real screen capture of the packaged QuotaMonitor --preview-v170 window (production WPF UI, synthetic preview fixture). Only the app window rectangle is captured.'
        CapturedUtc = [DateTimeOffset]::UtcNow
        Executable = $Exe
        ExecutableSha256 = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
        Fps = $Fps
        DpiScale = $dpi
        Window = [ordered]@{ Left = $region.Left; Top = $region.Top; Width = $region.Width; Height = $region.Height }
        SyntheticOnly = $true
        Takes = $takeRecords
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutDir 'takes.json') -Encoding utf8
    Write-Host "Wrote captures to $OutDir"
} finally {
    if ($recorder -and -not $recorder.HasExited) { try { $recorder.Kill() } catch { } }
    if (-not $KeepAppRunning -and $process -and -not $process.HasExited) {
        try { $process.CloseMainWindow() | Out-Null; if (-not $process.WaitForExit(4000)) { $process.Kill() } } catch { }
    }
    Move-Cursor 120 1400
}
