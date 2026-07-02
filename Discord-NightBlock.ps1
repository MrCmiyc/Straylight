<#
.SYNOPSIS
  Scheduled-task entry point: block/unblock Discord on the Technitium DNS server.
  Reads the API token from a DPAPI-encrypted file (no plaintext secret on disk or in args).

.PARAMETER Action  Block | Unblock

.NOTES
  Token store: %LOCALAPPDATA%\Straylight\technitium.token  (DPAPI, LocalMachine scope)
  Decryptable only on this machine; lets the task run whether logged on or not.
  Logs each run to %LOCALAPPDATA%\Straylight\discord-schedule.log
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Block','Unblock')] [string]$Action
)

$ErrorActionPreference = 'Stop'
$Server     = 'mqtt-host:5380'
$here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$blockPs1   = Join-Path $here 'Technitium-Block.ps1'
$stateDir   = Join-Path $env:LOCALAPPDATA 'Straylight'
$tokenFile  = Join-Path $stateDir 'technitium.token'
$logFile    = Join-Path $stateDir 'discord-schedule.log'

function Log($msg) {
    $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    Add-Content -Path $logFile -Value $line
}

try {
    Add-Type -AssemblyName System.Security
    $enc   = [IO.File]::ReadAllBytes($tokenFile)
    $plain = [Security.Cryptography.ProtectedData]::Unprotect($enc, $null, 'LocalMachine')
    $token = [Text.Encoding]::UTF8.GetString($plain)

    & $blockPs1 -Action $Action -Server $Server -Token $token | Out-Null
    Log "OK  $Action"
}
catch {
    Log "FAIL $Action : $($_.Exception.Message)"
    throw
}
