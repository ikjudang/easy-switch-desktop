using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Controls;

internal sealed class SlideToggle : ToggleButton
{
    private TranslateTransform? _thumb;
    private int _animation;

    public SlideToggle()
    {
        Loaded+=(_,_)=>Move(false);
        Unloaded+=(_,_)=>Move(false);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if(GetTemplateChild("Thumb") is FrameworkElement thumb)
        {
            // Each instance owns its transform, including templates restored after navigation.
            _thumb=new TranslateTransform();thumb.RenderTransform=_thumb;Move(false);
        }
    }

    protected override void OnChecked(RoutedEventArgs e){base.OnChecked(e);Move(IsLoaded);}
    protected override void OnUnchecked(RoutedEventArgs e){base.OnUnchecked(e);Move(IsLoaded);}
    protected override void OnIndeterminate(RoutedEventArgs e){base.OnIndeterminate(e);Move(IsLoaded);}

    private void Move(bool animate)
    {
        if(_thumb is null)return;
        var current=_thumb.X;var target=IsChecked==true?18d:0d;var animationId=++_animation;
        _thumb.BeginAnimation(TranslateTransform.XProperty,null);
        _thumb.X=target;
        if(animate && Math.Abs(current-target)>.01)
        {
            UiAnimationCadence.Request(this,140);
            _thumb.X=current;var transform=_thumb;
            var animation=new DoubleAnimation(current,target,TimeSpan.FromMilliseconds(140)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
            animation.Completed+=(_,_)=>{if(animationId==_animation){transform.X=target;transform.BeginAnimation(TranslateTransform.XProperty,null);}};
            transform.BeginAnimation(TranslateTransform.XProperty,animation);
        }
    }
}
