<#
.SYNOPSIS
  Disable DNS-over-HTTPS (DoH) on pc-2 so a DNS-level Discord/site block actually works.

.DESCRIPTION
  DoH lets Windows and browsers send DNS lookups encrypted over 443 straight to
  Cloudflare/Google, bypassing your Pi-hole/router resolver. This turns DoH off in three
  places, all machine-wide (HKLM), so it applies to every user and survives the non-admin
  child:
    1. Windows built-in resolver  (Dnscache EnableAutoDoh = 0)
    2. Chrome + Edge policy        (DnsOverHttpsMode = "off")
    3. Firefox policy              (DNSOverHTTPS Enabled = 0, Locked = 1)
  Optionally pins the adapter DNS to your filtering resolver (-DnsServer), which a non-admin
  user cannot change back.

  HKLM-only => no interactive session needed => safe to run as SYSTEM/admin and remotely.
  Browsers must be restarted to pick up the policy.

.PARAMETER DnsServer
  Optional. One or more resolver IPs to pin on all "Up" adapters (e.g. your Pi-hole/NextDNS).
  Omit to leave DNS assignment as-is (e.g. if the router already hands out the right DNS).

.PARAMETER Status
  Show current DoH-related settings instead of changing anything.

.PARAMETER Revert
  Undo: re-enable Windows auto-DoH and remove the browser DoH policies. Does NOT touch
  adapter DNS (set that back manually if you pinned it).

.EXAMPLE
  .\Disable-DoH.ps1
.EXAMPLE
  .\Disable-DoH.ps1 -DnsServer 192.168.1.10
.EXAMPLE
  .\Disable-DoH.ps1 -Status
.EXAMPLE
  .\Disable-DoH.ps1 -Revert
#>
[CmdletBinding(DefaultParameterSetName='Apply')]
param(
    [Parameter(ParameterSetName='Apply')] [string[]]$DnsServer,
    [Parameter(ParameterSetName='Status')] [switch]$Status,
    [Parameter(ParameterSetName='Revert')] [switch]$Revert
)

$ErrorActionPreference = 'Stop'

$Dnscache = 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters'
$Chrome   = 'HKLM:\SOFTWARE\Policies\Google\Chrome'
$Edge     = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
$FxRoot   = 'HKLM:\SOFTWARE\Policies\Mozilla\Firefox'
$FxDoH    = "$FxRoot\DNSOverHTTPS"

function Set-Reg($Path, $Name, $Value, $Type) {
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
    New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
}

switch ($PSCmdlet.ParameterSetName) {

    'Status' {
        Write-Host '== Windows resolver ==' -ForegroundColor Cyan
        (Get-ItemProperty $Dnscache -Name EnableAutoDoh -EA SilentlyContinue).EnableAutoDoh |
            ForEach-Object { "EnableAutoDoh = $_ (0=off, 2=auto/default)" }
        Write-Host '== Chrome ==' -ForegroundColor Cyan
        (Get-ItemProperty $Chrome -Name DnsOverHttpsMode -EA SilentlyContinue).DnsOverHttpsMode
        Write-Host '== Edge ==' -ForegroundColor Cyan
        (Get-ItemProperty $Edge -Name DnsOverHttpsMode -EA SilentlyContinue).DnsOverHttpsMode
        Write-Host '== Firefox ==' -ForegroundColor Cyan
        (Get-ItemProperty $FxDoH -Name Enabled -EA SilentlyContinue).Enabled |
            ForEach-Object { "DNSOverHTTPS Enabled = $_ (0=off)" }
        Write-Host '== Adapter DNS ==' -ForegroundColor Cyan
        Get-DnsClientServerAddress -AddressFamily IPv4 |
            Where-Object ServerAddresses |
            Select-Object InterfaceAlias, ServerAddresses
        break
    }

    'Revert' {
        Set-Reg $Dnscache 'EnableAutoDoh' 2 'DWord'           # back to auto
        foreach ($p in $Chrome,$Edge) {
            Remove-ItemProperty -Path $p -Name DnsOverHttpsMode -EA SilentlyContinue
        }
        if (Test-Path $FxDoH) { Remove-Item $FxDoH -Recurse -Force -EA SilentlyContinue }
        Clear-DnsClientCache
        Write-Host 'Reverted: Windows auto-DoH re-enabled, browser DoH policies removed.'
        Write-Host 'Note: adapter DNS not changed; reset manually if you had pinned it.'
        break
    }

    'Apply' {
        # 1. Windows built-in resolver: disable automatic DoH upgrade
        Set-Reg $Dnscache 'EnableAutoDoh' 0 'DWord'

        # 2. Chrome + Edge: force Secure DNS off
        foreach ($p in $Chrome,$Edge) { Set-Reg $p 'DnsOverHttpsMode' 'off' 'String' }

        # 3. Firefox: disable + lock so the user can't re-enable
        Set-Reg $FxDoH 'Enabled' 0 'DWord'
        Set-Reg $FxDoH 'Locked'  1 'DWord'

        # 4. Optional: pin adapter DNS to your filtering resolver
        if ($DnsServer) {
            Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object {
                Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ServerAddresses $DnsServer
                Write-Host "Pinned DNS on '$($_.Name)' -> $($DnsServer -join ', ')"
            }
        }

        Clear-DnsClientCache
        Write-Host 'DoH disabled (Windows + Chrome/Edge/Firefox). All DNS now goes plaintext'
        Write-Host 'through your configured resolver, so your Pi-hole/NextDNS Discord block applies.'
        Write-Host 'Restart any open browsers for the policy to take effect.'
        break
    }
}
