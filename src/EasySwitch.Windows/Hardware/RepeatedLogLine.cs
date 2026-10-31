namespace EasySwitch.Windows.Hardware;

/// <summary>
/// Collapses one failure line that repeats unchanged while an absent device keeps being polled.
/// The first occurrence is written at once; identical repeats are counted and summarized at most
/// once per interval, and when a different line or a reply ends the run. Only polling lookups use
/// this, so switch verification failures are still written one by one.
/// </summary>
internal sealed class RepeatedLogLine(Func<long>? now = null, long intervalMs = 60_000)
{
    private readonly object _gate = new();
    private readonly Func<long> _now = now ?? (() => Environment.TickCount64);
    private string? _line;
    private int _repeats;
    private long _since;

    /// <summary>Returns the lines to write now for this occurrence, oldest first.</summary>
    public IReadOnlyList<string> Next(string line)
    {
        lock (_gate)
        {
            var now = _now();
            if (line != _line)
            {
                var ended = EndLocked(now, "다른 결과로 바뀜");
                _line = line; _repeats = 0; _since = now;
                return ended is null ? [line] : [ended, line];
            }
            _repeats++;
            if (now - _since < intervalMs) return [];
            var summary = $"{line} (같은 줄 {_repeats}회 더 반복, 최근 {(now - _since) / 1000}초)";
            _repeats = 0; _since = now;
            return [summary];
        }
    }

    /// <summary>Ends the current run; returns the count of unwritten repeats, if any.</summary>
    public string? Reset(string reason)
    {
        lock (_gate)
        {
            var ended = EndLocked(_now(), reason);
            _line = null; _repeats = 0;
            return ended;
        }
    }

    private string? EndLocked(long now, string reason) =>
        _line is null || _repeats == 0 ? null : $"{_line} (같은 줄 {_repeats}회 더 반복, 최근 {(now - _since) / 1000}초) 뒤 {reason}";
}
