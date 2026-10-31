using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace EasySwitch.Windows.Controls;

// Transparent, non-activating topmost window shared by toasts and the floating button.
internal sealed class OverlayWindow : Window
{
    private readonly bool _passThrough;
    public OverlayWindow(bool passThrough)
    {
        _passThrough=passThrough;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;
        ShowInTaskbar=false;ShowActivated=false;Topmost=true;Focusable=false;Width=80;Height=80;
        SourceInitialized+=(_,_)=>
        {
            var hwnd=new WindowInteropHelper(this).Handle;
            var style=GetWindowLongPtr(hwnd,-20).ToInt64()|0x08000000L|0x80L|(_passThrough?0x20L:0);
            SetWindowLongPtr(hwnd,-20,new(style));
            HwndSource.FromHwnd(hwnd)!.AddHook((nint h,int m,nint w,nint l,ref bool handled)=>{if(m==0x21){handled=true;return new nint(3);}return 0;});
        };
    }
    private (int X,int Y,int W,int H)? _lastPlacement;
    internal int PlacementChanges {get;private set;}
    internal int ResizeChanges {get;private set;}
    internal bool LayoutReady
    {
        get
        {
            if(_lastPlacement is not {} placed || !GetWindowRect(new WindowInteropHelper(this).Handle,out var actual) || !actual.Matches(placed) || Content is not FrameworkElement{IsLoaded:true,IsMeasureValid:true,IsArrangeValid:true} view)return false;
            var dpi=VisualTreeHelper.GetDpi(view);
            return view.ActualWidth>0 && view.ActualHeight>0 && Math.Abs(view.ActualWidth*dpi.DpiScaleX-placed.W)<=1 && Math.Abs(view.ActualHeight*dpi.DpiScaleY-placed.H)<=1;
        }
    }
    public void Place(int x,int y,int width,int height)
    {
        var next=(X:x,Y:y,W:Math.Max(1,width),H:Math.Max(1,height));
        var hwnd=new WindowInteropHelper(this).EnsureHandle();
        var hasActual=GetWindowRect(hwnd,out var actual);
        // A DPI transition can resize the native window after SetWindowPos returns.
        // The cached request alone does not mean the window still has that geometry.
        if(_lastPlacement==next && hasActual && actual.Matches(next))return;
        if(SetWindowPos(hwnd,new(-1),next.X,next.Y,next.W,next.H,0x10))
        {PlacementChanges++;if(_lastPlacement is not {} previous || previous.W!=next.W || previous.H!=next.H || (hasActual && (actual.Right-actual.Left!=next.W || actual.Bottom-actual.Top!=next.H)))ResizeChanges++;_lastPlacement=next;}
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect
    {
        public int Left,Top,Right,Bottom;
        public readonly bool Matches((int X,int Y,int W,int H) placement)=>Left==placement.X && Top==placement.Y && Right-Left==placement.W && Bottom-Top==placement.H;
    }
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint hwnd,out NativeRect rect);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
}
