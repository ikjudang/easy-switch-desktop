using EasySwitch.Probe;

// 사용: EasySwitch.Probe [--minutes N] [--log-dir 폴더] [--swid 0A] [--timeout-ms 1500] [--error-probe false] [--trace-frames]
// --swid 기본값 0A는 Windows 앱(09)과 일부러 다르게 둔다. 서비스가 함께 돌면 같은 값끼리 서로의 응답을 가로챌 수 있고,
// 다르게 두면 측정 도구가 앱의 응답을 "다른 앱 software id 09"로 기록한다.
// 멈출 때는 Ctrl+C. 로그는 기본으로 지금 폴더의 logs\probe-날짜-시각.txt에 남는다.
Console.OutputEncoding = System.Text.Encoding.UTF8;
var options = ProbeOptions.Parse(args);
using var log = new ProbeLog(options.LogDirectory);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
if (options.Minutes > 0) cancel.CancelAfter(TimeSpan.FromMinutes(options.Minutes));
await new Probe(log, options).RunAsync(cancel.Token);
