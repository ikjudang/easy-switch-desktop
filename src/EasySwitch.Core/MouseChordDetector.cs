namespace EasySwitch.Core;

/// <summary>Passive per-device chord recognition. Coordinates and slop are raw pixels, not DIPs.</summary>
public sealed class MouseChordDetector
{
    public readonly record struct Sample(nint Device, nint Display, long At, int Buttons, double X, double Y, bool Cancelled = false);
    private sealed class Press(Sample s)
    {
        public nint Display = s.Display;
        public long Started = s.At, Last = s.At;
        public double X = s.X, Y = s.Y;
        public int Buttons;
        public bool Armed, Rejected;
    }
    private readonly Dictionary<nint, Press> _presses = [];
    public void Reset() => _presses.Clear();
    public void Remove(nint device) => _presses.Remove(device);
    public bool Accept(Sample s)
    {
        if (s.Cancelled || !double.IsFinite(s.X + s.Y)) { _presses.Remove(s.Device); return false; }
        _presses.TryGetValue(s.Device, out var p);
        if (p is not null && (p.Display != s.Display || s.At < p.Last)) { _presses.Remove(s.Device); return false; }
        if (s.Buttons == 0)
        {
            _presses.Remove(s.Device);
            return p is not null && p.Armed && !p.Rejected && s.At - p.Started <= 5000;
        }
        if (p is null) _presses[s.Device] = p = new(s);
        if ((s.Buttons & ~3) != 0 || s.At - p.Started > 5000) p.Rejected = true;
        if (!p.Armed && Math.Sqrt(Math.Pow(s.X - p.X, 2) + Math.Pow(s.Y - p.Y, 2)) > 12) p.Rejected = true;
        if (!p.Armed && (p.Buttons & s.Buttons) != p.Buttons) p.Rejected = true;
        if ((s.Buttons & 3) == 3 && !p.Armed)
        {
            if (s.At - p.Started <= 150 && !p.Rejected) p.Armed = true;
            else p.Rejected = true;
        }
        p.Buttons = s.Buttons; p.Last = s.At;
        return false;
    }
}
