using System.IO;
using System.Windows.Threading;
using EasySwitch.Windows.Hardware;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Tests;

public sealed class ViewStateTests
{
    [Fact]
    public async Task MultiSelectionAndExplicitEmptySelectionSurviveLegacyMigration()
    {
        var directory=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store=new SettingsStore(directory);store.Save(new AppSettings{KeyboardId="key",MouseId="mouse"});
            using var log=new AppLog(Path.Combine(directory,"logs"));var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
            var keyboard=new DeviceInfo("key","MX Mechanical","Logi Bolt","키보드",true,3,2,9);
            var mouse=new DeviceInfo("mouse","MX Master 4","Logi Bolt","마우스",false,3,2,14);
            var secondMouse=new DeviceInfo("second","MX Anywhere 3","Unifying","마우스",true,3,2,10);
            vm.UsePreviewDevices([keyboard,mouse,secondMouse]);Assert.Equal(2,vm.SelectedCount);
            vm.ToggleDevice(secondMouse);Assert.Equal(3,vm.SelectedCount);
            vm.UsePreviewDevices([keyboard with{Kind="기기"},mouse with{Kind="기기"},secondMouse with{Kind="기기"}]);
            Assert.True(vm.KnowsRole("key","키보드"));Assert.True(vm.KnowsRole("second","마우스"));
            vm.ToggleDevice(keyboard);Assert.True(vm.KnowsRole("second","마우스"));vm.ToggleDevice(mouse);vm.ToggleDevice(secondMouse);Assert.Equal(0,vm.SelectedCount);
            Assert.Empty(new SettingsStore(directory).Value.SelectedDeviceIds!);
            await vm.DisposeAsync();
        }
        finally{Directory.Delete(directory,true);}
    }
    [Fact]
    public async Task OnlineLocalChannelsAndLastDestinationAreIndependent()
    {
        var directory=Path.Combine(Path.GetTempPath(),"EasySwitch.Tests",Guid.NewGuid().ToString("N"));
        try
        {
            var store=new SettingsStore(directory);store.Save(new AppSettings{KeyboardId="key",MouseId="mouse"});
            using var log=new AppLog(Path.Combine(directory,"logs"));var vm=new MainViewModel(store,log,Dispatcher.CurrentDispatcher);
            vm.UsePreviewDevices([new("key","MX Mechanical","Logi Bolt","키보드",false,3,1,9),new("mouse","MX Master 4","Bluetooth","마우스",true,3,2,14)]);
            Assert.False(vm.ChannelCurrent(1));Assert.True(vm.ChannelCurrent(2));Assert.Null(vm.LastDestination);
            vm.SetPreviewDestination(1);Assert.Equal("마지막 전환 채널",vm.ChannelStatus(1));Assert.Equal("현재 기기 연결 중",vm.ChannelStatus(2));Assert.False(vm.ChannelCurrent(1));
            vm.UsePreviewDevices([new("key","MX Mechanical","Logi Bolt","키보드",true,3,1,9),new("mouse","MX Master 4","Bluetooth","마우스",true,3,2,14)]);
            Assert.True(vm.ChannelCurrent(1));Assert.True(vm.ChannelCurrent(2));Assert.Equal(1,vm.LastDestination);
            vm.FloatingSize=300;Assert.Equal(200,vm.FloatingSize);vm.FloatingSize=-1;Assert.Equal(0,vm.FloatingSize);
            await vm.DisposeAsync();
        }
        finally{Directory.Delete(directory,true);}
    }
}
