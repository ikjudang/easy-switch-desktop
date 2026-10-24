namespace EasySwitch.Hidpp;

/// <summary>short(0x10, 7B) / long(0x11, 20B) 리포트 종류.</summary>
public enum ReportType
{
    Short,
    Long,
}

public static class ReportTypes
{
    public static int ReportId(this ReportType type) =>
        type == ReportType.Short ? HidppConstants.ReportIdShort : HidppConstants.ReportIdLong;

    public static int TotalLength(this ReportType type) =>
        type == ReportType.Short ? HidppConstants.ShortLength : HidppConstants.LongLength;

    /// <summary>헤더 4바이트를 제외한 파라미터 영역 길이.</summary>
    public static int ParamLength(this ReportType type) => type.TotalLength() - HidppConstants.HeaderLength;

    public static ReportType? FromReportId(int reportId) => reportId switch
    {
        HidppConstants.ReportIdShort => ReportType.Short,
        HidppConstants.ReportIdLong => ReportType.Long,
        _ => null,
    };
}

/// <summary>
/// HID++ 2.0 프레임의 구조적 표현.
///
/// 빌드는 <see cref="Build"/>로 하고, 파싱 결과는 <see cref="HidppFrameParser.Parse"/>가 이 타입으로 돌려준다.
/// <see cref="Params"/>는 항상 <see cref="ReportTypes.ParamLength"/>와 정확히 같은 길이다(부족분은 0 패딩).
/// </summary>
public sealed class HidppFrame : IEquatable<HidppFrame>
{
    private readonly byte[] _params;

    public HidppFrame(ReportType reportType, int deviceIndex, int featureIndex, int funcSwByte, ReadOnlySpan<byte> parameters)
    {
        if (parameters.Length != reportType.ParamLength())
            throw new ArgumentException($"params 길이는 {reportType.ParamLength()}여야 함(현재 {parameters.Length})", nameof(parameters));
        if (deviceIndex is < 0 or > 0xFF) throw new ArgumentOutOfRangeException(nameof(deviceIndex), $"deviceIndex 범위 밖: {deviceIndex}");
        if (featureIndex is < 0 or > 0xFF) throw new ArgumentOutOfRangeException(nameof(featureIndex), $"featureIndex 범위 밖: {featureIndex}");
        if (funcSwByte is < 0 or > 0xFF) throw new ArgumentOutOfRangeException(nameof(funcSwByte), $"funcSwByte 범위 밖: {funcSwByte}");
        ReportType = reportType;
        DeviceIndex = deviceIndex;
        FeatureIndex = featureIndex;
        FuncSwByte = funcSwByte;
        _params = parameters.ToArray();
    }

    public ReportType ReportType { get; }
    public int DeviceIndex { get; }
    public int FeatureIndex { get; }

    /// <summary>function_id&lt;&lt;4 | software_id</summary>
    public int FuncSwByte { get; }

    public ReadOnlySpan<byte> Params => _params;

    public int FunctionId => (FuncSwByte >> 4) & 0x0F;
    public int SoftwareId => FuncSwByte & 0x0F;

    /// <summary>params[i]를 0..255로. 범위 밖이면 0(Kotlin의 getOrElse와 같다).</summary>
    public int Param(int i) => i >= 0 && i < _params.Length ? _params[i] : 0;

    /// <summary>전송용 바이트 배열. 길이는 <see cref="ReportTypes.TotalLength"/>.</summary>
    public byte[] ToBytes()
    {
        var output = new byte[ReportType.TotalLength()];
        output[0] = (byte)ReportType.ReportId();
        output[1] = (byte)DeviceIndex;
        output[2] = (byte)FeatureIndex;
        output[3] = (byte)FuncSwByte;
        _params.CopyTo(output, HidppConstants.HeaderLength);
        return output;
    }

    /// <summary>
    /// 같은 요청을 long report로. 블루투스 직결 기기의 HID++ 통로는 long(20B)만 받는다(윈도우 실측 input·output 20B) —
    /// short로 만든 요청을 0으로 채워 보낸다. HID++는 어떤 함수든 long 요청을 받는다.
    /// </summary>
    public HidppFrame ToLong()
    {
        if (ReportType == ReportType.Long) return this;
        var padded = new byte[ReportType.Long.ParamLength()];
        _params.CopyTo(padded, 0);
        return new HidppFrame(ReportType.Long, DeviceIndex, FeatureIndex, FuncSwByte, padded);
    }

    /// <summary>응답이 이 요청을 에코하는지(device_index / feature_index / func|sw 일치).</summary>
    public bool Echoes(int deviceIndex, int featureIndex, int funcSwByte) =>
        DeviceIndex == deviceIndex && FeatureIndex == featureIndex && FuncSwByte == funcSwByte;

    /// <summary>
    /// 검증과 0 패딩을 거쳐 프레임을 만든다.
    /// </summary>
    /// <param name="functionId">0..15 (상위 nibble)</param>
    /// <param name="softwareId">1..15, 0 제외 (하위 nibble)</param>
    /// <param name="parameters">paramLength 이하. 부족분은 0으로 채워진다.</param>
    public static HidppFrame Build(
        int featureIndex,
        int functionId,
        ReportType reportType = ReportType.Short,
        int deviceIndex = HidppConstants.DeviceIndexDirect,
        int softwareId = HidppConstants.DefaultSoftwareId,
        ReadOnlySpan<byte> parameters = default)
    {
        if (functionId is < 0x0 or > 0xF) throw new ArgumentOutOfRangeException(nameof(functionId), $"functionId는 nibble(0..15): {functionId}");
        if (softwareId is < 0x1 or > 0xF) throw new ArgumentOutOfRangeException(nameof(softwareId), $"softwareId는 1..15(0 제외): {softwareId}");
        var paramLength = reportType.ParamLength();
        if (parameters.Length > paramLength)
            throw new ArgumentException($"params가 {reportType}에 비해 김: {parameters.Length} > {paramLength}", nameof(parameters));
        var padded = new byte[paramLength];
        parameters.CopyTo(padded);
        return new HidppFrame(reportType, deviceIndex, featureIndex, (functionId << 4) | softwareId, padded);
    }

    public bool Equals(HidppFrame? other) =>
        other is not null &&
        ReportType == other.ReportType &&
        DeviceIndex == other.DeviceIndex &&
        FeatureIndex == other.FeatureIndex &&
        FuncSwByte == other.FuncSwByte &&
        _params.AsSpan().SequenceEqual(other._params);

    public override bool Equals(object? obj) => Equals(obj as HidppFrame);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ReportType);
        hash.Add(DeviceIndex);
        hash.Add(FeatureIndex);
        hash.Add(FuncSwByte);
        hash.AddBytes(_params);
        return hash.ToHashCode();
    }

    public override string ToString() => Hex.Format(ToBytes());
}

/// <summary>"10 FF 00 0A" 형태의 헥스 문자열 변환. 로그와 테스트가 같이 쓴다.</summary>
public static class Hex
{
    public static string Format(ReadOnlySpan<byte> bytes) => string.Join(' ', bytes.ToArray().Select(b => b.ToString("X2")));

    public static byte[] Parse(string spaced) =>
        spaced.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => Convert.ToByte(token, 16))
            .ToArray();
}
