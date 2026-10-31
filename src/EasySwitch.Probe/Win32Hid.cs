using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EasySwitch.Hidpp;
using Microsoft.Win32.SafeHandles;

namespace EasySwitch.Probe;

/// <summary>HID collection 하나의 정체. 윈도우는 한 기기를 usage별 collection 여러 개로 나눠 보인다.</summary>
internal sealed record HidInterface(
    string Path, int VendorId, int ProductId, int UsagePage, int Usage, int InputLength, int OutputLength, string Product)
{
    /// <summary>블루투스 직결 기기의 HID++ 통로(0xFF43/0x0202).</summary>
    public bool IsBleHidpp => UsagePage == HidppUsages.BleVendorPage && Usage == HidppUsages.BleVendorUsage;

    /// <summary>수신기의 HID++ 통로(0xFF00, short 0x0001 또는 long 0x0002).</summary>
    public bool IsReceiverHidpp =>
        VendorId == HidppUsages.LogitechVendorId && UsagePage == HidppUsages.ReceiverVendorPage &&
        Usage is HidppUsages.ReceiverShortUsage or HidppUsages.ReceiverLongUsage;

    /// <summary>블루투스(HOGP) 경로인지. 경로에 HID 서비스 UUID가 들어 있다.</summary>
    public bool IsBluetooth => Path.Contains("{00001812-", StringComparison.OrdinalIgnoreCase);

    public string Collection => Regex.Match(Path, "&(col[0-9a-f]+)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "-";

    /// <summary>같은 인터페이스의 collection 번호와 인스턴스 끝 collection 순번을 함께 제거한다.</summary>
    public string GroupKey => Regex.Replace(
        Regex.Replace(Path, @"&col[0-9a-f]+", "", RegexOptions.IgnoreCase),
        @"&[0-9a-f]{4}(?=#\{)", "", RegexOptions.IgnoreCase).ToLowerInvariant();

    /// <summary>로그용 이름. 경로에는 기기 MAC이 들어 있어 남기지 않는다.</summary>
    public string Label => $"{(IsBluetooth ? "BT" : "USB")} pid={ProductId:X4} {Collection} {Product}".TrimEnd();

    public string Shape => $"page=0x{UsagePage:X4} usage=0x{Usage:X4} in={InputLength} out={OutputLength}";
}

/// <summary>윈도우 HID API(SetupAPI 계열·hid.dll) 호출. tools/hidscan.ps1과 같은 길이다.</summary>
internal static class Win32Hid
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;

    /// <summary>지금 있는 로지텍(046D) HID collection 경로 전부.</summary>
    public static HashSet<string> ListLogitechPaths()
    {
        var (status, _, paths) = ReadHidInterfaceList();
        if (status != 0) return [];
        return paths.Where(p => p.Contains("046d", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>CONFIGRET CR_BUFFER_SMALL: 크기 조회와 목록 조회 사이에 목록이 커졌다(기기 도착 순간).</summary>
    internal const int CrBufferSmall = 0x1A;

    /// <summary>HID 인터페이스 경로 목록. 실패하면 상태 코드와 실패한 호출 이름을 돌려준다.</summary>
    internal static (int Status, string Call, string[] Paths) ReadHidInterfaceList()
    {
        HidD_GetHidGuid(out var guid);
        return ReadInterfaceList(
            () => (CM_Get_Device_Interface_List_Size(out var length, ref guid, null, 0), length),
            buffer => CM_Get_Device_Interface_List(ref guid, null, buffer, buffer.Length, 0));
    }

    /// <summary>
    /// 크기 조회 → 목록 조회 2단계. 그 사이에 인터페이스가 생기면 목록 조회가 CR_BUFFER_SMALL로 실패하는데,
    /// 이는 오류가 아니라 "크기를 다시 물어라"는 뜻이라 크기부터 다시 읽는다(늘어난 크기도 반영). 다른 상태는 그대로 돌려준다.
    /// </summary>
    internal static (int Status, string Call, string[] Paths) ReadInterfaceList(Func<(int Status, int Length)> size, Func<char[], int> fill, int attempts = 5)
    {
        var status = 0;
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            (status, var length) = size();
            if (status != 0) return (status, "크기 조회", []);
            if (length <= 1) return (0, "", []);
            var buffer = new char[length];
            status = fill(buffer);
            if (status == 0) return (0, "", new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries));
            if (status != CrBufferSmall) return (status, "조회", []);
        }
        return (status, $"조회({attempts}회 모두 목록이 커짐)", []);
    }

    /// <summary>접근 권한 없이 열어 정체만 읽는다(키보드·마우스 collection도 이건 된다). 실패하면 null.</summary>
    public static HidInterface? Describe(string path)
    {
        using var handle = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
        HidD_GetAttributes(handle, ref attributes);
        var caps = new HidpCaps();
        if (HidD_GetPreparsedData(handle, out var preparsed))
        {
            HidP_GetCaps(preparsed, out caps);
            HidD_FreePreparsedData(preparsed);
        }
        var nameBuffer = new byte[256];
        var product = HidD_GetProductString(handle, nameBuffer, nameBuffer.Length)
            ? System.Text.Encoding.Unicode.GetString(nameBuffer).TrimEnd('\0')
            : "";
        return new HidInterface(path, attributes.VendorID, attributes.ProductID, caps.UsagePage, caps.Usage,
            caps.InputReportByteLength, caps.OutputReportByteLength, product);
    }

    /// <summary>읽기·쓰기로 연다(비동기 I/O). 다른 앱과 함께 열 수 있게 공유 모드로 연다. 실패하면 윈도우 오류 코드.</summary>
    public static (FileStream? Stream, int Error) OpenReadWrite(string path)
    {
        var handle = CreateFile(path, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return (null, error);
        }
        return (new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true), 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, int length);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_Size(out int length, ref Guid guid, string? deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List(ref Guid guid, string? deviceId, char[] buffer, int length, int flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
