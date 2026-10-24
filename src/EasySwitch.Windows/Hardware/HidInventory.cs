using System.Runtime.InteropServices;

namespace EasySwitch.Windows.Hardware;

internal static class HidInventory
{
    // A failed inventory must not look like removal of every connected device.
    public static HashSet<string> Read()
    {
        HidD_GetHidGuid(out var guid);
        var status = CM_Get_Device_Interface_List_Size(out var length, ref guid, null, 0);
        if (status != 0) throw new IOException($"HID 목록 크기 조회 실패: CONFIGRET 0x{status:X}");
        if (length <= 1) return [];
        var buffer = new char[length];
        status = CM_Get_Device_Interface_List(ref guid, null, buffer, length, 0);
        if (status != 0) throw new IOException($"HID 목록 조회 실패: CONFIGRET 0x{status:X}");
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Contains("046d", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_Interface_List_Size(out int length, ref Guid guid, string? deviceId, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_Interface_List(ref Guid guid, string? deviceId, char[] buffer, int length, int flags);
}
