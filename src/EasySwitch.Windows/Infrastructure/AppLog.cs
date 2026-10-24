using System.Text;

namespace EasySwitch.Windows.Infrastructure;

public sealed record LogEntry(DateTimeOffset At, string Message)
{
    public string Time => At.ToString("HH:mm:ss.fff");
    public string Line => $"{At:yyyy-MM-dd HH:mm:ss.fff zzz}  {Message}";
    public bool IsProtocolTrace => Message.StartsWith('[') && (Message.Contains("] TX ",StringComparison.Ordinal) || Message.Contains("] RX ",StringComparison.Ordinal) || Message.Contains("] RX 응답 ",StringComparison.Ordinal) || Message.Contains("] 수신기 미확인 슬롯 조회:",StringComparison.Ordinal) || Message.Contains("] 조회 시간 초과:",StringComparison.Ordinal));
}
public sealed record CodeLogEntry(DateTimeOffset At,string Code)
{
    public string Line=>At.ToString("yyyyMMdd'T'HHmmss.fffzzz",System.Globalization.CultureInfo.InvariantCulture)+"|"+Code;
}

public sealed class AppLog : IDisposable
{
    private sealed record Delivery(LogEntry? Log=null,CodeLogEntry? Code=null,Action? Action=null,TaskCompletionSource? Completion=null);
    private readonly System.Threading.Channels.Channel<Delivery> _delivery=System.Threading.Channels.Channel.CreateUnbounded<Delivery>(new(){SingleReader=true});
    private readonly Task _writer;
    private volatile bool _defer;
    private int _disposed;
    private Exception? _deliveryError;
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _recent = new();
    private readonly StreamWriter _local;
    private readonly StreamWriter _codes;
    private readonly Queue<CodeLogEntry> _recentCodes=[];
    private StreamWriter? _export;
    public string FilePath { get; }
    public string CodeFilePath { get; }
    public event Action<LogEntry>? Added;
    public event Action<CodeLogEntry>? CodeAdded;

    public AppLog(string? directory = null)
    {
        directory ??= Path.Combine(SettingsStore.DataDirectory, "logs");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"easyswitch-pc-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.txt");
        _local = Open(FilePath);
        CodeFilePath=Path.ChangeExtension(FilePath,"codes.txt");
        _codes=Open(CodeFilePath);
        _writer=Task.Run(DeliverAsync);
    }
    public void EnableDeferredWrites()=>_defer=true;
    public void Post(string message)=>_delivery.Writer.TryWrite(new(Log:new(DateTimeOffset.Now,message)));
    public void PostCode(string code)
    {
        code=NormalizeCode(code);_delivery.Writer.TryWrite(new(Code:new(DateTimeOffset.Now,code)));
    }
    public Task FlushAsync()=>QueueWork(()=>{if(Volatile.Read(ref _deliveryError) is {} error)throw new IOException("로그 저장 작업이 실패했어.",error);});
    public Task QueueWork(Action action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!_delivery.Writer.TryWrite(new(Action:action,Completion:completion)))completion.SetException(new ObjectDisposedException(nameof(AppLog)));
        return completion.Task;
    }
    private async Task DeliverAsync()
    {
        while(await _delivery.Reader.WaitToReadAsync())
        {
            var batch=new List<Delivery>();
            while(batch.Count<256 && _delivery.Reader.TryRead(out var item))batch.Add(item);
            foreach(var item in batch)
            {
                try
                {
                    if(item.Log is {} log)ImportLog(log);
                    if(item.Code is {} code)ImportCode(code);
                    item.Action?.Invoke();item.Completion?.TrySetResult();
                }
                catch(Exception error)
                {
                    item.Completion?.TrySetException(error);
                    if(item.Log is not null || item.Code is not null){Volatile.Write(ref _deliveryError,error);try{DeliveryFailed?.Invoke(error);}catch(Exception){}}
                }
            }
        }
    }
    public event Action<Exception>? DeliveryFailed;
    private static string NormalizeCode(string code)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(code,@"\A[A-Z0-9 |.:+/_-]+\z"))throw new ArgumentException("Status codes must contain only fixed ASCII fields",nameof(code));
        return string.Join('|',code.Split([' ','|'],StringSplitOptions.RemoveEmptyEntries));
    }

    private static StreamWriter Open(string path) => new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true)) { AutoFlush = true };

    public IReadOnlyList<LogEntry> Recent()
    {
        lock (_gate) return _recent.Reverse().ToArray();
    }
    public IReadOnlyList<CodeLogEntry> RecentCodes() { lock(_gate)return _recentCodes.Reverse().ToArray(); }
    public void ExportCodes(string destination)
    {
        var target=Path.GetFullPath(destination);
        lock(_gate)
        {
            var activePaths=new[]{CodeFilePath,FilePath,(_export?.BaseStream as FileStream)?.Name};
            if(activePaths.Any(path=>path is not null && string.Equals(Path.GetFullPath(path),target,StringComparison.OrdinalIgnoreCase)))
                throw new IOException("현재 기록 중인 로그 파일에는 덮어쓸 수 없어.");
            _codes.Flush();
            File.Copy(CodeFilePath,target,true);
        }
    }
    public void WriteCode(string code)
    {
        if(_defer){PostCode(code);return;}
        code=NormalizeCode(code);
        var entry=new CodeLogEntry(DateTimeOffset.Now,code);
        lock(_gate)
        {
            _codes.WriteLine(entry.Line);_recentCodes.Enqueue(entry);
            while(_recentCodes.Count>600)_recentCodes.Dequeue();
        }
        CodeAdded?.Invoke(entry);
    }
    public void Import(CodeLogEntry entry)
    {if(_defer){_delivery.Writer.TryWrite(new(Code:entry));return;}ImportCode(entry);}
    private void ImportCode(CodeLogEntry entry)
    {
        lock(_gate){_codes.WriteLine(entry.Line);_recentCodes.Enqueue(entry);while(_recentCodes.Count>600)_recentCodes.Dequeue();}
        CodeAdded?.Invoke(entry);
    }
    public void Import(LogEntry entry)
    {if(_defer){_delivery.Writer.TryWrite(new(Log:entry));return;}ImportLog(entry);}
    private void ImportLog(LogEntry entry)
    {
        Exception? failure=null;
        lock(_gate)
        {
            _local.WriteLine(entry.Line);_recent.Enqueue(entry);while(_recent.Count>600)_recent.Dequeue();
            try{_export?.WriteLine(entry.Line);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){try{_export?.Dispose();}catch(IOException){} _export=null;failure=error;}
        }
        Added?.Invoke(entry);
        if(failure is not null)Write("외부 폴더 기록 중단: "+failure.Message+". 서비스·로컬 로그는 계속 보존함");
    }

    public void SetExportDirectory(string? directory)
    {
        // Open the replacement first, so an invalid folder preserves the current export.
        StreamWriter? replacement = null;
        var needsHistory = false;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Path.GetFileName(FilePath));
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
            {
                needsHistory = !File.Exists(path) || new FileInfo(path).Length <= 3;
                replacement = Open(path);
            }
        }
        lock (_gate)
        {
            _export?.Dispose();
            _export = replacement;
            if (_export is not null && needsHistory)
                foreach (var entry in _recent) _export.WriteLine(entry.Line);
        }
    }

    public void Write(string message)
    {
        if(_defer){Post(message);return;}
        var entry = new LogEntry(DateTimeOffset.Now, message);
        LogEntry? exportFailure = null;
        lock (_gate)
        {
            _local.WriteLine(entry.Line);
            _recent.Enqueue(entry);
            while (_recent.Count > 600) _recent.Dequeue();
            try { _export?.WriteLine(entry.Line); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                try { _export?.Dispose(); } catch (IOException) { }
                _export = null;
                exportFailure = new LogEntry(DateTimeOffset.Now, $"외부 폴더 기록 중단: {error.Message}. 로컬 파일 기록은 계속함");
                _local.WriteLine(exportFailure.Line);
                _recent.Enqueue(exportFailure);
            }
        }
        Added?.Invoke(entry);
        if (exportFailure is not null) Added?.Invoke(exportFailure);
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _delivery.Writer.TryComplete();_writer.GetAwaiter().GetResult();
        lock (_gate)
        {
            _export?.Dispose();
            _local.Dispose();
            _codes.Dispose();
        }
    }
}
