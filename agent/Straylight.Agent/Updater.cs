using System.Runtime.InteropServices;
using System.Text;

namespace Straylight.Agent;

/// <summary>
/// Applies a downloaded, hash-verified update. Launches a DETACHED SYSTEM process that outlives
/// this service and: stops Straylight, backs up the current exe (.bak), swaps in the new one,
/// restarts, and rolls back to .bak if the new build doesn't reach RUNNING. Detached
/// (CREATE_BREAKAWAY_FROM_JOB, with a plain-detached fallback) so stopping our own service can't
/// kill the swapper mid-flight.
/// </summary>
internal static class Updater
{
    const string Dir = @"C:\ProgramData\Straylight";

    // __NEW__ is replaced with the verified new-exe path before the script is written.
    const string SwapScript = @"
$svc='Straylight'
$bin='C:\ProgramData\Straylight\bin\straylight-agent.exe'
$new='__NEW__'
$bak=""$bin.bak""
$log='C:\ProgramData\Straylight\update.log'
function Log($m){ try { Add-Content $log (""{0}  {1}"" -f (Get-Date -Format 's'), $m) } catch {} }
function State(){ (sc.exe query $svc 2>$null | Select-String 'STATE') -join ' ' }
try {
  Log 'stopping service'
  sc.exe stop $svc | Out-Null
  for($i=0;$i -lt 40;$i++){ if((State) -match 'STOPPED'){break}; Start-Sleep -Milliseconds 500 }
  Copy-Item $bin $bak -Force
  Copy-Item $new $bin -Force
  Log 'swapped in new exe, starting'
  sc.exe start $svc | Out-Null
  $ok=$false; for($i=0;$i -lt 40;$i++){ if((State) -match 'RUNNING'){$ok=$true;break}; Start-Sleep -Milliseconds 500 }
  if(-not $ok){
    Log 'new exe did not reach RUNNING -> rolling back'
    sc.exe stop $svc | Out-Null; Start-Sleep -Seconds 2
    Copy-Item $bak $bin -Force
    sc.exe start $svc | Out-Null
    Log 'rolled back to previous exe'
  } else { Log 'update OK' }
  Remove-Item $new -Force -ErrorAction SilentlyContinue
} catch { Log ('FAILED: ' + $_.Exception.Message) }
";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int pid, tid; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);

    const uint DETACHED_PROCESS = 0x8, CREATE_NEW_PROCESS_GROUP = 0x200,
               CREATE_BREAKAWAY_FROM_JOB = 0x1000000, CREATE_NO_WINDOW = 0x8000000;

    public static bool Run(string newExePath)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string script = Path.Combine(Dir, "update-swap.ps1");
            File.WriteAllText(script, SwapScript.Replace("__NEW__", newExePath));
            string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            string args = $"\"{ps}\" -NoProfile -ExecutionPolicy Bypass -File \"{script}\"";
            uint baseFlags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW;

            // prefer breaking away from any service job so the stop can't take the swapper with it;
            // if we're not in a breakaway-permitted job that call fails, so fall back to plain detach.
            if (Launch(ps, args, baseFlags | CREATE_BREAKAWAY_FROM_JOB)) return true;
            return Launch(ps, args, baseFlags);
        }
        catch { return false; }
    }

    static bool Launch(string exe, string args, uint flags)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        bool ok = CreateProcess(exe, new StringBuilder(args), IntPtr.Zero, IntPtr.Zero, false,
            flags, IntPtr.Zero, Dir, ref si, out var pi);
        if (ok) { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
        return ok;
    }
}
