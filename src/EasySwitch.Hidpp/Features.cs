namespace EasySwitch.Hidpp;

/// <summary>Root feature 조회 결과: featureId → 런타임 index + 메타.</summary>
public sealed record FeatureLookup(int FeatureIndex, int TypeFlags = 0, int Version = 0)
{
    /// <summary>index 0은 Root 자신이므로, 조회 대상 feature에 대해서는 "미지원"을 뜻한다.</summary>
    public bool IsSupported => FeatureIndex != 0;
}

/// <summary>
/// Root feature(index 0x00). function 0 = getFeature(featureId: UInt16). 응답 params[0]이 그 feature의 런타임 index다.
/// feature index는 하드코딩하지 않고 이 조회로만 얻는다(펌웨어 버전에 따라 달라진다).
/// </summary>
public static class RootFeature
{
    /// <summary>getFeature 요청. params = [featureId_hi, featureId_lo].</summary>
    public static HidppFrame BuildGetFeature(
        int featureId,
        int deviceIndex = HidppConstants.DeviceIndexDirect,
        int softwareId = HidppConstants.DefaultSoftwareId)
    {
        if (featureId is < 0 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(featureId), $"featureId는 16-bit: {featureId}");
        return HidppFrame.Build(
            featureIndex: HidppConstants.RootFeatureIndex,
            functionId: HidppConstants.RootFnGetFeature,
            deviceIndex: deviceIndex,
            softwareId: softwareId,
            parameters: [(byte)(featureId >> 8), (byte)featureId]);
    }

    /// <summary>getFeature 응답 파싱. params[0]=index, [1]=type flags, [2]=version.</summary>
    public static FeatureLookup ParseGetFeatureResponse(HidppFrame frame) =>
        new(frame.Param(0), frame.Param(1), frame.Param(2));
}

/// <summary>getHostInfo 결과.</summary>
public sealed record HostInfo(int HostCount, int CurrentHost);

/// <summary>
/// ChangeHost(0x1814).
///  - function 0 = getHostInfo (현재 호스트/슬롯 수)
///  - function 1 = setCurrentHost(hostIndex) — hostIndex는 0-based
///
/// setCurrentHost를 보내면 기기가 즉시 링크를 끊고 응답이 오지 않는다 → 응답을 기다리지 않는 경로로 보내야 한다.
/// featureIndex는 반드시 <see cref="RootFeature"/> 조회로 얻은 런타임 값을 넣는다.
/// </summary>
public static class ChangeHostFeature
{
    public static HidppFrame BuildSetCurrentHost(
        int featureIndex,
        int hostIndex,
        int deviceIndex = HidppConstants.DeviceIndexDirect,
        int softwareId = HidppConstants.DefaultSoftwareId)
    {
        if (hostIndex is < 0 or > 0xFF) throw new ArgumentOutOfRangeException(nameof(hostIndex), $"hostIndex(0-based) 범위 밖: {hostIndex}");
        return HidppFrame.Build(
            featureIndex: featureIndex,
            functionId: HidppConstants.ChangeHostFnSetCurrentHost,
            deviceIndex: deviceIndex,
            softwareId: softwareId,
            parameters: [(byte)hostIndex]);
    }

    public static HidppFrame BuildGetHostInfo(
        int featureIndex,
        int deviceIndex = HidppConstants.DeviceIndexDirect,
        int softwareId = HidppConstants.DefaultSoftwareId) =>
        HidppFrame.Build(
            featureIndex: featureIndex,
            functionId: HidppConstants.ChangeHostFnGetHostInfo,
            deviceIndex: deviceIndex,
            softwareId: softwareId);

    /// <summary>fn0 getHostInfo 응답: params[0]=호스트 수, params[1]=현재 호스트(0-based). 값이 말이 안 되면 기본 채널로 오인하지 않고 예외.</summary>
    public static HostInfo ParseGetHostInfo(HidppFrame frame)
    {
        var count = frame.Param(0);
        var current = frame.Param(1);
        if (count is < 1 or > 3 || current < 0 || current >= count)
            throw new InvalidDataException($"잘못된 호스트 정보: count={count}, current={current}");
        return new HostInfo(count, current);
    }

    /// <summary>
    /// 기기가 <b>스스로 채널 전환을 시작할 때</b> 현재 호스트로 보내는 이벤트를 파싱한다.
    ///
    /// Easy-Switch 키를 누르면 링크가 끊기기 직전 <c>[0x1814의 index][funcSw=0x00][출발 슬롯][목적지]…</c> 이벤트가 온다.
    /// funcSw=0x00(function 0, softwareId 0)이 기기 자발 이벤트의 표식이다(응답은 swId를 에코한다).
    ///
    /// params[0]은 판정에 쓰지 않는다. 안드로이드 0.2.17에서 "params[0]이 0이 아니면 버린다"를 넣은 뒤 MX KEYS S의 전환
    /// 감지가 사흘간 0건이 됐다(params[0]은 출발 슬롯이었다: 태블릿에서 01, 휴대폰에서 02). 실측 잡음(MX MCHNCL
    /// <c>09 00 4B 08</c>)은 목적지 범위 검사로 걸러진다.
    /// </summary>
    /// <returns>목적지 호스트(0-based). 이 프레임이 해당 이벤트가 아니면 null.</returns>
    public static int? ParseHostChangeEvent(HidppFrame frame, int changeHostFeatureIndex)
    {
        if (frame.FeatureIndex != changeHostFeatureIndex) return null;
        if (frame.FuncSwByte != 0x00) return null;
        var target = frame.Param(1);
        return target is >= 0 and <= 2 ? target : null;
    }
}

/// <summary>DeviceName(0x0005). 수신기 뒤 기기는 HID 제품 이름이 수신기 이름이라, 어느 슬롯이 어느 기기인지 이걸로 가른다.</summary>
public static class DeviceNameFeature
{
    public static HidppFrame BuildGetCount(int featureIndex, int deviceIndex, int softwareId = HidppConstants.DefaultSoftwareId) =>
        HidppFrame.Build(featureIndex, HidppConstants.DeviceNameFnGetCount, deviceIndex: deviceIndex, softwareId: softwareId);

    public static HidppFrame BuildGetName(int featureIndex, int charIndex, int deviceIndex, int softwareId = HidppConstants.DefaultSoftwareId) =>
        HidppFrame.Build(featureIndex, HidppConstants.DeviceNameFnGetName, deviceIndex: deviceIndex, softwareId: softwareId,
            parameters: [(byte)charIndex]);

    /// <summary>getCount 응답: params[0] = 이름 길이.</summary>
    public static int ParseCount(HidppFrame frame) => frame.Param(0);

    /// <summary>getName 응답: params = 이름 조각(ASCII, 0으로 끝날 수 있다).</summary>
    public static string ParseNameChunk(HidppFrame frame, int remaining)
    {
        var take = Math.Min(remaining, frame.Params.Length);
        var chunk = frame.Params[..take];
        var end = chunk.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(end >= 0 ? chunk[..end] : chunk);
    }
}

/// <summary>
/// 수신기(HID++ 1.0) 메시지. 볼트에서는 아직 실측 전이라, 해석은 유니파잉 문서(Solaar) 기준의 추정이다.
/// 측정 도구는 원본 바이트를 함께 남긴다.
/// </summary>
public static class ReceiverMessages
{
    /// <summary>Read-modify-write for the wireless notification bit; preserve every other receiver flag.</summary>
    public static byte[] BuildEnableWirelessNotifications(ReadOnlySpan<byte> registerReply)
    {
        if (registerReply.Length < HidppConstants.ShortLength || registerReply[1] != 0xFF ||
            registerReply[2] != HidppConstants.Hidpp10GetShortRegister || registerReply[3] != HidppConstants.RegisterNotifications)
            throw new InvalidDataException("알림 설정 레지스터의 정상 응답이 아닙니다.");
        return [0x10, 0xFF, 0x80, 0x00, registerReply[4], (byte)(registerReply[5] | 0x01), registerReply[6]];
    }

    /// <summary>짧은 레지스터 읽기 요청: [0x10][0xFF][0x81][주소][0][0][0].</summary>
    public static byte[] BuildGetShortRegister(int address) =>
        [HidppConstants.ReportIdShort, HidppConstants.DeviceIndexDirect, HidppConstants.Hidpp10GetShortRegister, (byte)address, 0, 0, 0];

    /// <summary>
    /// 기기 연결 상태 알림(0x41): [report][기기 번호][0x41][프로토콜][플래그][무선 PID 하위][무선 PID 상위].
    /// 유니파잉에서는 플래그 bit 6이 1이면 "링크 없음"이다 — 볼트도 같은지는 미실측.
    /// </summary>
    public static DeviceConnectionNotice? ParseDeviceConnection(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < HidppConstants.ShortLength) return null;
        if (raw[0] != HidppConstants.ReportIdShort && raw[0] != HidppConstants.ReportIdLong) return null;
        if (raw[2] != HidppConstants.Hidpp10DeviceConnection) return null;
        return new DeviceConnectionNotice(raw[1], raw[3], raw[4], raw[5] | (raw[6] << 8));
    }
}

/// <param name="DeviceIndex">수신기 슬롯(1..6).</param>
/// <param name="Protocol">유니파잉 0x04 등. 볼트 값은 미실측.</param>
/// <param name="Flags">기기 종류·링크 상태 플래그.</param>
/// <param name="WirelessProductId">무선 PID.</param>
public sealed record DeviceConnectionNotice(int DeviceIndex, int Protocol, int Flags, int WirelessProductId)
{
    /// <summary>유니파잉 기준 해석(bit 6 = 링크 없음). 볼트에서 맞는지는 측정으로 확인한다.</summary>
    public bool LinkEstablishedAssumed => (Flags & 0x40) == 0;
}
