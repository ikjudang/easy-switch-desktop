using System.Text.Json;

namespace EasySwitch.Windows.Infrastructure;

public sealed record AppSettings
{
    public bool FollowEnabled { get; init; }
    public string? KeyboardId { get; init; }
    public string? KeyboardName { get; init; }
    public string? MouseId { get; init; }
    public string? MouseName { get; init; }
    public string[]? SelectedDeviceIds { get; init; }
    public DeviceInfoCache[] SelectedDeviceNames { get; init; } = [];
    public long EngineSettingsRevision { get; init; }
    public string? ExportDirectory { get; init; }
    public bool FloatingEnabled { get; init; }
    public bool BounceEnabled { get; init; } = true;
    public bool FloatingAlwaysShow { get; init; }
    public int FloatingTransparency { get; init; }
    public int FloatingSize { get; init; } = 100;
    public int FloatingAnimationSpeed { get; init; } = EasySwitch.Core.FloatingAnimationSpeed.Default;
    public bool ToastEnabled { get; init; } = true;
    public int ToastSize { get; init; } = 100;
    public int ToastTransparency { get; init; }
    public double FloatingX { get; init; } = .88;
    public double FloatingY { get; init; } = .58;
    public string? FloatingMonitor { get; init; }
    public string Language { get; init; } = "ko";
    public string[] ChannelLabels { get; init; } = ["", "", ""];
    public bool ExportEnabled { get; init; }
}
public sealed record DeviceInfoCache(string Id,string Name,string Kind);

public sealed class SettingsStore
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasySwitch");
    private readonly string _path;
    private AppSettings _value = new();
    private readonly object _saveGate=new();
    private (AppSettings Value,long Revision,List<TaskCompletionSource> Waiters)? _pending;
    private long _revision,_valueRevision;
    private bool _writing;
    private Task _lastSave=Task.CompletedTask;
    private readonly Action<AppSettings>? _persistOverride;
    public AppSettings Value => Volatile.Read(ref _value);
    internal string StorageDirectory=>Path.GetDirectoryName(_path)!;
    public string? LoadError { get; private set; }
    public event Action<AppSettings>? Saved;
    public event Action<Exception>? SaveFailed;

    public SettingsStore(string? directory = null)
    {
        directory ??= DataDirectory;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        try
        {
            if (File.Exists(_path)) _value = Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new());
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            LoadError = error.Message;
        }
    }
    internal SettingsStore(string directory,Action<AppSettings> persist):this(directory){_persistOverride=persist;}

    public void Save(AppSettings value)
    {
        EnqueueSave(value,false).GetAwaiter().GetResult();Saved?.Invoke(Value);
    }
    public Task SaveDeferred(AppSettings value)
        =>EnqueueSave(value,true);
    private Task EnqueueSave(AppSettings value,bool immediate)
    {
        value=Normalize(value);
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(_saveGate)
        {
            var revision=++_revision;
            if(immediate){Volatile.Write(ref _value,value);_valueRevision=revision;}
            var waiters=_pending?.Waiters??[];waiters.Add(completion);_pending=(value,revision,waiters);_lastSave=completion.Task;
            if(!_writing){_writing=true;_=Task.Run(PersistPending);}
        }
        if(immediate)Saved?.Invoke(value);
        return completion.Task;
    }
    public Task FlushAsync(){lock(_saveGate)return _lastSave;}
    internal void PreserveServiceSynchronization(AppSettings service)
    {
        var backup=JsonSerializer.Serialize(new{Local=Value,Service=service,UnreadableLocal=LoadError is not null && File.Exists(_path)?File.ReadAllText(_path):null},new JsonSerializerOptions{WriteIndented=true});
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(backup)));
        var directory=Path.Combine(Path.GetDirectoryName(_path)!,"selection-backups");Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,hash+".json");
        if(!File.Exists(path))File.WriteAllText(path,backup);
    }
    private void PersistPending()
    {
        while(true)
        {
            (AppSettings Value,long Revision,List<TaskCompletionSource> Waiters) pending;
            lock(_saveGate){if(_pending is not {} next){_writing=false;return;}pending=next;_pending=null;}
            try
            {
                if(_persistOverride is not null)_persistOverride(pending.Value);
                else{var temp=_path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(pending.Value,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,_path,true);}
                lock(_saveGate){if(pending.Revision>=_valueRevision){Volatile.Write(ref _value,pending.Value);_valueRevision=pending.Revision;}}
                foreach(var waiter in pending.Waiters)waiter.TrySetResult();
            }
            catch(Exception error)
            {
                foreach(var waiter in pending.Waiters)waiter.TrySetException(error);
                try{SaveFailed?.Invoke(error);}catch(Exception){}
            }
        }
    }
    private static AppSettings Normalize(AppSettings v)=>v with
    {
        FloatingSize=Math.Clamp(v.FloatingSize,0,200),FloatingTransparency=Math.Clamp(v.FloatingTransparency,0,100),
        FloatingAnimationSpeed=EasySwitch.Core.FloatingAnimationSpeed.Clamp(v.FloatingAnimationSpeed),
        ToastSize=Math.Clamp(v.ToastSize,50,200),ToastTransparency=Math.Clamp(v.ToastTransparency,0,100),
        EngineSettingsRevision=v.EngineSettingsRevision is >=0 && v.EngineSettingsRevision<=DateTime.MaxValue.Ticks?v.EngineSettingsRevision:0,
        FloatingX=double.IsFinite(v.FloatingX)?Math.Clamp(v.FloatingX,0,1):.88,FloatingY=double.IsFinite(v.FloatingY)?Math.Clamp(v.FloatingY,0,1):.58,
        Language=v.Language=="en"?"en":"ko",
        ChannelLabels=v.ChannelLabels??["","",""],SelectedDeviceIds=v.SelectedDeviceIds?.Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct().ToArray(),SelectedDeviceNames=v.SelectedDeviceNames??[],
    };
}
