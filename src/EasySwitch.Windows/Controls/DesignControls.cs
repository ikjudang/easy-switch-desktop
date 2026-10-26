using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace EasySwitch.Windows.Controls;

internal sealed class CircleMarker:FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        var radius=Math.Min(ActualWidth,ActualHeight)/2;
        dc.DrawEllipse(Brushes.White,null,new(ActualWidth/2,ActualHeight/2),radius,radius);
    }
}

internal sealed class AppIcon(string kind) : FrameworkElement
{
    public static readonly DependencyProperty TintProperty=DependencyProperty.Register(nameof(Tint),typeof(Brush),typeof(AppIcon),new FrameworkPropertyMetadata(Brushes.Gray,FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Tint { get=>(Brush)GetValue(TintProperty);set=>SetValue(TintProperty,value); }
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(ActualWidth/24,ActualHeight/24));
        var pen = new Pen(Tint,2) { StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round };
        void Path(string data, bool fill=false) => dc.DrawGeometry(fill?Tint:null,fill?null:pen,Geometry.Parse(data));
        switch (kind)
        {
            case "home": Path("M12 3L21 11H18.5V20H5.5V11H3Z",true); break;
            case "devices": Path("M4 7H20M4 12H20M4 17H20"); break;
            case "keyboard":
                dc.PushTransform(new ScaleTransform(24d/28,1)); Path("M6 5H22A3 3 0 0 1 25 8V17A3 3 0 0 1 22 20H6A3 3 0 0 1 3 17V8A3 3 0 0 1 6 5ZM7 9H7.02M12 9H12.02M17 9H17.02M21 9H21.02M9 15H19"); dc.Pop(); break;
            case "mouse": Path("M12 2A5 5 0 0 1 17 7V17A5 5 0 0 1 7 17V7A5 5 0 0 1 12 2ZM12 5V9"); break;
            case "refresh": Path("M20 6V11H15M19.2 10A7.5 7.5 0 1 0 19 17"); break;
            case "clock": dc.DrawEllipse(null,pen,new(12,12),8,8); Path("M12 7.5V12L15.2 13.8"); break;
            case "bell": Path("M18 8A6 6 0 0 0 6 8C6 15 3 16 3 16H21C21 16 18 15 18 8ZM10 20H14"); break;
            case "folder": Path("M3 7H9.5L11.5 9.5H21V19H3ZM3 7V5H10L12 7H21V9.5"); break;
            case "globe": dc.DrawEllipse(null,pen,new(12,12),9,9); dc.DrawEllipse(null,pen,new(12,12),4,9); Path("M3 12H21"); break;
            case "overlay": Path("M4 4H20V20H4ZM9 9H16V16H9Z"); break;
            case "follow": Path("M3 7H15M11 3L15 7L11 11M21 17H9M13 13L9 17L13 21"); break;
            case "keys": Path("M4 5H20V19H4ZM8 9H8.1M12 9H12.1M16 9H16.1M8 14H16"); break;
            case "chevron-up":pen.Thickness=3;Path("M6 15L12 9L18 15");break;
            case "chevron-down":pen.Thickness=3;Path("M6 9L12 15L18 9");break;
            case "chevron-right":pen.Thickness=3;Path("M9 6L15 12L9 18");break;
            case "chevron-left":pen.Thickness=3;Path("M15 6L9 12L15 18");break;
            case "gear": Path("F0 M19.14 12.94C19.18 12.64 19.2 12.33 19.2 12C19.2 11.68 19.18 11.36 19.13 11.06L21.16 9.48C21.34 9.34 21.39 9.07 21.28 8.87L19.36 5.55C19.24 5.33 18.99 5.26 18.77 5.33L16.38 6.29C15.88 5.91 15.35 5.59 14.76 5.35L14.4 2.81C14.36 2.57 14.16 2.4 13.92 2.4H10.08C9.84 2.4 9.65 2.57 9.61 2.81L9.25 5.35C8.66 5.59 8.12 5.92 7.63 6.29L5.24 5.33C5.02 5.25 4.77 5.33 4.65 5.55L2.74 8.87C2.62 9.08 2.66 9.34 2.86 9.48L4.89 11.06C4.84 11.36 4.8 11.69 4.8 12C4.8 12.31 4.82 12.64 4.87 12.94L2.84 14.52C2.66 14.66 2.61 14.93 2.72 15.13L4.64 18.45C4.76 18.67 5.01 18.74 5.23 18.67L7.62 17.71C8.12 18.09 8.65 18.41 9.24 18.65L9.6 21.19C9.65 21.43 9.84 21.6 10.08 21.6H13.92C14.16 21.6 14.36 21.43 14.39 21.19L14.75 18.65C15.34 18.41 15.88 18.09 16.37 17.71L18.76 18.67C18.98 18.75 19.23 18.67 19.35 18.45L21.27 15.13C21.39 14.91 21.34 14.66 21.15 14.52ZM12 15.6A3.6 3.6 0 1 1 12 8.4A3.6 3.6 0 1 1 12 15.6",true); break;
        }
        dc.Pop();
    }
}
internal sealed class TokenBrushConverter(string on,string off):IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>Application.Current.Resources[value is true?on:off];
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
internal sealed class BooleanTextConverter(string on,string off):IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value is true?on:off;
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}

internal sealed class FineSlider : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value),typeof(double),typeof(FineSlider),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty,Math.Clamp(value,Minimum,Maximum)); }
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;
    private double Range=>Math.Max(1,Maximum-Minimum);
    private double ThumbX=>20+Track*(Value-Minimum)/Range;
    private double _lastX;
    private bool _hover;
    private double Track => Math.Max(1,ActualWidth-40);
    public FineSlider()
    {
        Height=48; MinWidth=90; Cursor=Cursors.Hand; Focusable=true;
        MouseMove += (_,e) =>
        {
            var point=e.GetPosition(this);var x=point.X;
            if (IsMouseCaptured) { SetCurrentValue(ValueProperty,Math.Clamp(Value+(x-_lastX)/(Track*1.5)*Range,Minimum,Maximum)); _lastX=x; }
            _hover=!IsMouseCaptured && (point-new Point(ThumbX,24)).Length<=16; InvalidateVisual();
        };
        MouseLeave += (_,_) => { _hover=false; InvalidateVisual(); };
        MouseLeftButtonDown += (_,e) =>
        {
            var x=e.GetPosition(this).X;
            if (Math.Abs(x-ThumbX)>16) SetCurrentValue(ValueProperty,Math.Clamp(Minimum+(x-20)/Track*Range,Minimum,Maximum));
            _lastX=x; CaptureMouse(); _hover=false; e.Handled=true; InvalidateVisual();
        };
        MouseLeftButtonUp += (_,e) => { ReleaseMouseCapture(); _hover=false; e.Handled=true; InvalidateVisual(); };
        LostMouseCapture += (_,_) => { _hover=false; InvalidateVisual(); };
        KeyDown += (_,e) => { if(e.Key is Key.Left or Key.Right) { SetCurrentValue(ValueProperty,Math.Clamp(Value+(e.Key==Key.Right?1:-1),Minimum,Maximum)); e.Handled=true; } };
        System.Windows.Automation.AutomationProperties.SetName(this,"플로팅 버튼 투명도");
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent,null,new(0,0,ActualWidth,ActualHeight));
        Brush B(string name) => (Brush)Application.Current.Resources[name];
        var x=ThumbX;
        dc.DrawRoundedRectangle(B("BorderStrong"),null,new(20,22,Track,4),2,2);
        dc.DrawRoundedRectangle(B("PointTintText"),null,new(20,22,Math.Max(0,x-20),4),2,2);
        if(_hover) dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(46,197,182,247)),null,new(x,24),16,16);
        dc.DrawEllipse(B("PointTintText"),null,new(x,24),8,8);
    }
}
