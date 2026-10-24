using EasySwitch.Hidpp;

namespace EasySwitch.Hidpp.Tests;

/// <summary>안드로이드 <c>HidppFrameTest.kt</c>에서 옮겼다. 같은 입력에 같은 결과가 나와야 한다.</summary>
public class HidppFrameTests
{
    [Fact(DisplayName = "SHORT 프레임은 7바이트, 헤더+파라미터 배치가 정확하다")]
    public void ShortFrameLayout()
    {
        var frame = HidppFrame.Build(featureIndex: 0x05, functionId: 0x01, reportType: ReportType.Short,
            deviceIndex: 0xFF, softwareId: 0x0A, parameters: [0x02]);
        // [0x10][0xFF][0x05][func1|sw0A=0x1A][0x02][0x00][0x00]
        Assert.Equal(Hex.Parse("10 FF 05 1A 02 00 00"), frame.ToBytes());
        Assert.Equal(7, frame.ToBytes().Length);
    }

    [Fact(DisplayName = "LONG 프레임은 20바이트로 0 패딩된다")]
    public void LongFramePadding()
    {
        var bytes = HidppFrame.Build(featureIndex: 0x00, functionId: 0x00, reportType: ReportType.Long,
            softwareId: 0x0A, parameters: [0x18, 0x14]).ToBytes();
        Assert.Equal(20, bytes.Length);
        Assert.Equal(0x11, bytes[0]);
        Assert.Equal(Hex.Parse("18 14"), bytes[4..6]);
    }

    [Fact(DisplayName = "func_sw 바이트에서 functionId 상위, softwareId 하위 nibble이 분리된다")]
    public void FuncSwNibbles()
    {
        var frame = HidppFrame.Build(featureIndex: 0x05, functionId: 0x03, softwareId: 0x0A);
        Assert.Equal(0x3A, frame.FuncSwByte);
        Assert.Equal(0x03, frame.FunctionId);
        Assert.Equal(0x0A, frame.SoftwareId);
    }

    [Fact(DisplayName = "toBytes-parse 왕복이 동일하다")]
    public void RoundTrip()
    {
        var original = HidppFrame.Build(featureIndex: 0x07, functionId: 0x01, softwareId: 0x0A, parameters: [0x01]);
        Assert.Equal(original, HidppFrameParser.Parse(original.ToBytes()));
    }

    [Fact(DisplayName = "echoes는 device_feature_funcSw가 모두 일치할 때만 참")]
    public void Echoes()
    {
        var response = HidppFrame.Build(featureIndex: 0x00, functionId: 0x00, softwareId: 0x0A);
        Assert.True(response.Echoes(deviceIndex: 0xFF, featureIndex: 0x00, funcSwByte: 0x0A));
        Assert.False(response.Echoes(deviceIndex: 0xFF, featureIndex: 0x00, funcSwByte: 0x0B));
        Assert.False(response.Echoes(deviceIndex: 0x00, featureIndex: 0x00, funcSwByte: 0x0A));
    }

    [Fact(DisplayName = "softwareId 0은 거부된다")]
    public void RejectsSoftwareIdZero() =>
        Assert.ThrowsAny<ArgumentException>(() => HidppFrame.Build(featureIndex: 0x05, functionId: 0x01, softwareId: 0x00));

    [Fact(DisplayName = "functionId nibble 초과는 거부된다")]
    public void RejectsFunctionOverflow() =>
        Assert.ThrowsAny<ArgumentException>(() => HidppFrame.Build(featureIndex: 0x05, functionId: 0x10, softwareId: 0x0A));

    [Fact(DisplayName = "params가 SHORT 용량을 초과하면 거부된다")]
    public void RejectsShortOverflow() =>
        Assert.ThrowsAny<ArgumentException>(() => HidppFrame.Build(featureIndex: 0x05, functionId: 0x01,
            reportType: ReportType.Short, softwareId: 0x0A, parameters: new byte[4])); // SHORT paramLength는 3

    [Fact(DisplayName = "short 요청을 long으로 바꾸면 헤더는 그대로고 0으로 채워진다(블루투스 직결 통로용)")]
    public void ToLongKeepsHeader()
    {
        var request = RootFeature.BuildGetFeature(HidppConstants.FeatureChangeHost);
        var bytes = request.ToLong().ToBytes();
        Assert.Equal(Hex.Parse("11 FF 00 0A 18 14 00 00 00 00 00 00 00 00 00 00 00 00 00 00"), bytes);
    }
}
