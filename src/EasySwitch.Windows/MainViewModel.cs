using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using EasySwitch.Core;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;
using EasySwitch.Windows.Service;

namespace EasySwitch.Windows;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _settings;
    private readonly AppLog _log;
    private readonly DeviceMonitor _monitor;
    private readonly SwitchCoordinator _switches;
    private ServiceClient? _service;
    private IReadOnlyList<DeviceInfo>? _serviceDevices;
    private bool _applyingService;
    private string? _engineFingerprint;
    private readonly Dictionary<(string Epoch,int Run,bool Connection),int> _serviceRuns=[];
    private int _serviceNoticeSequence;
    private long _powerGeneration;
    private readonly SemaphoreSlim _powerGate=new(1);
    private volatile bool _powerSuspended;
    private bool _deferUiWork;
    private sealed record ServicePresentation(ServiceSnapshot Snapshot,bool AcceptPreferences);
    private ServicePresentation? _pendingPresentation;
    private int _presentationScheduled,_logsScheduled;
    private int _exportGeneration;
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _pendingLogs=new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<CodeLogEntry> _pendingCodes=new();
    private bool _refreshing;
    private IReadOnlyList<DeviceInfo>? _previewDevices;
    private int _stopping;
    private readonly object _followGate = new();
    private FollowIntent? _pendingFollow;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _observedArrivals = new();
    private string _page = "홈";
    private string _lastRequest = "앱이 선택한 기기에 전환 명령을 보내.";
    private string _settingsFeedback = "";
    private bool _enableAfterFolder;
    private int? _lastDestination;
    private long _batchGeneration;
    private readonly ManualSwitchGuard _manual=new();
    private readonly KeyboardDepartureFeedback _keyboardFeedback=new();
    private string? _keyboardEventEpoch;
    private long _keyboardEventSequence;
    private int _codeSequence;
    private int _connectionNoticeSequence;
    private readonly LocalConnectionNotices _localConnections=new();
    private readonly DispatcherTimer _followExpiry=new(){Interval=TimeSpan.FromMilliseconds(500)};
    public event Action? FloatingSettingsChanged;
    public event Action<SwitchNotice>? NoticeRequested;
    public event Action<SwitchNotice>? ConnectionNoticeRequested;
    public event Action<int>? KeyboardSwitchObserved;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? StateChanged;
    public event Action? ExitRequested;
    public ObservableCollection<DeviceInfo> Devices { get; } = [];
    public ObservableCollection<DeviceInfo> KeyboardOptions { get; } = [];
    public ObservableCollection<DeviceInfo> MouseOptions { get; } = [];
    public ObservableCollection<DeviceChoice> DeviceChoices { get; } = [];
    public ObservableCollection<DeviceChoice> HomeDevices { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ObservableCollection<LogEntry> ActivityLogs { get; } = [];
    public ObservableCollection<CodeLogEntry> CodeLogs { get; } = [];
    public ICommand NavigateCommand { get; }
    public ICommand SwitchCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand ChooseExportCommand { get; }
    public ICommand StopExportCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand ToggleLogCommand { get; }
    public ICommand ExportLogCommand { get; }
    public ICommand ExportCodesCommand { get; }
    public string Page { get => _page; private set { _page = value; if(IsLog){Logs.Clear();foreach(var entry in _log.Recent())Logs.Add(entry);}Notify(); Notify(nameof(PageTitle)); Notify(nameof(IsHome)); Notify(nameof(IsDevices)); Notify(nameof(IsLog)); Notify(nameof(IsSettings)); } }
    public string PageTitle => IsDevices ? "기기 선택" : IsLog ? "설정" : Page;
    public bool IsHome => Page == "홈";
    public bool IsDevices => Page == "기기";
    public bool IsLog => Page == "연결 로그";
    public bool IsSettings => Page is "설정" or "연결 로그";
    public string LastRequest { get => _lastRequest; private set { _lastRequest = value; Notify(); } }
    public string SettingsFeedback { get => _settingsFeedback; private set { _settingsFeedback = value; Notify(); } }
    public string LogPath => _log.FilePath;
    public string ExportPath => _settings.Value.ExportDirectory ?? "앱이 로컬 로그 폴더에 기록하고 있어.";
    public bool HasDevices => Devices.Count > 0;
    public int ConnectedCount => Devices.Count(d => d.Online);
    public string ConnectionSummary => $"연결된 기기 ({ConnectedCount}대)";
    private string[] SelectedIds => _settings.Value.SelectedDeviceIds ?? new[] { KeyboardId, MouseId }.Where(id => !string.IsNullOrEmpty(id)).Cast<string>().Distinct().ToArray();
    public bool IsSelectedDevice(string id) => SelectedIds.Contains(id);
    internal bool KnowsRole(string id,string role)
    {
        var observed=_monitor.Find(id)?.Info.Kind ?? (_serviceDevices??_previewDevices)?.FirstOrDefault(d=>d.Id==id)?.Kind;
        if(observed is not null and not "기기")return observed==role;
        return _settings.Value.SelectedDeviceNames.Any(d=>d.Id==id && d.Kind==role) || (role=="키보드"?id==KeyboardId:id==MouseId);
    }
    public int SelectedCount => SelectedIds.Length;
    public string SelectionSummary => $"{SelectedCount}개 선택 완료";
    public bool HasSelection => SelectedCount > 0;
    public bool NoDevices => DeviceChoices.Count == 0;
    public bool NoHomeDevices => HomeDevices.Count == 0;
    public string LogSummary => T($"{Logs.Count}건",$"{Logs.Count} entries");
    public string LogCardDescription => IsLog ? "연결 로그 표시 중" : "연결·전환 기록 확인";
    public string ExportLabel => _settings.Value.ExportDirectory is { } directory ? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : "로컬 폴더에 기록 중";
    public bool HasExportFolder => _settings.Value.ExportDirectory is not null;
    public bool LogFolderEnabled
    {
        get=>_settings.Value.ExportEnabled && HasExportFolder;
        set
        {
            if(value==LogFolderEnabled)return;
            if(value && !HasExportFolder){_enableAfterFolder=true;try{ChooseExport();}finally{_enableAfterFolder=false;}Notify(nameof(LogFolderEnabled));return;}
            if(_deferUiWork){_=ConfigureExportAsync(_settings.Value.ExportDirectory,value);return;}
            try{_log.SetExportDirectory(value?_settings.Value.ExportDirectory:null);SavePreference(_settings.Value with {ExportEnabled=value});}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException){_log.Write($"외부 로그 저장 변경 실패: {error.Message}");Notify(nameof(LogFolderEnabled));}
        }
    }
    public bool CanSwitch => _service is not null ? _service.Connected && Devices.Any(d=>d.Online && IsSelectedDevice(d.Id)) : _previewDevices is not null
        ? _previewDevices.Any(d => d.Online && IsSelectedDevice(d.Id))
        : SelectedDevices().Any(d => d.TransportAvailable && d.LocalLinkConnected != false && d.HostCount > 0);
    public string FollowStatus => FollowEnabled ? T("마우스 따라가기 켜짐", "Mouse follow on") : T("마우스 따라가기 꺼짐", "Mouse follow off");
    public bool SelectedKeyboardOnline => Devices.Any(d => d.Id == KeyboardId && d.Online);
    public bool SelectedMouseOnline => Devices.Any(d => d.Id == MouseId && d.Online);
    public string KeyboardStatus => SelectionStatus(KeyboardId, "키보드");
    public string MouseStatus => SelectionStatus(MouseId, "마우스");
    public string KeyboardName => Devices.FirstOrDefault(d => d.Id == KeyboardId)?.Name ?? _settings.Value.KeyboardName ?? "키보드";
    public string MouseName => Devices.FirstOrDefault(d => d.Id == MouseId)?.Name ?? _settings.Value.MouseName ?? "마우스";
    public bool Channel1Current => IsCurrent(0);
    public bool Channel2Current => IsCurrent(1);
    public bool Channel3Current => IsCurrent(2);
    public bool English => _settings.Value.Language == "en";
    public string T(string ko, string en) => English ? en : ko;
    public int? LastDestination => _lastDestination;
    public string ChannelLabel(int target) => _settings.Value.ChannelLabels.ElementAtOrDefault(target) is { Length: > 0 } text ? text : T($"채널 {target + 1}", $"Channel {target + 1}");
    public bool ChannelCurrent(int target) => IsCurrent(target);
    public string ChannelStatus(int target) => IsCurrent(target) ? T("현재 기기 연결 중", "Connected to this device") : _lastDestination == target ? T("마지막 전환 채널", "Last switched channel") : T("눌러서 전환", "Click to switch");
    public bool FloatingEnabled
    {
        get => _settings.Value.FloatingEnabled;
        set { if (value == FloatingEnabled) return; SavePreference(_settings.Value with { FloatingEnabled = value }); FloatingSettingsChanged?.Invoke(); }
    }
    public string FloatingSettingsLabel=>FloatingEnabled?T("플로팅 버튼 사용 중","Floating button · In use"):T("플로팅 버튼 꺼짐","Floating button · Off");
    public int FloatingTransparency
    {
        get => _settings.Value.FloatingTransparency;
        set { value = Math.Clamp(value, 0, 100); if (value == FloatingTransparency) return; SavePreference(_settings.Value with { FloatingTransparency = value }); FloatingSettingsChanged?.Invoke(); }
    }
    public AppSettings Preferences => _settings.Value;
    public bool BounceEnabled
    {
        get => _settings.Value.BounceEnabled;
        set { if(value==BounceEnabled)return;SavePreference(_settings.Value with {BounceEnabled=value});FloatingSettingsChanged?.Invoke(); }
    }
    public int FloatingSize
    {
        get => _settings.Value.FloatingSize;
        set { value=Math.Clamp(value,0,200); if(value==FloatingSize)return; SavePreference(_settings.Value with {FloatingSize=value});FloatingSettingsChanged?.Invoke(); }
    }
    public bool FloatingAlwaysShow
    {
        get => _settings.Value.FloatingAlwaysShow;
        set { if(value==FloatingAlwaysShow)return;SavePreference(_settings.Value with{FloatingAlwaysShow=value});FloatingSettingsChanged?.Invoke(); }
    }
    public int FloatingAnimationSpeed
    {
        get => _settings.Value.FloatingAnimationSpeed;
        set { value=EasySwitch.Core.FloatingAnimationSpeed.Clamp(value);if(value==FloatingAnimationSpeed)return;SavePreference(_settings.Value with {FloatingAnimationSpeed=value});FloatingSettingsChanged?.Invoke(); }
    }
    public void SetLanguage(string language) { SavePreference(_settings.Value with { Language = language }); }
    public void SetChannelLabel(int channel, string text)
    {
        var labels = Enumerable.Range(0, 3).Select(i => _settings.Value.ChannelLabels.ElementAtOrDefault(i) ?? "").ToArray();
        labels[channel] = text.Trim(); SavePreference(_settings.Value with { ChannelLabels = labels });
    }
    public void SaveFloatingPosition(string monitor, double x, double y) => SavePreference(_settings.Value with { FloatingMonitor = monitor, FloatingX = Math.Clamp(x, 0, 1), FloatingY = Math.Clamp(y, 0, 1) });
    public void ToggleDevice(DeviceInfo device)
    {
        var selected=SelectedIds.ToHashSet();if(!selected.Remove(device.Id))selected.Add(device.Id);
        var names=_settings.Value.SelectedDeviceNames.Where(d=>selected.Contains(d.Id)).ToList();
        foreach(var info in Devices.Where(d=>selected.Contains(d.Id)))
        {
            if(info.Kind=="기기" && names.Any(d=>d.Id==info.Id && d.Kind!="기기"))continue;
            names.RemoveAll(d=>d.Id==info.Id);names.Add(new(info.Id,info.Name,info.Kind));
        }
        Save(_settings.Value with {SelectedDeviceIds=selected.ToArray(),SelectedDeviceNames=names.ToArray()},$"[{device.Name}] 전환 대상 {(selected.Contains(device.Id)?"선택":"해제")}");
    }
    private void SavePreference(AppSettings value)
    {
        try { StoreSettings(value); NotifyAllState(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _log.Write($"설정 저장 실패: {error.Message}"); }
    }
    private void PublishDestination(int target) => Dispatch(() => { _lastDestination = target; NotifyAllState(); });

    public string? KeyboardId
    {
        get => _settings.Value.KeyboardId ?? "";
        set
        {
            if (_refreshing || value == KeyboardId) return;
            var chosen = Devices.FirstOrDefault(d => d.Id == value);
            if (!string.IsNullOrEmpty(value) && value == MouseId) { SettingsFeedback = "사용자는 키보드와 마우스를 서로 다른 기기로 선택해야 해."; Notify(); return; }
            Save(_settings.Value with { KeyboardId = EmptyToNull(value), KeyboardName = chosen?.Name }, "키보드 선택 변경");
        }
    }
    public string? MouseId
    {
        get => _settings.Value.MouseId ?? "";
        set
        {
            if (_refreshing || value == MouseId) return;
            var chosen = Devices.FirstOrDefault(d => d.Id == value);
            if (!string.IsNullOrEmpty(value) && value == KeyboardId) { SettingsFeedback = "사용자는 키보드와 마우스를 서로 다른 기기로 선택해야 해."; Notify(); return; }
            Save(_settings.Value with { MouseId = EmptyToNull(value), MouseName = chosen?.Name }, "마우스 선택 변경");
        }
    }
    public bool FollowEnabled
    {
        get => _settings.Value.FollowEnabled;
        set { if (value != FollowEnabled) Save(_settings.Value with { FollowEnabled = value }, $"키보드 따라 전환 {(value ? "켬" : "끔")}"); }
    }
    public MainViewModel(SettingsStore settings, AppLog log, Dispatcher dispatcher)
    {
        _settings = settings;
        _log = log;
        _dispatcher = dispatcher;
        _monitor = new DeviceMonitor(log);
        _switches = new SwitchCoordinator(log.Write);
        _page=HasSelection?"홈":"기기";
        NavigateCommand = new RelayCommand(p => Page = p?.ToString() ?? "홈");
        SwitchCommand = new RelayCommand(p => _ = SwitchAsync(Convert.ToInt32(p), "채널 버튼"));
        RefreshCommand = new RelayCommand(_ => { _log.Write("사용자가 기기 새로 고침을 요청함"); if(_service is not null)_=RefreshServiceAsync();else _monitor.Refresh(); });
        OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(Path.GetDirectoryName(LogPath)!));
        ChooseExportCommand = new RelayCommand(_ => ChooseExport());
        StopExportCommand = new RelayCommand(_ => SetExport(null));
        ExportLogCommand=new RelayCommand(_=>ExportLog());
        ExportCodesCommand=new RelayCommand(_=>ExportCodes());
        ExitCommand = new RelayCommand(_ => ExitRequested?.Invoke());
        ToggleLogCommand = new RelayCommand(_ => { Page = IsLog ? "설정" : "연결 로그"; Notify(nameof(LogCardDescription)); });
        foreach (var entry in log.Recent()) {Logs.Add(entry);if(!entry.IsProtocolTrace)ActivityLogs.Add(entry);}
        foreach(var entry in log.RecentCodes())CodeLogs.Add(entry);
        log.Added += OnLog;
        log.CodeAdded+=OnCode;
        log.WriteCode($"{SwitchCode.Prefix} APP START");
        _monitor.Changed += () => { TryFollow();if(_deferUiWork)ObserveConnections(_monitor.Snapshot());Dispatch(UpdateDevices); };
        _monitor.HostChanged += OnHostChanged;
        _monitor.PairingInvalidated += device => Dispatch(() =>
        {
            _switches.Cancel(device.Id, "수신기 슬롯 등록이 변경됨");
            var value = _settings.Value;
            if(IsSelectedDevice(device.Id))
                Save(value with {SelectedDeviceIds=SelectedIds.Where(id=>id!=device.Id).ToArray(),SelectedDeviceNames=value.SelectedDeviceNames.Where(d=>d.Id!=device.Id).ToArray()},"변경된 수신기 슬롯의 이전 선택 해제");
            value=_settings.Value;
            if (value.KeyboardId == device.Id) Save(value with { KeyboardId = null, KeyboardName = null }, "이전 키보드 등록 선택 해제");
            if (_settings.Value.MouseId == device.Id) Save(_settings.Value with { MouseId = null, MouseName = null }, "이전 마우스 등록 선택 해제");
        });
        _monitor.Arrived += device =>
        {
            var seenBefore = !_observedArrivals.TryAdd(device.Id, true);
            if(IsSelectedDevice(device.Id) && !device.DepartureSignalActive)_log.WriteCode($"{SwitchCode.Prefix} R0000 {CodeRole(device.Id)} {(seenBefore?"RET":"RDY")}");
            if (!IsSelectedDevice(device.Id) || !KnowsRole(device.Id,"키보드")) return;
            if (device.DepartureSignalActive)
            {
                _log.WriteCode($"{SwitchCode.Prefix} R0000 {CodeRole(device.Id)} LATE");
                _log.Write($"[{device.Name}] 늦은 도착 확인 생략: 이미 더 최근의 출발 신호를 처리함");
                return;
            }
            _keyboardFeedback.Arrived(device);
            // A keyboard returning to this PC makes all previous departure intent stale.
            if (seenBefore)
            {
                Interlocked.Increment(ref _batchGeneration);
                ClearFollow("키보드의 로컬 응답이 다시 확인됨");
                _switches.CancelAll("키보드의 로컬 응답이 다시 확인됨");
            }
            _log.Write($"[{device.Name}] 키보드 도착 감지. 태블릿 LAN 알림은 이 버전에서 제공하지 않음");
        };
        UpdateDevices();
        CodeSettings();
        _followExpiry.Tick+=(_,_)=>TryFollow();
    }

    public void Start() { if(_service is not null){_service.Start();return;}if (_previewDevices is null) {_monitor.Start();_followExpiry.Start();} }

    internal bool HardwareStarted=>_monitor.Started;
    internal void EnableDeferredUiWork()
    {
        _deferUiWork=true;_log.EnableDeferredWrites();_settings.SaveFailed+=SettingsSaveFailed;
    }
    private void StoreSettings(AppSettings value)
    {
        if(_deferUiWork)_=_settings.SaveDeferred(value);else _settings.Save(value);
    }
    private void SettingsSaveFailed(Exception error)=>_log.Post("설정 저장 실패: "+error.Message);
    internal void AttachService(ServiceClient client)
    {
        if(_monitor.Started && !_powerSuspended)throw new InvalidOperationException("기기 통로를 연 뒤 서비스로 변경할 수 없어.");
        _service=client;_serviceDevices=[];_engineFingerprint=EnginePreferences.From(_settings.Value).Fingerprint;
        _settings.Saved+=ServiceSettingsSaved;
        client.SnapshotReceived+=snapshot=>ReceiveServiceSnapshot(snapshot,!client.ConfigurationPending);
        client.AvailabilityChanged+=available=>{if(!available)Dispatch(()=>{_serviceDevices=(_serviceDevices??[]).Select(d=>d with{Online=false}).ToArray();UpdateDevices();});};
    }
    internal async Task InstallBootServiceAsync()
    {
        if(_service is not null){await ServiceRegistration.EnsureInstalledAsync(_log,true);return;}
        await ChangePowerAsync(true,"서비스 설치 준비");
        try
        {
            await _monitor.SuspendAndWaitAsync();
            await ServiceRegistration.EnsureInstalledAsync(_log,true);
            if(ServiceRegistration.Installed){AttachService(new ServiceClient(_log));_service!.Start();}
        }
        finally{if(_service is null)await ChangePowerAsync(false,"서비스 설치 미완료 후 복구");}
    }
    internal void FlushServiceSettings(){try{_settings.FlushAsync().GetAwaiter().GetResult();_service?.FlushConfigurationAsync().GetAwaiter().GetResult();}catch(Exception error){_log.Write("로그아웃 전 서비스 설정 저장 확인: "+error.Message);}}
    private void ServiceSettingsSaved(AppSettings settings)
    {
        var preferences=EnginePreferences.From(settings);var fingerprint=preferences.Fingerprint;
        if(_applyingService || fingerprint==_engineFingerprint)return;
        _engineFingerprint=fingerprint;_service?.Configure(preferences);
    }
    private void ReceiveServiceSnapshot(ServiceSnapshot snapshot,bool acceptPreferences)
    {
        if(Volatile.Read(ref _stopping)!=0)return;
        foreach(var item in snapshot.Events)
        {
            if(item.Log is {} entry)_log.Import(entry);
            if(item.Code is {} code)_log.Import(code);
            if(item.KeyboardTarget is {} target)
            {
                if(_keyboardEventEpoch!=snapshot.Epoch){_keyboardEventEpoch=snapshot.Epoch;_keyboardEventSequence=0;}
                if(item.Sequence>_keyboardEventSequence)
                {
                    _keyboardEventSequence=item.Sequence;
                    if(target is >=0 and <=2 && DateTimeOffset.Now-item.At<TimeSpan.FromSeconds(2))KeyboardSwitchObserved?.Invoke(target);
                }
            }
            if(item.Notice is {} notice && DateTimeOffset.Now-item.At<TimeSpan.FromSeconds(2))
            {
                var key=(snapshot.Epoch,notice.RunId,item.Connection);
                if(!_serviceRuns.TryGetValue(key,out var run)){if(_serviceRuns.Count>256)_serviceRuns.Clear();_serviceRuns[key]=run=++_serviceNoticeSequence;}
                var forwarded=notice with{RunId=run};
                if(item.Connection)ConnectionNoticeRequested?.Invoke(forwarded);else NoticeRequested?.Invoke(forwarded);
            }
        }
        if(snapshot.HistoryTruncated)_log.Write("서비스 표시 이력의 일부가 이전 구간으로 넘어감. 전체 원문·코드는 서비스 로그 파일과 내보내기에 보존됨");
        Interlocked.Exchange(ref _pendingPresentation,new(snapshot with{Events=[]},acceptPreferences));SchedulePresentation();
    }
    private void SchedulePresentation()
    {
        if(_dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _presentationScheduled,1)!=0)return;
        _dispatcher.BeginInvoke(()=>
        {
            try{if(Interlocked.Exchange(ref _pendingPresentation,null) is {} presentation && Volatile.Read(ref _stopping)==0)ApplyServiceSnapshot(presentation.Snapshot,presentation.AcceptPreferences);}
            finally{Interlocked.Exchange(ref _presentationScheduled,0);if(Volatile.Read(ref _pendingPresentation) is not null)SchedulePresentation();}
        },DispatcherPriority.Background);
    }
    private void ApplyServiceSnapshot(ServiceSnapshot snapshot,bool acceptPreferences)
    {
        var changed=_serviceDevices is null || !_serviceDevices.SequenceEqual(snapshot.Devices) || _lastDestination!=snapshot.LastDestination;
        if(acceptPreferences && _service?.ConfigurationPending!=true && snapshot.Preferences.Fingerprint!=EnginePreferences.From(_settings.Value).Fingerprint)
        {
            _applyingService=true;
            try{StoreSettings(snapshot.Preferences.Apply(_settings.Value));_engineFingerprint=snapshot.Preferences.Fingerprint;changed=true;}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException){_log.Write("서비스 설정 반영 실패: "+error.Message);}
            finally{_applyingService=false;}
        }
        _serviceDevices=snapshot.Devices;_lastDestination=snapshot.LastDestination;
        if(LastRequest!=snapshot.LastRequest)LastRequest=snapshot.LastRequest;
        if(changed)UpdateDevices();
    }
    internal void ApplyEnginePreferences(EnginePreferences preferences)
    {
        preferences.Validate();if(preferences.Fingerprint==EnginePreferences.From(_settings.Value).Fingerprint)return;
        Save(preferences.Apply(_settings.Value),"Windows 서비스의 전환 설정 변경");
    }
    private async Task RefreshServiceAsync(){try{await _service!.RefreshAsync();}catch(Exception error)when(error is IOException or TimeoutException or OperationCanceledException){_log.Write("서비스 새로 고침 실패: "+error.Message);}}
    internal async Task ChangePowerAsync(bool suspend,string reason)
    {
        var generation=Interlocked.Increment(ref _powerGeneration);_powerSuspended=suspend;
        _followExpiry.Stop();_monitor.Suspend();Interlocked.Increment(ref _batchGeneration);ClearFollow(reason);_switches.CancelAll(reason);
        await _powerGate.WaitAsync();
        try
        {
            await _switches.StopAsync(reason);
            if(generation!=Volatile.Read(ref _powerGeneration) || Volatile.Read(ref _stopping)!=0)return;
            _observedArrivals.Clear();_log.Write(reason+": 오래된 전환을 취소함");
            _log.WriteCode($"{SwitchCode.Prefix} SVC POWER {(suspend?"SUSPEND":"RESUME")}");
            if(!suspend){_monitor.Recover();if(_previewDevices is null)_followExpiry.Start();}
        }
        finally{_powerGate.Release();}
    }
    internal void BlockForSuspend(string reason)
    {
        _powerSuspended=true;_monitor.Suspend();Interlocked.Increment(ref _batchGeneration);ClearFollow(reason);_switches.CancelAll(reason);
    }

    internal void UsePreviewDevices(IReadOnlyList<DeviceInfo> devices)
    {
        _previewDevices = devices;
        UpdateDevices();
    }
    internal void SetPreviewDestination(int target) => PublishDestination(target);

    public async Task SwitchAsync(int target, string reason)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        if (target is < 0 or > 2) { _log.Write($"전환 생략: 잘못된 목적지 {target}"); return; }
        if(_service is not null)
        {
            try{await _service.SwitchAsync(target,reason);}catch(Exception error)when(error is IOException or TimeoutException or OperationCanceledException){_log.Write("서비스 전환 요청 취소: "+error.Message);}
            return;
        }
        if(_powerSuspended){_log.Write("전환 생략: 절전 전환 처리 중");return;}
        var code=NewCodeRun(target,reason.Contains("트레이")?"T":reason.Contains("플로팅")?"P":"B");
        CodeEvent(code,"BEGIN");
        ClearFollow("사용자가 채널을 직접 요청함");
        var generation = Interlocked.Increment(ref _batchGeneration);
        _switches.CancelAll("더 최근의 수동 전환 요청");
        var devices = SelectedDevices().Where(d => d.TransportAvailable && d.LocalLinkConnected != false && d.Info.Online && d.HostCount > 0).ToArray();
        if (devices.Length == 0) { CodeEvent(code,"SKIP_LINK");_log.Write($"채널 {target + 1} 전환 생략: 선택한 기기에 사용할 수 있는 통로가 없음");PostNotice(code,[]);return; }
        Dispatch(() => LastRequest = $"마지막 요청: 채널 {target + 1}");
        _manual.Begin(generation,target);
        BeginNotice(code,devices.Select(d=>d.Id));
        // Start mouse writes first, before the keyboard leaves this PC.
        (HostDevice Device,SwitchOutcome Outcome)[] results;
        try {results = await Task.WhenAll(devices.Select(async d =>
        {
            var outcome=await SwitchWithCodeAsync(d,target,reason,code,generation).ConfigureAwait(false);
            if(generation==Volatile.Read(ref _batchGeneration))PostNotice(code,[DeviceResult.FromOutcome(d.Id,d.Name,d.Info.Kind,outcome)]);
            return (Device:d,Outcome:outcome);
        })).ConfigureAwait(false);}
        finally{_manual.Complete(generation);}
        if (generation != Volatile.Read(ref _batchGeneration)) return;
        if (results.Any(r => r.Outcome is SwitchOutcome.TargetReported or SwitchOutcome.LocalLinkClosed)) PublishDestination(target);
        Dispatch(UpdateDevices);
    }

    private void OnHostChanged(HostDevice keyboard, int target)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        if(_powerSuspended){_log.Write($"[{keyboard.Name}] 전환 신호 처리 생략: 절전 전환 처리 중");return;}
        var settings = _settings.Value;
        var code=NewCodeRun(target,"F");
        if (!IsSelectedDevice(keyboard.Id) || !KnowsRole(keyboard.Id,"키보드")) {CodeEvent(code,"SKIP_KEY");_log.Write($"[{keyboard.Name}] 따라 전환 생략: 선택한 키보드가 아님"); return; }
        CodeEvent(code,"KEY",CodeRole(keyboard.Id));
        PublishDestination(target);
        if (_manual.Matches(Volatile.Read(ref _batchGeneration),target))
        { CodeEvent(code,"SKIP_MAN");_log.Write("따라 전환 생략: 앱에서 같은 채널을 직접 전환하는 중"); return; }
        if(_keyboardFeedback.TryAccept(keyboard,target))KeyboardSwitchObserved?.Invoke(target);
        Interlocked.Increment(ref _batchGeneration);
        _switches.CancelAll("더 최근의 키보드 전환 신호");
        if (!settings.FollowEnabled) { CodeEvent(code,"SKIP_OFF");_log.Write("따라 전환 생략: 사용자가 자동 전환을 꺼 둠"); return; }
        var mouseIds=SelectedIds.Where(id=>KnowsRole(id,"마우스")).ToArray();
        if (mouseIds.Length==0) { CodeEvent(code,"SKIP_SEL");_log.Write("따라 전환 생략: 사용자가 마우스를 선택하지 않음"); return; }
        lock (_followGate)
        {
            if (_pendingFollow is not null) {CodeEvent(_pendingFollow.Code,"CAN_NEW");_log.Write("이전 대기 전환 취소: 더 최근의 키보드 전환 신호를 받음");}
            _pendingFollow = new FollowIntent(mouseIds, target, Environment.TickCount64,code);
        }
        BeginNotice(code,[],keyboard.Id);
        Dispatch(() => LastRequest = $"키보드 전환 감지: 채널 {target + 1}");
        if (!TryFollow()) {lock(_followGate){if(_pendingFollow?.Code==code){CodeEvent(code,"WAIT");_log.Write("마우스 복귀 대기: 이 PC에서 확인했던 마우스의 통로가 10초 안에 준비되면 최신 전환을 보냄");}}}
    }

    private bool TryFollow()
    {
        lock (_followGate)
        {
            if (_pendingFollow is not { } intent || Volatile.Read(ref _stopping) != 0) return false;
            if (!_settings.Value.FollowEnabled || intent.MouseIds.Any(id=>!IsSelectedDevice(id)))
            {
                ClearFollow("자동 전환 설정 또는 선택한 마우스가 바뀜");
                return false;
            }
            if (FollowPreparation.Expired(intent.At,Environment.TickCount64))
            {
                ClearFollow("마우스 준비 대기 10초 만료. 오래된 전환을 보내지 않음");
                return false;
            }
            var waiting=new List<string>();var ready=new List<HostDevice>();
            foreach(var id in intent.MouseIds)
            {
                var mouse=_monitor.Find(id);
                if(intent.Recoveries?.TryGetValue(id,out var recovery)==true)
                {
                    if(mouse is null || ReferenceEquals(mouse,recovery.FailedDevice) || !mouse.IdentityReady || mouse.LocalLinkConnected!=true || !mouse.Info.Online){waiting.Add(id);continue;}
                    if(mouse.Name!=recovery.Name || mouse.Info.Kind!=recovery.Kind)
                    {CodeEvent(intent.Code,"SKIP_ID",CodeRole(id));_log.Write("출력 복구 생략: 새 통로의 기기 정보가 이전 선택과 다름");PostNotice(intent.Code,[NoticeRow(id,DeviceNoticeState.Unavailable)]);continue;}
                }
                var readiness=FollowPreparation.Readiness(mouse is not null,mouse?.TransportAvailable??false,mouse?.LocalLinkConnected,mouse?.Info.Online??false,mouse?.HostCount??0,_observedArrivals.ContainsKey(id));
                if(readiness==FollowReadiness.Exclude){CodeEvent(intent.Code,"SKIP_LINK",CodeRole(id));_log.Write($"[{mouse!.Name}] 따라 전환 대상 제외: 이 실행에서 로컬 연결을 확인한 적이 없고 다른 호스트에 있음");PostNotice(intent.Code,[NoticeRow(id,DeviceNoticeState.Unavailable)]);continue;}
                if(readiness==FollowReadiness.Wait){waiting.Add(id);continue;}
                ready.Add(mouse!);
            }
            _pendingFollow=waiting.Count==0?null:intent with {MouseIds=waiting.ToArray()};
            if(ready.Count>0)_=ExecuteFollowAsync(ready.ToArray(),intent.Target,Volatile.Read(ref _batchGeneration),intent.Code,intent.At);
            return ready.Count>0;
        }
    }
    private async Task ExecuteFollowAsync(HostDevice[] devices,int target,long generation,CodeRun code,long requestedAt)
    {
        PostNotice(code,devices.Select(d=>new DeviceResult(d.Name,null,d.Info.Kind,d.Id,DeviceNoticeState.Switching)).ToArray());
        await Task.WhenAll(devices.Select(async device=>
        {
            var outcome=await SwitchWithCodeAsync(device,target,"키보드 Easy-Switch 신호",code,generation).ConfigureAwait(false);
            if(device.TransportRecoveryRequired && outcome is not (SwitchOutcome.AlreadyCurrent or SwitchOutcome.TargetReported or SwitchOutcome.LocalLinkClosed) && QueueRecoveredFollow(device,target,generation,code,requestedAt))return;
            if(generation==Volatile.Read(ref _batchGeneration) && Volatile.Read(ref _stopping)==0)
                PostNotice(code,[DeviceResult.FromOutcome(device.Id,device.Name,device.Info.Kind,outcome)]);
        })).ConfigureAwait(false);
    }
    private bool QueueRecoveredFollow(HostDevice device,int target,long generation,CodeRun code,long requestedAt)
    {
        lock(_followGate)
        {
            if(generation!=Volatile.Read(ref _batchGeneration) || Volatile.Read(ref _stopping)!=0 || !_settings.Value.FollowEnabled || !IsSelectedDevice(device.Id) || FollowPreparation.Expired(requestedAt,Environment.TickCount64))
            {CodeEvent(code,"CAN_IO",CodeRole(device.Id));_log.Write("출력 복구 전환 취소: 더 최신 요청·기기 선택/따라가기 변경·종료 또는 기존 10초 대기 한도에 해당함");return false;}
            var current=_pendingFollow;
            if(current is not null && current.Code!=code)return false;
            var cached=_settings.Value.SelectedDeviceNames.FirstOrDefault(d=>d.Id==device.Id);
            var identity=new FollowRecovery(device,device.Info.Kind=="기기"?cached?.Name??device.Name:device.Name,device.Info.Kind=="기기"?cached?.Kind??device.Info.Kind:device.Info.Kind);
            var recoveries=current?.Recoveries?.ToDictionary(p=>p.Key,p=>p.Value)??[];recoveries[device.Id]=identity;
            _pendingFollow=new((current?.MouseIds??[]).Append(device.Id).Distinct().ToArray(),target,requestedAt,code,recoveries);
            CodeEvent(code,"WAIT_IO",CodeRole(device.Id));_log.Write("출력 통로 복구 대기: 기기 선택은 유지하며 새 통로에서 같은 기기의 로컬 응답을 확인한 뒤 최신 전환만 이어감");
            PostNotice(code,[new(device.Name,null,device.Info.Kind,device.Id,DeviceNoticeState.Switching)]);
        }
        TryFollow();return true;
    }
    private void BeginNotice(CodeRun code,IEnumerable<string> switching,string? keyboard=null)
    {
        var sending=switching.ToHashSet();var ids=SelectedIds;
        void Publish()=>PostNotice(code,ids.Select(id=>
        {
            var info=_monitor.Find(id)?.Info ?? _previewDevices?.FirstOrDefault(d=>d.Id==id);
            var cached=_settings.Value.SelectedDeviceNames.FirstOrDefault(d=>d.Id==id);
            var name=info?.Name??cached?.Name??(id==KeyboardId?_settings.Value.KeyboardName??T("키보드","Keyboard"):id==MouseId?_settings.Value.MouseName??T("마우스","Mouse"):T("기기","Device"));
            var kind=KnowsRole(id,"키보드")?"키보드":KnowsRole(id,"마우스")?"마우스":info?.Kind??cached?.Kind??"기기";
            var state=id==keyboard?DeviceNoticeState.SignalObserved:sending.Contains(id)?DeviceNoticeState.Switching:keyboard is not null && kind=="마우스"?DeviceNoticeState.Waiting:DeviceNoticeState.Unavailable;
            return new DeviceResult(name,null,kind,id,state);
        }).ToArray());
        if(_deferUiWork)Publish();else Dispatch(Publish);
    }
    private void PostNotice(CodeRun code,IReadOnlyList<DeviceResult> rows)
    {
        var notice=new SwitchNotice(T($"채널 {code.Target+1} 전환",$"Switch to channel {code.Target+1}"),rows,code.Id);
        if(_deferUiWork)NoticeRequested?.Invoke(notice);else Dispatch(()=>NoticeRequested?.Invoke(notice));
    }
    private DeviceResult NoticeRow(string id,DeviceNoticeState state)
    {
        var device=_monitor.Find(id);var cached=_settings.Value.SelectedDeviceNames.FirstOrDefault(d=>d.Id==id);
        return new(device?.Name??cached?.Name??T("마우스","Mouse"),null,"마우스",id,state);
    }

    private void ClearFollow(string reason)
    {
        lock (_followGate)
        {
            if (_pendingFollow is null) return;
            var expired=reason.Contains("10초 만료");
            CodeEvent(_pendingFollow.Code,expired?"CAN_EXP":reason.Contains("로컬 응답")?"CAN_RET":reason.Contains("직접")?"CAN_MAN":reason.Contains("종료")?"CAN_STOP":"CAN_CFG");
            PostNotice(_pendingFollow.Code,_pendingFollow.MouseIds.Select(id=>NoticeRow(id,expired?DeviceNoticeState.WaitExpired:DeviceNoticeState.Cancelled)).ToArray());
            _pendingFollow = null;
            _log.Write($"대기 중인 따라 전환 취소: {reason}");
        }
    }

    private IEnumerable<HostDevice> SelectedDevices()
    {
        foreach(var device in SelectedIds.Select(id=>_monitor.Find(id)).Where(d=>d is not null).Cast<HostDevice>().OrderBy(d=>d.Info.Kind=="마우스"?0:1))yield return device;
    }
    private CodeRun NewCodeRun(int target,string source)=>new(Interlocked.Increment(ref _codeSequence),source,target);
    private void CodeSettings()=>_log.WriteCode($"{SwitchCode.Prefix} CFG F{(FollowEnabled?1:0)} K{SelectedIds.Count(id=>KnowsRole(id,"키보드"))} M{SelectedIds.Count(id=>KnowsRole(id,"마우스"))}");
    private void CodeEvent(CodeRun code,string stage,string role="ALL")=>_log.WriteCode($"{SwitchCode.Prefix} R{code.Id:D4} {code.Source}{code.Target+1} {role} {stage} E{code.Clock.ElapsedMilliseconds}");
    private string CodeRole(string id)
    {
        var role=KnowsRole(id,"키보드")?"키보드":KnowsRole(id,"마우스")?"마우스":"기기";
        var ids=SelectedIds.Where(x=>role=="기기" || KnowsRole(x,role)).ToArray();
        return (role=="키보드"?"K":role=="마우스"?"M":"D")+(Math.Max(0,Array.IndexOf(ids,id))+1);
    }
    private Task<SwitchOutcome> SwitchWithCodeAsync(HostDevice device,int target,string reason,CodeRun code,long generation)
    {
        var role=CodeRole(device.Id);
        return _switches.SwitchAsync(device,target,reason,p=>
        {
            _log.WriteCode(SwitchCode.Format(code.Id,code.Source,target,role,code.Clock.ElapsedMilliseconds,p));
            if(generation==Volatile.Read(ref _batchGeneration) && Volatile.Read(ref _stopping)==0 && DeviceResult.FromProgress(device.Id,device.Name,device.Info.Kind,p) is {} row)
            {
                // A write can complete synchronously on the UI thread. Queue painting so it
                // cannot hold up scheduling the other selected device's write.
                void Publish()
                {
                    if(generation==Volatile.Read(ref _batchGeneration) && Volatile.Read(ref _stopping)==0)PostNotice(code,[row]);
                }
                if(_deferUiWork)_=Task.Run(Publish);else if(!_dispatcher.HasShutdownStarted)_dispatcher.BeginInvoke(Publish);
            }
        });
    }

    private bool IsCurrent(int channel)
    {
        var selected = Devices.Where(d => IsSelectedDevice(d.Id)).ToArray();
        return selected.Any(d => d.Online && d.CurrentHost == channel);
    }

    private string SelectionStatus(string? id, string role) => string.IsNullOrEmpty(id)
        ? $"사용자가 {role}를 선택해 줘."
        : Devices.FirstOrDefault(d => d.Id == id)?.Status ?? "연결 안 됨";

    private void UpdateDevices()
    {
        var snapshot = _serviceDevices ?? _previewDevices ?? _monitor.Snapshot();
        _refreshing = true;
        try
        {
            Devices.Clear();
            foreach (var device in snapshot) Devices.Add(device);
            UpdateOptions(KeyboardOptions, "키보드", _settings.Value.KeyboardId, _settings.Value.KeyboardName);
            UpdateOptions(MouseOptions, "마우스", _settings.Value.MouseId, _settings.Value.MouseName);
            DeviceChoices.Clear();
            HomeDevices.Clear();
            var visible = snapshot.ToList();
            foreach(var saved in _settings.Value.SelectedDeviceNames.Where(s=>IsSelectedDevice(s.Id)))
                if(visible.All(d=>d.Id!=saved.Id))visible.Add(new(saved.Id,saved.Name,"",saved.Kind,false,0,null,0));
            foreach (var saved in new[] { (KeyboardId, _settings.Value.KeyboardName, "키보드"), (MouseId, _settings.Value.MouseName, "마우스") })
            {
                if (!string.IsNullOrEmpty(saved.Item1) && visible.All(d => d.Id != saved.Item1))
                    visible.Add(new DeviceInfo(saved.Item1, saved.Item2 ?? saved.Item3, "", saved.Item3, false, 0, null, 0));
            }
            foreach (var device in visible)
            {
                var choice = new DeviceChoice(this, device);
                DeviceChoices.Add(choice);
                if (choice.IsSelected) HomeDevices.Add(choice);
            }
        }
        finally { _refreshing = false; }
        NotifyAllState();
        if(!_deferUiWork)ObserveConnections(snapshot);
    }
    private void ObserveConnections(IReadOnlyList<DeviceInfo> snapshot)
    {
        var connected=new List<DeviceResult>();
        foreach(var info in snapshot)
        {
            var device=_monitor.Find(info.Id);
            if(device is null || device.LocalLinkConnected==false || device.DepartureSignalActive)continue;
            if(_localConnections.Observe(device.Info,device,device.ConnectionGeneration) is {} notice)connected.Add(notice);
        }
        if(connected.Count>0)ConnectionNoticeRequested?.Invoke(new(T("기기 연결됨","Devices connected"),connected,Interlocked.Increment(ref _connectionNoticeSequence)));
    }

    private void UpdateOptions(ObservableCollection<DeviceInfo> options, string role, string? selected, string? selectedName)
    {
        options.Clear();
        options.Add(new DeviceInfo("", "선택 안 함", "", role, false, 0, null, 0));
        foreach (var device in Devices.OrderByDescending(d => d.Kind == role)) options.Add(device);
        if (selected is not null && options.All(d => d.Id != selected))
            options.Add(new DeviceInfo(selected, selectedName ?? role, "", role, false, 0, null, 0));
    }

    private void Save(AppSettings settings, string reason)
    {
        try
        {
            StoreSettings(settings);
            Interlocked.Increment(ref _batchGeneration);
            ClearFollow(reason);
            _switches.CancelAll(reason);
            _log.Write(reason);
            CodeSettings();
            SettingsFeedback = "앱이 설정을 저장했어.";
            UpdateDevices();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Write($"설정 저장 실패: {error.Message}");
            SettingsFeedback = "앱이 설정을 저장하지 못했어. 연결 로그에서 원인을 확인해 줘.";
        }
        NotifyAllState();
    }

    private void ChooseExport()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "앱이 로그를 함께 기록할 폴더 선택", Multiselect = false };
        if (dialog.ShowDialog() == true) SetExport(dialog.FolderName);
    }

    private void SetExport(string? path)
    {
        if(_deferUiWork){_=ConfigureExportAsync(path,path is not null && (_settings.Value.ExportEnabled || _enableAfterFolder));return;}
        var previous = _settings.Value;
        try
        {
            var enabled=path is not null && (previous.ExportEnabled || _enableAfterFolder);
            _log.SetExportDirectory(enabled?path:null);
            StoreSettings(previous with { ExportDirectory = path, ExportEnabled=enabled });
            _log.Write(path is null ? "외부 폴더 동시 기록 끔" : $"외부 폴더 동시 기록 켬: {path}");
            SettingsFeedback = path is null ? "앱이 로컬 폴더에만 로그를 기록해." : "앱이 선택한 폴더에도 새 로그를 자동으로 기록해.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            try { _log.SetExportDirectory(previous.ExportEnabled?previous.ExportDirectory:null); } catch (Exception restore) { _log.Write($"외부 기록 복구 실패: {restore.Message}"); }
            _log.Write($"로그 폴더 변경 실패: {error.Message}");
            SettingsFeedback = "앱이 로그 폴더를 변경하지 못했어. 연결 로그에서 원인을 확인해 줘.";
        }
        Notify(nameof(ExportPath));
        Notify(nameof(ExportLabel));
        Notify(nameof(HasExportFolder));
        Notify(nameof(LogFolderEnabled));
    }
    private async Task ConfigureExportAsync(string? path,bool enabled)
    {
        var generation=++_exportGeneration;var previous=_settings.Value;
        StoreSettings(previous with{ExportDirectory=path,ExportEnabled=enabled});NotifyAllState();
        try
        {
            await _log.QueueWork(()=>_log.SetExportDirectory(enabled?path:null));
            if(generation!=_exportGeneration)return;
            SettingsFeedback=enabled?"앱이 선택한 폴더에도 새 로그를 자동으로 기록해.":"앱이 로컬 폴더에만 로그를 기록해.";
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Post("로그 폴더 변경 실패: "+error.Message);
            if(generation==_exportGeneration){StoreSettings(_settings.Value with{ExportDirectory=previous.ExportDirectory,ExportEnabled=previous.ExportEnabled});SettingsFeedback="앱이 로그 폴더를 변경하지 못했어. 연결 로그에서 원인을 확인해 줘.";NotifyAllState();}
        }
    }
    private async void ExportLog()
    {
        var picker=new Microsoft.Win32.SaveFileDialog{Title=T("연결 로그 내보내기","Export connection log"),Filter="Text log (*.txt)|*.txt",FileName=$"easyswitch-pc-{DateTime.Now:yyyyMMdd-HHmmss}.txt"};
        if(picker.ShowDialog()!=true)return;
        try{if(_service is not null)await _service.ExportAsync(picker.FileName,false);else File.WriteAllLines(picker.FileName,_log.Recent().Reverse().Select(e=>e.Line),new System.Text.UTF8Encoding(true));_log.Write($"사용자가 연결 로그를 내보냄: {picker.FileName}");}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException){_log.Write($"로그 내보내기 실패: {error.Message}");}
    }
    private async void ExportCodes()
    {
        var picker=new Microsoft.Win32.SaveFileDialog{Title=T("코드 로그 내보내기","Export status codes"),Filter="Code log (*.codes.txt)|*.codes.txt|Text log (*.txt)|*.txt",FileName=$"easyswitch-pc-{DateTime.Now:yyyyMMdd-HHmmss}.codes.txt"};
        if(picker.ShowDialog()!=true)return;
        try{if(_service is not null)await _service.ExportAsync(picker.FileName,true);else _log.ExportCodes(picker.FileName);_log.Write($"사용자가 코드 로그를 내보냄: {picker.FileName}");}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException){_log.Write($"코드 로그 내보내기 실패: {error.Message}");}
    }

    private void OpenFolder(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) { _log.Write($"로그 폴더 열기 실패: {error.Message}"); }
    }

    private void OnLog(LogEntry entry)
    {
        // Raw traces still go to the complete file and the opt-in TX/RX view. Do not queue a UI
        // notification for every packet while the user is on another screen.
        if(entry.IsProtocolTrace && !IsLog)return;
        if(_dispatcher.CheckAccess()){AddLog(entry);Notify(nameof(LogSummary));return;}
        _pendingLogs.Enqueue(entry);ScheduleLogs();
    }
    private void AddLog(LogEntry entry)
    {
        if(Logs.Contains(entry))return;
        Logs.Insert(0,entry);while(Logs.Count>600)Logs.RemoveAt(Logs.Count-1);
        if(!entry.IsProtocolTrace){ActivityLogs.Insert(0,entry);while(ActivityLogs.Count>600)ActivityLogs.RemoveAt(ActivityLogs.Count-1);}
    }
    private void OnCode(CodeLogEntry entry)
    {
        if(_dispatcher.CheckAccess()){CodeLogs.Insert(0,entry);while(CodeLogs.Count>600)CodeLogs.RemoveAt(CodeLogs.Count-1);return;}
        _pendingCodes.Enqueue(entry);ScheduleLogs();
    }
    private void ScheduleLogs()
    {
        if(_dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _logsScheduled,1)!=0)return;
        _dispatcher.BeginInvoke(()=>
        {
            var clock=System.Diagnostics.Stopwatch.StartNew();var count=0;
            while(count++<128 && clock.ElapsedMilliseconds<6 && _pendingLogs.TryDequeue(out var entry))AddLog(entry);
            count=0;
            while(count++<128 && clock.ElapsedMilliseconds<8 && _pendingCodes.TryDequeue(out var code)){CodeLogs.Insert(0,code);while(CodeLogs.Count>600)CodeLogs.RemoveAt(CodeLogs.Count-1);}
            Notify(nameof(LogSummary));Interlocked.Exchange(ref _logsScheduled,0);
            if(!_pendingLogs.IsEmpty || !_pendingCodes.IsEmpty)ScheduleLogs();
        },DispatcherPriority.Background);
    }
    private void Dispatch(Action action)
    {
        if (_dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }
    internal void ReportNoticeError(Exception error)=>_log.Write($"토스트 표시 오류: {error}");
    private void NotifyAllState()
    {
        Notify("");
        StateChanged?.Invoke();
    }
    private static string? EmptyToNull(string? text) => string.IsNullOrEmpty(text) ? null : text;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _followExpiry.Stop();
        ClearFollow("앱 종료");
        await _switches.StopAsync("앱 종료").ConfigureAwait(false);
        await _monitor.DisposeAsync().ConfigureAwait(false);
        _log.WriteCode($"{SwitchCode.Prefix} APP STOP");
        _log.Added -= OnLog;
        _log.CodeAdded-=OnCode;
        _settings.Saved-=ServiceSettingsSaved;
        if(_service is not null)await _service.DisposeAsync();
        await _settings.FlushAsync().ConfigureAwait(false);await _log.FlushAsync().ConfigureAwait(false);
        _settings.SaveFailed-=SettingsSaveFailed;
    }

    private sealed record FollowRecovery(HostDevice FailedDevice,string Name,string Kind);
    private sealed record FollowIntent(string[] MouseIds, int Target, long At,CodeRun Code,IReadOnlyDictionary<string,FollowRecovery>? Recoveries=null);
    private sealed record CodeRun(int Id,string Source,int Target)
    {
        public System.Diagnostics.Stopwatch Clock { get; }=System.Diagnostics.Stopwatch.StartNew();
    }
}

public sealed class DeviceChoice
{
    private readonly MainViewModel _owner;
    public DeviceInfo Info { get; }
    public bool IsSelected => _owner.IsSelectedDevice(Info.Id);
    public bool IsKeyboard => _owner.KnowsRole(Info.Id,"키보드");
    public string Status => Info.Online ? $"연결됨 · {(IsKeyboard ? "키보드" : Info.Kind)}" : "연결 안 됨";
    public string HomeStatus => !Info.Online ? "연결 안 됨" : IsKeyboard
        ? _owner.FollowEnabled ? "키보드: 전환 감지 중" : "키보드: 연결됨"
        : "마우스: 전환 준비됨";
    public ICommand UseAsKeyboardCommand { get; }
    public ICommand UseAsMouseCommand { get; }
    public ICommand ToggleCommand { get; }
    public DeviceChoice(MainViewModel owner, DeviceInfo info)
    {
        _owner = owner;
        Info = info;
        ToggleCommand = new RelayCommand(_ => Toggle());
        UseAsKeyboardCommand = new RelayCommand(_ => owner.KeyboardId = owner.KeyboardId == info.Id ? "" : info.Id);
        UseAsMouseCommand = new RelayCommand(_ => owner.MouseId = owner.MouseId == info.Id ? "" : info.Id);
    }
    public void Toggle()
    {
        if(IsKeyboard || _owner.KnowsRole(Info.Id,"마우스"))_owner.ToggleDevice(Info);
    }
}



public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}
