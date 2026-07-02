# Set a DDC/CI VCP feature.  -Code 0xD6 power (4=off,5=standby,1=on);  -Code 0x10 brightness (0-100).
# Must run in the interactive session. Reports the set result + a re-read.
param([int]$Code = 0xD6, [int]$Value = 4)
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
  [DllImport("dxva2.dll")] public static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
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
foreach ($pm in $handles) {
  $r = [DDC]::SetVCPFeature($pm.h, [byte]$Code, [uint32]$Value)
  "SET code=$('0x{0:X2}' -f $Code) value=$Value '$($pm.desc)' -> returned $r"
}
Start-Sleep -Seconds 5
foreach ($pm in $handles) {
  [uint32]$t=0; [uint32]$cur=0; [uint32]$max=0
  $ok = [DDC]::GetVCPFeatureAndVCPFeatureReply($pm.h, [byte]$Code, [ref]$t, [ref]$cur, [ref]$max)
  "RE-READ after 5s '$($pm.desc)' -> ok=$ok current=$cur  (4=off,5=standby,1=on)"
}
