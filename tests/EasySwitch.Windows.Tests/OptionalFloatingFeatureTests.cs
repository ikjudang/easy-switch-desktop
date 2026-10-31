using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

// Creates the single WPF Application of the test process, so it runs apart from parallel tests.
[CollectionDefinition(nameof(ApplicationResourcesCollection), DisableParallelization = true)]
public sealed class ApplicationResourcesCollection;

// The public source builds without the floating button. These checks run in both builds:
// the release source must keep every floating entry point, the public source must offer none.
[Collection(nameof(ApplicationResourcesCollection))]
public sealed class OptionalFloatingFeatureTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));
#if EASYSWITCH_FLOATING
    private static readonly bool FloatingBuild=true;
#else
    private static readonly bool FloatingBuild=false;
#endif

    [Fact]
    public void BuildSymbolMatchesTheCompiledApp()
    {
        var floatingTypes=typeof(App).Assembly.GetTypes().Where(type=>type.Namespace=="EasySwitch.Windows.Floating").ToArray();
        Assert.Equal(FloatingBuild,floatingTypes.Length>0);
        // Toasts, the logo artwork, overlay windows and the frame clock are shared, so every build has them.
        Assert.NotNull(typeof(App).Assembly.GetType("EasySwitch.Windows.Notices.NoticePresenter"));
        Assert.NotNull(typeof(App).Assembly.GetType("EasySwitch.Windows.Controls.OverlayWindow"));
    }

    [Fact]
    public Task TrayLeftClickTurnsOnFloatingOnlyWhenItIsBuilt()=>Sta(async()=>
    {
        var store=new SettingsStore(_directory);using var log=new AppLog(Path.Combine(_directory,"logs"));
        await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);vm.UsePreviewDevices([]);
        Assert.False(vm.FloatingEnabled);var shown=0;
        App.TrayPrimaryClick(vm,()=>shown++);
        Assert.Equal(FloatingBuild,vm.FloatingEnabled);
        Assert.Equal(FloatingBuild?0:1,shown);
    });

    [Fact]
    public Task SettingsAndTrayMenuOfferFloatingOnlyWhenItIsBuilt()=>Sta(async()=>
    {
        var application=Application.Current??new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        application.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/EasySwitch;component/Theme.xaml",UriKind.Relative)));
        var store=new SettingsStore(_directory);using var log=new AppLog(Path.Combine(_directory,"logs"));
        await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);vm.UsePreviewDevices([]);

        var menu=App.BuildTrayMenu(vm,()=>{},()=>{});
        var items=menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(FloatingBuild,items.Any(item=>item.Name=="FloatingToggle"));
        Assert.Contains(items,item=>item.IsCheckable);

        var window=new MainWindow{ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.DataContext=vm;vm.NavigateCommand.Execute("설정");
            var tags=Descendants(window).OfType<FrameworkElement>().Select(element=>element.Tag as string).OfType<string>().ToHashSet();
            Assert.Contains("SettingsCards",tags);
            Assert.Contains("ToastSettingsCard",tags);
            Assert.Equal(FloatingBuild,tags.Contains("FloatingSettings"));
        }
        finally{window.AllowClose=true;window.Close();}
    });

    private static IEnumerable<object> Descendants(object root)
    {
        yield return root;
        if(root is not DependencyObject parent)yield break;
        foreach(var child in LogicalTreeHelper.GetChildren(parent))
        foreach(var descendant in Descendants(child))yield return descendant;
    }
    private static Task Sta(Func<Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async()=>{try{await action();done.SetResult();}catch(Exception error){done.SetException(error);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});
            Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
    public void Dispose(){if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
