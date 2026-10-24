namespace EasySwitch.Hidpp;

/// <summary>HID++ 2.0 표준 error code.</summary>
public enum HidppErrorCode
{
    NoError = 0x00,
    Unknown = 0x01,
    InvalidArgument = 0x02,
    OutOfRange = 0x03,
    HardwareError = 0x04,
    LogitechInternal = 0x05,
    InvalidFeatureIndex = 0x06,
    InvalidFunctionId = 0x07,
    Busy = 0x08,
    Unsupported = 0x09,
}

/// <summary>
/// 에러 응답에서 뽑아낸 정보.
///
/// 배치(HID++ 1.0·2.0 공통): <c>[report_id][device_index][마커][실패한 요청의 byte2][실패한 요청의 byte3][error code]</c>.
/// 2.0이면 byte2·byte3은 요청의 feature index·func|sw이고, 1.0이면 sub_id·주소다. Solaar(base.py)가 이 배치로 상관한다.
///
/// <b>안드로이드 앱과 다르다:</b> 안드로이드 <c>HidppFrameParser.parseError</c>는 byte[3]을 func|sw, byte[4]를 feature index로
/// 읽는다(2026-10-20 포팅 중 발견, 실기기 미확인). 위 배치가 맞다면 안드로이드의 에러 응답은 요청과 상관되지 않아 시간 초과로 끝난다.
/// 측정 도구가 일부러 잘못된 함수를 보내 실제 바이트로 가른다.
/// </summary>
/// <param name="Protocol">2 = HID++ 2.0 에러(byte[2]==0xFF), 1 = HID++ 1.0 에러(byte[2]==0x8F).</param>
/// <param name="FailedFeatureIndex">실패한 요청의 feature index(1.0이면 sub_id) — byte[3].</param>
/// <param name="FuncSwByte">실패한 요청의 func|sw(1.0이면 레지스터 주소) — byte[4].</param>
/// <param name="ErrorCode">error code — byte[5].</param>
public sealed record HidppErrorInfo(int Protocol, int FailedFeatureIndex, int FuncSwByte, int ErrorCode)
{
    public HidppErrorCode? ErrorCodeEnum => Enum.IsDefined(typeof(HidppErrorCode), ErrorCode) ? (HidppErrorCode)ErrorCode : null;

    /// <summary>이 에러가 <paramref name="request"/>에 대한 것인지.</summary>
    public bool IsFor(HidppFrame request) =>
        FailedFeatureIndex == request.FeatureIndex && FuncSwByte == request.FuncSwByte;
}

/// <summary>기기가 에러 응답을 돌려줬을 때.</summary>
public sealed class HidppProtocolException(HidppErrorInfo info)
    : Exception($"HID++ {info.Protocol}.0 error: code=0x{info.ErrorCode:X2}({info.ErrorCodeEnum?.ToString() ?? "?"}) feature=0x{info.FailedFeatureIndex:X2}")
{
    public HidppErrorInfo Info { get; } = info;
}

/// <summary>
/// feature index 조회 결과가 0(미지원)일 때. index 0이면 그 feature가 없는 것이므로, 엉뚱한 feature(index 0 = Root)를
/// 조용히 실행하지 않도록 반드시 예외로 올린다.
/// </summary>
public sealed class FeatureUnsupportedException(int featureId)
    : Exception($"Feature 0x{featureId:X4} 미지원(index 0)")
{
    public int FeatureId { get; } = featureId;
}

/// <summary>raw 바이트 → <see cref="HidppFrame"/> 파싱 및 에러 판별. 입력은 report_id를 포함한 표준 프레임이다.</summary>
public static class HidppFrameParser
{
    /// <summary>정상 프레임 파싱. 리포트 종류/길이가 맞지 않으면 null. 에러 프레임도 형식상 파싱은 된다.</summary>
    public static HidppFrame? Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty) return null;
        if (ReportTypes.FromReportId(raw[0]) is not { } type) return null;
        if (raw.Length < type.TotalLength()) return null;
        return new HidppFrame(type, raw[1], raw[2], raw[3], raw[HidppConstants.HeaderLength..type.TotalLength()]);
    }

    /// <summary>HID++ 1.0/2.0 에러 응답인지. byte[2]가 에러 마커(SHORT/LONG 모두 허용).</summary>
    public static bool IsError(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < HidppConstants.ShortLength) return false;
        if (raw[0] != HidppConstants.ReportIdShort && raw[0] != HidppConstants.ReportIdLong) return false;
        return raw[2] == HidppConstants.Hidpp20Error || raw[2] == HidppConstants.Hidpp10Error;
    }

    /// <summary>에러 응답을 <see cref="HidppErrorInfo"/>로. 에러가 아니면 null.</summary>
    public static HidppErrorInfo? ParseError(ReadOnlySpan<byte> raw)
    {
        if (!IsError(raw)) return null;
        var protocol = raw[2] == HidppConstants.Hidpp20Error ? 2 : 1;
        return new HidppErrorInfo(protocol, FailedFeatureIndex: raw[3], FuncSwByte: raw[4], ErrorCode: raw[5]);
    }
}
