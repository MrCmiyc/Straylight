<#
.SYNOPSIS
  Collect lightweight usage telemetry for a machine and publish it to MQTT with Home
  Assistant auto-discovery. Runs locally on the target as a hidden SYSTEM scheduled task
  every 15 minutes. Read-only collection; dependency-free MQTT (raw TCP, no extra software).

.DESCRIPTION
  Config (non-secret): C:\ProgramData\Straylight\mqtt.json
    { "host":"mqtt-host","port":1883,"username":"","tls":false,
      "base_topic":"pc-2","discovery_prefix":"homeassistant","device_name":"PC 2" }
  Secret: C:\ProgramData\Straylight\mqtt.pass  (DPAPI LocalMachine-encrypted password)

  -Discovery   Force (re)publish of HA discovery config topics (retained). Otherwise
               discovery is published once, tracked by a marker file.
  -NoPublish   Collect and print JSON only; do not connect to MQTT (for testing).

  Telemetry JSON shape:
    ts, online, user, state, idle_seconds, active, browsers[], top_apps[{name,mem_mb}], process_count
#>
[CmdletBinding()]
param(
    [string]$ConfigPath = 'C:\ProgramData\Straylight\mqtt.json',
    [int]$IdleActiveSeconds = 300,
    [int]$Top = 5,
    [switch]$Discovery,
    [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'
$stateDir = Split-Path -Parent $ConfigPath   # state/marker/pass live next to the config

# ============================ COLLECT ============================
function Get-Telemetry {
    param([int]$IdleActiveSeconds,[int]$Top)

    $csv = tasklist /fo csv /nh 2>$null | ConvertFrom-Csv -Header Name,PID,SessionName,SessionNum,Mem

    $sysDeny = @(
        'System','System Idle Process','Memory Compression','Registry','svchost.exe','dwm.exe',
        'csrss.exe','wininit.exe','winlogon.exe','services.exe','lsass.exe','fontdrvhost.exe',
        'MsMpEng.exe','NisSrv.exe','SecurityHealthService.exe','RuntimeBroker.exe','dllhost.exe',
        'taskhostw.exe','ctfmon.exe','SearchHost.exe','SearchIndexer.exe','sihost.exe',
        'ShellExperienceHost.exe','StartMenuExperienceHost.exe','explorer.exe','smss.exe',
        'spoolsv.exe','conhost.exe','WmiPrvSE.exe','audiodg.exe','PSEXESVC.exe','cmd.exe',
        'powershell.exe','WUDFHost.exe','wlanext.exe','OCControl.Service.exe'
    )
    $topApps = $csv | Where-Object { $sysDeny -notcontains $_.Name } |
        Group-Object Name | ForEach-Object {
            $kb = ($_.Group | ForEach-Object { [int]($_.Mem -replace '[^\d]','') } | Measure-Object -Sum).Sum
            [pscustomobject]@{ name = $_.Name; mem_mb = [math]::Round($kb/1024,0) }
        } | Sort-Object mem_mb -Descending | Select-Object -First $Top

    $browserMap = [ordered]@{
        'chrome.exe'='Chrome'; 'msedge.exe'='Edge'; 'firefox.exe'='Firefox';
        'opera.exe'='Opera'; 'opera_gx.exe'='Opera GX'; 'brave.exe'='Brave';
        'vivaldi.exe'='Vivaldi'; 'iexplore.exe'='IE'
    }
    $names = $csv.Name | Sort-Object -Unique
    $browsers = @($browserMap.Keys | Where-Object { $names -contains $_ } | ForEach-Object { $browserMap[$_] })

    $user=$null; $state='none'; $idleSec=$null; $logon=$null
    $q = quser 2>$null
    $row = ($q | Select-Object -Skip 1 | Select-Object -First 1)
    if ($row) {
        $row = $row -replace '^\s*>?',''
        $f = $row -split '\s{2,}'
        $user = $f[0]; $state = $f[-3]; $idleRaw = $f[-2]; $logon = $f[-1]
        switch -regex ($idleRaw) {
            '^(none|\.)$'          { $idleSec = 0; break }
            '^\d+$'                { $idleSec = [int]$idleRaw * 60; break }
            '^(\d+)\+(\d+):(\d+)$' { $idleSec = ([int]$Matches[1]*1440 + [int]$Matches[2]*60 + [int]$Matches[3])*60; break }
            '^(\d+):(\d+)$'        { $idleSec = ([int]$Matches[1]*60 + [int]$Matches[2])*60; break }
            default                { $idleSec = $null }
        }
    }
    $active = ($state -eq 'Active') -and ($null -ne $idleSec) -and ($idleSec -lt $IdleActiveSeconds)

    [pscustomobject]@{
        ts=(Get-Date).ToString('s'); online=$true; user=$user; state=$state
        idle_seconds=$idleSec; active=$active; browsers=$browsers
        top_apps=$topApps; process_count=$csv.Count
    }
}

# ============================ MQTT (raw TCP, QoS0) ============================
function Convert-RemLen([int]$len){
    $b=New-Object System.Collections.Generic.List[byte]
    do { $d=$len -band 0x7F; $len=$len -shr 7; if($len -gt 0){$d=$d -bor 0x80}; $b.Add([byte]$d) } while($len -gt 0)
    ,$b.ToArray()
}
function Convert-MqttStr([string]$s){
    $bytes=[Text.Encoding]::UTF8.GetBytes($s)
    $out=New-Object System.Collections.Generic.List[byte]
    $out.Add([byte](($bytes.Length -shr 8) -band 0xFF)); $out.Add([byte]($bytes.Length -band 0xFF))
    $out.AddRange($bytes); ,$out.ToArray()
}
function Send-MqttBatch {
    param([string]$BrokerHost,[int]$Port,[string]$User,[string]$Pass,[bool]$Tls,
          [object[]]$Messages)  # each: @{ topic; payload; retain }
    $client=[Net.Sockets.TcpClient]::new(); $client.Connect($BrokerHost,$Port)
    $stream=$client.GetStream()
    if($Tls){
        $ssl=[Net.Security.SslStream]::new($stream,$false,{ param($s,$c,$ch,$e) $true })
        $ssl.AuthenticateAsClient($BrokerHost); $stream=$ssl
    }
    try {
        # CONNECT
        $payload=New-Object System.Collections.Generic.List[byte]
        $payload.AddRange((Convert-MqttStr ("pc-2-"+([guid]::NewGuid().ToString('N').Substring(0,8)))))
        $flags=0x02
        if($User){ $flags=$flags -bor 0x80; $payload.AddRange((Convert-MqttStr $User)) }
        if($Pass){ $flags=$flags -bor 0x40; $payload.AddRange((Convert-MqttStr $Pass)) }
        $vh=New-Object System.Collections.Generic.List[byte]
        $vh.AddRange([byte[]](0x00,0x04)); $vh.AddRange([Text.Encoding]::ASCII.GetBytes('MQTT'))
        $vh.Add(0x04); $vh.Add([byte]$flags); $vh.AddRange([byte[]](0x00,0x3C))  # keepalive 60
        $rest=New-Object System.Collections.Generic.List[byte]; $rest.AddRange($vh); $rest.AddRange($payload)
        $pkt=New-Object System.Collections.Generic.List[byte]; $pkt.Add(0x10)
        $pkt.AddRange((Convert-RemLen $rest.Count)); $pkt.AddRange($rest)
        $stream.Write($pkt.ToArray(),0,$pkt.Count); $stream.Flush()
        # read CONNACK (4 bytes); byte[3]=return code, 0=accepted
        $ack=New-Object byte[] 4; $null=$stream.Read($ack,0,4)
        if($ack[3] -ne 0){ throw "MQTT CONNECT refused, code $($ack[3])" }
        # PUBLISH each
        foreach($m in $Messages){
            $hdr=0x30; if($m.retain){$hdr=$hdr -bor 0x01}
            $body=New-Object System.Collections.Generic.List[byte]
            $body.AddRange((Convert-MqttStr $m.topic))
            $body.AddRange([Text.Encoding]::UTF8.GetBytes([string]$m.payload))
            $p=New-Object System.Collections.Generic.List[byte]; $p.Add([byte]$hdr)
            $p.AddRange((Convert-RemLen $body.Count)); $p.AddRange($body)
            $stream.Write($p.ToArray(),0,$p.Count); $stream.Flush()
        }
        $stream.Write([byte[]](0xE0,0x00),0,2); $stream.Flush()  # DISCONNECT
    } finally { $client.Close() }
}

# ============================ HA DISCOVERY ============================
function Get-DiscoveryMessages {
    param($cfg,$stateTopic)
    $node=$cfg.node_id; $dn=$cfg.device_name
    $dev=@{ identifiers=@($node); name=$dn; manufacturer='Straylight'; model='telemetry-agent' }
    $avail=@{ expire_after=2100 }   # ~35 min: tolerates one missed 15-min cycle
    $defs=@(
        @{ comp='binary_sensor'; id='active'; nm='Active'; tmpl="{{ 'ON' if value_json.active else 'OFF' }}"; extra=@{ device_class='running'; payload_on='ON'; payload_off='OFF' } }
        @{ comp='sensor'; id='idle'; nm='Idle'; tmpl='{{ value_json.idle_seconds }}'; extra=@{ unit_of_measurement='s'; device_class='duration'; icon='mdi:timer-sand' } }
        @{ comp='sensor'; id='user'; nm='User'; tmpl='{{ value_json.user }}'; extra=@{ icon='mdi:account' } }
        @{ comp='sensor'; id='session'; nm='Session'; tmpl='{{ value_json.state }}'; extra=@{ icon='mdi:monitor' } }
        @{ comp='sensor'; id='top_app'; nm='Top App'; tmpl='{{ value_json.top_apps[0].name if value_json.top_apps else "none" }}'; extra=@{ icon='mdi:application'; json_attributes_topic=$stateTopic; json_attributes_template="{{ {'apps': value_json.top_apps} | tojson }}" } }
        @{ comp='sensor'; id='browsers'; nm='Browsers'; tmpl="{{ value_json.browsers | join(', ') if value_json.browsers else 'none' }}"; extra=@{ icon='mdi:web' } }
        @{ comp='sensor'; id='procs'; nm='Processes'; tmpl='{{ value_json.process_count }}'; extra=@{ icon='mdi:counter' } }
    )
    foreach($d in $defs){
        $obj=@{
            name="$dn $($d.nm)"; unique_id="${node}_$($d.id)"; state_topic=$stateTopic
            value_template=$d.tmpl; device=$dev
        } + $avail
        foreach($k in $d.extra.Keys){ $obj[$k]=$d.extra[$k] }
        @{
            topic="$($cfg.discovery_prefix)/$($d.comp)/$node/$($d.id)/config"
            payload=($obj | ConvertTo-Json -Depth 5 -Compress); retain=$true
        }
    }
}

# ============================ MAIN ============================
$t = Get-Telemetry -IdleActiveSeconds $IdleActiveSeconds -Top $Top
$json = $t | ConvertTo-Json -Depth 4 -Compress

# always drop a local copy for debugging
try { if(Test-Path $stateDir){ Set-Content "$stateDir\last-telemetry.json" $json -Encoding UTF8 } } catch {}

if($NoPublish -or -not (Test-Path $ConfigPath)){
    $json; if(-not (Test-Path $ConfigPath)){ Write-Verbose "No MQTT config at $ConfigPath; printed JSON only." }
    return
}

$cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$pass = $null
$passFile = "$stateDir\mqtt.pass"
if(Test-Path $passFile){
    Add-Type -AssemblyName System.Security
    $pass=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($passFile),$null,'LocalMachine'))
}
$stateTopic = "$($cfg.base_topic)/telemetry/state"

$msgs = New-Object System.Collections.Generic.List[object]
$marker = "$stateDir\discovery.sent"
if($Discovery -or -not (Test-Path $marker)){
    (Get-DiscoveryMessages -cfg $cfg -stateTopic $stateTopic) | ForEach-Object { $msgs.Add($_) }
}
$msgs.Add(@{ topic=$stateTopic; payload=$json; retain=$true })

Send-MqttBatch -BrokerHost $cfg.host -Port $cfg.port -User $cfg.username -Pass $pass -Tls ([bool]$cfg.tls) -Messages $msgs.ToArray()
if($Discovery -or -not (Test-Path $marker)){ Set-Content $marker (Get-Date).ToString('s') }
Write-Verbose "Published telemetry to $($cfg.host):$($cfg.port) topic $stateTopic"
