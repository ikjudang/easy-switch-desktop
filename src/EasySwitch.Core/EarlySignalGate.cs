namespace EasySwitch.Core;

/// <summary>Hold notifications until the source route is known, without replaying an older connection's departure.</summary>
public sealed class EarlySignalGate<T>(TimeProvider? timeProvider = null, int capacity = 16, TimeSpan? lifetime = null)
{
    private readonly object _gate = new();
    private readonly Queue<(T Frame, long Generation, long At)> _held = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _lifetime = lifetime ?? TimeSpan.FromSeconds(5);
    public bool IsEmpty { get { lock (_gate) return _held.Count == 0; } }

    public bool Hold(T frame, long generation)
    {
        lock (_gate)
        {
            var evicted = _held.Count >= Math.Max(1, capacity);
            if (evicted) _held.Dequeue();
            _held.Enqueue((frame, generation, _clock.GetTimestamp()));
            return evicted;
        }
    }

    public (IReadOnlyList<T> Frames, int Discarded) Open(long currentGeneration)
    {
        lock (_gate)
        {
            var frames = new List<T>();
            var discarded = 0;
            while (_held.TryDequeue(out var entry))
            {
                var age = _clock.GetElapsedTime(entry.At);
                if (entry.Generation != currentGeneration || age < TimeSpan.Zero || age >= _lifetime) discarded++;
                else frames.Add(entry.Frame);
            }
            return (frames, discarded);
        }
    }
}
