# Straylight — install & deploy

The Straylight agent is a self-contained .NET Windows service that reports telemetry to an
MQTT broker (for Home Assistant) and accepts commands (poll interval, messages, screen dim).
It publishes MQTT auto-discovery, so each machine shows up as a device with no HA-side YAML.

Aliases used below: **`mqtt-host`** = the MQTT broker host; **`pc-1`** = the admin box you run
deploys from; **`pc-2`** = a remote target machine.

---

## 1. Build

Needs the .NET SDK (10.x). No admin required — you can install the SDK to your user profile:
```powershell
& ([scriptblock]::Create((irm https://dot.net/v1/dotnet-install.ps1))) -Channel LTS -InstallDir "$env:USERPROFILE\.dotnet"
& "$env:USERPROFILE\.dotnet\dotnet.exe" nuget add source https://api.nuget.org/v3/index.json -n nuget.org   # if no feed
```
Publish a self-contained single-file exe (targets need no .NET runtime):
```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" publish agent/Straylight.Agent/Straylight.Agent.csproj -c Release
# -> agent/Straylight.Agent/bin/Release/net10.0-windows/win-x64/publish/straylight-agent.exe
```

## 2. Config (per machine)

Create `C:\ProgramData\Straylight\mqtt.json` on the target:
```json
{
  "host":"mqtt-host", "port":1883, "username":"", "tls":false,
  "base_topic":"pc-2", "node_id":"pc-2", "device_name":"PC 2",
  "discovery_prefix":"homeassistant"
}
```
- `base_topic`/`node_id` = the machine's slug (used in MQTT topics); `device_name` = HA device label.
- Broker auth: put the password DPAPI-encrypted (LocalMachine scope) at
  `C:\ProgramData\Straylight\mqtt.pass`. **Never commit secrets** — they live only on the machine.

## 3. Install locally (on the machine itself)

In an **elevated** PowerShell, point the installer at the published exe:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\agent\install-service.ps1 `
  -ExeSource "<path>\publish\straylight-agent.exe"
```
It stops any prior service, copies the exe to `C:\ProgramData\Straylight\bin\`, creates an
auto-start LocalSystem service `Straylight` with restart-on-failure recovery, and starts it.
Re-run the same command to update — it's idempotent and self-cleans the staged copy.

## 4. Enable remote admin on a target (one-time, on the target)

To deploy/update from `pc-1` to `pc-2` over SMB, `pc-2` must allow remote admin. On `pc-2`,
in an **elevated** shell (once):
```powershell
# let a local/Microsoft-account admin use admin shares (C$) over the network
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v LocalAccountTokenFilterPolicy /t REG_DWORD /d 1 /f
Enable-NetFirewallRule -DisplayGroup "File and Printer Sharing"
```
Notes:
- The account you connect **as** must be a **local Administrator** on `pc-2`.
- Microsoft-account identities don't map machine-to-machine over SMB — a **dedicated local
  admin account** (a dedicated local service account) is the reliable option; connect with
  `pc-2\<account>` and cache it via `cmdkey`/`net use`.
- An open Explorer window to `\\pc-2` pins an SMB session under your interactive user and
  causes "System error 1219" when connecting as a different account — close it first.

## 5. Push / update remotely from pc-1 (PowerShell + PsExec)

With remote admin working, copy the exe over the admin share and run the installer as SYSTEM
(PsExec from Sysinternals):
```powershell
$exe = "<path>\publish\straylight-agent.exe"
Copy-Item $exe "\\pc-2\c$\ProgramData\Straylight\straylight-agent.new.exe" -Force
Copy-Item .\agent\install-service.ps1 "\\pc-2\c$\ProgramData\Straylight\install-service.ps1" -Force
psexec \\pc-2 -s -accepteula cmd /c `
  "powershell -NoProfile -ExecutionPolicy Bypass -File C:\ProgramData\Straylight\install-service.ps1 -ExeSource C:\ProgramData\Straylight\straylight-agent.new.exe"
```
`-s` runs the installer as LocalSystem; the installer self-removes the `.new.exe` after copying
it into place. Running as SYSTEM means updates are invisible to the logged-in user.

## 6. Verify

Watch the broker (`mqtt-host`) for the machine's telemetry, e.g.:
```
mosquitto_sub -h mqtt-host -t "pc-2/telemetry/state"
```
Each agent stamps a `version` field and publishes an "Agent version" diagnostic sensor, so a
build mismatch across machines is visible in Home Assistant at a glance.

See [HA-interface.md](HA-interface.md) for the full MQTT topic/command/entity contract.

## Secrets

Nothing sensitive is committed. Secrets live only on the machines:
- `C:\ProgramData\Straylight\mqtt.json` (broker host/user) and `mqtt.pass` (DPAPI-encrypted).
- DNS/API tokens: DPAPI files or environment variables, never in scripts.
