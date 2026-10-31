using EasySwitch.Probe;

namespace EasySwitch.Windows.Hardware;

internal static class HidInventory
{
    // A failed inventory must not look like removal of every connected device. A list that grew
    // between the size and list calls (a device arriving) is re-read, not reported as a failure.
    public static HashSet<string> Read()
    {
        var (status, call, paths) = Win32Hid.ReadHidInterfaceList();
        if (status != 0) throw new IOException($"HID 목록 {call} 실패: CONFIGRET 0x{status:X}");
        return paths.Where(p => p.Contains("046d", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
