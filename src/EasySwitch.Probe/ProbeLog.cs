using System.Text;

namespace EasySwitch.Probe;

/// <summary>
/// 측정 로그. 줄마다 밀리초 시각을 붙여 파일(UTF-8)과 콘솔에 같이 쓴다. 키보드가 붙은 뒤 신호를 받기까지의 빈틈은
/// 1초 안쪽에서 갈리므로 초 단위로는 판정할 수 없다. 파일은 줄마다 비워 내 프로그램이 갑자기 끝나도 남는다.
/// </summary>
internal sealed class ProbeLog : IDisposable
{
    private readonly StreamWriter _file;
    private readonly object _gate = new();

    public ProbeLog(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        _file = new StreamWriter(FilePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
    }

    public string FilePath { get; }

    public void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        lock (_gate)
        {
            _file.WriteLine(line);
            Console.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate) _file.Dispose();
    }
}
