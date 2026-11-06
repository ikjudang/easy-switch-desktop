using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasySwitch.Windows.Controls;

namespace EasySwitch.Windows.Tests;

// The splash shows the same app mark as the window header: the enlarged foreground of the current Android
// app icon (about 75% of the tile). It once kept the earlier smaller rail mark after the icon grew.
[Collection(nameof(ApplicationResourcesCollection))]
public sealed class StartupSplashLogoTests
{
    [Fact]
    public Task SplashUsesTheCurrentAppMarkAtTileSize() => WithSplash(splash =>
    {
        var marks=Descendants(splash.Artwork).ToArray();
        var mark=Assert.Single(marks.OfType<HeaderBrandMark>());
        Assert.Empty(marks.OfType<RailBrandMark>());
        Assert.Equal(88,mark.Width);Assert.Equal(88,mark.Height);
    });

    [Fact]
    public Task SplashCompositesAntialiasedCornersAtCommonScales() => WithSplash(splash =>
    {
        Assert.True(splash.AllowsTransparency);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(splash.Background).Color);
        var artwork = splash.Artwork;
        var size = new Size(splash.Width, splash.Height);
        artwork.Measure(size);
        artwork.Arrange(new Rect(size));
        artwork.UpdateLayout();

        foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            var width = (int)(size.Width * scale);
            var height = (int)(size.Height * scale);
            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(artwork);

            var corner = (int)(24 * scale);
            var stride = corner * 4;
            var pixels = new byte[stride * corner];
            bitmap.CopyPixels(new Int32Rect(0, 0, corner, corner), pixels, stride, 0);
            var alpha = pixels.Where((_, index) => index % 4 == 3);
            Assert.Equal(0, pixels[3]);
            Assert.Contains(alpha, value => value is > 0 and < 255);
            Assert.Equal(255, pixels[^1]);
        }
    });

    private static Task WithSplash(Action<StartupSplash> verify)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(()=>
            {
                try
                {
                    var application=Application.Current??new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
                    application.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/EasySwitch;component/Theme.xaml",UriKind.Relative)));
                    var splash=new StartupSplash();
                    try{verify(splash);}
                    finally{splash.Close();}
                    done.SetResult();
                }
                catch(Exception error){done.SetException(error);}
                finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            });
            Dispatcher.Run();
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }

    private static IEnumerable<object> Descendants(object root)
    {
        yield return root;
        if(root is not DependencyObject parent)yield break;
        foreach(var child in LogicalTreeHelper.GetChildren(parent))
            foreach(var item in Descendants(child))yield return item;
    }
}
