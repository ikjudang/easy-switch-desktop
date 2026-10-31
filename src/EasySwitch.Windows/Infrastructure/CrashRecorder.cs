using System.Text;

namespace EasySwitch.Windows.Infrastructure;

// Last-resort record of failures that escape normal handling. Records are appended with millisecond
// timestamps so an earlier crash is never overwritten by a later one.
internal static class CrashRecorder
{
    internal const long MaximumBytes=1024*1024;
    private static readonly object Gate=new();
    private static string? _file;
    private static AppLog? _log;
    private static int _installed;

    internal static string? FilePath=>Volatile.Read(ref _file);

    /// <summary>Registers process-wide handlers once and chooses the crash file.</summary>
    public static void Install(string file)
    {
        Volatile.Write(ref _file,file);
        if(Interlocked.Exchange(ref _installed,1)!=0)return;
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>
            Record(e.IsTerminating?"처리하지 못한 예외로 프로세스 종료":"처리하지 못한 예외",e.ExceptionObject?.ToString()??"(내용 없음)");
        TaskScheduler.UnobservedTaskException+=(_,e)=>{Record("관찰되지 않은 작업 예외",e.Exception.ToString(),fileWhenLogged:false);e.SetObserved();};
    }
    /// <summary>Also sends records to this log while it is attached.</summary>
    public static void Attach(AppLog log)=>Volatile.Write(ref _log,log);
    public static void Detach(AppLog log)=>Interlocked.CompareExchange(ref _log,null,log);

    public static void Record(string kind,string detail,bool fileWhenLogged=true)
    {
        var logged=false;
        if(Volatile.Read(ref _log) is {} log)
        {
            try{log.Write(kind+": "+detail);logged=true;}catch(Exception){}
        }
        if(logged && !fileWhenLogged)return;
        Append(Format(DateTimeOffset.Now,Environment.ProcessId,kind,detail));
    }
    internal static string Format(DateTimeOffset at,int processId,string kind,string detail)=>$"{at:yyyy-MM-dd HH:mm:ss.fff zzz} [{processId}] {kind}: {detail}";
    private static void Append(string line)
    {
        if(Volatile.Read(ref _file) is not {} file)return;
        try
        {
            lock(Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var info=new FileInfo(file);
                if(info.Exists && info.Length>MaximumBytes)File.Move(file,file+".old",true);
                File.AppendAllText(file,line+Environment.NewLine,new UTF8Encoding(false));
            }
        }
        catch(Exception){}
    }
}
