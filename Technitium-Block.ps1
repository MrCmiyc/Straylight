<#
.SYNOPSIS
  Add/remove domains in the Technitium DNS custom "blocked zone" via its HTTP API.
  Built for scheduled parental blocks (e.g. Discord during school/bedtime).

.DESCRIPTION
  Technitium exposes the same HTTP API the web console uses. This wraps the
  /api/blocked/{add,delete,list} endpoints. Blocking a domain also blocks all its
  subdomains. Pairs with disabling DoH on the client (Disable-DoH.ps1) so the client
  can't bypass the DNS block.

  Get a permanent token once from the Technitium console:
    User menu (top-right) -> Create API Token
  or:
    http://<server>:5380/api/user/createToken?user=admin&pass=PASS&tokenName=parental

  Store the token out of source control. This script reads it from the
  TECHNITIUM_TOKEN environment variable by default, or pass -Token.

.PARAMETER Action  Block | Unblock | Status
.PARAMETER Server  host:port of Technitium (default the-child-dns:5380)
.PARAMETER Domains Domains to act on (default: the Discord set)
.PARAMETER Token   API token (default: $env:TECHNITIUM_TOKEN)

.EXAMPLE
  $env:TECHNITIUM_TOKEN = '...'; .\Technitium-Block.ps1 -Action Block
.EXAMPLE
  .\Technitium-Block.ps1 -Action Status -Server 192.168.1.10:5380 -Token abc123
#>
[CmdletBinding()]
param(
    [ValidateSet('Block','Unblock','Status')] [string]$Action = 'Status',
    [string]$Server = 'the-child-dns:5380',
    [string[]]$Domains = @(
        'discord.com','discordapp.com','discord.gg','discordapp.net',
        'discord.media','discord.gift','discordcdn.com','dis.gd'
    ),
    [string]$Token = $env:TECHNITIUM_TOKEN
)

if (-not $Token) { throw "No API token. Set `$env:TECHNITIUM_TOKEN or pass -Token." }
$base = "http://$Server/api"

function Invoke-Tech($path, $query) {
    $uri = "$base/$path`?token=$Token$query"
    $r = Invoke-RestMethod -Uri $uri -Method Get -ErrorAction Stop
    if ($r.status -ne 'ok') { throw "Technitium API error on $path : $($r.errorMessage)" }
    $r
}

switch ($Action) {
    'Block' {
        foreach ($d in $Domains) {
            Invoke-Tech 'blocked/add' "&domain=$d" | Out-Null
            Write-Host "blocked  $d"
        }
        Write-Host "Done. $($Domains.Count) domains in the block list."
    }
    'Unblock' {
        foreach ($d in $Domains) {
            Invoke-Tech 'blocked/delete' "&domain=$d" | Out-Null
            Write-Host "unblocked $d"
        }
        Write-Host "Done."
    }
    'Status' {
        # The blocked zone is hierarchical; query each domain directly. A blocked
        # domain returns NS/SOA records, an unblocked one returns an empty record set.
        foreach ($d in $Domains) {
            $r = Invoke-Tech 'blocked/list' "&domain=$d"
            $on = @($r.response.records).Count -gt 0
            "{0,-18} {1}" -f $d, ($(if ($on) {'BLOCKED'} else {'allowed'}))
        }
    }
}
