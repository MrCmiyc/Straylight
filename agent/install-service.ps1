<#
  Install or UPDATE the Straylight agent as a Windows service. MUST run elevated (or as SYSTEM).
  Idempotent: stops any running service (so the exe unlocks), refreshes it, recreates the service.
  Reads runtime config from C:\ProgramData\Straylight\mqtt.json.
  -ExeSource <path>  copy the agent exe from here into the install dir first (for install/update).

  Native cleanup runs via cmd with stderr swallowed, so a missing prior service/task can't abort.
#>
param([string]$ExeSource)

$binDir = 'C:\ProgramData\Straylight\bin'
$exe    = Join-Path $binDir 'straylight-agent.exe'
New-Item -ItemType Directory -Force $binDir | Out-Null

# stop first so the running exe is unlocked before we overwrite it
cmd /c "sc stop Straylight >nul 2>&1"
Start-Sleep -Seconds 2

if ($ExeSource -and (Test-Path $ExeSource)) { Copy-Item $ExeSource $exe -Force }
if (-not (Test-Path $exe)) { Write-Error "agent exe not found at $exe (pass -ExeSource)"; exit 1 }
# clean up the staged copy once it's in place
if ($ExeSource -and ($ExeSource -ne $exe) -and (Test-Path $ExeSource)) { Remove-Item $ExeSource -Force -ErrorAction SilentlyContinue }

# remove prior service / old scheduled task
cmd /c "sc delete Straylight >nul 2>&1"
cmd /c "schtasks /delete /tn Straylight-Telemetry /f >nul 2>&1"
Start-Sleep -Seconds 1

# (re)create as auto-start LocalSystem service
cmd /c "sc create Straylight binPath= `"$exe`" start= auto DisplayName= `"Straylight Agent`""
cmd /c "sc description Straylight `"Straylight telemetry and control agent`""
# recovery = how the nightly self-restart (non-zero exit) relaunches, plus crash resilience
cmd /c "sc failure Straylight reset= 86400 actions= restart/5000/restart/5000/restart/5000"
cmd /c "sc start Straylight"
Start-Sleep -Seconds 2
cmd /c "sc query Straylight"
