using System.Runtime.CompilerServices;
using EasySwitch.Windows.Service;

namespace EasySwitch.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Keep WPF types out of this method's JIT so service modes can start
        // without constructing Application or loading its theme resources.
        if(args.Contains("--install-service"))return InstallService(args);
        if(args.Contains("--uninstall-service"))return ServiceInstaller.Uninstall();
        if(args.Contains("--service"))return RunService();
#if EASYSWITCH_DIAGNOSTICS
        if(args.Contains("--service-startup-smoke") && !args.Contains("--wpf-service-startup-smoke"))
            return ServiceStartupFixture.RunAsync(args).GetAwaiter().GetResult();
#endif
        return RunWpf();
    }

    private static int InstallService(string[] args)
    {
        var setup=Array.IndexOf(args,"--service-setup");
        return ServiceInstaller.Install(setup>=0 && setup+1<args.Length?args[setup+1]:null);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunService()
    {
        try{System.ServiceProcess.ServiceBase.Run(new EasySwitchDeviceService());return 0;}
        catch(Exception error)
        {
            try{Directory.CreateDirectory(ServiceRegistration.Root);File.WriteAllText(Path.Combine(ServiceRegistration.Root,"host-error.txt"),error.ToString());}catch(Exception){}
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWpf()
    {
        var app=new App();app.InitializeComponent();return app.Run();
    }
}
