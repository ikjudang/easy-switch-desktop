using EasySwitch.Hidpp;

namespace EasySwitch.Hidpp.Tests;

/// <summary>안드로이드 <c>HidppFrameParserTest.kt</c>·<c>RootFeatureTest.kt</c>·<c>ChangeHostFeatureTest.kt</c>에서 옮겼다.</summary>
public class FeatureTests
{
    private static HidppFrame Frame(string hex) => HidppFrameParser.Parse(Hex.Parse(hex))!;

    // ---- 파서 ----

    [Fact(DisplayName = "알 수 없는 report_id는 null")]
    public void UnknownReportId() => Assert.Null(HidppFrameParser.Parse(Hex.Parse("99 FF 00 0A 00 00 00")));

    [Fact(DisplayName = "SHORT 리포트가 7바이트보다 짧으면 null")]
    public void TooShort() => Assert.Null(HidppFrameParser.Parse(Hex.Parse("10 FF 00")));

    [Fact(DisplayName = "HID++ 2.0 에러: byte3=실패 feature index, byte4=func|sw, byte5=error code (Solaar 배치)")]
    public void Hidpp20ErrorLayout()
    {
        // 안드로이드 테스트와 같은 바이트지만 읽는 방법이 다르다 — HidppErrorInfo 주석 참고. 실기기 측정으로 확정한다.
        var raw = Hex.Parse("10 FF FF 0A 05 09 00");
        Assert.True(HidppFrameParser.IsError(raw));
        var info = HidppFrameParser.ParseError(raw)!;
        Assert.Equal(2, info.Protocol);
        Assert.Equal(0x0A, info.FailedFeatureIndex);
        Assert.Equal(0x05, info.FuncSwByte);
        Assert.Equal(0x09, info.ErrorCode);
        Assert.Equal(HidppErrorCode.Unsupported, info.ErrorCodeEnum);
    }

    [Fact(DisplayName = "에러는 실패한 요청의 feature index와 func|sw가 모두 맞을 때만 그 요청의 것이다")]
    public void ErrorMatchesRequest()
    {
        var request = RootFeature.BuildGetFeature(HidppConstants.FeatureChangeHost, deviceIndex: 2); // feature 0x00, func|sw 0x0A
        var error = HidppFrameParser.ParseError(Hex.Parse("10 02 8F 00 0A 08 00"))!; // 수신기가 1.0 에러로 돌려준 경우
        Assert.True(error.IsFor(request));
        Assert.False(HidppFrameParser.ParseError(Hex.Parse("10 02 8F 0A 00 08 00"))!.IsFor(request));
    }

    [Fact(DisplayName = "HID++ 1.0 에러 마커(0x8F)도 에러로 감지된다")]
    public void Hidpp10Error()
    {
        var raw = Hex.Parse("10 FF 8F 0A 05 03 00");
        Assert.True(HidppFrameParser.IsError(raw));
        Assert.Equal(1, HidppFrameParser.ParseError(raw)!.Protocol);
    }

    [Fact(DisplayName = "정상 응답은 에러가 아니다")]
    public void NormalIsNotError()
    {
        var raw = Hex.Parse("10 FF 00 0A 05 00 04");
        Assert.False(HidppFrameParser.IsError(raw));
        Assert.Null(HidppFrameParser.ParseError(raw));
    }

    // ---- Root ----

    [Fact(DisplayName = "getFeature(0x1814) 요청 바이트가 골든벡터와 일치한다")]
    public void GetFeatureGolden() =>
        Assert.Equal(Hex.Parse("10 FF 00 0A 18 14 00"), RootFeature.BuildGetFeature(HidppConstants.FeatureChangeHost).ToBytes());

    [Fact(DisplayName = "getFeature는 16-bit featureId를 hi_lo로 분해한다")]
    public void GetFeatureSplitsId() =>
        Assert.Equal(Hex.Parse("10 FF 00 0A 18 15 00"), RootFeature.BuildGetFeature(HidppConstants.FeatureHostsInfo).ToBytes());

    [Fact(DisplayName = "수신기 뒤 기기에는 그 슬롯 번호로 보낸다")]
    public void GetFeatureForReceiverSlot() =>
        Assert.Equal(Hex.Parse("10 03 00 0A 18 14 00"), RootFeature.BuildGetFeature(HidppConstants.FeatureChangeHost, deviceIndex: 3).ToBytes());

    [Fact(DisplayName = "getFeature 응답에서 feature index를 읽는다")]
    public void GetFeatureResponse()
    {
        var lookup = RootFeature.ParseGetFeatureResponse(Frame("10 FF 00 0A 05 00 04"));
        Assert.Equal(0x05, lookup.FeatureIndex);
        Assert.Equal(0x04, lookup.Version);
        Assert.True(lookup.IsSupported);
    }

    [Fact(DisplayName = "feature index 0은 미지원으로 판정된다")]
    public void IndexZeroUnsupported()
    {
        var lookup = RootFeature.ParseGetFeatureResponse(Frame("10 FF 00 0A 00 00 00"));
        Assert.Equal(0, lookup.FeatureIndex);
        Assert.False(lookup.IsSupported);
    }

    // ---- ChangeHost ----

    [Fact(DisplayName = "잡음의 잘못된 채널은 마우스 전환을 만들지 않는다")]
    public void NoiseIsIgnored()
    {
        Assert.Null(ChangeHostFeature.ParseHostChangeEvent(Frame("11 FF 09 00 00 FF 00 00 00 00 00 00 00 00 00 00 00 00 00 00"), 9));
        // 실측 잡음(MX MCHNCL, 2026-07-22 logcat): params=[4B, 08]
        Assert.Null(ChangeHostFeature.ParseHostChangeEvent(Frame("11 FF 09 00 4B 08 00 00 00 00 00 00 00 00 00 00 00 00 00 00"), 9));
    }

    [Fact(DisplayName = "params0이 0이 아니어도 목적지는 params1로 읽는다")]
    public void Params0IsNotUsed() =>
        // params[0]으로 거르던 안드로이드 0.2.17~0.2.19에서 MX KEYS S의 전환 감지가 0건이었다(2026-10-19).
        Assert.Equal(0, ChangeHostFeature.ParseHostChangeEvent(Frame("11 FF 0A 00 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00"), 0x0A));

    [Fact(DisplayName = "잘못된 현재 호스트 정보는 기본 채널 1로 오인하지 않는다")]
    public void InvalidHostInfoThrows() =>
        Assert.Throws<InvalidDataException>(() =>
            ChangeHostFeature.ParseGetHostInfo(Frame("11 FF 09 0A 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00")));

    [Fact(DisplayName = "setCurrentHost 채널1(host 0) 바이트가 골든벡터와 일치한다")]
    public void SetCurrentHostCh1() =>
        Assert.Equal(Hex.Parse("10 FF 05 1A 00 00 00"), ChangeHostFeature.BuildSetCurrentHost(featureIndex: 0x05, hostIndex: 0).ToBytes());

    [Fact(DisplayName = "setCurrentHost 채널3(host 2) 바이트")]
    public void SetCurrentHostCh3() =>
        Assert.Equal(Hex.Parse("10 FF 05 1A 02 00 00"), ChangeHostFeature.BuildSetCurrentHost(featureIndex: 0x05, hostIndex: 2).ToBytes());

    [Fact(DisplayName = "feature index가 다르면 세번째 바이트가 그에 맞게 바뀐다")]
    public void SetCurrentHostOtherIndex() =>
        Assert.Equal(Hex.Parse("10 FF 0C 1A 01 00 00"), ChangeHostFeature.BuildSetCurrentHost(featureIndex: 0x0C, hostIndex: 1).ToBytes());

    [Fact(DisplayName = "getHostInfo 바이트(fn 0)")]
    public void GetHostInfoBytes() =>
        Assert.Equal(Hex.Parse("10 FF 05 0A 00 00 00"), ChangeHostFeature.BuildGetHostInfo(featureIndex: 0x05).ToBytes());

    [Fact(DisplayName = "음수 hostIndex는 거부된다")]
    public void NegativeHostRejected() =>
        Assert.ThrowsAny<ArgumentException>(() => ChangeHostFeature.BuildSetCurrentHost(featureIndex: 0x05, hostIndex: -1));

    [Fact(DisplayName = "getHostInfo 응답에서 호스트 수와 현재 호스트를 파싱한다")]
    public void ParseHostInfo()
    {
        var info = ChangeHostFeature.ParseGetHostInfo(Frame("11 FF 09 0A 03 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00"));
        Assert.Equal(3, info.HostCount);
        Assert.Equal(1, info.CurrentHost);
    }

    [Fact(DisplayName = "전환 이벤트에서 목적지 host를 파싱한다")]
    public void ParseSwitchEvent()
    {
        // 실측: feat=0x09(0x1814 index), funcSw=0x00, params=[00, target]
        Assert.Equal(1, ChangeHostFeature.ParseHostChangeEvent(Frame("11 FF 09 00 00 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00"), 0x09));
        Assert.Equal(2, ChangeHostFeature.ParseHostChangeEvent(Frame("11 FF 09 00 00 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00"), 0x09));
    }

    [Fact(DisplayName = "다른 feature의 이벤트는 무시한다")]
    public void OtherFeatureIgnored() =>
        // 실측 잡음: 배터리(feat=0x07) 이벤트
        Assert.Null(ChangeHostFeature.ParseHostChangeEvent(Frame("11 FF 07 00 64 08 00 00 00 00 00 00 00 00 00 00 00 00 00 00"), 0x09));

    [Fact(DisplayName = "swId가 에코된 응답(이벤트 아님)은 무시한다")]
    public void EchoedResponseIgnored() =>
        Assert.Null(ChangeHostFeature.ParseHostChangeEvent(Frame("11 FF 09 0A 00 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00"), 0x09));

    // ---- DeviceName·수신기 (새로 추가) ----

    [Fact(DisplayName = "기기 이름 조각은 남은 길이만큼, 0에서 끊어 읽는다")]
    public void DeviceNameChunk()
    {
        var frame = Frame("11 02 03 1A 4D 58 20 4B 45 59 53 20 53 00 00 00 00 00 00 00"); // "MX KEYS S"
        Assert.Equal("MX KEYS S", DeviceNameFeature.ParseNameChunk(frame, remaining: 9));
        Assert.Equal("MX K", DeviceNameFeature.ParseNameChunk(frame, remaining: 4));
    }

    [Fact(DisplayName = "수신기 기기 연결 알림(0x41)을 읽는다")]
    public void DeviceConnectionNotice()
    {
        var notice = ReceiverMessages.ParseDeviceConnection(Hex.Parse("10 02 41 10 41 66 B3"))!;
        Assert.Equal(2, notice.DeviceIndex);
        Assert.Equal(0x10, notice.Protocol);
        Assert.Equal(0xB366, notice.WirelessProductId);
        Assert.False(notice.LinkEstablishedAssumed); // flags 0x41: bit 6 = 링크 없음(유니파잉 기준)
        Assert.Null(ReceiverMessages.ParseDeviceConnection(Hex.Parse("10 02 40 10 41 66 B3")));
    }

    [Fact(DisplayName = "짧은 레지스터 읽기 요청 바이트")]
    public void GetShortRegister() =>
        Assert.Equal(Hex.Parse("10 FF 81 00 00 00 00"), ReceiverMessages.BuildGetShortRegister(HidppConstants.RegisterNotifications));
}
