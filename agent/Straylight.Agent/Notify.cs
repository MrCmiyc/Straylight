using System.Runtime.InteropServices;

namespace Straylight.Agent;

/// <summary>
/// Two ways to reach the user:
///  - Popup(): WTSSendMessage from the Session-0 service -> a top-most message box that shows
///    even over a full-screen game. Used for "urgent".
///  - Toast(): a friendly Windows toast (branded "Parent"), raised by a helper in the user session.
///    Nicer, but Windows suppresses toasts during full-screen games (hence urgent uses Popup).
/// </summary>
public static class Notify
{
    const string Dir = @"C:\ProgramData\Straylight";

    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WTSSendMessage(IntPtr hServer, uint sessionId, string title, int titleBytes,
        string msg, int msgBytes, uint style, int timeoutSec, out uint response, bool wait);
    const uint MB_ICONWARNING = 0x30, MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000;

    public static void Popup(string title, string message)
    {
        uint sid = WTSGetActiveConsoleSessionId();
        if (sid == 0xFFFFFFFF) return;
        string t = string.IsNullOrEmpty(title) ? "Message" : title;
        string m = message ?? "";
        WTSSendMessage(IntPtr.Zero, sid, t, t.Length * 2, m, m.Length * 2,
            MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND, 0, out _, false);
    }

    // WinRT toast raised via PowerShell (avoids pulling the Windows SDK projection into the build).
    // Uses ToastGeneric with one <text> per line so multi-line messages render as separate lines.
    const string ToastScript = @"
$ErrorActionPreference='SilentlyContinue'
$m = Get-Content 'C:\ProgramData\Straylight\message.txt' -Raw
if($null -eq $m){ $m='' }
$m = $m -replace '[\r\n]+$',''
$title = 'Message'
try { if(Test-Path 'C:\ProgramData\Straylight\title.txt'){ $title = (Get-Content 'C:\ProgramData\Straylight\title.txt' -Raw).Trim() } } catch {}
if([string]::IsNullOrWhiteSpace($title)){ $title = 'Message' }
$k = 'HKCU:\Software\Classes\AppUserModelId\Straylight.Notify'
if(-not (Test-Path $k)){ New-Item $k -Force | Out-Null }
New-ItemProperty $k -Name DisplayName -Value 'Parent' -PropertyType String -Force | Out-Null
function Esc($s){ (($s -replace '&','&amp;') -replace '<','&lt;') -replace '>','&gt;' }
$body=''
foreach($ln in ($m -split ""`r?`n"")){ $body += '<text>' + (Esc $ln) + '</text>' }
$xml = '<toast><visual><binding template=""ToastGeneric""><text>' + (Esc $title) + '</text>' + $body + '</binding></visual></toast>'
$doc = [Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom,ContentType=WindowsRuntime]::new()
$doc.LoadXml($xml)
$toast = [Windows.UI.Notifications.ToastNotification,Windows.UI.Notifications,ContentType=WindowsRuntime]::new($doc)
[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]::CreateToastNotifier('Straylight.Notify').Show($toast)
";

    public static void Toast(string title, string message)
    {
        Directory.CreateDirectory(Dir);
        try
        {
            File.WriteAllText(Path.Combine(Dir, "title.txt"), string.IsNullOrWhiteSpace(title) ? "Message" : title);
            File.WriteAllText(Path.Combine(Dir, "message.txt"), message ?? "");
            File.WriteAllText(Path.Combine(Dir, "toast.ps1"), ToastScript);
            string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            SessionLauncher.Run(ps, $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{Path.Combine(Dir, "toast.ps1")}\"");
        }
        catch { }
    }
}
