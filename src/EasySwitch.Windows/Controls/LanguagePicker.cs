using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using EasySwitch.Core;

namespace EasySwitch.Windows.Controls;

internal sealed class LanguagePicker : ComboBox
{
    internal object? PlacementDetails {get;private set;}
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if(GetTemplateChild("PART_Popup") is not Popup popup)return;
        popup.Placement=PlacementMode.Custom;
        popup.CustomPopupPlacementCallback=(size,target,offset)=>
        {
            var origin=PointToScreen(new());var dpi=VisualTreeHelper.GetDpi(this).DpiScaleX;
            var area=System.Windows.Forms.Screen.FromPoint(new((int)origin.X,(int)origin.Y)).WorkingArea;
            // WPF supplies physical pixel sizes/offsets here for per-monitor-aware HWNDs.
            // The shared Android placement rule uses DIPs; convert both directions once.
            var width=size.Width/dpi;var height=size.Height/dpi;
            var position=LanguageMenuPlacement.Place(new(origin.X/dpi,origin.Y/dpi,origin.X/dpi+target.Width/dpi,origin.Y/dpi+target.Height/dpi),new(area.Left/dpi,area.Top/dpi,area.Right/dpi,area.Bottom/dpi),width,height);
            PlacementDetails=new{size,target,offset,origin,dpi,position};
            return [new(new((position.X-origin.X/dpi-8)*dpi,(position.Y-origin.Y/dpi-8)*dpi),PopupPrimaryAxis.None)];
        };
        Unloaded+=(_,_)=>IsDropDownOpen=false;
    }
}
