<#
.SYNOPSIS
  Agentless "is pc-2 being used, and what's running" check, over SMB/PsExec.

.DESCRIPTION
  Three signals, no agent required:
    1. Online      - SMB/445 reachable (ICMP ping is firewalled on pc-2, so don't use ping).
    2. In use      - `quser` reports the interactive session's STATE and IDLE TIME. Idle of
                     none/0 means active input within the last minute = actively using it.
    3. What's run  - tasklist executed on the box via PsExec (RPC tasklist /s hangs because
                     RPC dynamic ports are firewalled), written to a temp file and read back
                     over the admin share, then filtered to a watchlist of notable apps.

  Read-only. Run as often as you like (e.g. a dashboard poll).

.PARAMETER AllProcesses  Show every process, not just the watchlist.
.PARAMETER Raw           Return an object instead of formatted text (for scripting/dashboards).

.EXAMPLE
  .\Get-DeviceActivity.ps1
#>
[CmdletBinding()]
param(
    [string]$ComputerName = 'pc-2',
    [string]$PsExec       = 'D:\apps\bin\PSTools\psexec.exe',
    [string[]]$Watch = @(
        'Discord','Steam','steamwebhelper','Roblox','chrome','msedge','firefox','opera',
        'Lunar','Minecraft','javaw','Epic','VALORANT','Fortnite','CapCut','Spotify',
        'obs','Medal','BlueStacks','HD-Player','vlc'
    ),
    [switch]$AllProcesses,
    [switch]$Raw
)

# 1. Online?
$online = (Test-NetConnection $ComputerName -Port 445 -WarningAction SilentlyContinue).TcpTestSucceeded
if (-not $online) {
    if ($Raw) { return [pscustomobject]@{ Online=$false } }
    Write-Host "$ComputerName is OFFLINE (SMB/445 unreachable)." -ForegroundColor Red
    return
}

# 2. Session / idle
$user=$null; $state=$null; $idle=$null; $logon=$null; $inUse=$false
try {
    $q = quser /server:$ComputerName 2>$null
    $row = ($q | Select-Object -Skip 1 | Select-Object -First 1) -replace '^\s*>?',''
    if ($row) {
        $f = $row -split '\s{2,}'
        # columns: USERNAME SESSIONNAME ID STATE IDLE LOGON  (SESSIONNAME may be blank)
        $user = $f[0]; $state = $f[-3]; $idle = $f[-2]; $logon = $f[-1]
        $inUse = ($state -eq 'Active') -and ($idle -in @('none','.','') -or ($idle -match '^\d+$' -and [int]$idle -lt 5))
    }
} catch {}

# 3. Processes via PsExec -> file -> read over SMB
$remoteCsv = "C:\Windows\Temp\activity-tasklist.csv"
& $PsExec "\\$ComputerName" -accepteula -n 10 cmd /c "tasklist /fo csv > $remoteCsv" 2>$null | Out-Null
$share = "\\$ComputerName\c`$\Windows\Temp\activity-tasklist.csv"
$running = @()
if (Test-Path $share) {
    $procs = Import-Csv $share
    $sel = if ($AllProcesses) { $procs } else {
        $procs | Where-Object { $n=$_.'Image Name'; $Watch | Where-Object { $n -like "*$_*" } }
    }
    $running = $sel | Group-Object 'Image Name' | ForEach-Object {
        $kb = ($_.Group | ForEach-Object { [int]($_.'Mem Usage' -replace '[^\d]','') } | Measure-Object -Sum).Sum
        [pscustomobject]@{ App=$_.Name; Count=$_.Count; MemMB=[math]::Round($kb/1024) }
    } | Sort-Object MemMB -Descending
}

if ($Raw) {
    return [pscustomobject]@{
        Online=$true; User=$user; State=$state; Idle=$idle; InUse=$inUse
        LogonTime=$logon; Running=$running
    }
}

# Formatted output
$verdict = if (-not $user) { "ON, but NO ONE logged in (logged off or lock screen)" }
           elseif ($inUse) { "IN USE - $user is active right now" }
           else            { "ON - $user logged in but idle ($idle)" }
$color = if ($inUse) {'Green'} elseif ($user) {'Yellow'} else {'Gray'}
Write-Host $verdict -ForegroundColor $color
if ($user) { Write-Host "session: $state | idle: $idle | logon: $logon" }
if ($running) {
    Write-Host "running:"
    $running | Format-Table -AutoSize
} elseif ($user) { Write-Host "(no watchlist apps running)" }
