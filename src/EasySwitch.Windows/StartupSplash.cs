using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EasySwitch.Windows.Controls;

namespace EasySwitch.Windows;

internal sealed class StartupSplash : Window
{
    private readonly Border _fill;
    internal FrameworkElement Artwork { get; }
    public StartupSplash()
    {
        Width=440;Height=260;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        // Let the rounded artwork's partially transparent edge composite against the desktop.
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar=false;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;ShowActivated=false;
        var body=new Grid{Margin=new Thickness(32)};
        body.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        body.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var art=new StackPanel{VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};
        art.Children.Add(new Border{Width=88,Height=88,CornerRadius=new(26),Background=(Brush)Application.Current.Resources["Point"],Margin=new Thickness(0,0,0,12),Child=new HeaderBrandMark{Width=88,Height=88,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}});
        art.Children.Add(new TextBlock{Text="EasySwitch",FontSize=26,FontWeight=FontWeights.ExtraBold,HorizontalAlignment=HorizontalAlignment.Center});body.Children.Add(art);
        var gauge=new Grid{Height=5,Margin=new Thickness(0,24,0,0)};Grid.SetRow(gauge,1);
        gauge.Children.Add(new Border{Background=(Brush)Application.Current.Resources["Inset"],CornerRadius=new(2.5)});
        _fill=new Border{Background=(Brush)Application.Current.Resources["Point"],CornerRadius=new(2.5),HorizontalAlignment=HorizontalAlignment.Left,Width=0};
        gauge.Children.Add(_fill);body.Children.Add(gauge);
        Artwork=new Border{Background=(Brush)Application.Current.Resources["Bg"],BorderBrush=(Brush)Application.Current.Resources["BorderStrong"],BorderThickness=new Thickness(1),CornerRadius=new(24),Child=body};
        Content=Artwork;
    }
    internal void SetProgress(double value)=>_fill.Width=374*Math.Clamp(value,0,1);
    public async Task AdvanceAsync(double value)
    {
        SetProgress(value);
        await Dispatcher.Yield(DispatcherPriority.Background);
    }
}
