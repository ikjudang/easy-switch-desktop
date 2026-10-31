using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using EasySwitch.Windows.Controls;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

// Uses the process's single WPF Application, so it shares the non-parallel collection.
[Collection(nameof(ApplicationResourcesCollection))]
public sealed class MainWindowReliabilityTests : IDisposable
{
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));
    private static readonly DeviceInfo Keyboard=new("key","MX Keys","Bluetooth","키보드",true,3,0,9);
    private static readonly DeviceInfo Mouse=new("mouse","MX Master","Bluetooth","마우스",true,3,0,9);

    [Fact]
    public Task APressedChannelTileSurvivesStateChangesSoItsClickStillRequestsTheSwitch()=>Sta(async()=>
    {
        var theme=Theme();
        var store=new SettingsStore(_directory);store.Save(new AppSettings{SelectedDeviceIds=["key","mouse"],SelectedDeviceNames=[new("key","MX Keys","키보드"),new("mouse","MX Master","마우스")]});
        using var log=new AppLog(Path.Combine(_directory,"logs"));
        await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);vm.UsePreviewDevices([Keyboard,Mouse]);
        // Shown off-screen and inactive: the refresh path under test runs only for a loaded, visible window.
        var window=new MainWindow{ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-32000,Top=-32000};
        try
        {
            window.DataContext=vm;window.Show();await Idle();
            Assert.True(window.IsLoaded && window.IsVisible && vm.IsHome);
            var tile=Tile(window,1);var button=(Button)tile.Child;
            SetPressed(button,true);
            // A device-state notification arrives between mouse-down and mouse-up.
            vm.UsePreviewDevices([Keyboard with{CurrentHost=1},Mouse with{CurrentHost=1}]);await Idle();
            Assert.Same(tile,Tile(window,1));Assert.NotNull(PresentationSource.FromVisual(button));

            // The mouse-up still reaches the pressed tile, and its click requests channel 2.
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Contains(log.RecentCodes(),entry=>entry.Code.Contains("|B2|ALL|BEGIN|",StringComparison.Ordinal));

            // Releasing the press runs the postponed refresh, which shows the new state.
            SetPressed(button,false);
            button.RaiseEvent(new MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=System.Windows.Input.Mouse.LostMouseCaptureEvent});
            await Idle();
            Assert.NotSame(tile,Tile(window,1));Assert.True(vm.ChannelCurrent(1));

            // Without a press, a notification refreshes the page as before.
            var current=Tile(window,1);vm.UsePreviewDevices([Keyboard,Mouse]);await Idle();
            Assert.NotSame(current,Tile(window,1));
        }
        finally{window.AllowClose=true;window.Close();}
    });

    [Fact]
    public Task SettingsSliderStillUpdatesTheValueAndItsNumber()=>Sta(async()=>
    {
        Theme();
        var store=new SettingsStore(_directory);using var log=new AppLog(Path.Combine(_directory,"logs"));
        await using var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);vm.UsePreviewDevices([]);
        var window=new MainWindow{ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.DataContext=vm;vm.NavigateCommand.Execute("설정");
            var row=Descendants(window).OfType<Grid>().Single(grid=>grid.Tag as string=="ToastPercent-size");
            var slider=Descendants(row).OfType<FineSlider>().Single();var input=Descendants(row).OfType<TextBox>().Single();
            slider.Value=137;
            Assert.Equal(137,vm.ToastSize);Assert.Equal("137",input.Text);
            slider.SetCurrentValue(FineSlider.ValueProperty,88d);
            Assert.Equal(88,vm.ToastSize);Assert.Equal("88",input.Text);
            input.Text="150";
            Assert.Equal(150,vm.ToastSize);Assert.Equal(150,slider.Value);
        }
        finally{window.AllowClose=true;window.Close();}
    });

    [Fact]
    public Task LanguagePickerKeepsOneUnloadHandlerWhenItsTemplateIsAppliedAgain()=>Sta(()=>
    {
        var theme=Theme();
        var picker=new LanguagePicker{Style=(Style)theme["LanguagePickerStyle"]};
        Assert.True(picker.ApplyTemplate());picker.OnApplyTemplate();picker.OnApplyTemplate();
        var store=typeof(UIElement).GetProperty("EventHandlersStore",Hidden)!.GetValue(picker)!;
        var handlers=(RoutedEventHandlerInfo[])store.GetType().GetMethod("GetRoutedEventHandlers",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(store,[FrameworkElement.UnloadedEvent])!;
        Assert.Single(handlers);
        return Task.CompletedTask;
    });

    private static ResourceDictionary Theme()
    {
        var application=Application.Current??new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var theme=(ResourceDictionary)Application.LoadComponent(new Uri("/EasySwitch;component/Theme.xaml",UriKind.Relative));
        application.Resources.MergedDictionaries.Add(theme);return theme;
    }
    private static Border Tile(Window window,int channel)=>Descendants(window).OfType<Border>().Single(border=>border.Tag as string==$"ChannelTile{channel}");
    private static void SetPressed(ButtonBase button,bool pressed)=>typeof(ButtonBase).GetMethod("SetIsPressed",Hidden)!.Invoke(button,[pressed]);
    private static async Task Idle()=>await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
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
