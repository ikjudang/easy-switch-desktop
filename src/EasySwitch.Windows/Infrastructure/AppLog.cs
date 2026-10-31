using System.Text;

namespace EasySwitch.Windows.Infrastructure;

public sealed record LogEntry(DateTimeOffset At, string Message)
{
    public string Time => At.ToString("HH:mm:ss.fff");
    public string Line => $"{At:yyyy-MM-dd HH:mm:ss.fff zzz}  {Message}";
    public bool IsProtocolTrace => Message.StartsWith('[') && (Message.Contains("] TX ",StringComparison.Ordinal) || Message.Contains("] RX ",StringComparison.Ordinal) || Message.Contains("] RX 응답 ",StringComparison.Ordinal));
}
public sealed record CodeLogEntry(DateTimeOffset At,string Code)
{
    public string Line=>At.ToString("yyyyMMdd'T'HHmmss.fffzzz",System.Globalization.CultureInfo.InvariantCulture)+"|"+Code;
}

public sealed class AppLog : IDisposable
{
    // A long-running service keeps one session; parts bound each file and retention bounds the folder.
    internal const long DefaultPartBytes=16L*1024*1024;
    internal const long DefaultRetainedBytes=128L*1024*1024;
    internal static readonly TimeSpan DefaultRetainedAge=TimeSpan.FromDays(30);
    internal const string FilePrefix="easyswitch-pc-";
    private sealed class Sink(StreamWriter writer,string path,int part)
    {
        public StreamWriter Writer{get;}=writer;public string Path{get;}=path;public int Part{get;}=part;
    }
    private sealed record Delivery(LogEntry? Log=null,CodeLogEntry? Code=null,Action? Action=null,TaskCompletionSource? Completion=null,bool History=false);
    private readonly System.Threading.Channels.Channel<Delivery> _delivery=System.Threading.Channels.Channel.CreateUnbounded<Delivery>(new(){SingleReader=true});
    private readonly Task _writer;
    private volatile bool _defer;
    private int _disposed;
    private Exception? _deliveryError;
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _recent = new();
    private readonly string _directory,_session;
    private readonly long _partBytes,_retainedBytes;private readonly TimeSpan _retainedAge;
    private Sink _local;
    private Sink _codes;
    private readonly Queue<CodeLogEntry> _recentCodes=[];
    private StreamWriter? _export;
    private string? _exportDirectory;
    private int _rolling;private long _nextRollAttempt;
    private long _localWriteFailures;private long _failedWrites;
    private readonly List<LogEntry> _notices=[];
    private Task _maintenance;
    public string FilePath=>Volatile.Read(ref _local).Path;
    public string CodeFilePath=>Volatile.Read(ref _codes).Path;
    /// <summary>Local file writes that failed (disk full, removed folder). Callers never see these as exceptions.</summary>
    public long LocalWriteFailures=>Interlocked.Read(ref _localWriteFailures);
    internal Task Maintenance=>Volatile.Read(ref _maintenance);
    public event Action<LogEntry>? Added;
    public event Action<CodeLogEntry>? CodeAdded;

    public AppLog(string? directory = null):this(directory,DefaultPartBytes,DefaultRetainedBytes,DefaultRetainedAge){}
    internal AppLog(string? directory,long partBytes,long retainedBytes,TimeSpan retainedAge)
    {
        directory ??= Path.Combine(SettingsStore.DataDirectory, "logs");
        Directory.CreateDirectory(directory);
        _directory=directory;_partBytes=partBytes;_retainedBytes=retainedBytes;_retainedAge=retainedAge;
        _session=$"{FilePrefix}{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        _local=OpenSink(false,1);
        _codes=OpenSink(true,1);
        _writer=Task.Run(DeliverAsync);
        // Retention runs off the caller's thread so startup and switching never wait for folder scans.
        _maintenance=Task.Run(()=>Retain("시작"));
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
                    if(item.Log is {} log){if(item.History)RememberLog(log);else ImportLog(log);}
                    if(item.Code is {} code){if(item.History)RememberCode(code);else ImportCode(code);}
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

    private static StreamWriter Open(string path,bool preamble=true) => new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(preamble)) { AutoFlush = true };
    private string PartPath(bool codes,int part)=>Path.Combine(_directory,_session+(part==1?"":$"-p{part}")+(codes?".codes.txt":".txt"));
    // Later parts omit the byte-order mark so a session export can concatenate them into one UTF-8 file.
    private Sink OpenSink(bool codes,int part){var path=PartPath(codes,part);return new(Open(path,part==1),path,part);}

    public IReadOnlyList<LogEntry> Recent()
    {
        lock (_gate) return _recent.Reverse().ToArray();
    }
    public IReadOnlyList<CodeLogEntry> RecentCodes() { lock(_gate)return _recentCodes.Reverse().ToArray(); }
    public void ExportCodes(string destination)
    {
        var target=Path.GetFullPath(destination);
        // Folder scans and copying stay outside the write lock: switch paths keep logging during an export.
        var parts=SessionParts(true);
        string? exportPath;lock(_gate)exportPath=(_export?.BaseStream as FileStream)?.Name;
        var activePaths=parts.Concat(SessionParts(false)).Append(exportPath);
        if(activePaths.Any(path=>path is not null && string.Equals(Path.GetFullPath(path),target,StringComparison.OrdinalIgnoreCase)))
            throw new IOException("현재 기록 중인 로그 파일에는 덮어쓸 수 없어.");
        using var output=new FileStream(target,FileMode.Create,FileAccess.Write,FileShare.None);
        foreach(var part in parts)
        {
            using var input=new FileStream(part,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            input.CopyTo(output);
        }
    }
    /// <summary>Reads this session's log as one stream: earlier parts first, then the part being written.</summary>
    internal (byte[] Data,long Length,string Epoch) ReadSession(bool codes,long offset,int maximum)
    {
        if(offset<0)throw new InvalidDataException("잘못된 로그 위치");
        var parts=SessionPartsWithNumbers(codes);
        if(parts.Count==0)throw new IOException("현재 로그 파일을 찾지 못했어.");
        var lengths=parts.Select(p=>new FileInfo(p.Path).Length).ToArray();
        var length=lengths.Sum();
        if(offset>length)throw new InvalidDataException("로그 크기를 벗어난 위치");
        var data=new byte[(int)Math.Min(maximum,length-offset)];
        var copied=0;var start=0L;
        for(var i=0;i<parts.Count && copied<data.Length;i++)
        {
            var end=start+lengths[i];
            if(offset+copied<end)
            {
                using var input=new FileStream(parts[i].Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                input.Position=offset+copied-start;
                var count=(int)Math.Min(data.Length-copied,end-(offset+copied));
                input.ReadExactly(data,copied,count);copied+=count;
            }
            start=end;
        }
        // The first part number changes when retention removes an old part; a reader must restart then.
        return(data,length,parts[0].Part.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private string[] SessionParts(bool codes)=>SessionPartsWithNumbers(codes).Select(p=>p.Path).ToArray();
    private List<(int Part,string Path)> SessionPartsWithNumbers(bool codes)
    {
        var parts=new List<(int Part,string Path)>();
        foreach(var path in Directory.EnumerateFiles(_directory,_session+"*.txt"))
        {
            var name=Path.GetFileName(path);var isCode=name.EndsWith(".codes.txt",StringComparison.OrdinalIgnoreCase);
            if(isCode!=codes)continue;
            var middle=name[_session.Length..^(isCode?".codes.txt".Length:".txt".Length)];
            if(middle.Length==0)parts.Add((1,path));
            else if(middle.StartsWith("-p",StringComparison.Ordinal) && int.TryParse(middle[2..],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var part) && part>1)parts.Add((part,path));
        }
        parts.Sort((a,b)=>a.Part.CompareTo(b.Part));
        return parts;
    }
    public void WriteCode(string code)
    {
        if(_defer){PostCode(code);return;}
        code=NormalizeCode(code);
        var entry=new CodeLogEntry(DateTimeOffset.Now,code);
        LogEntry[] notices;
        lock(_gate)
        {
            Append(true,entry.Line);_recentCodes.Enqueue(entry);
            while(_recentCodes.Count>600)_recentCodes.Dequeue();
            notices=TakeNotices();
        }
        CodeAdded?.Invoke(entry);
        RaiseNotices(notices);
    }
    public void Import(CodeLogEntry entry)
    {if(_defer){_delivery.Writer.TryWrite(new(Code:entry));return;}ImportCode(entry);}
    /// <summary>Shows an entry that an earlier process already wrote to its file, without writing it again.</summary>
    public void ImportHistory(CodeLogEntry entry)
    {if(_defer){_delivery.Writer.TryWrite(new(Code:entry,History:true));return;}RememberCode(entry);}
    private void ImportCode(CodeLogEntry entry)
    {
        LogEntry[] notices;
        lock(_gate){Append(true,entry.Line);_recentCodes.Enqueue(entry);while(_recentCodes.Count>600)_recentCodes.Dequeue();notices=TakeNotices();}
        CodeAdded?.Invoke(entry);
        RaiseNotices(notices);
    }
    private void RememberCode(CodeLogEntry entry)
    {
        lock(_gate){_recentCodes.Enqueue(entry);while(_recentCodes.Count>600)_recentCodes.Dequeue();}
        CodeAdded?.Invoke(entry);
    }
    public void Import(LogEntry entry)
    {if(_defer){_delivery.Writer.TryWrite(new(Log:entry));return;}ImportLog(entry);}
    /// <summary>Shows an entry that an earlier process already wrote to its file, without writing it again.</summary>
    public void ImportHistory(LogEntry entry)
    {if(_defer){_delivery.Writer.TryWrite(new(Log:entry,History:true));return;}RememberLog(entry);}
    private void ImportLog(LogEntry entry)
    {
        Exception? failure=null;LogEntry[] notices;
        lock(_gate)
        {
            Append(false,entry.Line);_recent.Enqueue(entry);while(_recent.Count>600)_recent.Dequeue();
            try{_export?.WriteLine(entry.Line);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){try{_export?.Dispose();}catch(IOException){} _export=null;_exportDirectory=null;failure=error;}
            notices=TakeNotices();
        }
        Added?.Invoke(entry);
        RaiseNotices(notices);
        if(failure is not null)Write("외부 폴더 기록 중단: "+failure.Message+". 서비스·로컬 로그는 계속 보존함");
    }
    private void RememberLog(LogEntry entry)
    {
        lock(_gate){_recent.Enqueue(entry);while(_recent.Count>600)_recent.Dequeue();}
        Added?.Invoke(entry);
    }

    public void SetExportDirectory(string? directory)
    {
        // Open the replacement first, so an invalid folder preserves the current export.
        StreamWriter? replacement = null;
        string? replacementDirectory=null;
        var needsHistory = false;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Path.GetFileName(FilePath));
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
            {
                needsHistory = !File.Exists(path) || new FileInfo(path).Length <= 3;
                replacement = Open(path);replacementDirectory=directory;
            }
        }
        lock (_gate)
        {
            _export?.Dispose();
            _export = replacement;_exportDirectory=replacementDirectory;
            if (_export is not null && needsHistory)
                foreach (var entry in _recent) _export.WriteLine(entry.Line);
        }
    }

    public void Write(string message)
    {
        if(_defer){Post(message);return;}
        var entry = new LogEntry(DateTimeOffset.Now, message);
        LogEntry? exportFailure = null;
        LogEntry[] notices;
        lock (_gate)
        {
            Append(false,entry.Line);
            _recent.Enqueue(entry);
            while (_recent.Count > 600) _recent.Dequeue();
            try { _export?.WriteLine(entry.Line); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                try { _export?.Dispose(); } catch (IOException) { }
                _export = null;_exportDirectory=null;
                exportFailure = new LogEntry(DateTimeOffset.Now, $"외부 폴더 기록 중단: {error.Message}. 로컬 파일 기록은 계속함");
                Append(false,exportFailure.Line);
                _recent.Enqueue(exportFailure);
            }
            notices=TakeNotices();
        }
        Added?.Invoke(entry);
        if (exportFailure is not null) Added?.Invoke(exportFailure);
        RaiseNotices(notices);
    }

    // Called under _gate. A failed local write is counted and reported, never thrown at the caller:
    // log calls sit on the keyboard-departure path and must not abort a follow.
    private void Append(bool codes,string line)
    {
        var sink=codes?_codes:_local;
        try
        {
            sink.Writer.WriteLine(line);
            if(_failedWrites>0)
            {
                var resumed=new LogEntry(DateTimeOffset.Now,$"로컬 로그 파일 기록 재개: 이전에 실패한 쓰기 {_failedWrites}회");
                _failedWrites=0;
                try{_local.Writer.WriteLine(resumed.Line);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or ObjectDisposedException){}
                _notices.Add(resumed);
            }
            if(sink.Writer.BaseStream.Position>=_partBytes)RequestRoll(codes);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _localWriteFailures);
            if(_failedWrites++==0)_notices.Add(new(DateTimeOffset.Now,$"로컬 로그 파일 기록 실패: {error.Message}. 파일 기록이 복구될 때까지 화면 기록만 유지함"));
        }
    }
    // Called under _gate: queued notices join the in-memory history before the lock is released.
    private LogEntry[] TakeNotices()
    {
        if(_notices.Count==0)return [];
        var notices=_notices.ToArray();_notices.Clear();
        foreach(var notice in notices){_recent.Enqueue(notice);while(_recent.Count>600)_recent.Dequeue();}
        return notices;
    }
    private void RaiseNotices(LogEntry[] notices)
    {
        foreach(var notice in notices)Added?.Invoke(notice);
    }
    private void RequestRoll(bool codes)
    {
        var flag=codes?2:1;
        if(Environment.TickCount64<Interlocked.Read(ref _nextRollAttempt) || (Interlocked.Or(ref _rolling,flag)&flag)!=0)return;
        // Opening the next part happens off the writer's thread; writers keep using the current part meanwhile.
        _=Task.Run(()=>Roll(codes,flag));
    }
    private void Roll(bool codes,int flag)
    {
        try
        {
            var current=codes?Volatile.Read(ref _codes):Volatile.Read(ref _local);
            Sink next;
            try
            {
                next=OpenSink(codes,current.Part+1);
                if(!codes)next.Writer.WriteLine(new LogEntry(DateTimeOffset.Now,$"로그 파일 이어짐: 이전 파일 {Path.GetFileName(current.Path)}").Line);
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException)
            {
                // Keep writing to the current part; retry the split later instead of on every line.
                Interlocked.Exchange(ref _nextRollAttempt,Environment.TickCount64+60000);
                lock(_gate)_notices.Add(new(DateTimeOffset.Now,$"로그 파일 나누기 실패: {error.Message}. 현재 파일에 계속 기록함"));
                return;
            }
            string? exportDirectory=null;StreamWriter? export=null;string? exportError=null;
            if(!codes && Volatile.Read(ref _exportDirectory) is {} directory)
            {
                exportDirectory=directory;
                try{export=Open(Path.Combine(directory,Path.GetFileName(next.Path)),false);}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException){exportError=error.Message;}
            }
            StreamWriter? previousExport=null;
            lock(_gate)
            {
                if(Volatile.Read(ref _disposed)!=0){next.Writer.Dispose();export?.Dispose();return;}
                if(codes)Volatile.Write(ref _codes,next);else Volatile.Write(ref _local,next);
                if(!codes && exportDirectory is not null && _exportDirectory==exportDirectory)
                {
                    previousExport=_export;_export=export;export=null;
                    if(exportError is not null)
                    {
                        _exportDirectory=null;
                        var stopped=new LogEntry(DateTimeOffset.Now,$"외부 폴더 기록 중단: {exportError}. 로컬 파일 기록은 계속함");
                        Append(false,stopped.Line);_notices.Add(stopped);
                    }
                }
            }
            export?.Dispose();
            try{current.Writer.Dispose();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or ObjectDisposedException){}
            try{previousExport?.Dispose();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or ObjectDisposedException){}
            Retain("파일 나누기");
        }
        finally{Interlocked.And(ref _rolling,~flag);}
    }
    private void Retain(string reason)
    {
        try
        {
            var protect=new[]{FilePath,CodeFilePath};
            var result=ApplyRetention(_directory,protect,DateTime.UtcNow,_retainedAge,_retainedBytes);
            if(Volatile.Read(ref _disposed)!=0)return;
            if(result.Deleted>0 || result.Skipped>0)
                Write($"로그 보존 정리({reason}): 오래된 로그 파일 {result.Deleted}개({result.DeletedBytes/1048576d:0.0} MB) 삭제, 사용 중이라 남긴 파일 {result.Skipped}개 (기준 {_retainedAge.TotalDays:0}일·{_retainedBytes/1048576} MB)");
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException)
        {
            if(Volatile.Read(ref _disposed)==0)Write($"로그 보존 정리 실패({reason}): {error.Message}. 로그 기록은 계속함");
        }
    }
    /// <summary>
    /// Deletes this app's own log files that are older than <paramref name="maximumAge"/>, then the oldest
    /// until the folder holds at most <paramref name="maximumBytes"/>. Files in use by any process are kept.
    /// </summary>
    internal static (int Deleted,long DeletedBytes,int Skipped) ApplyRetention(string directory,IEnumerable<string> protect,DateTime nowUtc,TimeSpan maximumAge,long maximumBytes)
    {
        var kept=new HashSet<string>(protect.Select(Path.GetFullPath),StringComparer.OrdinalIgnoreCase);
        var files=new DirectoryInfo(directory).EnumerateFiles(FilePrefix+"*.txt").OrderBy(f=>f.LastWriteTimeUtc).ToList();
        var total=files.Sum(f=>f.Length);
        int deleted=0,skipped=0;long deletedBytes=0;
        foreach(var file in files)
        {
            if(kept.Contains(file.FullName))continue;
            if(nowUtc-file.LastWriteTimeUtc<=maximumAge && total<=maximumBytes)continue;
            try{var length=file.Length;file.Delete();deleted++;deletedBytes+=length;total-=length;}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException){skipped++;}
        }
        return(deleted,deletedBytes,skipped);
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _delivery.Writer.TryComplete();_writer.GetAwaiter().GetResult();
        lock (_gate)
        {
            // A final flush can fail like any other write (disk full); shutdown must still complete.
            foreach(var writer in new[]{_export,_local.Writer,_codes.Writer})
            {
                try{writer?.Dispose();}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException or ObjectDisposedException){Interlocked.Increment(ref _localWriteFailures);}
            }
        }
    }
}
