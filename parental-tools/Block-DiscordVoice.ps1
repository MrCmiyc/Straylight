<#
.SYNOPSIS
  Block/unblock Discord VOICE while leaving text chat working.

.DESCRIPTION
  Discord voice/video media is UDP; text, DMs, and gateway are TCP 443. Blocking only
  UDP outbound for the Discord executables kills voice but leaves messaging intact, and
  does NOT touch games (the rule is scoped to the Discord process, not a port range).

  Discord installs to a per-version folder (AppData\Local\Discord\app-<ver>\Discord.exe)
  that changes on every auto-update, so this script RE-RESOLVES the current path each time
  it is turned on. Re-run -On after a Discord update (or on a schedule) to keep it effective.

  Run as Administrator. Run locally on the target PC (e.g. via the agent / PsExec -s),
  not over the wire with credentials.

.EXAMPLE
  .\Block-DiscordVoice.ps1 -On
.EXAMPLE
  .\Block-DiscordVoice.ps1 -Off
.EXAMPLE
  .\Block-DiscordVoice.ps1 -Status
#>
[CmdletBinding(DefaultParameterSetName='Status')]
param(
    [Parameter(ParameterSetName='On')]  [switch]$On,
    [Parameter(ParameterSetName='Off')] [switch]$Off,
    [Parameter(ParameterSetName='Status')] [switch]$Status,
    # Whose Discord to target. Default: every user profile on the machine.
    [string]$UserProfile
)

$RuleGroup = 'Straylight-DiscordVoice'

function Get-DiscordExe {
    # Find current Discord.exe (and Update.exe) under each user's LocalAppData.
    $roots = if ($UserProfile) {
        @(Join-Path $UserProfile 'AppData\Local\Discord')
    } else {
        Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'AppData\Local\Discord' }
    }
    foreach ($root in $roots) {
        if (Test-Path $root) {
            Get-ChildItem $root -Recurse -Include Discord.exe,Update.exe -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty FullName
        }
    }
}

function Remove-Rules {
    Get-NetFirewallRule -Group $RuleGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

switch ($PSCmdlet.ParameterSetName) {
    'On' {
        Remove-Rules   # clear stale rules (old version paths) first
        $exes = Get-DiscordExe | Sort-Object -Unique
        if (-not $exes) { Write-Warning 'No Discord.exe found.'; break }
        foreach ($exe in $exes) {
            New-NetFirewallRule -DisplayName "Block Discord voice (UDP) - $([IO.Path]::GetFileName($exe))" `
                -Group $RuleGroup -Direction Outbound -Action Block `
                -Program $exe -Protocol UDP -Profile Any -Enabled True | Out-Null
            Write-Host "Blocked UDP out for $exe"
        }
        Write-Host "Discord VOICE blocked. Text chat still works. Re-run -On after Discord updates."
    }
    'Off' {
        Remove-Rules
        Write-Host 'Discord voice block removed.'
    }
    default {
        $rules = Get-NetFirewallRule -Group $RuleGroup -ErrorAction SilentlyContinue
        if ($rules) { $rules | Select-Object DisplayName, Enabled, Action }
        else { Write-Host 'No Discord voice block active.' }
    }
}
