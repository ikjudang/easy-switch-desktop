using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EasySwitch.Windows.Controls;

namespace EasySwitch.Windows.Notices;

/// <summary>Retained Android toast artwork. Status changes redraw the existing surface.</summary>
internal sealed class NoticeBubble : FrameworkElement
{
    internal const double DisplayScale=1.5;
    private const double PaddingSize=12,DeviceNameWidth=100;
    private readonly Typeface _font=new(new FontFamily("pack://application:,,,/EasySwitch;component/Assets/Fonts/#Pretendard"),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
    private readonly Dictionary<string,FormattedText> _text=[];
    private static readonly Brush Background=Brush(240,48,48,48),Complete=Brush(255,0,230,118),Failed=Brush(255,229,57,53);
    private SwitchNotice? _notice;
    private bool _english,_connection;
    private double _contentWidth,_statusWidth,_rowHeight,_headerHeight,_height,_dpi,_maximumWidth;
    public NoticeBubble(){Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=6*DisplayScale,ShadowDepth=2*DisplayScale,Opacity=.35};}
    public double BodyWidth=>(_contentWidth+58)*DisplayScale;
    public double BodyHeight=>_height*DisplayScale;
    internal double IconCenterY=>(8+_height/2)*DisplayScale;
    internal double FirstRowCenterY=>(8+PaddingSize+_headerHeight/2)*DisplayScale;
    internal IReadOnlyList<string> DisplayedStatuses=>_notice?.Devices.Select(d=>Status(d,_english).Text).ToArray()??[];
    private static Brush Brush(byte a,byte r,byte g,byte b){var value=new SolidColorBrush(Color.FromArgb(a,r,g,b));value.Freeze();return value;}
    internal static (string Text,Brush Color) Status(DeviceResult row,bool english)
    {
        if(row.State==DeviceNoticeState.Connected)return(english?"Connected":"연결됨",Brushes.White);
        if(row.Success==true || row.State is DeviceNoticeState.Confirmed or DeviceNoticeState.SignalObserved or DeviceNoticeState.Departed)return(english?"Complete":"완료",Complete);
        if(row.Success==false || row.State is DeviceNoticeState.Failed or DeviceNoticeState.Unavailable or DeviceNoticeState.WaitExpired)return(english?"Failed":"실패",Failed);
        if(row.State is DeviceNoticeState.Unconfirmed or DeviceNoticeState.Cancelled)return(english?"Unconfirmed":"확인 필요",Brushes.White);
        return(english?"Switching":"전환 중",Brushes.White);
    }
    private FormattedText Text(string value)
    {
        if(_text.TryGetValue(value,out var cached))return cached;
        return _text[value]=new(value,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,_font,14,Brushes.White,_dpi*DisplayScale);
    }
    internal bool Update(SwitchNotice notice,bool english,bool connection,IReadOnlyList<string> reservedNames,double maximumWidth)
    {
        var dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fresh=_notice is null || _notice.RunId!=notice.RunId || _english!=english || _connection!=connection || _dpi!=dpi || _maximumWidth!=maximumWidth;
        var oldWidth=BodyWidth;var oldHeight=BodyHeight;
        _english=english;_connection=connection;
        if(fresh)
        {
            _text.Clear();_dpi=dpi;_maximumWidth=maximumWidth;
            var maximum=Math.Max(1,Math.Min(notice.Devices.Count==0?320:300,maximumWidth/DisplayScale)-58);
            var labels=english?new[]{"Complete","Failed","Switching","Unconfirmed","Connected"}:new[]{"완료","실패","전환 중","확인 필요","연결됨"};
            _statusWidth=Math.Ceiling(labels.Max(s=>Text(s).WidthIncludingTrailingWhitespace));
            var sample=Text("Mg한글");_rowHeight=Math.Max(18,Math.Ceiling(sample.Height));_headerHeight=Math.Max(24,Math.Ceiling(sample.Height));
            _contentWidth=Math.Min(DeviceNameWidth,Math.Max(1,maximum-8-_statusWidth))+8+_statusWidth;
            if(notice.Devices.Count==0)
            {
                var message=Text(english?"No devices to switch":"전환할 기기가 없음");
                _contentWidth=Math.Min(maximum,Math.Ceiling(Math.Max(120,Math.Max(Text(notice.Title).WidthIncludingTrailingWhitespace,message.WidthIncludingTrailingWhitespace))));
                message.MaxTextWidth=Math.Max(1,_contentWidth);message.MaxLineCount=5;
                _height=2*PaddingSize+_headerHeight+message.Height;
            }
        }
        if(notice.Devices.Count>0)_height=2*PaddingSize+Math.Max(24,(connection?0:_headerHeight)+_rowHeight*notice.Devices.Count);
        _notice=notice;
        var resized=fresh || oldWidth!=BodyWidth || oldHeight!=BodyHeight;
        if(resized)InvalidateMeasure();InvalidateVisual();return resized;
    }
    protected override Size MeasureOverride(Size availableSize)=>new(BodyWidth+16*DisplayScale,BodyHeight+16*DisplayScale);
    protected override void OnRender(DrawingContext dc)
    {
        if(_notice is not {} notice)return;
        dc.PushTransform(new ScaleTransform(DisplayScale,DisplayScale));
        var body=new Rect(8,8,_contentWidth+58,_height);
        dc.DrawRoundedRectangle(Background,null,body,24,24);
        var iconY=8+(_height-24)/2;
        dc.DrawRoundedRectangle(Brush(255,108,60,224),null,new(20,iconY,24,24),9,9);
        dc.PushTransform(new TranslateTransform(20,iconY));BrandArtwork.RailBrand(dc,24);dc.Pop();
        var x=54d;var y=8+PaddingSize;
        void Draw(string value,double left,double top,double width,Brush? color=null)
        {
            var text=Text(value);text.MaxTextWidth=Math.Max(1,width);text.MaxLineCount=1;text.Trimming=TextTrimming.CharacterEllipsis;text.SetForegroundBrush(color??Brushes.White);dc.DrawText(text,new(left,top));
        }
        if(notice.Devices.Count==0)
        {
            Draw(notice.Title,x,y+(_headerHeight-Text(notice.Title).Height)/2,_contentWidth);
            var message=Text(_english?"No devices to switch":"전환할 기기가 없음");
            message.MaxTextWidth=Math.Max(1,_contentWidth);message.MaxLineCount=5;dc.DrawText(message,new(x,y+_headerHeight));dc.Pop();return;
        }
        if(!_connection){Draw(notice.Title,x,y+(_headerHeight-Text(notice.Title).Height)/2,_contentWidth);y+=_headerHeight;}
        if(_connection)y+=Math.Max(0,(_height-2*PaddingSize-_rowHeight*notice.Devices.Count)/2);
        foreach(var row in notice.Devices.OrderBy(d=>d.Kind=="키보드"?0:1))
        {
            var height=_rowHeight;
            Draw(row.Name,x,y+(height-Text(row.Name).Height)/2,_contentWidth-_statusWidth-8);
            var status=Status(row,_english);Draw(status.Text,x+_contentWidth-_statusWidth,y+(height-Text(status.Text).Height)/2,_statusWidth,status.Color);y+=height;
        }
        dc.Pop();
    }
}
