using System.Runtime.InteropServices;

namespace Straylight.Agent;

/// <summary>
/// DDC/CI monitor brightness control (VCP 0x10). MUST run in the interactive session
/// (has a desktop) - the service reaches it via SessionLauncher. Dim() saves the current
/// per-monitor brightness to a file then sets 0; Restore() writes the saved values back.
/// </summary>
public static class Monitors
{
    const byte BRIGHTNESS = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc; }
    delegate bool MonEnum(IntPtr h, IntPtr hdc, IntPtr rc, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonEnum cb, IntPtr data);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] a);
    [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint cur, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr h, byte code, uint value);

    static List<PHYSICAL_MONITOR> GetPhysical()
    {
        var hmons = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, a, b, c) => { hmons.Add(h); return true; }, IntPtr.Zero);
        var result = new List<PHYSICAL_MONITOR>();
        foreach (var hm in hmons)
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out uint n) && n > 0)
            {
                var arr = new PHYSICAL_MONITOR[n];
                if (GetPhysicalMonitorsFromHMONITOR(hm, n, arr)) result.AddRange(arr);
            }
        return result;
    }

    public static void Dim(string saveFile)
    {
        var mons = GetPhysical();
        var saved = new List<uint>();
        foreach (var m in mons)
            saved.Add(GetVCPFeatureAndVCPFeatureReply(m.h, BRIGHTNESS, out _, out uint cur, out _) ? cur : 50);
        try { File.WriteAllLines(saveFile, saved.Select(x => x.ToString())); } catch { }
        foreach (var m in mons) SetVCPFeature(m.h, BRIGHTNESS, 0);
    }

    /// <summary>Current brightness of the first monitor as a percent (0-100), or -1 if unreadable.</summary>
    public static int GetBrightnessPercent()
    {
        foreach (var m in GetPhysical())
            if (GetVCPFeatureAndVCPFeatureReply(m.h, BRIGHTNESS, out _, out uint cur, out uint max) && max > 0)
                return (int)Math.Round(100.0 * cur / max);
        return -1;
    }

    public static void Restore(string saveFile)
    {
        var mons = GetPhysical();
        uint[] saved = Array.Empty<uint>();
        try { if (File.Exists(saveFile)) saved = File.ReadAllLines(saveFile).Select(uint.Parse).ToArray(); } catch { }
        for (int i = 0; i < mons.Count; i++)
        {
            uint v = (i < saved.Length && saved[i] > 0) ? saved[i] : 50;
            SetVCPFeature(mons[i].h, BRIGHTNESS, v);
        }
    }
}
