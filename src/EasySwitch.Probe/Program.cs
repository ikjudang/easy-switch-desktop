using EasySwitch.Probe;

// 사용: EasySwitch.Probe [--minutes N] [--log-dir 폴더] [--swid 0A] [--timeout-ms 1500] [--error-probe false]
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
