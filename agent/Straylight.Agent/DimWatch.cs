using System.Runtime.InteropServices;

namespace Straylight.Agent;

/// <summary>
/// Runs in the user session (launched by the service as `--dim-watch`). Dims the screen (DDC
/// brightness 0) and restores it the moment REAL input arrives — using low-level hooks and the
/// LLMHF/LLKHF_INJECTED flag so a software auto-clicker (SendInput/AutoHotkey) is IGNORED but a
/// genuine mouse move / keypress wakes it. Also exits+restores if the service drops a stop flag.
/// This is what makes Screen dim safe: it can never leave the panel dark with no way to wake it.
/// </summary>
public static class DimWatch
{
    const string Save = @"C:\ProgramData\Straylight\brightness.sav";
    const string StopFlag = @"C:\ProgramData\Straylight\dim.stop";
    const string DimState = @"C:\ProgramData\Straylight\dimmed.state";

    const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
    const int HC_ACTION = 0;
    const uint LLMHF_INJECTED = 0x00000001, LLKHF_INJECTED = 0x00000010;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }

    static HookProc? _mouse, _kbd;   // keep alive
    static int _woke;

    public static void Run()
    {
        Monitors.Dim(Save);                       // save current brightness, set 0
        try { File.WriteAllText(DimState, "1"); } catch { }
        try { if (File.Exists(StopFlag)) File.Delete(StopFlag); } catch { }

        _mouse = MouseProc; _kbd = KbdProc;
        var h1 = SetWindowsHookEx(WH_MOUSE_LL, _mouse, IntPtr.Zero, 0);
        var h2 = SetWindowsHookEx(WH_KEYBOARD_LL, _kbd, IntPtr.Zero, 0);

        // watch for the service's stop flag on a background thread
        var t = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(400);
                if (File.Exists(StopFlag)) { try { File.Delete(StopFlag); } catch { } Wake(); }
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
            if ((d.flags & LLMHF_INJECTED) == 0) Wake();   // real hardware only
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    static IntPtr KbdProc(int code, IntPtr w, IntPtr l)
    {
        if (code == HC_ACTION)
        {
            var d = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
            if ((d.flags & LLKHF_INJECTED) == 0) Wake();
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    static void Wake()
    {
        if (Interlocked.Exchange(ref _woke, 1) != 0) return;  // once
        try { Monitors.Restore(Save); } catch { }
        try { File.WriteAllText(DimState, "0"); } catch { }
        Environment.Exit(0);
    }
}
