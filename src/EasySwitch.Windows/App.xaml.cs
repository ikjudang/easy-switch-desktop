using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using EasySwitch.Windows.Infrastructure;
using EasySwitch.Windows.Notices;
#if EASYSWITCH_FLOATING
using EasySwitch.Windows.Floating;
#endif
using EasySwitch.Windows.Service;
using Forms = System.Windows.Forms;

namespace EasySwitch.Windows;

public partial class App : Application
{
    private SingleInstance? _instance;
    private bool _ready, _exitAfterStartup;
    private AppLog? _log;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private MainWindow.ViewState? _mainViewState;
    private Rect? _mainBounds;
    private int _mainGeneration;
#if EASYSWITCH_FLOATING
    // The floating button ships only in release builds; the public source builds without it.
    private FloatingRuntime? _floating;
#endif
    private NoticePresenter? _notices;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private bool _exiting;
    private bool _english;
    private readonly ErrorDialogGate _errorDialogs=new();
    // Before the view model exists, the saved language decides the dialog language.
    private string T(string ko,string en)=>(_viewModel?.English ?? _english)?en:ko;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var startup=new StartupTiming();startup.Mark("RESOURCES");
        base.OnStartup(e);
        var memoryTest=e.Args.Contains("--memory-test");
        if(memoryTest || e.Args.Contains("--software-render-smoke"))System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
#if EASYSWITCH_FLOATING
        if(memoryTest || e.Args.Contains("--compact-drag-host-smoke"))FloatingController.CompactDragHostForMemoryProbe=true;
#endif
#if EASYSWITCH_DIAGNOSTICS
        if(e.Args.Contains("--service-startup-smoke")){Shutdown(await ServiceStartupFixture.RunAsync(e.Args));return;}
#endif
        if(e.Args.Contains("--service-check"))
        {
            var check=Array.IndexOf(e.Args,"--service-check");var folder=Path.GetFullPath(check+1<e.Args.Length?e.Args[check+1]:"artifacts/service-check");Directory.CreateDirectory(folder);
            try
            {
                using var log=new AppLog(Path.Combine(folder,"logs"));await using var client=new ServiceClient(log){CursorFile=null};
                var ready=new TaskCompletionSource<ServiceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);client.SnapshotReceived+=state=>ready.TrySetResult(state);client.Start();
                var snapshot=await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
                File.WriteAllText(Path.Combine(folder,"service.json"),System.Text.Json.JsonSerializer.Serialize(new{mode="Read-only service check; no channel commands",installation=ServiceRegistration.Installation,state=snapshot},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));Shutdown();
            }
            catch(Exception error){File.WriteAllText(Path.Combine(folder,"error.txt"),error.ToString());Shutdown(1);}
            return;
        }
        if(e.Args.Contains("--instance-fixture")) { RunInstanceFixture(e.Args); return; }
#if EASYSWITCH_DIAGNOSTICS
        if(e.Args.Contains("--resource-usage-smoke"))
        {
            var index=Array.IndexOf(e.Args,"--resource-usage-smoke");
            var directory=Path.GetFullPath(index+1<e.Args.Length?e.Args[index+1]:"artifacts/resource-usage");
            Directory.CreateDirectory(directory);
            try{await PreviewRenderer.ResourceUsageSmoke(directory);Shutdown();}
            catch(Exception error){File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());Shutdown(1);}
            return;
        }
        if(e.Args.Contains("--presentation-memory-smoke"))
        {
            var index=Array.IndexOf(e.Args,"--presentation-memory-smoke");
            var directory=Path.GetFullPath(index+1<e.Args.Length?e.Args[index+1]:"artifacts/presentation-memory");
            Directory.CreateDirectory(directory);
            try{await PreviewRenderer.PresentationMemorySmoke(directory);Shutdown();}
            catch(Exception error){File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());Shutdown(1);}
            return;
        }
        if(e.Args.Contains("--splash-memory-smoke"))
        {
            var index=Array.IndexOf(e.Args,"--splash-memory-smoke");
            var directory=Path.GetFullPath(index+1<e.Args.Length?e.Args[index+1]:"artifacts/splash-memory");
            Directory.CreateDirectory(directory);
            try{await PreviewRenderer.SplashMemorySmoke(directory);Shutdown();}
            catch(Exception error){File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());Shutdown(1);}
            return;
        }
        if(e.Args.Contains("--floating-canvas-memory-smoke"))
        {
            var index=Array.IndexOf(e.Args,"--floating-canvas-memory-smoke");
            var directory=Path.GetFullPath(index+1<e.Args.Length?e.Args[index+1]:"artifacts/floating-canvas-memory");
            Directory.CreateDirectory(directory);
            try{await PreviewRenderer.FloatingCanvasMemorySmoke(directory,e.Args.Contains("--animation-first-smoke"));Shutdown();}
            catch(Exception error){File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());Shutdown(1);}
            return;
        }
        if(e.Args.Contains("--floating-far-drag-smoke"))
        {
            var index=Array.IndexOf(e.Args,"--floating-far-drag-smoke");
            var directory=Path.GetFullPath(index+1<e.Args.Length?e.Args[index+1]:"artifacts/floating-far-drag");
            Directory.CreateDirectory(directory);
            try{await PreviewRenderer.FloatingFarDragSmoke(directory,e.Args.Contains("--always-show-smoke"),e.Args.Contains("--delete-mode-smoke"));Shutdown();}
            catch(Exception error){File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());Shutdown(1);}
            return;
        }
        if(e.Args.Contains("--animation-performance-smoke"))
        {
            var index=Array.IndexOf(e.Args,"--animation-performance-smoke");var directory=Path.GetFullPath(index+1<e.Args.Length?e.Args[index+1]:"artifacts/animation-performance");Directory.CreateDirectory(directory);
            try{await PreviewRenderer.AnimationPerformanceSmoke(directory);Shutdown();}catch(Exception error){File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());Shutdown(1);}return;
        }
        if(e.Args.Contains("--ui-isolation-smoke"))
        {
            var index=Array.IndexOf(e.Args,"--ui-isolation-smoke");var directory=Path.GetFullPath(index+1<e.Args.Length?e.Args[index+1]:"artifacts/ui-isolation");Directory.CreateDirectory(directory);
            try{await PreviewRenderer.UiIsolationSmoke(directory);Shutdown();}catch(Exception error){File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());Shutdown(1);}return;
        }
        if(e.Args.Contains("--floating-handoff-smoke"))
        {
            var index=Array.IndexOf(e.Args,"--floating-handoff-smoke");var directory=Path.GetFullPath(index+1<e.Args.Length?e.Args[index+1]:"artifacts/floating-handoff");Directory.CreateDirectory(directory);
            try{await PreviewRenderer.FloatingHandoffSmoke(directory);Shutdown();}catch(Exception error){File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());Shutdown(1);}return;
        }
        if(e.Args.Contains("--channel-rename-smoke"))
        {
            var index=Array.IndexOf(e.Args,"--channel-rename-smoke");var directory=Path.GetFullPath(index+1<e.Args.Length?e.Args[index+1]:"artifacts/channel-rename");Directory.CreateDirectory(directory);
            try{PreviewRenderer.ChannelRenameSmoke(directory);Shutdown();}catch(Exception error){File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());Shutdown(1);}return;
        }
        if(e.Args.Contains("--tray-settings-smoke"))
        {
            var index=Array.IndexOf(e.Args,"--tray-settings-smoke");var directory=Path.GetFullPath(index+1<e.Args.Length?e.Args[index+1]:"artifacts/tray-settings");Directory.CreateDirectory(directory);
            try{PreviewRenderer.TraySettingsSmoke(directory);Shutdown();}catch(Exception error){File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());Shutdown(1);}return;
        }
        if(e.Args.Contains("--log-toolbar-smoke"))
        {
            var index=Array.IndexOf(e.Args,"--log-toolbar-smoke");var directory=Path.GetFullPath(index+1<e.Args.Length?e.Args[index+1]:"artifacts/log-toolbar");
            Directory.CreateDirectory(directory);
            try{PreviewRenderer.LogToolbarSmoke(directory);Shutdown();}catch(Exception error){File.WriteAllText(Path.Combine(directory,"error.txt"),error.ToString());Shutdown(1);}return;
        }
        if(e.Args.Contains("--drawing-benchmark"))
        {
            var i=Array.IndexOf(e.Args,"--drawing-benchmark");
            FloatingBenchmark.Run(i+1<e.Args.Length?e.Args[i+1]:"artifacts/benchmark");Shutdown();return;
        }
        if (e.Args.Contains("--render") || e.Args.Contains("--overlay-smoke"))
        {
            var smoke=e.Args.Contains("--overlay-smoke");
            var index = Array.IndexOf(e.Args, smoke?"--overlay-smoke":"--render");
            var directory = index + 1 < e.Args.Length ? e.Args[index + 1] : Path.Combine(Environment.CurrentDirectory, "artifacts", "render");
            try { if(smoke)PreviewRenderer.NativeSmoke(Path.GetFullPath(directory),e.Args.Contains("--floating-only-smoke"));else PreviewRenderer.RenderAll(Path.GetFullPath(directory)); Shutdown(); }
            catch (Exception error)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "render-error.txt"), error.ToString());
                Shutdown(1);
            }
            return;
        }
#endif
        var lifecycle=e.Args.Contains("--main-lifecycle-smoke");
        var trayBaseline=e.Args.Contains("--tray-baseline-smoke");
        var packageMemory=e.Args.Contains("--package-memory-smoke");
        var startupIndex=Array.IndexOf(e.Args,packageMemory?"--package-memory-smoke":trayBaseline?"--tray-baseline-smoke":lifecycle?"--main-lifecycle-smoke":"--startup-smoke");
        var startupDirectory=startupIndex>=0?Path.GetFullPath(startupIndex+1<e.Args.Length?e.Args[startupIndex+1]:"artifacts/startup"):null;
        if(startupDirectory is null)
        {
            _instance = new SingleInstance();
            try
            {
                if (!_instance.Enter(AppVersion.Current,
                    () => Dispatcher.BeginInvoke(ShowMainWindow),
                    () => Dispatcher.BeginInvoke(() => { if(_ready)RequestExit();else _exitAfterStartup=true; })))
                { Shutdown(); return; }
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or WaitHandleCannotBeOpenedException)
            {
                // The exception text is diagnostic; it goes to a log file, the dialog says what to do.
                try{_english=new SettingsStore().Value.Language=="en";}catch(Exception){}
                try{using var log=new AppLog();log.Write($"실행 중인 앱 확인 실패로 시작 중단: {error}");}catch(Exception){}
                MessageBox.Show(T("이미 실행 중인 EasySwitch를 확인하지 못해 새로 시작하지 않았습니다. 트레이에서 EasySwitch를 종료한 뒤 다시 실행해 주세요.",
                    "EasySwitch did not start because it could not confirm the copy that is already running. Please quit EasySwitch from the tray and start it again."),
                    "Easy Switch",MessageBoxButton.OK,MessageBoxImage.Information);
                Shutdown(1);return;
            }
        }
        var settingsTask=Task.Run(()=>new SettingsStore(startupDirectory is null?null:Path.Combine(startupDirectory,"fixtures")));
        var logTask=Task.Run(()=>new AppLog(startupDirectory is null?null:Path.Combine(startupDirectory,"fixtures","logs")));
        var skipSplash=startupDirectory is not null && e.Args.Contains("--skip-splash-smoke");
        var splash=skipSplash?null:new StartupSplash();splash?.Show();
        async Task AdvanceSplash(double value)
        {
            if(splash is not null)await splash.AdvanceAsync(value);
            else await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }
        try
        {
            await AdvanceSplash(.08);
            startup.Mark("SPLASH");
            var settings = await settingsTask;
            _english=settings.Value.Language=="en";
            await AdvanceSplash(.22);
            _log = await logTask;startup.Mark("LOCAL");
            CrashRecorder.Attach(_log);
            if (settings.LoadError is { } settingsError) _log.Write($"이전 설정을 읽지 못해 {(settings.RestoredFromBackup?"백업 설정으로 시작함":"기본값 사용")}: {settingsError}");
            try { if(settings.Value.ExportEnabled && settings.Value.ExportDirectory is { } exportDirectory)await Task.Run(()=>_log.SetExportDirectory(exportDirectory)); }
            catch (Exception error) { _log.Write($"외부 로그 폴더 열기 실패: {error.Message}. 로컬 파일은 계속 기록함"); }
            DispatcherUnhandledException += (_, args) =>
            {
                try { _log.Write($"처리하지 못한 오류: {args.Exception}"); } catch (Exception) { }
                args.Handled = true;
                // The dialog's own message loop can raise more errors; every one is logged above.
                if (!_errorDialogs.TryEnter(args.Exception)) return;
                try { MessageBox.Show(T("앱에서 오류가 발생했습니다. 연결 로그에서 자세한 내용을 확인해 주세요.", "An error occurred in EasySwitch. Please check the connection log for details."), "Easy Switch", MessageBoxButton.OK, MessageBoxImage.Error); }
                finally { _errorDialogs.Exit(); }
            };
            await AdvanceSplash(.38);
            _viewModel = new MainViewModel(settings, _log, Dispatcher);
            _viewModel.EnableDeferredUiWork();
            _notices = new NoticePresenter(_viewModel);
            if(startupDirectory is not null)_viewModel.UsePreviewDevices([]);
            else
            {
                await ServiceRegistration.EnsureInstalledAsync(_log);
                if(ServiceRegistration.Installed)_viewModel.AttachService(new ServiceClient(_log));
            }
            _viewModel.Start();startup.Mark("MONITOR");
            await AdvanceSplash(.55);
            var trayStart=e.Args.Contains("--tray");
            if(!trayStart)EnsureMainWindow();
            startup.Mark("WINDOW");
            await AdvanceSplash(.70);
#if EASYSWITCH_FLOATING
            if(!(startupDirectory is not null && e.Args.Contains("--skip-floating-smoke") && !_viewModel.FloatingEnabled))
            {
                _floating = new FloatingRuntime(_viewModel,Dispatcher,_log,ShowMainWindow);
                _floating.SetMainWindow(_window);
            }
#endif
            CreateTray();
            await AdvanceSplash(.88);
            _ready=true;
            startup.Mark("SERVICES");
            await AdvanceSplash(1);
            if (!trayStart){_window!.ShowActivated=startupDirectory is null;_window.Show();}
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            startup.Mark("READY");
            if(_exitAfterStartup)RequestExit();
            if(startupDirectory is not null)
            {
                startup.Save(startupDirectory);
#if EASYSWITCH_DIAGNOSTICS
                if(packageMemory)_=Dispatcher.BeginInvoke(async()=>{try{await PackageMemoryFixture.RunAsync(e.Args,startupDirectory,_floating,_viewModel!);}catch(Exception error){File.WriteAllText(Path.Combine(startupDirectory,"error.txt"),error.ToString());}finally{RequestExit();}},System.Windows.Threading.DispatcherPriority.ContextIdle);
                else if(trayBaseline)_=Dispatcher.BeginInvoke(async()=>{try{await RunTrayBaselineSmoke(startupDirectory,skipSplash,e.Args.Contains("--tray-menu-probe"));}catch(Exception error){File.WriteAllText(Path.Combine(startupDirectory,"error.txt"),error.ToString());}finally{RequestExit();}},System.Windows.Threading.DispatcherPriority.ContextIdle);
                else if(lifecycle)_=Dispatcher.BeginInvoke(async()=>{try{await RunMainLifecycleSmoke(startupDirectory,e.Args.Contains("--keep-toast-off-smoke"));}catch(Exception error){File.WriteAllText(Path.Combine(startupDirectory,"error.txt"),error.ToString());}finally{RequestExit();}},System.Windows.Threading.DispatcherPriority.ContextIdle);
                else RequestExit();
#else
                RequestExit();
#endif
            }
        }
        catch (Exception error)
        {
            var logged = false;
            try { if (_log is not null) { _log.Write($"앱 시작 실패: {error}"); logged = true; } } catch (Exception) { }
            MessageBox.Show(logged
                ? T("앱을 시작하지 못했습니다. 로그 파일에서 자세한 내용을 확인한 뒤 앱을 다시 실행해 주세요.", "EasySwitch could not start. Please check the log file for details and start the app again.")
                : T("앱을 시작하지 못했습니다. 앱을 다시 실행해 주세요.", "EasySwitch could not start. Please start the app again."),
                "Easy Switch", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
        finally { splash?.Close(); if(_window is null && ReferenceEquals(MainWindow,splash))MainWindow=null; }
    }

    private void CreateTray()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/EasySwitch.ico")).Stream;
        _icon = new System.Drawing.Icon(stream);
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "Easy Switch", Visible = true };
        _tray.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                Dispatcher.Invoke(() => { if (_viewModel is not null) TrayPrimaryClick(_viewModel, ShowMainWindow); });
                return;
            }
            if (args.Button != Forms.MouseButtons.Right) return;
            Dispatcher.Invoke(OpenTrayMenu);
        };
    }
    // Left-click turns on the floating button. A build without it opens the main window instead.
    internal static void TrayPrimaryClick(MainViewModel viewModel, Action show)
    {
#if EASYSWITCH_FLOATING
        viewModel.FloatingEnabled = true;
#else
        show();
#endif
    }

    // Real process/mutex/event exercise with an isolated namespace. Never creates a view model,
    // HID monitor, tray, user settings or log files.
    private void RunInstanceFixture(string[] args)
    {
        var directory=Path.GetFullPath(args[Array.IndexOf(args,"--instance-fixture")+1]);Directory.CreateDirectory(directory);
        var versionIndex=Array.IndexOf(args,"--fixture-version");
        var version=versionIndex>=0?Version.Parse(args[versionIndex+1]):AppVersion.Current;
        var scope="EasySwitch.Fixture."+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..24];
        try
        {
            _instance=new SingleInstance(scope,directory);
            var entered=_instance.Enter(version,
                ()=>Dispatcher.BeginInvoke(()=>File.AppendAllText(Path.Combine(directory,"activated.txt"),Environment.ProcessId+"\n")),
                ()=>Dispatcher.BeginInvoke(()=>{File.WriteAllText(Path.Combine(directory,$"stopped-{Environment.ProcessId}.txt"),"graceful");RequestExit();}));
            File.WriteAllText(Path.Combine(directory,$"launch-{Environment.ProcessId}.json"),System.Text.Json.JsonSerializer.Serialize(new{pid=Environment.ProcessId,version=version.ToString(),entered,_instance.Messages}));
            if(!entered){Shutdown();return;}
            var timeout=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(45)};
            timeout.Tick+=(_,_)=>{timeout.Stop();RequestExit();};timeout.Start();
        }
        catch(Exception error){File.WriteAllText(Path.Combine(directory,$"error-{Environment.ProcessId}.txt"),error.ToString());Shutdown(1);}
    }

    private MainWindow EnsureMainWindow()
    {
        if(_window is not null)return _window;
        var window=new MainWindow();
        window.RestoreViewState(_mainViewState);
        if(_mainBounds is { } bounds && !bounds.IsEmpty)
        {
            window.WindowStartupLocation=WindowStartupLocation.Manual;
            window.Left=bounds.Left;window.Top=bounds.Top;window.Width=bounds.Width;window.Height=bounds.Height;
        }
        var generation=++_mainGeneration;
        window.CloseToTrayRequested+=()=>Dispatcher.BeginInvoke(()=>ReleaseMainWindow(generation));
        window.DataContext=_viewModel;_window=window;MainWindow=window;
        new WindowInteropHelper(window).EnsureHandle();
#if EASYSWITCH_FLOATING
        _floating?.SetMainWindow(window);
#endif
        return window;
    }
    private void ShowMainWindow()
    {
        if(_exiting || _viewModel is null)return;
        EnsureMainWindow().ShowFromTray();
    }
    private void ReleaseMainWindow(int generation)
    {
        if(_exiting || generation!=_mainGeneration || _window is not {} window)return;
        var bounds=window.RestoreBounds;
        if(!bounds.IsEmpty && bounds.Width>0 && bounds.Height>0)_mainBounds=bounds;
        _mainViewState=window.CaptureViewState();
#if EASYSWITCH_FLOATING
        _floating?.SetMainWindow(null);
#endif
        System.Windows.Input.Keyboard.ClearFocus();System.Windows.Input.FocusManager.SetFocusedElement(window,null);
        window.DataContext=null;window.AllowClose=true;
        _window=null;MainWindow=null;window.Close();
    }
    private void OpenTrayMenu()
    {
        if (_viewModel is null) return;
        var menu=BuildTrayMenu(_viewModel,ShowMainWindow,RequestExit);
        if(_window is not null)
        {
            SetForegroundWindow(new WindowInteropHelper(_window).Handle);
            menu.PlacementTarget=_window;
        }
        // A click elsewhere dismisses the menu only while its own window is in the foreground.
        // The usual tray state has no main window, so bring the menu's window forward itself.
        menu.Opened+=(_,_)=>{if(PresentationSource.FromVisual(menu) is HwndSource source)SetForegroundWindow(source.Handle);};
        menu.IsOpen=true;
    }

    internal static ContextMenu BuildTrayMenu(MainViewModel viewModel,Action show,Action exit)
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, Padding = new Thickness(4) };
        var itemStyle=(Style)Current.FindResource("CompactTrayItem");
        void Add(string label, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled, Style=itemStyle };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add(viewModel.T("EasySwitch 열기","Open EasySwitch"), show);
        menu.Items.Add(new Separator());
        for (var target = 0; target < 3; target++)
        {
            var captured = target;
            Add(viewModel.T($"채널 {target + 1}로 전환",$"Switch to channel {target+1}"), () => viewModel.RequestSwitch(captured, SwitchRequestSource.Tray), viewModel.CanSwitch);
        }
        menu.Items.Add(new Separator());
#if EASYSWITCH_FLOATING
        var floating=new MenuItem{Header=viewModel.FloatingEnabled?viewModel.T("플로팅 버튼 끄기","Turn floating button off"):viewModel.T("플로팅 버튼 켜기","Turn floating button on"),Name="FloatingToggle",Style=itemStyle};
        floating.Click+=(_,_)=>viewModel.FloatingEnabled=!viewModel.FloatingEnabled;
        menu.Items.Add(floating);
#endif
        var follow = new MenuItem { Header = viewModel.T("키보드를 따라 마우스 전환","Follow keyboard with mouse"), IsCheckable = true, IsChecked = viewModel.FollowEnabled, Style=itemStyle };
        follow.Click += (_, _) => viewModel.FollowEnabled = follow.IsChecked;
        menu.Items.Add(follow);
        Add(viewModel.T("연결 로그","Connection log"), () => { viewModel.NavigateCommand.Execute("연결 로그"); show(); });
        Add(viewModel.T("로그인 전 실행 설치·업데이트","Install or update boot service"),async()=>{try{await viewModel.InstallBootServiceAsync();}catch(Exception error){viewModel.ReportNoticeError(error);}});
        menu.Items.Add(new Separator());
        Add(viewModel.T("EasySwitch 종료","Quit EasySwitch"), exit);
        menu.Closed += (_, _) => menu.Items.Clear();
        return menu;
    }

    public async void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
#if EASYSWITCH_FLOATING
        var floating=_floating;_floating=null;
#endif
        var notices=_notices;_notices=null;
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        try
        {
#if EASYSWITCH_FLOATING
            if(floating is not null)await floating.DisposeAsync();
#endif
            if(notices is not null)await notices.DisposeAsync();
            if (_viewModel is not null) await _viewModel.DisposeAsync();
        }
        catch (Exception error) { _log?.Write($"앱 종료 처리 오류: {error}"); }
        finally { if (_window is not null) _window.AllowClose = true; Shutdown(); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
#if EASYSWITCH_FLOATING
        _floating?.Dispose();
#endif
        _notices?.Dispose();
        _tray?.Dispose();
        _icon?.Dispose();
        _log?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _viewModel?.FlushServiceSettings();base.OnSessionEnding(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}

// One error dialog at a time, once per kind of failure. A dialog runs a nested message loop that
// can raise the same failure again; stacked or repeated dialogs would bury the app.
internal sealed class ErrorDialogGate
{
    private readonly HashSet<string> _shown=[];
    private bool _open;
    internal bool TryEnter(Exception error)
    {
        if(_open || !_shown.Add(error.GetType().FullName+"|"+error.TargetSite))return false;
        _open=true;return true;
    }
    internal void Exit()=>_open=false;
}
