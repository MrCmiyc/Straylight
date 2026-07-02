# Trace which process is querying a domain. Enables the DNS-Client operational log briefly,
# reads Event 3006 (fired in the CALLING process) for the target, maps PID->process, disables.
param([string]$Match = 'marphezis', [int]$Seconds = 90)
$ErrorActionPreference = 'SilentlyContinue'
$log = 'Microsoft-Windows-DNS-Client/Operational'

& wevtutil sl $log /e:true
"enabled $log; watching ${Seconds}s for '$Match'..."
Start-Sleep -Seconds $Seconds

$ev = Get-WinEvent -FilterHashtable @{LogName=$log; Id=3006} -MaxEvents 5000 -ErrorAction SilentlyContinue |
      Where-Object { $_.Message -match $Match }
"matching 3006 events: $(@($ev).Count)"
$ev | Group-Object ProcessId | Sort-Object Count -Descending | ForEach-Object {
    $procId = [int]$_.Name
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    "  PID={0,-6} hits={1,-4} proc={2}  path={3}" -f $procId, $_.Count, $p.Name, $p.Path
}
& wevtutil sl $log /e:false
"disabled $log"
