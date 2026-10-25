using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using EasySwitch.Windows.Infrastructure;
using EasySwitch.Windows.Floating;
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
    private FloatingRuntime? _floating;
    private NoticePresenter? _notices;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var startup=new StartupTiming();startup.Mark("RESOURCES");
        base.OnStartup(e);
        if(e.Args.Contains("--install-service"))
        {var setup=Array.IndexOf(e.Args,"--service-setup");Shutdown(ServiceInstaller.Install(setup>=0 && setup+1<e.Args.Length?e.Args[setup+1]:null));return;}
        if(e.Args.Contains("--uninstall-service")){Shutdown(ServiceInstaller.Uninstall());return;}
        if(e.Args.Contains("--service"))
        {
            try{System.ServiceProcess.ServiceBase.Run(new EasySwitchDeviceService());Shutdown();}
            catch(Exception error){try{Directory.CreateDirectory(ServiceRegistration.Root);File.WriteAllText(Path.Combine(ServiceRegistration.Root,"host-error.txt"),error.ToString());}catch(Exception){}Shutdown(1);}
            return;
        }
        if(e.Args.Contains("--service-check"))
        {
            var check=Array.IndexOf(e.Args,"--service-check");var folder=Path.GetFullPath(check+1<e.Args.Length?e.Args[check+1]:"artifacts/service-check");Directory.CreateDirectory(folder);
            try
            {
                using var log=new AppLog(Path.Combine(folder,"logs"));await using var client=new ServiceClient(log);
                var ready=new TaskCompletionSource<ServiceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);client.SnapshotReceived+=state=>ready.TrySetResult(state);client.Start();
                var snapshot=await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
                File.WriteAllText(Path.Combine(folder,"service.json"),System.Text.Json.JsonSerializer.Serialize(new{mode="Read-only service check; no channel commands",installation=ServiceRegistration.Installation,state=snapshot},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));Shutdown();
            }
            catch(Exception error){File.WriteAllText(Path.Combine(folder,"error.txt"),error.ToString());Shutdown(1);}
            return;
        }
        if(e.Args.Contains("--instance-fixture")) { RunInstanceFixture(e.Args); return; }
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
            try { if(smoke)PreviewRenderer.NativeSmoke(Path.GetFullPath(directory));else PreviewRenderer.RenderAll(Path.GetFullPath(directory)); Shutdown(); }
            catch (Exception error)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "render-error.txt"), error.ToString());
                Shutdown(1);
            }
            return;
        }
        var startupIndex=Array.IndexOf(e.Args,"--startup-smoke");
        var startupDirectory=startupIndex>=0?Path.GetFullPath(startupIndex+1<e.Args.Length?e.Args[startupIndex+1]:"artifacts/startup"):null;
        if(startupDirectory is null)
        {
            _instance = new SingleInstance();
            try
            {
                if (!_instance.Enter(AppVersion.Current,
                    () => Dispatcher.BeginInvoke(() => _window?.ShowFromTray()),
                    () => Dispatcher.BeginInvoke(() => { if(_ready)RequestExit();else _exitAfterStartup=true; })))
                { Shutdown(); return; }
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or WaitHandleCannotBeOpenedException)
            { MessageBox.Show(error.Message,"Easy Switch",MessageBoxButton.OK,MessageBoxImage.Information);Shutdown(1);return; }
        }
        var settingsTask=Task.Run(()=>new SettingsStore(startupDirectory is null?null:Path.Combine(startupDirectory,"fixtures")));
        var logTask=Task.Run(()=>new AppLog(startupDirectory is null?null:Path.Combine(startupDirectory,"fixtures","logs")));
        var splash=new StartupSplash();splash.Show();
        try
        {
            await splash.AdvanceAsync(.08);
            startup.Mark("SPLASH");
            var settings = await settingsTask;
            await splash.AdvanceAsync(.22);
            _log = await logTask;startup.Mark("LOCAL");
            _log.Write($"EasySwitch Windows v{AppVersion.Label} 시작 · HID++ software id 0x09");
            _log.WriteCode($"{SwitchCode.Prefix} APP GFX G{System.Windows.Media.RenderCapability.Tier>>16}");
            if(_instance is not null)foreach(var message in _instance.Messages)_log.Write(message);
            if (settings.LoadError is { } settingsError) _log.Write($"이전 설정을 읽지 못해 기본값 사용: {settingsError}");
            try { if(settings.Value.ExportEnabled && settings.Value.ExportDirectory is { } exportDirectory)await Task.Run(()=>_log.SetExportDirectory(exportDirectory)); }
            catch (Exception error) { _log.Write($"외부 로그 폴더 열기 실패: {error.Message}. 로컬 파일은 계속 기록함"); }
            DispatcherUnhandledException += (_, args) =>
            {
                try { _log.Write($"처리하지 못한 오류: {args.Exception}"); } catch (Exception) { }
                args.Handled = true;
                MessageBox.Show("앱에서 오류가 발생했어. 연결 로그 파일에서 자세한 내용을 확인해 줘.", "Easy Switch", MessageBoxButton.OK, MessageBoxImage.Error);
            };
            await splash.AdvanceAsync(.38);
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
            await splash.AdvanceAsync(.55);
            _viewModel.ExitRequested += RequestExit;
            _window = new MainWindow { DataContext = _viewModel };
            MainWindow = _window;
            new WindowInteropHelper(_window).EnsureHandle();startup.Mark("WINDOW");
            await splash.AdvanceAsync(.70);
            _floating = new FloatingRuntime(_viewModel, _window, _log);
            CreateTray();
            await splash.AdvanceAsync(.88);
            _ready=true;
            startup.Mark("SERVICES");
            await splash.AdvanceAsync(1);
            if (!e.Args.Contains("--tray")){_window.ShowActivated=startupDirectory is null;_window.Show();}
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            startup.Mark("READY");_log.WriteCode($"{SwitchCode.Prefix} APP UI E{startup.Elapsed}");
            if(_exitAfterStartup)RequestExit();
            if(startupDirectory is not null){startup.Save(startupDirectory);RequestExit();}
        }
        catch (Exception error)
        {
            try { _log?.Write($"앱 시작 실패: {error}"); } catch (Exception) { }
            MessageBox.Show($"앱을 시작하지 못했어.\n{error.Message}", "Easy Switch", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
        finally { splash.Close(); }
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
                Dispatcher.Invoke(() => { if (_viewModel is not null) _viewModel.FloatingEnabled = true; });
                return;
            }
            if (args.Button != Forms.MouseButtons.Right) return;
            Dispatcher.Invoke(OpenTrayMenu);
        };
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

    private void OpenTrayMenu()
    {
        if (_viewModel is null || _window is null) return;
        var menu = BuildTrayMenu(_viewModel,_window.ShowFromTray,RequestExit);
        // A foreground owner lets Windows dismiss this menu when the user clicks elsewhere.
        SetForegroundWindow(new WindowInteropHelper(_window).Handle);
        menu.PlacementTarget = _window;
        menu.IsOpen = true;
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
            Add(viewModel.T($"채널 {target + 1}로 전환",$"Switch to channel {target+1}"), () => _ = viewModel.SwitchAsync(captured, "트레이 채널 버튼"), viewModel.CanSwitch);
        }
        menu.Items.Add(new Separator());
        var floating=new MenuItem{Header=viewModel.FloatingEnabled?viewModel.T("플로팅 버튼 끄기","Turn floating button off"):viewModel.T("플로팅 버튼 켜기","Turn floating button on"),Name="FloatingToggle",Style=itemStyle};
        floating.Click+=(_,_)=>viewModel.FloatingEnabled=!viewModel.FloatingEnabled;
        menu.Items.Add(floating);
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
        var floating=_floating;_floating=null;var notices=_notices;_notices=null;
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        try
        {
            if(floating is not null)await floating.DisposeAsync();
            if(notices is not null)await notices.DisposeAsync();
            if (_viewModel is not null) await _viewModel.DisposeAsync();
            _log?.Write("Easy Switch 정상 종료");
        }
        catch (Exception error) { _log?.Write($"앱 종료 처리 오류: {error}"); }
        finally { if (_window is not null) _window.AllowClose = true; Shutdown(); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _floating?.Dispose();
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
