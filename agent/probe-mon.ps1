# Read-only DDC/CI capability probe: can we control monitor power (VCP 0xD6)?
# Does NOT change anything (Get only) - no blink. Must run in the interactive session.
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
  [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitors(uint n, PHYSICAL_MONITOR[] a);
  public static List<IntPtr> Mons() {
    var list = new List<IntPtr>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h,a,b,c)=>{ list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
}
"@
$mons = [DDC]::Mons()
"monitors enumerated: $($mons.Count)"
$i = 0
foreach ($hm in $mons) {
  $i++
  [uint32]$n = 0
  if (-not [DDC]::GetNumberOfPhysicalMonitorsFromHMONITOR($hm, [ref]$n)) { "  [$i] no physical monitor handle"; continue }
  $arr = New-Object 'DDC+PHYSICAL_MONITOR[]' $n
  if ([DDC]::GetPhysicalMonitorsFromHMONITOR($hm, $n, $arr)) {
    foreach ($pm in $arr) {
      [uint32]$t=0; [uint32]$cur=0; [uint32]$max=0
      $ok = [DDC]::GetVCPFeatureAndVCPFeatureReply($pm.h, [byte]0xD6, [ref]$t, [ref]$cur, [ref]$max)
      "  [$i] '$($pm.desc)'  0xD6_supported=$ok  current=$cur  max=$max  (1=on 4=off 5=standby)"
    }
    [DDC]::DestroyPhysicalMonitors($n, $arr) | Out-Null
  } else { "  [$i] GetPhysicalMonitors failed" }
}
