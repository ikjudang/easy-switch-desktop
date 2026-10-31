using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using EasySwitch.Windows.Infrastructure;

namespace EasySwitch.Windows;

public partial class MainWindow : Window
{
    public bool AllowClose { get; set; }
    internal event Action? CloseToTrayRequested;
    internal sealed record ViewState(bool FloatingExpanded,bool ExportExpanded,bool ToastExpanded,bool ShowCodeLog);
    internal ViewState CaptureViewState()=>new(_floatingExpanded,_exportExpanded,_toastExpanded,_showCodeLog);
    internal void RestoreViewState(ViewState? state)
    {
        if(state is null)return;
        _floatingExpanded=state.FloatingExpanded;_exportExpanded=state.ExportExpanded;_toastExpanded=state.ToastExpanded;
        _showCodeLog=state.ShowCodeLog;
    }
    public MainWindow()
    {
        InitializeComponent();
        UiAnimationCadence.ObserveButtonHover();
        InitializeLayout();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            var caption = 0x0016120F;
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        };
        Closing += OnClosing;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) CloseToTray(); };
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void CloseToTray()
    {
        if(CloseToTrayRequested is {} callback)callback();else Hide();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        CloseToTray();
    }

    private void LogList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var lines = LogList.SelectedItems.Cast<object>().Select(l=>l switch {LogEntry raw=>(raw.At,raw.Line),CodeLogEntry code=>(code.At,code.Line),_=>(DateTimeOffset.MinValue,"")}).OrderByDescending(l=>l.Item1).Select(l=>l.Item2).Where(l=>l.Length>0);
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length > 0) _ = CopyWithRetryAsync(text, Clipboard.SetText, message => _vm?.WriteUiLog(message));
        e.Handled = true;
    }

    // Another process can hold the clipboard open for a moment. Retry briefly without blocking the
    // UI thread, and log a copy that still fails instead of raising it as an app error.
    internal static async Task<bool> CopyWithRetryAsync(string text, Action<string> setText, Action<string> log, int attempts = 4, int delayMilliseconds = 50)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { setText(text); return true; }
            catch (ExternalException) when (attempt < attempts) { }
            catch (Exception error) { log($"로그 복사 실패({attempt}회 시도): {error.Message}"); return false; }
            await Task.Delay(delayMilliseconds);
        }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
