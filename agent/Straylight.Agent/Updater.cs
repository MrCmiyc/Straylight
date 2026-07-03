using System.Diagnostics;

namespace Straylight.Agent;

/// <summary>
/// Applies a downloaded, hash-verified update via a one-shot SYSTEM scheduled task that runs
/// independently of this service: it stops Straylight, backs up the current exe (.bak), swaps in
/// the new one, restarts, and rolls back to .bak if the new build doesn't reach RUNNING. The task
/// scheduler (rather than a detached CreateProcess — powershell with no console exits immediately)
/// gives the swapper a real SYSTEM session that survives this service stopping mid-swap.
/// </summary>
internal static class Updater
{
    const string Dir = @"C:\ProgramData\Straylight";
    const string TaskName = "StraylightSelfUpdate";

    // __NEW__ is replaced with the verified new-exe path. The task self-deletes at the end.
    const string SwapScript = @"
$svc='Straylight'
$bin='C:\ProgramData\Straylight\bin\straylight-agent.exe'
$new='__NEW__'
$bak=""$bin.bak""
$log='C:\ProgramData\Straylight\update.log'
function Log($m){ try { Add-Content $log (""{0}  {1}"" -f (Get-Date -Format 's'), $m) } catch {} }
function State(){ (sc.exe query $svc 2>$null | Select-String 'STATE') -join ' ' }
try {
  Log 'swapper: stopping service'
  sc.exe stop $svc | Out-Null
  for($i=0;$i -lt 40;$i++){ if((State) -match 'STOPPED'){break}; Start-Sleep -Milliseconds 500 }
  # session helpers (idle-watch/dim-watch/reply-window) run from bin and keep the exe locked; kill
  # them so the swap can replace it. (This is what stranded v2-enabled boxes on the old swapper.)
  taskkill /f /im straylight-agent.exe 2>$null | Out-Null
  Start-Sleep -Milliseconds 800
  Copy-Item $bin $bak -Force -ErrorAction SilentlyContinue
  $swapped=$false
  for($i=0;$i -lt 20;$i++){ try { Copy-Item $new $bin -Force; $swapped=$true; break } catch { Start-Sleep -Milliseconds 500 } }
  if($swapped){ Log 'swapper: swapped in new exe, starting' } else { Log 'swapper: swap failed (exe locked) -> restarting old build' }
  sc.exe start $svc | Out-Null
  $ok=$false; for($i=0;$i -lt 40;$i++){ if((State) -match 'RUNNING'){$ok=$true;break}; Start-Sleep -Milliseconds 500 }
  if($swapped -and -not $ok){
    Log 'swapper: new exe did not reach RUNNING -> rolling back'
    sc.exe stop $svc | Out-Null; Start-Sleep -Seconds 2; taskkill /f /im straylight-agent.exe 2>$null | Out-Null; Start-Sleep -Milliseconds 800
    Copy-Item $bak $bin -Force; sc.exe start $svc | Out-Null
    Log 'swapper: rolled back to previous exe'
  } elseif($swapped) { Log 'swapper: update OK' }
  Remove-Item $new -Force -ErrorAction SilentlyContinue
} catch { Log ('swapper FAILED: ' + $_.Exception.Message); try { sc.exe start $svc | Out-Null } catch {} }
schtasks.exe /delete /tn StraylightSelfUpdate /f 2>$null | Out-Null
";

    public static bool Run(string newExePath)
    {
        string log = Path.Combine(Dir, "update.log");
        try
        {
            Directory.CreateDirectory(Dir);
            string script = Path.Combine(Dir, "update-swap.ps1");
            File.WriteAllText(script, SwapScript.Replace("__NEW__", newExePath));
            // the script path has no spaces (…\Straylight\update-swap.ps1), so /tr needs no inner
            // quoting; ArgumentList quotes the whole /tr value as a single token for schtasks.
            string tr = $"powershell -NoProfile -ExecutionPolicy Bypass -File {script}";
            bool created = Schtasks("/create", "/tn", TaskName, "/tr", tr, "/sc", "ONCE",
                                    "/st", "00:00", "/ru", "SYSTEM", "/rl", "HIGHEST", "/f");
            bool ran = created && Schtasks("/run", "/tn", TaskName);
            try { File.AppendAllText(log, $"{DateTime.Now:s}  updater: task create={created} run={ran}\n"); } catch { }
            return created && ran;
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(log, $"{DateTime.Now:s}  updater launch failed: {ex.Message}\n"); } catch { }
            return false;
        }
    }

    static bool Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return false;
        p.WaitForExit(15000);
        return p.ExitCode == 0;
    }
}
