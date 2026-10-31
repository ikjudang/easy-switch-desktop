using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows.Controls;

/// <summary>Reveals an already measured settings section and moves following cards with it.</summary>
internal sealed class AnimatedSection : Decorator
{
    private static readonly DependencyProperty ProgressProperty=DependencyProperty.Register(
        "Progress",typeof(double),typeof(AnimatedSection),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsMeasure));
    public bool Expanded {get;private set;}
    private int _animation;
    internal double Progress=>(double)GetValue(ProgressProperty);

    public AnimatedSection(){ClipToBounds=true;}

    public void SetExpanded(bool expanded,bool animate=true)
    {
        Expanded=expanded;IsHitTestVisible=expanded;
        if(Child is null)return;
        Child.Visibility=Visibility.Visible;
        var from=Progress;var target=expanded?1d:0d;var animationId=++_animation;
        BeginAnimation(ProgressProperty,null);SetValue(ProgressProperty,target);
        if(animate && IsLoaded && Math.Abs(from-target)>.001)
        {
            UiAnimationCadence.Request(this,180);
            SetValue(ProgressProperty,from);
            var animation=new DoubleAnimation(from,target,TimeSpan.FromMilliseconds(180))
            {EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
            animation.Completed+=(_,_)=>
            {
                if(animationId!=_animation)return;
                SetValue(ProgressProperty,target);BeginAnimation(ProgressProperty,null);
                if(!Expanded && Child is not null)Child.Visibility=Visibility.Hidden;
            };
            BeginAnimation(ProgressProperty,animation);
        }
        else if(!expanded)Child.Visibility=Visibility.Hidden;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if(Child is null)return new();
        Child.Measure(new(availableSize.Width,double.PositiveInfinity));
        return new(Child.DesiredSize.Width,Child.DesiredSize.Height*Math.Clamp(Progress,0,1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0,0,finalSize.Width,Child.DesiredSize.Height));
        return finalSize;
    }
}
