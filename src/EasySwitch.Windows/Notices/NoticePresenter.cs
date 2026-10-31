using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasySwitch.Windows.Controls;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Notices;

internal sealed record ToastPreferences(bool Enabled,int Size,int Transparency)
{
    public static ToastPreferences From(AppSettings settings)=>new(settings.ToastEnabled,settings.ToastSize,settings.ToastTransparency);
    public double Scale=>Size/100d;
    public double Opacity=>1-Transparency/100d;
}

// Each toast surface owns a dispatcher, independent of floating input and rendering.
internal sealed class NoticePresenter : IDisposable,IAsyncDisposable
{
    private readonly MainViewModel _vm;
    private readonly ConcurrentQueue<Action<Surface>> _pending=[];
    private readonly TaskCompletionSource<Surface> _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _scheduled;
    private volatile bool _disposed;
    public NoticePresenter(MainViewModel vm)
    {
        _vm=vm;vm.NoticeRequested+=Show;vm.ConnectionNoticeRequested+=ShowConnection;vm.ToastSettingsChanged+=Configure;
        var english=vm.English;var names=Names();var preferences=ToastPreferences.From(vm.Preferences);
        var thread=new Thread(()=>
        {
            Surface? surface=null;
            try{Dispatcher.CurrentDispatcher.UnhandledException+=(_,e)=>{Report(e.Exception);e.Handled=true;};surface=new Surface(preferences);try{surface.Prepare(english,names);}catch(Exception error){Report(error);}_ready.SetResult(surface);Dispatcher.Run();}
            catch(Exception error){_ready.TrySetException(error);Report(error);}
            finally{try{surface?.Dispose();}catch(Exception error){Report(error);}_stopped.TrySetResult();}
        }){IsBackground=true,Name="EasySwitch toasts"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        Queue(_=>{});
    }
    private void Report(Exception error){try{_vm.ReportNoticeError(error);}catch(ObjectDisposedException){}}
    private void Queue(Action<Surface> action)
    {
        if(_disposed)return;_pending.Enqueue(action);Schedule();
    }
    private void Schedule()
    {
        if(Interlocked.Exchange(ref _scheduled,1)!=0)return;
        _=_ready.Task.ContinueWith(task=>
        {
            if(task.IsCompletedSuccessfully && !task.Result.Dispatcher.HasShutdownStarted)
                task.Result.Dispatcher.BeginInvoke(Drain,DispatcherPriority.Background);
        },TaskScheduler.Default);
    }
    private void Drain()
    {
        var surface=_ready.Task.Result;
        while(_pending.TryDequeue(out var action))
            if(!_disposed)try{action(surface);}catch(Exception error){Report(error);}
        Interlocked.Exchange(ref _scheduled,0);
        if(!_pending.IsEmpty && !_disposed)Schedule();
    }
    private string[] Names()=>_vm.Preferences.SelectedDeviceNames.Select(d=>d.Name).ToArray();
    private void Configure()
    {
        var preferences=ToastPreferences.From(_vm.Preferences);Queue(surface=>surface.Configure(preferences));
    }
    internal void Show(SwitchNotice notice)
    {
        var snapshot=notice with{Devices=notice.Devices.ToArray()};var english=_vm.English;var names=Names();
        Queue(surface=>surface.Show(snapshot,english,names));
    }
    internal void ShowConnection(SwitchNotice notice)
    {
        var snapshot=notice with{Devices=notice.Devices.ToArray()};var english=_vm.English;var names=Names();
        Queue(surface=>surface.ShowConnection(snapshot,english,names));
    }
    internal static FrameworkElement CreateBubble(SwitchNotice notice,bool english)
    {
        var bubble=new NoticeBubble();bubble.Update(notice,english,notice.Devices.Count>0 && notice.Devices.All(d=>d.State==DeviceNoticeState.Connected),[],600);return bubble;
    }
    internal void FlushForFixture()=>_ready.Task.GetAwaiter().GetResult().Dispatcher.Invoke(Drain);
    internal sealed record PresentationProbe(int Thread,int? Run,bool Visible)
    {
        public int Size{get;init;}
        public double Opacity{get;init;}
        public Rect? Bounds{get;init;}
        public bool ConnectionVisible{get;init;}
        public int WindowCount{get;init;}
    }
    internal async Task<PresentationProbe> ProbeAsync()
    {var surface=await _ready.Task.ConfigureAwait(false);return await surface.Dispatcher.InvokeAsync(surface.Probe);}
    internal Task RunForFixture(Action action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue(_=>{try{action();done.TrySetResult();}catch(Exception error){done.TrySetException(error);}});return done.Task;
    }
    internal object CheckPresentation()
    {
        var caller=Environment.CurrentManagedThreadId;var surface=_ready.Task.GetAwaiter().GetResult();
        var result=surface.Dispatcher.Invoke(surface.CheckPresentation);
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        Queue(_=>{entered.Set();release.Wait(TimeSpan.FromSeconds(3));});
        if(!entered.Wait(TimeSpan.FromSeconds(3)))throw new InvalidOperationException("Toast dispatcher did not start");
        var clock=Stopwatch.StartNew();
        try{Show(new("채널 2 전환",[new("MX Master 4",null,"마우스","m",DeviceNoticeState.CommandSent)],20));}
        finally{release.Set();}
        var enqueueMs=clock.Elapsed.TotalMilliseconds;FlushForFixture();
        if(surface.Dispatcher.Thread.ManagedThreadId==caller || enqueueMs>=100)throw new InvalidOperationException("Toast work blocked the caller dispatcher");
        return new{separateDispatcher=true,enqueueWhileWorkerBusyMs=enqueueMs,presentation=result};
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_vm.NoticeRequested-=Show;_vm.ConnectionNoticeRequested-=ShowConnection;_vm.ToastSettingsChanged-=Configure;
        _=_ready.Task.ContinueWith(task=>
        {
            if(task.IsCompletedSuccessfully && !task.Result.Dispatcher.HasShutdownStarted)
                task.Result.Dispatcher.BeginInvoke(()=>task.Result.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background));
        },TaskScheduler.Default);
    }
    public async ValueTask DisposeAsync(){Dispose();await _stopped.Task.ConfigureAwait(false);}
    private sealed class Slot
    {
        public readonly NoticeBubble Bubble=new();
        public readonly OverlayWindow Window=new(true){Opacity=0};
        public NativeRect? Bounds;
        public int Run=-1;
        public bool English;
        public double Scale=1;
        public Slot(){Window.Content=Bubble;}
    }
    private sealed class Surface : IDisposable
    {
        public Dispatcher Dispatcher {get;}=Dispatcher.CurrentDispatcher;
        private readonly DispatcherTimer _hide=new(){Interval=TimeSpan.FromSeconds(2)};
        private readonly DispatcherTimer _connectionHide=new(){Interval=TimeSpan.FromSeconds(2)};
        private readonly Dictionary<string,Slot> _windows=[],_connections=[];
        private SwitchNotice? _active,_latest,_connected;
        private bool _english,_disposed;
        private ToastPreferences _preferences;
        private int _suppressedRun=-1;
        private string[] _names=[];
        public PresentationProbe Probe()
        {
            var slot=_windows.Values.FirstOrDefault(slot=>slot.Bounds is not null);
            Rect? bounds=null;
            if(slot is not null && GetWindowRect(new WindowInteropHelper(slot.Window).Handle,out var native))bounds=new Rect(native.Left,native.Top,native.Right-native.Left,native.Bottom-native.Top);
            return new(Environment.CurrentManagedThreadId,_active?.RunId,_windows.Values.Any(slot=>slot.Window.IsVisible && slot.Window.Opacity>0))
            {
                Size=_preferences.Size,Opacity=slot?.Window.Opacity??0,Bounds=bounds,
                ConnectionVisible=_connections.Values.Any(slot=>slot.Window.IsVisible && slot.Window.Opacity>0),
                WindowCount=_windows.Count+_connections.Count,
            };
        }
        public Surface(ToastPreferences preferences)
        {
            _preferences=preferences;
            _hide.Tick+=(_,_)=>{_hide.Stop();_active=null;PresentActive();};
            _connectionHide.Tick+=(_,_)=>{_connectionHide.Stop();_connected=null;PresentActive();};
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged+=DisplayChanged;
        }
        public void Prepare(bool english,string[] names)
        {
            if(!_preferences.Enabled)return;
            _english=english;_names=names;
            if(_windows.Count!=0 || _connections.Count!=0)return;
            // Pay HWND, font and first-drawing costs before a channel click needs them.
            // This dispatcher runs independently; startup does not wait for the warm-up.
            foreach(var screen in System.Windows.Forms.Screen.AllScreens)
            foreach(var connection in new[]{false,true})
            {
                var slots=connection?_connections:_windows;var slot=new Slot();slots.Add(screen.DeviceName,slot);
                var area=screen.WorkingArea;slot.Window.Place(area.Left,area.Top,1,1);slot.Window.Show();
                var dpi=VisualTreeHelper.GetDpi(slot.Window);
                ApplyScale(slot);
                var rows=new[]{new DeviceResult(names.ElementAtOrDefault(0)??"Keyboard",null,"키보드","warm-k",connection?DeviceNoticeState.Connected:DeviceNoticeState.SignalObserved),new DeviceResult(names.ElementAtOrDefault(1)??"Mouse",null,"마우스","warm-m",connection?DeviceNoticeState.Connected:DeviceNoticeState.Switching)};
                slot.Bubble.Update(new(english?"Switch to channel 2":"채널 2 전환",rows,-1),english,connection,names,(area.Width/dpi.DpiScaleX-48)/_preferences.Scale);
                slot.Bubble.Measure(new(double.PositiveInfinity,double.PositiveInfinity));slot.Bubble.Arrange(new Rect(slot.Bubble.DesiredSize));slot.Bubble.UpdateLayout();
                var size=slot.Bubble.DesiredSize;
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(size.Width*dpi.DpiScaleX),(int)Math.Ceiling(size.Height*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(slot.Bubble);
            }
        }
        private void DisplayChanged(object? sender,EventArgs args)=>Dispatcher.BeginInvoke(()=>{if(!_disposed)PresentActive(true);});
        public void Configure(ToastPreferences preferences)
        {
            if(_disposed || preferences==_preferences)return;
            var resized=preferences.Size!=_preferences.Size;
            var previouslyEnabled=_preferences.Enabled;
            _preferences=preferences;
            if(!preferences.Enabled)
            {
                _suppressedRun=Math.Max(_suppressedRun,_latest?.RunId??-1);
                _hide.Stop();_connectionHide.Stop();_active=null;_connected=null;
                ReleaseWindows();
                return;
            }
            if(!previouslyEnabled)Prepare(_english,_names);
            PresentActive(resized);
        }
        private void ReleaseWindows()
        {
            foreach(var slot in _windows.Values.Concat(_connections.Values))slot.Window.Close();
            _windows.Clear();_connections.Clear();
        }
        private void ApplyScale(Slot slot)
        {
            if(slot.Scale==_preferences.Scale)return;
            slot.Scale=_preferences.Scale;slot.Bubble.LayoutTransform=new ScaleTransform(slot.Scale,slot.Scale);
        }
        public void Show(SwitchNotice notice,bool english,string[] names)
        {
            if(!_preferences.Enabled){_suppressedRun=Math.Max(_suppressedRun,notice.RunId);return;}
            if(notice.RunId<=_suppressedRun)return;
            if(_disposed || (_latest is {} latest && notice.RunId<latest.RunId))return;
            _english=english;_names=names;_latest=SwitchNotice.Merge(_latest,notice);_active=_latest;
            PresentActive();_hide.Stop();_hide.Start();
        }
        public void ShowConnection(SwitchNotice notice,bool english,string[] names)
        {
            if(_disposed || !_preferences.Enabled || notice.Devices.Count==0)return;
            _english=english;_names=names;_connected=SwitchNotice.Merge(_connected,notice with{RunId=_connected?.RunId??notice.RunId});
            PresentActive();_connectionHide.Stop();_connectionHide.Start();
        }
        private void PresentActive(bool displayChanged=false)
        {
            if(_active is {} active)Present(active,_windows,false,displayChanged);else foreach(var slot in _windows.Values)slot.Window.Hide();
            if(_connected is {} connected)Present(connected,_connections,true,displayChanged);else foreach(var slot in _connections.Values)slot.Window.Hide();
        }
        private void Present(SwitchNotice notice,Dictionary<string,Slot> slots,bool connection,bool displayChanged)
        {
            var screens=System.Windows.Forms.Screen.AllScreens;
            foreach(var removed in slots.Keys.Except(screens.Select(s=>s.DeviceName)).ToArray()){slots[removed].Window.Close();slots.Remove(removed);}
            foreach(var screen in screens)
            {
                var area=screen.WorkingArea;
                if(!slots.TryGetValue(screen.DeviceName,out var slot))
                {
                    slot=new();slots.Add(screen.DeviceName,slot);slot.Window.Place(area.Left,area.Top,1,1);slot.Window.Show();
                }
                var dpi=VisualTreeHelper.GetDpi(slot.Window).DpiScaleX;
                ApplyScale(slot);
                var resized=slot.Bubble.Update(notice,_english,connection,_names,(area.Width/dpi-48)/_preferences.Scale);
                var fresh=slot.Run!=notice.RunId || slot.English!=_english || displayChanged;slot.Run=notice.RunId;slot.English=_english;
                var below=connection && _active is not null && _windows.TryGetValue(screen.DeviceName,out var switching) && switching.Bounds is {} switchBounds
                    ?switchBounds.Bottom-switchBounds.Top+(int)Math.Round(12*dpi*_preferences.Scale):0;
                if(resized || fresh || connection)
                {
                    slot.Bubble.Measure(new(double.PositiveInfinity,double.PositiveInfinity));
                    var width=(int)Math.Ceiling(slot.Bubble.DesiredSize.Width*dpi);var height=(int)Math.Ceiling(slot.Bubble.DesiredSize.Height*dpi);
                    var x=area.Left+(area.Width-width)/2;
                    var top=area.Bottom-(int)Math.Round(64*dpi)-height-below;
                    if(connection && _active is not null && _windows.TryGetValue(screen.DeviceName,out var switchingSlot) && switchingSlot.Bounds is {} switchingBounds)
                        top=switchingBounds.Top-(int)Math.Round(12*dpi*_preferences.Scale)-height;
                    if(!connection && !fresh && slot.Bounds is {} previous){x=previous.Left;top=Math.Min(previous.Top,area.Bottom-(int)Math.Round(8*dpi)-height);}
                    slot.Window.Place(x,top,width,height);slot.Bounds=new(){Left=x,Top=top,Right=x+width,Bottom=top+height};
                }
                if(!slot.Window.IsVisible)slot.Window.Show();slot.Window.Opacity=_preferences.Opacity;
            }
        }
        internal object CheckPresentation()
        {
            const int run=10;
            Show(new("채널 2 전환",[new("MX Mechanical",null,"키보드","k",DeviceNoticeState.SignalObserved)],run),false,["MX Mechanical","MX Master 4"]);
            var before=_windows.ToDictionary(p=>p.Key,p=>(Bounds:p.Value.Bounds!.Value,Placements:p.Value.Window.PlacementChanges,Content:p.Value.Window.Content,IconCenter:p.Value.Bubble.IconCenterY));
            Show(new("채널 2 전환",[new("MX Mechanical",true,"키보드","k",DeviceNoticeState.Confirmed)],run),false,[]);
            var statusPlacements=_windows.Sum(p=>p.Value.Window.PlacementChanges-before[p.Key].Placements);
            Show(new("채널 2 전환",[new("MX Master 4",null,"마우스","m",DeviceNoticeState.CommandSent)],run),false,[]);
            var addedPlacements=_windows.Sum(p=>p.Value.Window.PlacementChanges-before[p.Key].Placements);
            var checks=_windows.Select(p=>
            {
                var prior=before[p.Key].Bounds;var next=p.Value.Bounds!.Value;var area=System.Windows.Forms.Screen.AllScreens.Single(s=>s.DeviceName==p.Key).WorkingArea;
                var fixedHeader=prior.Top==next.Top && prior.Left==next.Left && prior.Right==next.Right && next.Bottom>prior.Bottom;
                var retained=ReferenceEquals(before[p.Key].Content,p.Value.Window.Content);
                var centered=Math.Abs(p.Value.Bubble.IconCenterY-(8*NoticeBubble.DisplayScale+p.Value.Bubble.BodyHeight/2))<.001;
                var iconMovedDown=p.Value.Bubble.IconCenterY>before[p.Key].IconCenter;
                var passThrough=(GetWindowLongPtr(new WindowInteropHelper(p.Value.Window).Handle,-20).ToInt64()&0x08000020)==0x08000020;
                if(!fixedHeader || !retained || !centered || !iconMovedDown || !passThrough || Math.Abs((next.Left+next.Right-area.Left-area.Right)/2d)>1)throw new InvalidOperationException("Toast layout or pass-through changed");
                return new{monitor=p.Key,fixedHeader,retained,centered,iconMovedDown,passThrough};
            }).ToArray();
            Show(new("채널 2 전환",[new("MX Master 4",null,"마우스","m",DeviceNoticeState.Departed)],run),false,[]);
            var localComplete=_windows.Values.All(s=>s.Bubble.DisplayedStatuses.Contains("완료")) && _latest!.Devices.Single(d=>d.Id=="m").Success is null;
            Show(new("old",[new("old",false)],run-1),false,[]);var staleIgnored=_latest?.RunId==run;
            ShowConnection(new("기기 연결됨",[new("MX Mechanical",null,"키보드","k",DeviceNoticeState.Connected)],1),false,[]);
            var stacked=_connections.All(p=>p.Value.Bounds!.Value.Bottom+(int)Math.Round(12*VisualTreeHelper.GetDpi(p.Value.Window).DpiScaleX)==_windows[p.Key].Bounds!.Value.Top);
            if(statusPlacements!=0 || addedPlacements!=_windows.Count || !localComplete || !staleIgnored || !stacked)throw new InvalidOperationException($"Toast update policy failed: status={statusPlacements}, added={addedPlacements}, monitors={_windows.Count}, complete={localComplete}, stale={staleIgnored}, stacked={stacked}");
            return new{statusPlacements,addedPlacements,localStepCompleteWithoutDestinationClaim=localComplete,staleIgnored,stacked,monitors=checks};
        }
        public void Dispose()
        {
            if(_disposed)return;_disposed=true;_hide.Stop();_connectionHide.Stop();Microsoft.Win32.SystemEvents.DisplaySettingsChanged-=DisplayChanged;
            ReleaseWindows();
        }
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetWindowRect(nint window,out NativeRect rect);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr(nint hwnd,int index);
}
