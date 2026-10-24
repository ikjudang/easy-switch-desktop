# 로지텍(046D) HID collection을 열거하고 usage·리포트 길이·RW 열림 여부를 적는다. 기기에는 아무것도 쓰지 않는다.
param([string]$Out)
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class HidScan {
  [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
  [DllImport("hid.dll", SetLastError=true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
  [DllImport("hid.dll", SetLastError=true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
  [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
  [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
  [DllImport("hid.dll", CharSet=CharSet.Unicode)] public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
  [StructLayout(LayoutKind.Sequential)] public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }
  [StructLayout(LayoutKind.Sequential)] public struct HIDP_CAPS {
    public ushort Usage; public ushort UsagePage; public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
    public ushort NumberLinkCollectionNodes; public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps; public ushort NumberInputDataIndices;
    public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps; public ushort NumberOutputDataIndices;
    public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps; public ushort NumberFeatureDataIndices; }
  [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] public static extern int CM_Get_Device_Interface_List_Size(out int len, ref Guid g, string dev, int flags);
  [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] public static extern int CM_Get_Device_Interface_List(ref Guid g, string dev, char[] buf, int len, int flags);
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
}
"@
$g = [Guid]::Empty; [HidScan]::HidD_GetHidGuid([ref]$g)
$len = 0; [void][HidScan]::CM_Get_Device_Interface_List_Size([ref]$len, [ref]$g, $null, 0)
$buf = New-Object char[] $len; [void][HidScan]::CM_Get_Device_Interface_List([ref]$g, $null, $buf, $len, 0)
$paths = (New-Object string($buf, 0, $len)).Split([char]0) | Where-Object { $_ -match '046d' }
$lines = @()
foreach ($p in $paths) {
  $h = [HidScan]::CreateFile($p, 0, 3, [IntPtr]::Zero, 3, 0, [IntPtr]::Zero)
  if ($h.IsInvalid) { $lines += "OPEN0-FAIL err=$([Runtime.InteropServices.Marshal]::GetLastWin32Error()) $p"; continue }
  $a = New-Object HidScan+HIDD_ATTRIBUTES; $a.Size = [Runtime.InteropServices.Marshal]::SizeOf($a)
  [void][HidScan]::HidD_GetAttributes($h, [ref]$a)
  $pp = [IntPtr]::Zero; [void][HidScan]::HidD_GetPreparsedData($h, [ref]$pp)
  $c = New-Object HidScan+HIDP_CAPS; [void][HidScan]::HidP_GetCaps($pp, [ref]$c); [void][HidScan]::HidD_FreePreparsedData($pp)
  $nb = New-Object byte[] 256; $name = ''
  if ([HidScan]::HidD_GetProductString($h, $nb, 256)) { $name = [Text.Encoding]::Unicode.GetString($nb).TrimEnd([char]0) }
  $h.Close()
  $rw = [HidScan]::CreateFile($p, 0xC0000000, 3, [IntPtr]::Zero, 3, 0, [IntPtr]::Zero)
  $rwText = if ($rw.IsInvalid) { "RW-FAIL err=$([Runtime.InteropServices.Marshal]::GetLastWin32Error())" } else { 'RW-OK' }
  if (-not $rw.IsInvalid) { $rw.Close() }
  $col = if ($p -match '&(col\d+)') { $Matches[1] } else { '-' }
  $kind = if ($p -match '00001812') { 'BLE' } elseif ($p -match 'pid_c54[0-9a-f]') { 'RECEIVER' } else { 'OTHER' }
  $lines += ('{0,-8} pid={1:X4} {2,-5} page=0x{3:X4} usage=0x{4:X4} in={5} out={6} {7,-14} {8}' -f $kind, $a.ProductID, $col, $c.UsagePage, $c.Usage, $c.InputReportByteLength, $c.OutputReportByteLength, $rwText, $name)
}
$procs = Get-Process | Where-Object { $_.ProcessName -match 'logi' } | ForEach-Object { "PROC $($_.ProcessName) pid=$($_.Id)" }
$bt = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match '^(MX|Logi|Bolt|USB Receiver)' } |
  ForEach-Object { "PNP $($_.Class) [$($_.Status)] $($_.FriendlyName)" } | Sort-Object -Unique
($lines + '' + $procs + '' + $bt) | Set-Content -Path $Out -Encoding utf8
