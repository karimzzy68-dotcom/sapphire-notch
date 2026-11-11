# Sapphire NOTCh agent hook: records what a Claude Code session is doing so the notch can show who needs you.
# Only writes a small status file on this PC (project, branch, one-line message). Sends nothing anywhere and never changes Claude's decisions.
param([string]$EventName = 'Event')
try {
    $raw = [Console]::In.ReadToEnd()
    $j = $raw | ConvertFrom-Json
    $dir = Join-Path $env:LOCALAPPDATA 'Sapphire NOTCh\agents'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $file = Join-Path $dir ("{0}.json" -f $j.session_id)
    if ($EventName -eq 'SessionEnd') { Remove-Item $file -Force -ErrorAction SilentlyContinue; exit 0 }
    $cwd = [string]$j.cwd
    $branch = ''
    try { $branch = [string](git -C $cwd rev-parse --abbrev-ref HEAD 2>$null) } catch { }
    # Find the window this session lives in (terminal or editor), once, so the notch can jump straight to it.
    $hwnd = 0
    try { $hwnd = [int64](Get-Content $file -Raw -ErrorAction Stop | ConvertFrom-Json).hwnd } catch { }
    if ($hwnd -eq 0) {
        $procId = $PID
        for ($i = 0; $i -lt 12 -and $procId; $i++) {
            $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
            if ($p -and $p.MainWindowHandle -ne 0) { $hwnd = [int64]$p.MainWindowHandle; break }
            $procId = (Get-CimInstance Win32_Process -Filter "ProcessId=$procId" -ErrorAction SilentlyContinue).ParentProcessId
        }
    }
    $status = switch ($EventName) { 'Notification' { 'needs' } 'Stop' { 'idle' } default { 'working' } }
    $msg = switch ($EventName) {
        'Notification' { [string]$j.message }
        'Stop' { 'Finished. Waiting for your next prompt' }
        default { 'Working on your prompt' }
    }
    @{ session = [string]$j.session_id; cwd = $cwd; project = (Split-Path $cwd -Leaf); branch = $branch
       status = $status; message = $msg; time = (Get-Date).ToString('o'); agent = 'Claude Code'; hwnd = $hwnd } |
        ConvertTo-Json | Set-Content $file -Encoding utf8
} catch { }
exit 0
