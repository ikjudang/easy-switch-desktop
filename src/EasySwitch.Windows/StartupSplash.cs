using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EasySwitch.Windows.Controls;

namespace EasySwitch.Windows;

internal sealed class StartupSplash : Window
{
    private const double GaugeWidth = 374;
    private const double FillDurationMilliseconds = 500;
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
        // The inner edge blends over the solid frame color instead of the desktop.
        const double frameThickness = 1;
        const double frameRadius = 24;
        var panel = new Border
        {
            Background = (Brush)Application.Current.Resources["Bg"],
            CornerRadius = new(frameRadius - frameThickness / 2),
            Child = body
        };
        Artwork = new Border
        {
            Background = (Brush)Application.Current.Resources["BorderStrong"],
            CornerRadius = new(frameRadius + frameThickness / 2),
            Padding = new(frameThickness),
            Child = panel
        };
        Content=Artwork;
    }

    internal void SetProgress(double value) => _fill.Width = GaugeWidth * Math.Clamp(value, 0, 1);

    internal void StartProgress() =>
        _fill.BeginAnimation(WidthProperty, FillAnimation(0, GaugeWidth * .9));

    internal async Task CompleteProgressAsync()
    {
        var animation = FillAnimation(_fill.Width, GaugeWidth);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler finish = (_, _) => completed.TrySetResult();
        animation.Completed += finish;
        Closed += finish;
        try
        {
            _fill.BeginAnimation(WidthProperty, animation);
            await completed.Task;
        }
        finally
        {
            Closed -= finish;
        }
        await Dispatcher.Yield(DispatcherPriority.Background);
    }

    private static DoubleAnimation FillAnimation(double from, double to) =>
        new(from, to, TimeSpan.FromMilliseconds((to - from) / GaugeWidth * FillDurationMilliseconds));
}
