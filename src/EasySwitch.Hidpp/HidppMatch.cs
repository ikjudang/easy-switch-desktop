namespace EasySwitch.Hidpp;

/// <summary>들어온 리포트가 보낸 요청과 어떤 관계인지.</summary>
public enum ResponseKind
{
    /// <summary>정상 응답(byte1~3 에코).</summary>
    Response,

    /// <summary>[FF 또는 8F][요청 byte2][요청 byte3][코드] — Solaar(base.py) 배치.</summary>
    ErrorSolaarLayout,

    /// <summary>[FF 또는 8F][요청 byte3][요청 byte2][코드] — 안드로이드 <c>HidppFrameParser.parseError</c>가 가정한 배치.</summary>
    ErrorSwappedLayout,
}

/// <summary>
/// 응답을 요청과 짝짓는다. 정상 응답은 byte1~3(기기 번호·feature index·func|sw, HID++ 1.0이면 기기 번호·sub_id·주소)을 그대로 에코한다.
/// 같은 feature를 다른 앱이 물어도 software id(byte3 하위 4비트)가 달라 섞이지 않는다 — 기기 이벤트는 software id가 0이다.
/// 에러 배치는 아직 실기기로 확정하지 않아 두 배치를 모두 받고 어느 쪽이었는지 돌려준다(HidppErrorInfo 주석 참고).
/// </summary>
public static class HidppMatch
{
    public static ResponseKind? Classify(ReadOnlySpan<byte> request, ReadOnlySpan<byte> raw)
    {
        if (request.Length < HidppConstants.HeaderLength || raw.Length < HidppConstants.ShortLength) return null;
        if (raw[1] != request[1]) return null;
        if (raw[2] == request[2] && raw[3] == request[3]) return ResponseKind.Response;
        if (raw[2] is HidppConstants.Hidpp20Error or HidppConstants.Hidpp10Error)
        {
            if (raw[3] == request[2] && raw[4] == request[3]) return ResponseKind.ErrorSolaarLayout;
            if (raw[3] == request[3] && raw[4] == request[2]) return ResponseKind.ErrorSwappedLayout;
        }
        return null;
    }

    /// <summary>
    /// 요청의 byte2와 byte3이 같으면(예: feature index 0x09에 함수 0·software id 9) 두 에러 배치의 바이트가 같아 어느 배치인지 가를 수 없다.
    /// 이런 요청의 에러로는 배치를 기록하지 않는다.
    /// </summary>
    public static bool ErrorLayoutAmbiguous(ReadOnlySpan<byte> request) =>
        request.Length >= HidppConstants.HeaderLength && request[2] == request[3];
}
