using System.Diagnostics;
using System.Text.Json;

namespace EasySwitch.Windows.Infrastructure;

internal sealed class StartupTiming
{
    private readonly double _beforeManaged=Math.Max(0,(DateTime.UtcNow-Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds);
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private readonly Dictionary<string,long> _stages=[];
    public long Elapsed=>Math.Max(0,(long)(_beforeManaged+_clock.Elapsed.TotalMilliseconds));
    public void Mark(string stage)=>_stages[stage]=Elapsed;
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"startup.json"),JsonSerializer.Serialize(new{mode="Startup UI fixture; process lifetime includes bundle startup; HID disabled",elapsedMilliseconds=Elapsed,stages=_stages},new JsonSerializerOptions{WriteIndented=true}));
    }
}
