namespace PulseOfTime;

/// The four places the DualSense touches you: L2 / R2 under the fingertips (the front) and the left / right
/// grip coils in the palms (the back). Anything with a direction is spread over the four, so a blow from the
/// front-right trembles in R2, one from behind lands in the palms.
///
/// The triggers can't play a waveform, only an effect per HID report (~60 per second), so their part is an
/// envelope of vibration bursts. A "tremor" keeps the trigger's resistance: it alternates frame by frame
/// between the vibration and the resistance underneath, so the trigger stays firm and shakes at once.
sealed class Quad
{
    readonly record struct Ev(float L, float R, int Freq, long Start, long End, bool Decay, bool Tremor);
    readonly List<Ev> _ev = new();
    long _frame;

    /// A trigger burst: amplitude 0..8 per side, frequency Hz, length; delay to schedule it later.
    public void Pulse(long now, float ampL, float ampR, int freq, int ms, int delay = 0, bool decay = true, bool tremor = false)
    {
        if (ampL < 0.5f && ampR < 0.5f) return;
        _ev.Add(new Ev(ampL, ampR, freq, now + delay, now + delay + ms, decay, tremor));
    }

    /// Places a direction on the four points. side -1 left .. 1 right, front -1 behind .. 1 ahead.
    /// Returns the palm strength (more from behind), the palm pan and the fingertip amplitudes (only from ahead).
    public static (float palm, float pan, float l2, float r2) Place(float side, float front, float strength)
    {
        float ahead = Math.Max(0, front), behind = Math.Max(0, -front);
        float palm = strength * (0.5f + 0.5f * behind);
        float trig = 8 * strength * ahead;
        return (palm, side, trig * Math.Clamp(1 - side, 0, 1), trig * Math.Clamp(1 + side, 0, 1));
    }

    /// Lays the active bursts over the triggers' resistance effects for this frame.
    public (byte[] r, byte[] l) Apply(long now, byte[] r, byte[] l)
    {
        _frame++;
        float gain = Tuning.Get("triggers.tremor");
        float aL = 0, aR = 0; int fL = 40, fR = 40; bool tL = false, tR = false;
        for (int i = _ev.Count - 1; i >= 0; i--)
        {
            var e = _ev[i];
            if (now >= e.End) { _ev.RemoveAt(i); continue; }
            if (now < e.Start) continue;
            float k = e.Decay ? 1 - (now - e.Start) / (float)Math.Max(1, e.End - e.Start) * 0.7f : 1;
            if (e.L * k > aL) { aL = e.L * k; fL = e.Freq; tL = e.Tremor; }
            if (e.R * k > aR) { aR = e.R * k; fR = e.Freq; tR = e.Tremor; }
        }
        aL *= gain; aR *= gain;
        bool odd = (_frame & 1) == 1;
        if (aL >= 1 && !(tL && odd)) l = Effects.Vibration(0, (int)Math.Round(Math.Min(8, aL)), fL);
        if (aR >= 1 && !(tR && odd)) r = Effects.Vibration(0, (int)Math.Round(Math.Min(8, aR)), fR);
        return (r, l);
    }

    public bool Busy => _ev.Count > 0;
}
