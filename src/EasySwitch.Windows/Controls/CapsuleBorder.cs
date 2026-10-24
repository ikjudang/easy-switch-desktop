using System.Windows;
using System.Windows.Controls;

namespace EasySwitch.Windows.Controls;

internal sealed class CapsuleBorder:Border
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        // Equal x/y radii create semicircles; a fixed radius larger than half the
        // measured height makes WPF normalize the two axes into elliptical ends.
        SetCurrentValue(CornerRadiusProperty,new CornerRadius(Math.Min(finalSize.Width,finalSize.Height)/2));
        return base.ArrangeOverride(finalSize);
    }
}
