# Find the thing generating input (auto-clicker / jiggler / AHK macro) or a controller.
# Read-only. Run locally (PsExec -s).
$ErrorActionPreference = 'SilentlyContinue'
$sig = 'auto.?click|opauto|gsauto|clicker|jiggl|move.?mouse|movemouse|caffeine|tinytask|autohotkey|ahk|pulover|macro|auto.?mouse|automove|autokey|anti.?afk|afkbot|auto.?farm|mouserecorder|mini.?mouse|fake.?input|nircmd|keyauto'

$procs = Get-CimInstance Win32_Process

"==== CLICKER / MACRO / JIGGLER candidates (by name or command line) ===="
$cand = $procs | Where-Object { $_.Name -match $sig -or $_.CommandLine -match $sig }
if ($cand) { $cand | Select-Object ProcessId, Name, @{n='Path';e={$_.ExecutablePath}}, CommandLine | Format-List }
else { "(nothing matched the signature list)" }

""
"==== Non-Windows user EXEs running (eyeball for the culprit) ===="
$procs | Where-Object { $_.ExecutablePath -and $_.ExecutablePath -notmatch '^C:\\Windows' } |
    Select-Object @{n='Path';e={$_.ExecutablePath}} -Unique | Sort-Object Path | Format-Table -AutoSize -Wrap

""
"==== Input devices (extra mouse/keyboard or a gamepad = possible input source) ===="
Get-PnpDevice -PresentOnly | Where-Object { $_.Class -in 'Mouse','Keyboard','HIDClass','XnaComposite' -and $_.Status -eq 'OK' } |
    Select-Object Class, FriendlyName | Sort-Object Class, FriendlyName | Format-Table -AutoSize -Wrap
