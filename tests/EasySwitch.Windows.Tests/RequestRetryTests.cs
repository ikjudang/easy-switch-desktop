using EasySwitch.Hidpp;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

public sealed class RequestRetryTests
{
    [Theory]
    [InlineData("10 02 FF 09 09 08 00")]
    [InlineData("10 02 8F 09 09 07 00")]
    public async Task BusyReplyRetriesAndKeepsTheSuccessfulReply(string busy)
    {
        var replies=new Queue<byte[]?>([Hex.Parse(busy),Hex.Parse(busy),Hex.Parse("11 02 09 09 03 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00")]);
        var errors=0;
        var result=await HidppRequestRetry.ReadAsync(()=>Task.FromResult(replies.Dequeue()),(_,_)=>errors++,CancellationToken.None,TimeSpan.Zero);
        Assert.Equal(2,errors);Assert.Empty(replies);Assert.False(HidppFrameParser.IsError(result!));
    }
    [Fact]
    public async Task ResourceErrorOnUnconfirmedSlotIsNotRetriedAsBusy()
    {
        var raw=Hex.Parse("10 03 8F 00 09 09 00");var count=0;
        var result=await HidppRequestRetry.ReadAsync(()=>{count++;return Task.FromResult<byte[]?>(raw);},(_,_)=>{},CancellationToken.None,TimeSpan.Zero);
        Assert.Same(raw,result);Assert.Equal(1,count);
    }
    [Fact]
    public async Task PersistentBusyIsRetryableIOExceptionRatherThanFalseDisconnection()
    {
        var count=0;
        await Assert.ThrowsAsync<HidppBusyException>(()=>HidppRequestRetry.ReadAsync(()=>{count++;return Task.FromResult<byte[]?>(Hex.Parse("10 02 FF 09 09 08 00"));},(_,_)=>{},CancellationToken.None,TimeSpan.Zero));
        Assert.Equal(3,count);
    }
    [Fact]
    public async Task UserCancellationStopsBusyRetries()
    {
        using var cancel=new CancellationTokenSource();var count=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>HidppRequestRetry.ReadAsync(()=>{count++;return Task.FromResult<byte[]?>(Hex.Parse("10 02 FF 09 09 08 00"));},(_,_)=>cancel.Cancel(),cancel.Token));
        Assert.Equal(1,count);
    }
    [Theory]
    [InlineData("[Bolt] RX 응답 10 03 8F 00 09 09 00",true)]
    [InlineData("[Bolt] TX 10 03 00 09 18 14 00",true)]
    [InlineData("[Bolt #4] 조회 시간 초과: feature 0x00, 함수 0x0. 응답을 받지 못함",false)]
    [InlineData("[Mouse] HID++ 오류: 10 02 FF 09 09 08 00",false)]
    [InlineData("[Mouse] 전환 실패: IOException",false)]
    public void DefaultActivityViewKeepsFailuresAndFullTraceRemainsClassifiable(string message,bool trace)
        =>Assert.Equal(trace,new LogEntry(DateTimeOffset.Now,message).IsProtocolTrace);
}
