using EasySwitch.Hidpp;

namespace EasySwitch.Hidpp.Tests;

public class HidppMatchTests
{
    private static readonly byte[] GetFeatureOnSlot2 = RootFeature.BuildGetFeature(HidppConstants.FeatureChangeHost, deviceIndex: 2).ToBytes(); // 10 02 00 0A 18 14 00

    [Fact(DisplayName = "같은 기기·feature·func|sw를 에코하면 응답이다(short 요청에 long 응답도)")]
    public void EchoIsResponse() =>
        Assert.Equal(ResponseKind.Response,
            HidppMatch.Classify(GetFeatureOnSlot2, Hex.Parse("11 02 00 0A 09 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00")));

    [Fact(DisplayName = "다른 기기 번호의 응답은 짝이 아니다")]
    public void OtherDeviceIsNotMine() =>
        Assert.Null(HidppMatch.Classify(GetFeatureOnSlot2, Hex.Parse("11 03 00 0A 09 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00")));

    [Fact(DisplayName = "다른 앱(software id 다름)이 같은 걸 물은 응답은 짝이 아니다")]
    public void OtherSoftwareIdIsNotMine() =>
        Assert.Null(HidppMatch.Classify(GetFeatureOnSlot2, Hex.Parse("11 02 00 01 09 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00")));

    [Fact(DisplayName = "기기 이벤트(software id 0)는 짝이 아니다")]
    public void EventIsNotMine() =>
        Assert.Null(HidppMatch.Classify(GetFeatureOnSlot2, Hex.Parse("11 02 00 00 09 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00")));

    [Fact(DisplayName = "에러는 두 배치를 모두 짝짓되 어느 배치였는지 가른다")]
    public void ErrorLayouts()
    {
        var request = HidppFrame.Build(featureIndex: 0x7F, functionId: 0, deviceIndex: 2).ToBytes(); // 10 02 7F 0A 00 00 00
        Assert.Equal(ResponseKind.ErrorSolaarLayout, HidppMatch.Classify(request, Hex.Parse("10 02 FF 7F 0A 06 00")));
        Assert.Equal(ResponseKind.ErrorSwappedLayout, HidppMatch.Classify(request, Hex.Parse("10 02 FF 0A 7F 06 00")));
        Assert.Equal(ResponseKind.ErrorSolaarLayout, HidppMatch.Classify(request, Hex.Parse("10 02 8F 7F 0A 08 00"))); // 수신기 1.0 에러
    }

    [Fact(DisplayName = "수신기 레지스터 읽기 응답도 sub_id·주소 에코로 짝짓는다")]
    public void RegisterReadResponse() =>
        Assert.Equal(ResponseKind.Response,
            HidppMatch.Classify(ReceiverMessages.BuildGetShortRegister(0x00), Hex.Parse("10 FF 81 00 00 09 00")));
}
