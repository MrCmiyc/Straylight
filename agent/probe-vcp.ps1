# Read-only: report what VCP features the monitors actually answer (no changes, no blink).
# 0x10 brightness, 0x12 contrast, 0xD6 power. Must run in the interactive session.
$ErrorActionPreference = 'SilentlyContinue'
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class DDC {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct PHYSICAL_MONITOR { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string desc; }
  public delegate bool MonEnum(IntPtr hMon, IntPtr hdc, IntPtr rc, IntPtr data);
  [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonEnum cb, IntPtr data);
  [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
  [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] a);
  [DllImport("dxva2.dll")] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint cur, out uint max);
  public static List<IntPtr> Mons() {
    var list = new List<IntPtr>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h,a,b,c)=>{ list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
}
"@
$handles = New-Object System.Collections.Generic.List[object]
foreach ($hm in [DDC]::Mons()) {
  [uint32]$n = 0
  if ([DDC]::GetNumberOfPhysicalMonitorsFromHMONITOR($hm, [ref]$n)) {
    $arr = New-Object 'DDC+PHYSICAL_MONITOR[]' $n
    if ([DDC]::GetPhysicalMonitorsFromHMONITOR($hm, $n, $arr)) { foreach ($pm in $arr) { $handles.Add($pm) } }
  }
}
"physical monitors: $($handles.Count)"
$codes = @( @{n='brightness';c=0x10}, @{n='contrast';c=0x12}, @{n='power';c=0xD6} )
$i = 0
foreach ($pm in $handles) {
  "[$i] '$($pm.desc)'"
  foreach ($x in $codes) {
    [uint32]$t=0; [uint32]$cur=0; [uint32]$max=0
    $ok = [DDC]::GetVCPFeatureAndVCPFeatureReply($pm.h, [byte]$x.c, [ref]$t, [ref]$cur, [ref]$max)
    "    {0,-11} ({1}): ok={2} current={3} max={4}" -f $x.n, ('0x{0:X2}' -f $x.c), $ok, $cur, $max
  }
  $i++
}
