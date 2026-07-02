# Read brightness (VCP 0x10), drop to 0 for -Seconds, then restore the original value.
param([int]$Seconds = 6)
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
$orig = @{}
for ($i=0; $i -lt $handles.Count; $i++) {
  [uint32]$t=0; [uint32]$cur=0; [uint32]$max=0
  $ok = [DDC]::GetVCPFeatureAndVCPFeatureReply($handles[$i].h, [byte]0x10, [ref]$t, [ref]$cur, [ref]$max)
  "[$i] '$($handles[$i].desc)' brightness ok=$ok current=$cur max=$max"
  $orig[$i] = $cur
}
"--> setting brightness 0 for $Seconds seconds..."
foreach ($pm in $handles) { [DDC]::SetVCPFeature($pm.h, [byte]0x10, [uint32]0) | Out-Null }
Start-Sleep -Seconds $Seconds
for ($i=0; $i -lt $handles.Count; $i++) {
  $v = [uint32]$orig[$i]; if ($v -le 0) { $v = 75 }
  [DDC]::SetVCPFeature($handles[$i].h, [byte]0x10, $v) | Out-Null
}
"--> restored"
