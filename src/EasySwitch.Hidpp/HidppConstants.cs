namespace EasySwitch.Hidpp;

/// <summary>
/// HID++ 프로토콜 상수. 플랫폼과 무관하다(윈도우·맥이 같이 쓴다).
///
/// 프레임 구조: [report_id][device_index][feature_index][function_id&lt;&lt;4 | software_id][params…]
///
/// 기준은 안드로이드 앱의 <c>hidpp/HidppConstants.kt</c>다. 레퍼런스: Solaar, logiops 위키 "HIDPP 2.0",
/// 리눅스 커널 drivers/hid/hid-logitech-hidpp.c
/// </summary>
public static class HidppConstants
{
    public const int ReportIdShort = 0x10; // 총 7바이트
    public const int ReportIdLong = 0x11; // 총 20바이트
    public const int ShortLength = 7;
    public const int LongLength = 20;
    public const int HeaderLength = 4; // report_id, device_index, feature_index, func|sw

    /// <summary>
    /// 블루투스 직결 기기의 device_index. MX Master 4는 0x01로 보내면 침묵하고 0xFF에서만 응답한다(안드로이드 실측).
    /// 볼트 수신기 자신도 0xFF다.
    /// </summary>
    public const int DeviceIndexDirect = 0xFF;

    /// <summary>볼트 수신기 뒤의 기기 번호 범위(페어링 슬롯).</summary>
    public const int ReceiverFirstDevice = 1;
    public const int ReceiverLastDevice = 6;

    /// <summary>
    /// software_id: func|sw 바이트의 하위 4비트(1..15, 0 제외). 응답은 이 값을 에코하고, 기기가 스스로 보내는 이벤트는 0이다.
    /// 이 값(0x0A)은 안드로이드 앱의 값이며, 안드로이드에서 옮긴 테스트가 같은 바이트를 기대해서 빌더의 기본값으로 남겨 둔다.
    /// Windows 앱은 이 기본값을 쓰지 않고 <see cref="WindowsSoftwareId"/>를 모든 요청에 명시한다. 측정 도구는 서비스와 함께 돌 때
    /// 서로의 응답을 가로채지 않도록 일부러 이 값(앱과 다른 값)을 기본으로 쓴다.
    /// </summary>
    public const int DefaultSoftwareId = 0x0A;

    /// <summary>
    /// Windows 앱의 software_id. 2026-10-21 캡처에서 Options+ 실행 중 보인 다른 응답 ID 0B..0F와 겹치지 않았다.
    /// 전 세계적으로 예약된 값이라는 뜻은 아니다.
    /// </summary>
    public const int WindowsSoftwareId = 0x09;

    public const int RootFeatureIndex = 0x00;
    public const int RootFnGetFeature = 0x00;

    public const int FeatureRoot = 0x0000;
    public const int FeatureDeviceName = 0x0005;
    public const int FeatureChangeHost = 0x1814;
    public const int FeatureHostsInfo = 0x1815;

    public const int ChangeHostFnGetHostInfo = 0x00;
    public const int ChangeHostFnSetCurrentHost = 0x01;

    public const int DeviceNameFnGetCount = 0x00;
    public const int DeviceNameFnGetName = 0x01;

    /// <summary>
    /// 에러 마커(feature_index 자리, byte[2]). HID++ 2.0 에러 = 0xFF, HID++ 1.0 에러 sub_id = 0x8F.
    /// 바이트 배치는 <see cref="HidppErrorInfo"/>에 적었다.
    /// </summary>
    public const int Hidpp20Error = 0xFF;
    public const int Hidpp10Error = 0x8F;

    // ---- HID++ 1.0: 수신기 레지스터·알림 ----

    /// <summary>짧은 레지스터 읽기(sub_id). 요청 [0x10][0xFF][0x81][주소][0][0][0].</summary>
    public const int Hidpp10GetShortRegister = 0x81;

    /// <summary>수신기 알림 설정 레지스터. 무선 알림이 꺼져 있으면 기기 연결 알림(0x41)이 오지 않는다.</summary>
    public const int RegisterNotifications = 0x00;

    /// <summary>수신기가 보내는 기기 연결 상태 알림(sub_id). 수신기 뒤 기기가 붙거나 떨어질 때 온다.</summary>
    public const int Hidpp10DeviceConnection = 0x41;

    /// <summary>수신기가 보내는 기기 해제(페어링 해제) 알림(sub_id).</summary>
    public const int Hidpp10DeviceDisconnection = 0x40;
}

/// <summary>HID collection의 usage page·usage. 윈도우·맥 모두 HID 계층에서 같은 값으로 보인다.</summary>
public static class HidppUsages
{
    /// <summary>블루투스 직결 기기의 HID++ long report 통로(윈도우 실측 2026-09-12, input·output 20B).</summary>
    public const int BleVendorPage = 0xFF43;
    public const int BleVendorUsage = 0x0202;

    /// <summary>수신기(볼트·유니파잉)의 HID++ 통로. usage 0x0001 = short(7B), 0x0002 = long(20B). 볼트에서는 미실측.</summary>
    public const int ReceiverVendorPage = 0xFF00;
    public const int ReceiverShortUsage = 0x0001;
    public const int ReceiverLongUsage = 0x0002;

    public const int LogitechVendorId = 0x046D;

    /// <summary>볼트 수신기 PID.</summary>
    public const int BoltReceiverProductId = 0xC548;
    public const int UnifyingReceiverProductId = 0xC52B;
}
