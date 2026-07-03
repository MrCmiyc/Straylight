using System.Runtime.InteropServices;

namespace Straylight.Agent;

/// <summary>
/// Persistent real-input idle tracker, launched into the user session as `--idle-watch` while
/// idle_v2 is on. Installs low-level mouse+keyboard hooks and stamps the last REAL (non-injected)
/// input time to realinput.now — an autoclicker's SendInput carries LLMHF/LLKHF_INJECTED and is
/// ignored, so idle climbs when only a bot is "using" the box. A heartbeat file lets the service
/// know the watcher is alive (else it falls back to quser). Single-instance via a named mutex so a
/// re-launch from the service can't stack duplicates; exits on the stop flag.
///
/// Caveat: a *hardware* jiggler / driver-level injector isn't marked injected, so it counts as real
/// input — this defeats software autoclickers (SendInput/AutoHotkey), not hardware ones.
/// </summary>
public static class IdleWatch
{
    const string Dir = @"C:\ProgramData\Straylight";
    static readonly string RealInput = Path.Combine(Dir, "realinput.now");
    static readonly string Heartbeat = Path.Combine(Dir, "realinput.hb");
    static readonly string StopFlag = Path.Combine(Dir, "idlewatch.stop");

    const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13, HC_ACTION = 0;
    const uint LLMHF_INJECTED = 0x00000001, LLKHF_INJECTED = 0x00000010;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }

    static HookProc? _mouse, _kbd;   // keep alive
    static long _lastStamp;

    // at most one file write per second (mouse-move floods otherwise)
    static void Stamp()
    {
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        if (Interlocked.Exchange(ref _lastStamp, now) == now) return;
        try { File.WriteAllText(RealInput, now.ToString()); } catch { }
    }

    public static void Run()
    {
        using var mutex = new Mutex(true, @"Global\StraylightIdleWatch", out bool mine);
        if (!mine) return;   // another watcher already owns this session

        Directory.CreateDirectory(Dir);
        try { if (File.Exists(StopFlag)) File.Delete(StopFlag); } catch { }
        _lastStamp = 0; Stamp();   // start "just active"

        _mouse = MouseProc; _kbd = KbdProc;
        SetWindowsHookEx(WH_MOUSE_LL, _mouse, IntPtr.Zero, 0);
        SetWindowsHookEx(WH_KEYBOARD_LL, _kbd, IntPtr.Zero, 0);

        var t = new Thread(() =>
        {
            while (true)
            {
                try { File.WriteAllText(Heartbeat, DateTimeOffset.Now.ToUnixTimeSeconds().ToString()); } catch { }
                if (File.Exists(StopFlag)) { try { File.Delete(StopFlag); } catch { } Environment.Exit(0); }
                Thread.Sleep(3000);
            }
        }) { IsBackground = true };
        t.Start();

        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }  // pump hooks
    }

    static IntPtr MouseProc(int code, IntPtr w, IntPtr l)
    {
        if (code == HC_ACTION)
        {
            var d = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
            if ((d.flags & LLMHF_INJECTED) == 0) Stamp();   // real hardware only
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    static IntPtr KbdProc(int code, IntPtr w, IntPtr l)
    {
        if (code == HC_ACTION)
        {
            var d = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
            if ((d.flags & LLKHF_INJECTED) == 0) Stamp();
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }
}
