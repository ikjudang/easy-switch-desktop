using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EasySwitch.Windows.Controls;
#if EASYSWITCH_FLOATING
using EasySwitch.Windows.Floating;
#endif
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows;

public partial class MainWindow
{
    private MainViewModel? _vm;
    private Grid? _pageHost;
    private ListBox LogList = new();
    private string _builtPage = "", _builtLanguage = "";
    private bool _pageRefreshQueued,_pageRefreshHidden,_pageRefreshAfterPress;
    private bool _floatingExpanded, _exportExpanded, _toastExpanded;
    private bool _showCodeLog;
#if EASYSWITCH_FLOATING
    private AnimatedSection? _floatingSection;
#endif
    private AnimatedSection? _exportSection, _toastSection;
    private const double SettingsIconLeft=16+(53-40)/2d;
    private TextBox? _opacityInput;
    private int? _editingChannel;
    private string _channelDraft="";
    private ContextMenu? _channelNameMenu;
    private FrameworkElement? _floatingSizeRow;
    internal Rect? FloatingSizeControlScreenBounds=>IsActive && _floatingSizeRow is {IsVisible:true,ActualWidth:>0,ActualHeight:>0} row
        ?new Rect(row.PointToScreen(new()),row.PointToScreen(new(row.ActualWidth,row.ActualHeight))):null;
    internal bool IsFloatingSizeControlAt(Point screenPoint)
    {
        if(!IsActive || GetForegroundWindow()!=new System.Windows.Interop.WindowInteropHelper(this).Handle || _floatingSizeRow is not { IsVisible:true,ActualWidth:>0,ActualHeight:>0 } row)return false;
        return new Rect(row.PointToScreen(new()),row.PointToScreen(new(row.ActualWidth,row.ActualHeight))).Contains(screenPoint);
    }
    private readonly List<TextBox> _percentInputs=[];
    private Brush B(string key) => (Brush)FindResource(key);
    private string T(string ko,string en) => _vm!.T(ko,en);
    private TextBlock Text(string text,double size=14,string color="Text",FontWeight? weight=null) => new() { Text=text,FontSize=size,Foreground=B(color),FontWeight=weight??FontWeights.Normal,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis };
    private static void Bind(DependencyObject control,DependencyProperty property,string path) => BindingOperations.SetBinding(control,property,new Binding(path) { Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged });
    private Button Button(UIElement content,Action action,bool plain=false,CornerRadius? corners=null,bool outlined=false)
    {
        var button=new Button { Content=content,Padding=new Thickness(0),BorderThickness=new Thickness(0),Background=plain?Brushes.Transparent:B("Card"),HorizontalContentAlignment=HorizontalAlignment.Stretch };
        var border=new FrameworkElementFactory(typeof(Border));
        border.Name="Body"; border.SetValue(Border.CornerRadiusProperty,corners??new CornerRadius(16));
        border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
        if(outlined){border.SetValue(Border.BorderBrushProperty,B("Border"));border.SetValue(Border.BorderThicknessProperty,new Thickness(1));}
        var layer=new FrameworkElementFactory(typeof(Grid));
        var overlay=new FrameworkElementFactory(typeof(Border));overlay.Name="Hover";overlay.SetValue(Border.CornerRadiusProperty,corners??new CornerRadius(16));overlay.SetValue(Border.BackgroundProperty,new SolidColorBrush(Colors.Transparent));layer.AppendChild(overlay);
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Stretch);
        layer.AppendChild(presenter);border.AppendChild(layer);
        var template=new ControlTemplate(typeof(Button)) { VisualTree=border };
        var hover=new Trigger { Property=IsMouseOverProperty,Value=true };
        void Transition(TriggerActionCollection actions,Color color)
        {
            var animation=new ColorAnimation(color,TimeSpan.FromMilliseconds(100));Storyboard.SetTargetName(animation,"Hover");Storyboard.SetTargetProperty(animation,new PropertyPath("(Border.Background).(SolidColorBrush.Color)"));var storyboard=new Storyboard();storyboard.Children.Add(animation);actions.Add(new BeginStoryboard{Storyboard=storyboard});
        }
        Transition(hover.EnterActions,Color.FromArgb(18,238,241,245));Transition(hover.ExitActions,Colors.Transparent);
        template.Triggers.Add(hover);
        button.Template=template; button.Click+=(_,_)=>action(); return button;
    }
    private Border Card(UIElement child,double radius=16) => new() { Child=child,Background=B("Card"),BorderBrush=B("Border"),BorderThickness=new Thickness(1),CornerRadius=new(radius),Margin=new Thickness(0,0,0,10) };
    private new FrameworkElement Icon(string kind,double size=20,string color="Text2") => new AppIcon(kind) { Width=size,Height=size,Tint=B(color),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center };
    private Border IconTile(string kind,bool active=false,double size=40) => new() { Width=size,Height=size,CornerRadius=new(12),Background=B(active?"PointTintIconBg":"Inset"),Child=Icon(kind,20,active?"PointTintText":"Text2"),VerticalAlignment=VerticalAlignment.Center };
    private void InitializeLayout()
    {
        IsVisibleChanged+=(_,_)=>{if(IsVisible && _pageRefreshHidden){_pageRefreshHidden=false;BuildPage();}};
        DataContextChanged+=(_,_)=>
        {
            if(_vm is not null) _vm.PropertyChanged-=VmChanged;
            _vm=DataContext as MainViewModel;
            if(_vm is null) return;
            _vm.PropertyChanged+=VmChanged; BuildShell();
        };
        PreviewMouseDown+=(_,e)=>
        {
            var focused=_percentInputs.FirstOrDefault(i=>i.IsKeyboardFocusWithin);
            if(focused is null)return;
            var p=e.GetPosition(focused);
            if(p.X<0 || p.Y<0 || p.X>focused.ActualWidth || p.Y>focused.ActualHeight) ClearInputFocus();
        };
        PreviewMouseWheel+=(_,_)=>ClearInputFocus();
        // A page refresh postponed during a press resumes once the press has ended. The handlers
        // only queue that refresh, so the press's own click runs first and unchanged.
        AddHandler(Mouse.LostMouseCaptureEvent,new MouseEventHandler((_,_)=>ResumePageRefresh()),true);
        AddHandler(PreviewMouseUpEvent,new MouseButtonEventHandler((_,_)=>ResumePageRefresh()),true);
        AddHandler(PreviewKeyUpEvent,new KeyEventHandler((_,_)=>ResumePageRefresh()),true);
    }
    private void ClearInputFocus() { Keyboard.ClearFocus(); FocusManager.SetFocusedElement(this,null); }
    private void VmChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(_vm is null) return;
        if(_builtLanguage!=_vm.Preferences.Language) { BuildShell(); return; }
        if(_builtPage!=_vm.Page) { BuildShell(); return; }
        if(e.PropertyName=="" && !_vm.IsSettings)
        {
            if(_vm.IsHome && _editingChannel is not null)return;
            // LastRequest has no visual on these pages. Coalesce state notifications and
            // do not rebuild a hidden main window while a floating animation is running.
            if(IsLoaded && !IsVisible){_pageRefreshHidden=true;return;}
            if(!IsLoaded){BuildPage();return;}
            QueuePageRefresh();
        }
    }
    private void QueuePageRefresh()
    {
        if(_pageRefreshQueued)return;
        _pageRefreshQueued=true;
        Dispatcher.BeginInvoke(()=>
        {
            _pageRefreshQueued=false;if(_vm?.IsSettings!=false || (_vm.IsHome && _editingChannel is not null))return;
            if(!IsVisible){_pageRefreshHidden=true;return;}
            // A rebuild would replace the control under a press: its mouse-up would then reach a
            // new control that never saw the press, and a channel tile's switch request would be lost.
            if(IsPagePressed()){_pageRefreshAfterPress=true;return;}
            BuildPage();
        },DispatcherPriority.Background);
    }
    private void ResumePageRefresh()
    {
        if(!_pageRefreshAfterPress)return;
        _pageRefreshAfterPress=false;QueuePageRefresh();
    }
    private bool IsPagePressed()
    {
        if(_pageHost is null)return false;
        if(Mouse.Captured is Visual captured && _pageHost.IsAncestorOf(captured))return true;
        return PressedButtonWithin(_pageHost);
        static bool PressedButtonWithin(DependencyObject parent)
        {
            for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {
                var child=VisualTreeHelper.GetChild(parent,i);
                if(child is ButtonBase{IsPressed:true} || PressedButtonWithin(child))return true;
            }
            return false;
        }
    }
    private void BuildShell()
    {
        if(_vm is null) return;
        if(_channelNameMenu is {} menu)menu.IsOpen=false;
        _editingChannel=null;_channelDraft="";_channelNameMenu=null;
        _builtLanguage=_vm.Preferences.Language; _builtPage=_vm.Page; LayoutRoot.Children.Clear(); LayoutRoot.ColumnDefinitions.Clear();
        LayoutRoot.ColumnDefinitions.Add(new(){ Width=new GridLength(200) }); LayoutRoot.ColumnDefinitions.Add(new(){ Width=new GridLength(1,GridUnitType.Star) });
        var rail=new Grid { Background=B("BgRail"),Margin=new Thickness(0) };
        rail.RowDefinitions.Add(new(){ Height=GridLength.Auto }); rail.RowDefinitions.Add(new(){ Height=new GridLength(1,GridUnitType.Star) }); rail.RowDefinitions.Add(new(){ Height=GridLength.Auto });
        var upper=new StackPanel { Margin=new Thickness(16,26,16,0) };
        var brand=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(6,0,0,34) };
        brand.Children.Add(new Border { Width=34,Height=34,CornerRadius=new(10),Background=B("Point"),Child=new HeaderBrandMark{Width=34,Height=34,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center} }); brand.Children.Add(new TextBlock { Text="EasySwitch",FontSize=15,FontWeight=FontWeights.ExtraBold,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(11,0,0,0) }); upper.Children.Add(brand);
        foreach(var (page,icon,ko,en) in new[]{("홈","home","홈","Home"),("기기","devices","기기 선택","Devices"),("설정","gear","설정","Settings")})
        {
            var active=page=="설정"?_vm.IsSettings:_vm.Page==page;
            var row=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(12,11,12,11) };
            row.Children.Add(Icon(icon,18,active?"PointAccent":"Text2"));
            var label=Text(T(ko,en),13.5,active?"PointTintText":"Text2",active?FontWeights.Bold:FontWeights.SemiBold); label.Margin=new Thickness(12,0,0,0); row.Children.Add(label);
            var button=Button(row,()=>_vm.NavigateCommand.Execute(page),true,new(12)); button.Background=active?B("PointTintBg"):Brushes.Transparent; button.Margin=new Thickness(0,0,0,6); upper.Children.Add(button);
        }
        rail.Children.Add(upper);
        var footer=new StackPanel { Margin=new Thickness(22,0,14,24) };
        var statusRow=new StackPanel{Orientation=Orientation.Horizontal};var dot=new Ellipse{Width=6,Height=6,Margin=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Center};dot.SetBinding(Shape.FillProperty,new Binding(nameof(MainViewModel.FollowEnabled)){Converter=new TokenBrushConverter("Success","Warn")});statusRow.Children.Add(dot);var status=Text("",11,"Text2");status.SetBinding(TextBlock.TextProperty,new Binding(nameof(MainViewModel.FollowStatus)));statusRow.Children.Add(status);footer.Children.Add(statusRow);
        var version=Text($"Easy Switch v{AppVersion.Label}",11,"GlyphDisabled"); version.FontFamily=(FontFamily)FindResource("SpaceMono"); version.Margin=new Thickness(0,8,0,0); footer.Children.Add(version); Grid.SetRow(footer,2); rail.Children.Add(footer); LayoutRoot.Children.Add(rail);
        _pageHost=new Grid { Margin=new Thickness(32,30,32,30) }; Grid.SetColumn(_pageHost,1); LayoutRoot.Children.Add(_pageHost); BuildPage();
    }
    private void BuildPage()
    {
        if(_vm is null || _pageHost is null) return;
        _pageRefreshAfterPress=false;
        _pageHost.Children.Clear(); _pageHost.RowDefinitions.Clear(); _opacityInput=null;_floatingSizeRow=null;_percentInputs.Clear();
        _pageHost.RowDefinitions.Add(new(){Height=GridLength.Auto}); _pageHost.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var header=Text(_vm.IsHome?"EasySwitch":_vm.IsDevices?T("기기 선택","Select devices"):T("설정","Settings"),26,"Text",FontWeights.ExtraBold); header.Margin=new Thickness(0,0,0,_vm.IsSettings?22:24);
        if(_vm.IsDevices)
        {
            var frame=new Grid{Margin=new Thickness(0,0,0,16)};frame.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});frame.ColumnDefinitions.Add(new(){Width=new GridLength(24)});frame.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            var pane=new DockPanel();var refresh=Button(Icon("refresh"),()=>_vm.RefreshCommand.Execute(null));refresh.Width=38;refresh.Height=38;refresh.ToolTip=T("기기 새로고침","Refresh devices");DockPanel.SetDock(refresh,Dock.Right);pane.Children.Add(refresh);
            var titles=new StackPanel();header.Margin=new Thickness(0);titles.Children.Add(header);var subtitle=Text(T("함께 전환할 기기를 선택해 주세요.","Choose devices to switch together."),13,"Text2");subtitle.Margin=new Thickness(0,2,0,0);subtitle.LineHeight=24;titles.Children.Add(subtitle);pane.Children.Add(titles);frame.Children.Add(pane);_pageHost.Children.Add(frame);
        }
        else _pageHost.Children.Add(header);
        var body=_vm.IsHome?BuildHome():_vm.IsDevices?BuildDevices():BuildSettings(); Grid.SetRow(body,1); _pageHost.Children.Add(body);
    }
    private UIElement BuildHome()
    {
        var grid=new Grid(); grid.ColumnDefinitions.Add(new(){Width=new GridLength(300)}); grid.ColumnDefinitions.Add(new(){Width=new GridLength(24)}); grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto}); grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        TextBlock HomeHeader(string label,string color,string tag)
        {var text=Text(label,12,color,FontWeights.SemiBold);text.LineHeight=24;text.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;text.Tag=tag;return text;}
        var devicesHeader=new DockPanel { Margin=new Thickness(0,0,0,12),Height=28 };
        var selectText=HomeHeader(T("기기 선택","Select"),"PointAccent","SelectDevicesHeader");selectText.Margin=new Thickness(8,0,8,0);
        var select=Button(selectText,()=>_vm!.NavigateCommand.Execute("기기"),true,new(8));select.Height=28;select.Margin=new Thickness(6,0,0,0);DockPanel.SetDock(select,Dock.Right);devicesHeader.Children.Add(select);
        devicesHeader.Children.Add(HomeHeader(T($"연결된 기기 ({_vm!.HomeDevices.Count(d=>d.Info.Online)}대)",$"Connected devices ({_vm!.HomeDevices.Count(d=>d.Info.Online)})"),"Text2","ConnectedHeader"));grid.Children.Add(devicesHeader);
        var channelHeader=HomeHeader(T("전환할 채널","Switch channel"),"TextMuted","ChannelHeader");channelHeader.Margin=new Thickness(0,0,0,12);Grid.SetColumn(channelHeader,2);grid.Children.Add(channelHeader);
        var devices=new StackPanel();
        foreach(var choice in _vm.HomeDevices)
        {
            var row=new Grid { Margin=new Thickness(12,10,12,10),MinHeight=32 };
            row.ColumnDefinitions.Add(new(){Width=new GridLength(43)});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            row.Children.Add(IconTile(choice.IsKeyboard?"keyboard":"mouse",choice.Info.Online,32));
            var text=new StackPanel { VerticalAlignment=VerticalAlignment.Center };
            var name=Text(choice.Info.Name,13.5,choice.Info.Online?"Text":"Text2",FontWeights.Bold);name.LineHeight=24;name.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;text.Children.Add(name);
            var status=Text(T(choice.HomeStatus,choice.Info.Online?(choice.IsKeyboard?(_vm.FollowEnabled?"Keyboard: detecting switches":"Keyboard: connected"):"Mouse: ready to switch"):"Not connected"),11,"Text2");status.LineHeight=24;status.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;var statusLine=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,2,0,0)};statusLine.Children.Add(new Ellipse{Width=6,Height=6,Fill=B(choice.Info.Online?"Success":"NumeralIdle"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,6,0)});statusLine.Children.Add(status);text.Children.Add(statusLine);Grid.SetColumn(text,1);row.Children.Add(text);
            var card=Card(row,14); card.Margin=new Thickness(0,0,0,9);card.Opacity=choice.Info.Online?1:.6;devices.Children.Add(card);
        }
        if(_vm.NoHomeDevices) devices.Children.Add(Text(T("기기 선택에서 전환할 기기를 골라 주세요.","Select devices to switch."),13,"Text2"));
        Grid.SetRow(devices,1);grid.Children.Add(devices);
        var tiles=new UniformGrid { Columns=3,Height=153,VerticalAlignment=VerticalAlignment.Top };
        for(var channel=0;channel<3;channel++)
        {
            var target=channel; var current=_vm.ChannelCurrent(target);var last=_vm.LastDestination==target;
            var stack=new StackPanel { VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,8,0) };
            var numeral=Text($"{target+1}",44,current?"Text":"NumeralIdle",FontWeights.Bold);numeral.FontFamily=(FontFamily)FindResource("SpaceMono");numeral.HorizontalAlignment=HorizontalAlignment.Center;stack.Children.Add(numeral);
            var label=Text(_vm.ChannelLabel(target),14,"Text",FontWeights.Bold);label.HorizontalAlignment=HorizontalAlignment.Center;label.Margin=new Thickness(0,9,0,9);stack.Children.Add(label);
            var slot=new Grid { Height=32 };
            var status=Text(_vm.ChannelStatus(target),10.5,current||last?"Text":"TextMuted",current||last?FontWeights.SemiBold:FontWeights.Normal);status.TextAlignment=TextAlignment.Center;status.TextWrapping=TextWrapping.Wrap; status.TextTrimming=TextTrimming.None;status.LineHeight=11;
            var badge=new Grid();badge.ColumnDefinitions.Add(new(){Width=GridLength.Auto});badge.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            if(current||last)badge.Children.Add(new CircleMarker{Width=6,Height=6,Margin=new Thickness(0,0,5,0),VerticalAlignment=VerticalAlignment.Center,Tag="ChannelStatusDot"});
            Grid.SetColumn(status,1);badge.Children.Add(status);
            var pill=new CapsuleBorder { Child=badge,Tag="ChannelStatusPill",Padding=new Thickness(current||last?11:0,4,current||last?11:0,4),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Background=current?new SolidColorBrush(Color.FromArgb(56,255,255,255)):last?B("Point"):Brushes.Transparent };slot.Children.Add(pill);stack.Children.Add(slot);
            var button=Button(stack,()=>{if(_editingChannel is null)_vm.RequestSwitch(target,SwitchRequestSource.HomeTile);},corners:new(20));button.Background=current?B("Point"):B("Card");button.ContextMenu=BuildChannelNameMenu(target);
            var frame=new Border { Child=button,Tag=$"ChannelTile{target}",CornerRadius=new(20),BorderThickness=new Thickness(current?0:1),BorderBrush=B("Border"),Margin=new Thickness(target==0?0:7,0,target==2?0:7,0),ClipToBounds=true };tiles.Children.Add(frame);
        }
        Grid.SetRow(tiles,1);Grid.SetColumn(tiles,2);grid.Children.Add(tiles);return grid;
    }
    private UIElement BuildDevices()
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(24)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var body=new Grid(); body.RowDefinitions.Add(new(){Height=GridLength.Auto});body.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});body.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.Children.Add(body);
        var list=new StackPanel();
        if(_vm!.NoDevices){var empty=Text(T("이 PC에 기기를 연결하면 앱이 목록을 표시합니다.","Connect a device to this PC to see it here."),13,"Text2");empty.TextWrapping=TextWrapping.Wrap;empty.Margin=new Thickness(16,30,16,16);list.Children.Add(empty);}
        foreach(var choice in _vm!.DeviceChoices)
        {
            var row=new Grid { Height=68,Margin=new Thickness(13,0,13,0) };
            foreach(var width in new[]{35d,47d,double.NaN}) row.ColumnDefinitions.Add(new(){Width=double.IsNaN(width)?new GridLength(1,GridUnitType.Star):new GridLength(width)});
            var check=new Border { Width=22,Height=22,CornerRadius=new(7),Background=choice.IsSelected?B("Point"):Brushes.Transparent,BorderBrush=B(choice.IsSelected?"Point":"NumeralIdle"),BorderThickness=new Thickness(2),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left,Child=Text(choice.IsSelected?"✓":"",12,"Text",FontWeights.Bold) };((TextBlock)check.Child).HorizontalAlignment=HorizontalAlignment.Center;row.Children.Add(check);
            var icon=IconTile(choice.IsKeyboard?"keyboard":"mouse",choice.IsSelected,34);Grid.SetColumn(icon,1);row.Children.Add(icon);
            var texts=new StackPanel { VerticalAlignment=VerticalAlignment.Center };texts.Children.Add(Text(choice.Info.Name,15,choice.IsSelected?"Text":"Text2",FontWeights.Bold));var status=Text(T(choice.Status,choice.Info.Online?"Connected":"Not connected")+" · "+choice.Info.Connection,11,"Text2");var statusLine=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,3,0,0)};statusLine.Children.Add(new Ellipse{Width=7,Height=7,Fill=B(choice.Info.Online?"Success":"NumeralIdle"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,6,0)});statusLine.Children.Add(status);texts.Children.Add(statusLine);Grid.SetColumn(texts,2);row.Children.Add(texts);
            var frame=Card(Button(row,choice.Toggle));frame.BorderBrush=B(choice.IsSelected?"PointAccent":"Border");frame.BorderThickness=new Thickness(choice.IsSelected?1.5:1);frame.Opacity=choice.IsSelected?1:.62;list.Children.Add(frame);
        }
        var scroll=new ScrollViewer { Style=(Style)FindResource("StableScrollViewer"),Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };Grid.SetRow(scroll,1);body.Children.Add(scroll);
        var confirmLabel=Text(T($"{_vm.SelectedCount}개 선택 완료",$"Continue with {_vm.SelectedCount} selected"),16,"Text",FontWeights.Bold);confirmLabel.HorizontalAlignment=HorizontalAlignment.Center;var confirm=Button(confirmLabel,()=>_vm.NavigateCommand.Execute("홈"));confirm.Height=54;confirm.Background=B("Point");confirm.IsEnabled=_vm.HasSelection;confirm.Margin=new Thickness(0,16,0,16);Grid.SetRow(confirm,2);body.Children.Add(confirm);return grid;
    }
    private ToggleButton Toggle(string? binding)
    {
        var toggle=new SlideToggle { Width=82,Height=68,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Cursor=Cursors.Hand };
        var outer=new FrameworkElementFactory(typeof(Border));outer.Name="Hover";outer.SetValue(Border.CornerRadiusProperty,new CornerRadius(16));outer.SetValue(Border.BackgroundProperty,Brushes.Transparent);
        var track=new FrameworkElementFactory(typeof(Border));track.Name="Track";track.SetValue(WidthProperty,46d);track.SetValue(HeightProperty,28d);track.SetValue(Border.CornerRadiusProperty,new CornerRadius(14));track.SetValue(Border.BackgroundProperty,B("BorderStrong"));track.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center);track.SetValue(HorizontalAlignmentProperty,HorizontalAlignment.Center);
        var thumb=new FrameworkElementFactory(typeof(Ellipse));thumb.Name="Thumb";thumb.SetValue(WidthProperty,22d);thumb.SetValue(HeightProperty,22d);thumb.SetValue(Shape.FillProperty,B("Text"));thumb.SetValue(MarginProperty,new Thickness(3));thumb.SetValue(HorizontalAlignmentProperty,HorizontalAlignment.Left);thumb.SetValue(RenderTransformProperty,new TranslateTransform());track.AppendChild(thumb);outer.AppendChild(track);
        var template=new ControlTemplate(typeof(ToggleButton)){VisualTree=outer};var on=new Trigger{Property=ToggleButton.IsCheckedProperty,Value=true};on.Setters.Add(new Setter(Border.BackgroundProperty,B("Point"),"Track"));
        template.Triggers.Add(on);
        var hover=new Trigger{Property=IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Border.BackgroundProperty,new SolidColorBrush(Color.FromArgb(18,238,241,245)),"Hover"));template.Triggers.Add(hover);toggle.Template=template;if(binding is not null)Bind(toggle,ToggleButton.IsCheckedProperty,binding);return toggle;
    }
    private Border SettingsRow(string title,string icon,Action? click=null,string? toggle=null,bool? expanded=null,bool floating=false,UIElement? detail=null,bool selected=false,Action<FrameworkElement>? expansionArrow=null)
    {
        var all=new StackPanel();var row=new Grid();row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var main=new Grid{Height=68,Margin=new Thickness(16,0,16,0)};main.ColumnDefinitions.Add(new(){Width=new GridLength(53)});main.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});main.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
#if EASYSWITCH_FLOATING
        if(floating){var bubble=new BubbleIcon {Width=40,Height=40};bubble.SetBinding(BubbleIcon.EnabledProperty,new Binding(nameof(MainViewModel.FloatingEnabled)));main.Children.Add(bubble);}
        else
#endif
        {
            var tile=IconTile(icon,selected);
            if(toggle is not null){tile.SetBinding(Border.BackgroundProperty,new Binding(toggle){Converter=new TokenBrushConverter("PointTintIconBg","Inset")});((AppIcon)tile.Child).SetBinding(AppIcon.TintProperty,new Binding(toggle){Converter=new TokenBrushConverter("PointTintText","Text2")});}
            main.Children.Add(tile);
        }
        var label=Text(title,15,"Text",FontWeights.Bold);Grid.SetColumn(label,1);main.Children.Add(label);
        if(floating){label.SetBinding(TextBlock.TextProperty,new Binding(nameof(MainViewModel.FloatingSettingsLabel)));label.LineHeight=18;}
        if(toggle==nameof(MainViewModel.ToastEnabled))label.SetBinding(TextBlock.TextProperty,new Binding(nameof(MainViewModel.ToastSettingsLabel)));
        if(expanded is not null) { var arrow=Icon("chevron-down",16);arrow.RenderTransformOrigin=new(.5,.5);arrow.RenderTransform=new RotateTransform(expanded.Value?180:0);arrow.Margin=new Thickness(8,0,0,0);Grid.SetColumn(arrow,2);main.Children.Add(arrow);expansionArrow?.Invoke(arrow); }
        if(detail is not null && toggle is null){Grid.SetColumn(detail,2);main.Children.Add(detail);}
        Button? rowButton=null;
        if(click is not null){rowButton=Button(main,click,true);row.Children.Add(rowButton);}
        else row.Children.Add(main);
        if(toggle is not null)
        {
            var right=Toggle(toggle);Grid.SetColumn(right,1);row.Children.Add(right);
            if(click is null)
            {
                right.IsHitTestVisible=false;right.Focusable=false;
                all.Children.Add(Button(row,()=>right.SetCurrentValue(ToggleButton.IsCheckedProperty,right.IsChecked!=true),true));return Card(all);
            }
        }
        all.Children.Add(row);var card=Card(all);
        if(expanded is not null)
        {
            // The card owns the top and side strokes; only draw the header's lower curve.
            var boundary=new Canvas{IsHitTestVisible=false};
            var outline=new System.Windows.Shapes.Path{Stroke=card.BorderBrush};boundary.Children.Add(outline);
            Grid.SetColumnSpan(boundary,2);row.Children.Add(boundary);
            void DrawBoundary()
            {
                var dpi=VisualTreeHelper.GetDpi(card);
                var stroke=Math.Round(card.BorderThickness.Bottom*dpi.DpiScaleY)/dpi.DpiScaleY;
                var origin=boundary.TranslatePoint(new(),card);
                var bottomInset=card.ActualHeight-all.ActualHeight-origin.Y;
                outline.Visibility=card.ActualHeight>origin.Y+row.ActualHeight+bottomInset+.001?Visibility.Visible:Visibility.Hidden;
                var left=stroke/2-origin.X;var right=card.ActualWidth-stroke/2-origin.X;
                var bottom=row.ActualHeight+bottomInset-stroke/2;
                var radius=Math.Max(0,card.CornerRadius.BottomLeft-stroke/2);
                var curve=new StreamGeometry();
                using(var context=curve.Open())
                {
                    context.BeginFigure(new(left,bottom-radius),false,false);
                    context.ArcTo(new(left+radius,bottom),new Size(radius,radius),0,false,SweepDirection.Counterclockwise,true,false);
                    context.LineTo(new(right-radius,bottom),true,false);
                    context.ArcTo(new(right,bottom-radius),new Size(radius,radius),0,false,SweepDirection.Counterclockwise,true,false);
                }
                curve.Freeze();outline.StrokeThickness=stroke;outline.Data=curve;
            }
            boundary.SizeChanged+=(_,_)=>DrawBoundary();card.SizeChanged+=(_,_)=>DrawBoundary();
        }
        if(selected && rowButton is {} active)
        {
            void AlignCorners()
            {
                if(!active.IsLoaded || active.ActualWidth<=0 || card.ActualWidth<=0)return;
                var origin=active.TranslatePoint(new(),card);var right=card.ActualWidth-origin.X-active.ActualWidth;var bottom=card.ActualHeight-origin.Y-active.ActualHeight;
                double Inset(double radius,double x,double y)=>Math.Max(0,radius-Math.Max(x,y));
                var corners=new CornerRadius(Inset(card.CornerRadius.TopLeft,origin.X,origin.Y),Inset(card.CornerRadius.TopRight,right,origin.Y),Inset(card.CornerRadius.BottomRight,right,bottom),Inset(card.CornerRadius.BottomLeft,origin.X,bottom));
                active.ApplyTemplate();foreach(var name in new[]{"Body","Hover"})if(active.Template.FindName(name,active) is Border edge)edge.CornerRadius=corners;
            }
            active.Loaded+=(_,_)=>AlignCorners();active.SizeChanged+=(_,_)=>AlignCorners();card.SizeChanged+=(_,_)=>AlignCorners();
        }
        return card;
    }
    private UIElement BuildSettings()
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(24)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var cards=new StackPanel();var scroll=new ScrollViewer {Style=(Style)FindResource("StableScrollViewer"),Tag="SettingsCards",Content=cards,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};grid.Children.Add(scroll);
#if EASYSWITCH_FLOATING
        FrameworkElement? floatingArrow=null;
        var floating=SettingsRow(_vm!.FloatingSettingsLabel,"",()=>{_floatingExpanded=!_floatingExpanded;ExpandSection(_floatingSection!,floatingArrow!,_floatingExpanded);},nameof(MainViewModel.FloatingEnabled),_floatingExpanded,true,expansionArrow:arrow=>floatingArrow=arrow);
        var floatingContent=new StackPanel();floatingContent.Children.Add(new Border{Height=1});floatingContent.Children.Add(BuildOpacity());
        _floatingSection=new AnimatedSection{Child=floatingContent,Tag="FloatingSettings"};_floatingSection.SetExpanded(_floatingExpanded,false);((StackPanel)floating.Child).Children.Add(_floatingSection);cards.Children.Add(floating);
#endif
        FrameworkElement? toastArrow=null;
        var toast=SettingsRow(_vm!.ToastSettingsLabel,"bell",()=>{_toastExpanded=!_toastExpanded;ExpandSection(_toastSection!,toastArrow!,_toastExpanded);},nameof(MainViewModel.ToastEnabled),expanded:_toastExpanded,expansionArrow:arrow=>toastArrow=arrow);
        toast.Tag="ToastSettingsCard";
        var toastContent=new StackPanel();Grid.SetIsSharedSizeScope(toastContent,true);toastContent.Children.Add(new Border{Height=1});
        toastContent.Children.Add(BuildPercentRow("size",true));toastContent.Children.Add(BuildPercentRow("opacity",true));
        _toastSection=new AnimatedSection{Child=toastContent,Tag="ToastSettings"};_toastSection.SetExpanded(_toastExpanded,false);((StackPanel)toast.Child).Children.Add(_toastSection);cards.Children.Add(toast);
        cards.Children.Add(SettingsRow(T("키보드 따라 마우스 전환","Follow keyboard with mouse"),"follow",toggle:nameof(MainViewModel.FollowEnabled)));
        var logDetail=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0)};
        var logCount=Text("",11,"TextMuted");logCount.FontFamily=(FontFamily)FindResource("SpaceMono");logCount.SetBinding(TextBlock.TextProperty,new Binding(nameof(MainViewModel.LogSummary)));logDetail.Children.Add(logCount);var logArrow=Icon(_vm!.IsLog?"chevron-left":"chevron-right",12,_vm.IsLog?"PointTintText":"Text2");logArrow.Margin=new Thickness(10,0,0,0);logDetail.Children.Add(logArrow);
        var logRow=SettingsRow(T("연결 로그","Connection log"),"clock",()=>_vm.ToggleLogCommand.Execute(null),detail:logDetail,selected:_vm.IsLog);logRow.Tag="ConnectionLogCard";if(_vm.IsLog){logRow.Background=B("PointTintBg");logRow.BorderBrush=B("Point");}cards.Children.Add(logRow);
        FrameworkElement? exportArrow=null;
        var export=SettingsRow(T("로그 자동 저장","Save logs automatically"),"folder",()=>{_exportExpanded=!_exportExpanded;ExpandSection(_exportSection!,exportArrow!,_exportExpanded);},nameof(MainViewModel.LogFolderEnabled),expanded:_exportExpanded,expansionArrow:arrow=>exportArrow=arrow);
        {
            var inner=new StackPanel();inner.Children.Add(new Border{Height=1});
            var folderRow=new Grid{Height=70,Margin=new Thickness(69,0,16,0)};folderRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});folderRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
            var labels=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)};labels.Children.Add(Text(T("저장 폴더","Log folder"),15,"Text",FontWeights.Bold));
            var folderLabel=Text("",12,"Text2");folderLabel.Margin=new Thickness(0,2,0,0);folderLabel.SetBinding(TextBlock.TextProperty,new Binding(nameof(MainViewModel.ExportLabel)));labels.Children.Add(folderLabel);folderRow.Children.Add(labels);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            var choose=CardAction(T(_vm.HasExportFolder?"변경":"선택",_vm.HasExportFolder?"Change":"Choose"),()=>_vm.ChooseExportCommand.Execute(null));actions.Children.Add(choose);
            ((TextBlock)choose.Content).SetBinding(TextBlock.TextProperty,new Binding(nameof(MainViewModel.HasExportFolder)){Converter=new BooleanTextConverter(T("변경","Change"),T("선택","Choose"))});
            var clear=CardAction(T("해제","Clear"),()=>_vm.StopExportCommand.Execute(null));clear.Margin=new Thickness(8,0,0,0);clear.SetBinding(VisibilityProperty,new Binding(nameof(MainViewModel.HasExportFolder)){Converter=new BooleanToVisibilityConverter()});actions.Children.Add(clear);
            Grid.SetColumn(actions,1);folderRow.Children.Add(actions);inner.Children.Add(folderRow);inner.Children.Add(new Border{Height=1});
            var exportRow=new Grid{Height=70,Margin=new Thickness(69,0,16,0)};exportRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});exportRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});exportRow.Children.Add(Text(T("로그 내보내기","Export log"),15,"Text",FontWeights.Bold));
            var exportNow=CardAction(T("내보내기","Export"),()=>_vm.ExportLogCommand.Execute(null));Grid.SetColumn(exportNow,1);exportRow.Children.Add(exportNow);inner.Children.Add(exportRow);
            _exportSection=new AnimatedSection{Child=inner,Tag="ExportSettings"};_exportSection.SetExpanded(_exportExpanded,false);((StackPanel)export.Child).Children.Add(_exportSection);
        }
        // Local diagnostic files are always flushed as required by the PC handoff; folder mirroring is optional.
        cards.Children.Add(export);
        var language=new LanguagePicker{Style=(Style)FindResource("LanguagePickerStyle"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)};
        // SelectionBoxItem uses the implicit TextBlock style. Measure that same display
        // style so the fixed English width also leaves the arrow and its gap unclipped.
        var englishLabel=new TextBlock{Style=(Style)FindResource(typeof(TextBlock)),Text="English",FontWeight=FontWeights.SemiBold};englishLabel.Measure(new(double.PositiveInfinity,double.PositiveInfinity));
        var languageArrow=new TextBlock{Text="\uE70D",FontFamily=(FontFamily)FindResource("Glyphs"),FontSize=10,FontWeight=FontWeights.SemiBold};languageArrow.Measure(new(double.PositiveInfinity,double.PositiveInfinity));
        language.Width=Math.Ceiling(englishLabel.DesiredSize.Width+languageArrow.DesiredSize.Width+24+9+2);
        language.Items.Add(new ComboBoxItem{Content="한국어",Tag="ko"});language.Items.Add(new ComboBoxItem{Content="English",Tag="en"});language.SelectedIndex=_vm.English?1:0;
        language.SelectionChanged+=(_,_)=>{if(language.SelectedItem is ComboBoxItem item){language.IsDropDownOpen=false;_vm.SetLanguage((string)item.Tag);}};
        var languageRow=SettingsRow(T("언어 / Language","Language / 언어"),"globe",detail:language);languageRow.Margin=new Thickness(0);cards.Children.Add(languageRow);
        if(_vm.IsLog) {var logs=BuildLog();Grid.SetColumn(logs,2);grid.Children.Add(logs);}return grid;
    }
    private Button CardAction(string label,Action action)
    {
        var text=Text(label,12,"Text2",FontWeights.Bold);text.HorizontalAlignment=HorizontalAlignment.Center;text.Margin=new Thickness(12,7,12,7);
        var button=Button(text,action,corners:new(10),outlined:true);
        // Outline is decorative; the button, its hover layer and input boundary share one rectangle.
        button.VerticalAlignment=VerticalAlignment.Center;return button;
    }
    private static void ExpandSection(AnimatedSection section,FrameworkElement arrow,bool expanded)
    {
        section.SetExpanded(expanded);
        var rotation=(RotateTransform)arrow.RenderTransform;var from=rotation.Angle;var target=expanded?180d:0d;
        rotation.BeginAnimation(RotateTransform.AngleProperty,null);rotation.Angle=from;
        rotation.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(from,target,TimeSpan.FromMilliseconds(180)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
    }
    private UIElement BuildOpacity()
    {
        var rows=new StackPanel();Grid.SetIsSharedSizeScope(rows,true);
        rows.Children.Add(BuildPercentRow("opacity"));rows.Children.Add(BuildPercentRow("size"));rows.Children.Add(BuildPercentRow("speed"));
        var persistent=new Grid{Height=70,Tag="FloatingAlwaysShow",Margin=new Thickness(SettingsIconLeft,0,0,0)};persistent.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});persistent.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        persistent.Children.Add(Text(T("하위 버튼 항상 표시","Always show menu buttons"),13,"Text",FontWeights.Bold));var persistentToggle=Toggle(nameof(MainViewModel.FloatingAlwaysShow));persistentToggle.IsHitTestVisible=false;persistentToggle.Focusable=false;Grid.SetColumn(persistentToggle,1);persistent.Children.Add(persistentToggle);rows.Children.Add(Button(persistent,()=>_vm!.FloatingAlwaysShow=!_vm.FloatingAlwaysShow,true));
        var bounce=new Grid{Height=70,Margin=new Thickness(SettingsIconLeft,0,0,0)};bounce.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});bounce.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        bounce.Children.Add(Text(T("바운스 애니메이션","Bounce animation"),13,"Text",FontWeights.Bold));var toggle=Toggle(nameof(MainViewModel.BounceEnabled));toggle.IsHitTestVisible=false;toggle.Focusable=false;Grid.SetColumn(toggle,1);bounce.Children.Add(toggle);rows.Children.Add(Button(bounce,()=>_vm!.BounceEnabled=!_vm.BounceEnabled,true));return rows;
    }
    private UIElement BuildPercentRow(string kind,bool toast=false)
    {
        var size=kind=="size";var speed=kind=="speed";
        var row=new Grid{Height=70,Tag=$"{(toast?"Toast":"Floating")}Percent-{kind}",Margin=new Thickness(SettingsIconLeft,0,0,0)};row.ColumnDefinitions.Add(new(){Width=GridLength.Auto,SharedSizeGroup=toast?"ToastLabels":"FloatingLabels"});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        if(size && !toast)_floatingSizeRow=row;
        int Current()=>toast?(size?_vm!.ToastSize:_vm!.ToastTransparency):speed?_vm!.FloatingAnimationSpeed:size?_vm!.FloatingSize:_vm!.FloatingTransparency;
        void Set(int value){if(toast){if(size)_vm!.ToastSize=value;else _vm!.ToastTransparency=value;}else if(speed)_vm!.FloatingAnimationSpeed=value;else if(size)_vm!.FloatingSize=value;else _vm!.FloatingTransparency=value;}
        var minimum=speed?25:toast && size?50:0;var maximum=speed?300:size?200:100;
        var title=Text(speed?T("애니메이션 속도","Animation speed"):size?T("크기","Size"):T("투명도","Transparency"),13,"Text",FontWeights.Bold);row.Children.Add(title);
        _opacityInput=new TextBox {FontFamily=(FontFamily)FindResource("Pretendard"),FontSize=13,Foreground=B("Text"),CaretBrush=B("Text"),TextAlignment=TextAlignment.Right,VerticalContentAlignment=VerticalAlignment.Center,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(0),Text=Current().ToString(),MaxLength=3};
        var numeric=_opacityInput;
        numeric.BeginInit();numeric.EndInit();numeric.ApplyTemplate();
        var digitWidth=Enumerable.Range(0,10).Max(digit=>{numeric.Text=new string((char)('0'+digit),3);numeric.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));return numeric.DesiredSize.Width;});
        numeric.Text=Current().ToString();numeric.Width=Math.Ceiling(digitWidth)+1;
        var edit=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};edit.Children.Add(_opacityInput);var percent=Text("%",12,"Text2");percent.Margin=new Thickness(2,0,0,0);edit.Children.Add(percent);var frame=new Border{Background=B("Inset"),BorderBrush=B("BorderStrong"),BorderThickness=new Thickness(1),CornerRadius=new(10),MinHeight=28,Padding=new Thickness(8,0,8,0),Margin=new Thickness(12,0,0,0),VerticalAlignment=VerticalAlignment.Center,Child=edit};Grid.SetColumn(frame,1);row.Children.Add(frame);
        EventHandler? align=null;align=(_,_)=>
        {
            if(numeric.ActualHeight<=0 || percent.ActualHeight<=0 || numeric.Text.Length==0)return;
            var rect=numeric.GetRectFromCharacterIndex(0);
            if(rect.IsEmpty)return;
            var numberBaseline=numeric.TranslatePoint(new(0,rect.Top+numeric.FontFamily.Baseline*numeric.FontSize),edit).Y;
            var percentBaseline=percent.TranslatePoint(new(0,percent.BaselineOffset),edit).Y;
            percent.RenderTransform=new TranslateTransform(0,numberBaseline-percentBaseline);edit.LayoutUpdated-=align;
        };edit.LayoutUpdated+=align;edit.Unloaded+=(_,_)=>edit.LayoutUpdated-=align;
        var slider=new FineSlider { Margin=new Thickness(0),MinWidth=40,VerticalAlignment=VerticalAlignment.Center,Minimum=minimum,Maximum=maximum,Value=Current() };System.Windows.Automation.AutomationProperties.SetName(slider,title.Text);Grid.SetColumn(slider,2);row.Children.Add(slider);
        var syncing=false;
        var input=_opacityInput;
        _percentInputs.Add(input);
        input.TextChanged+=(_,_)=>{if(syncing)return;var raw=input.Text;if(raw.Length==0)return;if(!int.TryParse(raw,out var value)){syncing=true;input.Text=Current().ToString();syncing=false;return;}value=Math.Clamp(value,0,maximum);if(value>=minimum){syncing=true;Set(value);slider.Value=value;syncing=false;}if(raw!=value.ToString()){syncing=true;input.Text=value.ToString();input.CaretIndex=input.Text.Length;syncing=false;}};
        slider.ValueChanged+=(_,_)=>{if(syncing)return;syncing=true;Set((int)Math.Round(slider.Value));input.Text=Current().ToString();syncing=false;};
        input.LostKeyboardFocus+=(_,_)=>{var value=int.TryParse(input.Text,out var parsed)?Math.Clamp(parsed,minimum,maximum):Current();syncing=true;Set(value);slider.Value=value;input.Text=Current().ToString();syncing=false;};input.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){ClearInputFocus();e.Handled=true;}};return row;
    }
    private UIElement BuildLog()
    {
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var header=new Grid{Margin=new Thickness(0,0,0,12)};header.RowDefinitions.Add(new(){Height=GridLength.Auto});header.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var title=Text(_showCodeLog?T("코드 로그","Status codes"):T("연결 로그","Connection log"),15,"Text",FontWeights.Bold);title.Tag="LogTitle";title.Margin=new Thickness(0,0,0,4);header.Children.Add(title);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};
        var mode=CardAction(_showCodeLog?T("상세 보기","Details"):T("코드 보기","Codes"),()=>{_showCodeLog=!_showCodeLog;BuildPage();});actions.Children.Add(mode);
        mode.Tag="LogModeToggle";
        var open=CardAction(T("내보내기","Export"),()=> (_showCodeLog?_vm!.ExportCodesCommand:_vm!.ExportLogCommand).Execute(null));open.Tag=_showCodeLog?"ExportCodes":"ExportLog";open.Margin=new Thickness(8,0,0,0);actions.Children.Add(open);
        Grid.SetRow(actions,1);header.Children.Add(actions);grid.Children.Add(header);
        var body=new Grid();body.RowDefinitions.Add(new(){Height=GridLength.Auto});body.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        LogList=new ListBox{ItemsSource=_showCodeLog?_vm!.CodeLogs:_vm!.ActivityLogs,Background=Brushes.Transparent,BorderThickness=new Thickness(0),SelectionMode=SelectionMode.Extended};LogList.KeyDown+=LogList_KeyDown;LogList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled);
        LogList.Resources[typeof(ScrollViewer)]=(Style)FindResource("StableScrollViewer");
        var template=new DataTemplate();var line=new FrameworkElementFactory(typeof(TextBlock));line.SetBinding(TextBlock.TextProperty,new Binding("Line"));line.SetValue(TextBlock.FontFamilyProperty,FindResource("SpaceMono"));line.SetValue(TextBlock.FontSizeProperty,11d);line.SetValue(TextBlock.LineHeightProperty,16.5d);line.SetValue(TextBlock.LineStackingStrategyProperty,LineStackingStrategy.BlockLineHeight);line.SetValue(TextBlock.ForegroundProperty,B("TextBody"));line.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);line.SetValue(MarginProperty,new Thickness(0,4,0,4));template.VisualTree=line;LogList.ItemTemplate=template;
        var frame=new Border{Tag="ConnectionLogPanel",Background=B("LogBg"),BorderBrush=B("Border"),BorderThickness=new Thickness(1),CornerRadius=new(16),Padding=new Thickness(12),Child=LogList};Grid.SetRow(frame,1);body.Children.Add(frame);Grid.SetRow(body,1);grid.Children.Add(body);return grid;
    }
    private ContextMenu BuildChannelNameMenu(int target)
    {
        var menu=new ContextMenu{Tag="ChannelNameMenu"};
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(ContentPresenter.ContentSourceProperty,"Header");
        var item=new MenuItem{Focusable=false,StaysOpenOnClick=true,HeaderTemplate=null,Template=new ControlTemplate(typeof(MenuItem)){VisualTree=presenter}};menu.Items.Add(item);
        menu.Opened+=(_,_)=>
        {
            _editingChannel=target;_channelDraft=_vm!.ChannelLabel(target);_channelNameMenu=menu;
            item.Header=BuildChannelNameEditor(target);menu.UpdateLayout();
        };
        menu.Closed+=(_,_)=>
        {
            if(!ReferenceEquals(_channelNameMenu,menu))return;
            _editingChannel=null;_channelDraft="";_channelNameMenu=null;
            // The click that closes the menu may be a press on a channel tile; refresh after it.
            QueuePageRefresh();
        };
        return menu;
    }
    private UIElement BuildChannelNameEditor(int target)
    {
        var body=new StackPanel{Width=244,Margin=new Thickness(8),Tag="ChannelNameEditor"};
        var title=Text(T("채널 이름 변경","Rename channel"),13,"Text",FontWeights.SemiBold);title.Margin=new Thickness(0,0,0,8);body.Children.Add(title);
        var row=new Grid();row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var edit=new TextBox{Text=_channelDraft,FontSize=13,FontFamily=(FontFamily)FindResource("Pretendard"),Foreground=B("Text"),CaretBrush=B("Text"),Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(6,4,6,4),Height=30,VerticalContentAlignment=VerticalAlignment.Center,Tag="ChannelNameInput"};
        edit.TextChanged+=(_,_)=>_channelDraft=edit.Text;
        edit.PreviewKeyDown+=(_,e)=>{if(e.Key is Key.Enter or Key.Escape){e.Handled=true;FinishChannelRename(e.Key==Key.Enter);}};
        row.Children.Add(new Border{Child=edit,Background=B("Inset"),BorderBrush=B("BorderStrong"),BorderThickness=new Thickness(1),CornerRadius=new(8)});
        var confirmText=Text(T("확인","OK"),12);confirmText.HorizontalAlignment=HorizontalAlignment.Center;
        var confirm=Button(confirmText,()=>FinishChannelRename(true),true,new(8),true);confirm.Height=32;confirm.Width=48;confirm.Margin=new Thickness(8,0,0,0);confirm.Tag="ConfirmChannelName";Grid.SetColumn(confirm,1);row.Children.Add(confirm);body.Children.Add(row);
        edit.Loaded+=(_,_)=>Dispatcher.BeginInvoke(()=>
        {
            if(_editingChannel!=target || !edit.IsLoaded || _channelNameMenu is not {} menu)return;
            FocusManager.SetFocusedElement(menu,edit);Keyboard.Focus(edit);edit.SelectAll();
        },DispatcherPriority.Background);
        return body;
    }
    private void FinishChannelRename(bool save)
    {
        if(_editingChannel is not {} channel)return;
        var text=_channelDraft;var menu=_channelNameMenu;_editingChannel=null;_channelDraft="";_channelNameMenu=null;
        if(menu is not null)menu.IsOpen=false;
        if(save)_vm!.SetChannelLabel(channel,text);
        BuildPage();
    }
    internal void ExpandPreviewSettings() { _floatingExpanded=true;_exportExpanded=true;BuildPage(); }
    internal void ShowCodeLogPreview() { _showCodeLog=true;BuildPage(); }
}
