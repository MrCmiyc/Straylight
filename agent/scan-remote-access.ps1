# Look for remote-control / VPN / tunnel software, extra logon sessions, and what is
# asserting "stay awake / do not idle". Run locally (e.g. via PsExec -s). Read-only.
$ErrorActionPreference = 'SilentlyContinue'
$sig = 'teamviewer|anydesk|rustdesk|parsec|splashtop|logmein|gotoassist|screenconnect|connectwise|tightvnc|ultravnc|realvnc|tvnserver|winvnc|vncserver|radmin|dwagent|supremo|ammyy|remoteutilities|remotepc|remoting_host|chromoting|nomachine|dameware|atera|ngrok|tailscale|zerotier|wireguard|openvpn|nordvpn|expressvpn|protonvpn|hamachi|softether|windscribe|mullvad|tunnelbear|playit|localtonet|cloudflared|warp-svc|quickassist|getscreen|iperius|aeroadmin|litemanager'

"==== SESSIONS (quser): any rdp-tcp session means someone remoted in ===="
quser 2>&1
""
"==== powercfg /requests: what is blocking idle/sleep (e.g. a game) ===="
powercfg /requests 2>&1
""
"==== PROCESSES matching remote/VPN signatures ===="
$hit = Get-CimInstance Win32_Process | Where-Object { $_.Name -match $sig -or $_.CommandLine -match $sig }
if ($hit) { $hit | Select-Object ProcessId,Name,CommandLine | Format-List } else { "(none)" }
""
"==== SERVICES matching signatures ===="
$svc = Get-CimInstance Win32_Service | Where-Object { $_.Name -match $sig -or $_.DisplayName -match $sig -or $_.PathName -match $sig }
if ($svc) { $svc | Select-Object Name,DisplayName,State,StartMode,PathName | Format-List } else { "(none)" }
""
"==== LISTENING ports (watch 3389 RDP, 5900 VNC, 5938 TeamViewer, 6568 AnyDesk) ===="
Get-NetTCPConnection -State Listen | Select-Object LocalAddress,LocalPort,@{n='Proc';e={(Get-Process -Id $_.OwningProcess).Name}} | Sort-Object LocalPort | Format-Table -AutoSize
""
"==== ESTABLISHED to EXTERNAL (non-LAN) addresses ===="
Get-NetTCPConnection -State Established | Where-Object { $_.RemoteAddress -notmatch '^(127\.|192\.168\.|10\.|169\.254\.|172\.(1[6-9]|2\d|3[01])\.)' -and $_.RemoteAddress -notmatch ':' } | Select-Object RemoteAddress,RemotePort,@{n='Proc';e={(Get-Process -Id $_.OwningProcess).Name}} | Sort-Object Proc | Format-Table -AutoSize
