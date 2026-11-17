using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PulseOfTime;

/// DualSense voice-coil haptics in the style of native PS5 games: everything is an audio signal on
/// channels 3/4 of the controller's USB audio device (left / right actuator).
///
/// Layer 1, the game's own sound turned into touch (so every surface, impact and shockwave the game
/// sounds out is felt, in sync and from the side it comes from):
///   low  (< 140 Hz)      booms, stomps, explosions: the waveform itself, transients emphasised
///   mid  (180-1200 Hz)   wood, earth, body hits: envelope on a 95 Hz body carrier
///   high (> 1.8 kHz)     gravel, sand, metal, water, grass: envelope on band-limited noise, crossfaded
///                        from a dull 190 Hz grain to a crisp 330 Hz one as the sound gets brighter
///   An onset gate (fast vs slow envelope) passes changes and holds back sustained sound (music beds,
///   ambience), and a slow auto-level keeps it independent of Lichii's volume.
/// Layer 2, procedural effects from game state and the player's own actions (Daemon raises them, the
///   shapes are in HapticFx.cs), panned to the side they come from, plus continuous ones: the bow string
///   following the real R2 pull, the spin attack charge.
/// Channels 1/2 (controller speaker / headphone jack) stay silent: the speaker was removed on request.
/// Layer 3 is Lichii's own HD-rumble haptics, which Windows mixes in on the same device.
sealed partial class Haptics : IDisposable
{
    const int Rate = ProcessLoopback.Rate;
    public float Gain = 1.0f;          // master
    public float SoundGain = 1.0f;     // layer 1
    public float MusicSuppress = 1.0f; // onset gate strength, 0 = pass sustained sound too

    readonly WasapiOut _out;
    readonly ProcessLoopback? _in;
    readonly float[] _ring = new float[Rate * 2]; // stereo, 0.5 s
    long _w, _r;                                  // frames written / read
    readonly object _lock = new();
    readonly ConcurrentQueue<HVoice> _pending = new();
    readonly List<HVoice> _voices = new();
    public volatile float Charge;      // spin attack charge 0..1
    public volatile float Impact;      // peak of sudden mid/high sound since the daemon last reset it
    public volatile float LowImpact;   // the same for the low band (explosions, stomps, shockwaves)
    public volatile float ImpactL, ImpactR; // sudden mid/high sound per stereo side (audio -> trigger ticks)
    double _chargePhase;
    readonly Side _l = new(), _r2 = new();
    public volatile bool Aiming;
    public volatile bool Rubber;     // slingshot (child) instead of a bow string
    public float LowBoost = 1f;      // iron boots: heavier steps and impacts
    public volatile float Pull = -1; // R2 travel 0..1 from the controller, -1 = unknown
    public float PeakIn, PeakOut; // since last read, for --debug
    public double SumSq; public long SumN;
    public float TakeRms() { float v = SumN > 0 ? (float)Math.Sqrt(SumSq / SumN) : 0; SumSq = 0; SumN = 0; return v; }
    float _tension; double _creakPhase, _t;
    readonly Random _rng = new(1);

    public string Device { get; }

    Haptics(MMDevice dev, int pid)
    {
        Device = dev.FriendlyName;
        if (pid != 0) { _in = ProcessLoopback.Open(pid); _in.Data += OnCapture; } // 0: no game sound (demo)
        // The device's own mix format (WaveFormatExtensible, 32-bit float, quad channel mask): the only
        // 4-channel format shared mode is sure to take.
        var mix = dev.AudioClient.MixFormat;
        if (mix.BitsPerSample != 32) throw new NotSupportedException($"mix format {mix} is not 32-bit float");
        var provider = new Provider(this, mix);
        _out = new WasapiOut(dev, AudioClientShareMode.Shared, true, 10);
        _out.Init(provider);
        _out.PlaybackStopped += (_, _) => Stopped = true;
    }

    /// DualSense audio endpoint with 4 channels (Windows "Quadraphonic"), or null.
    public static MMDevice? FindDevice()
    {
        using var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            if (d.FriendlyName.Contains("DualSense", StringComparison.OrdinalIgnoreCase) && d.AudioClient.MixFormat.Channels >= 4)
                return d;
        return null;
    }

    public static Haptics? Start(int lichiiPid, out string error)
    {
        error = "";
        var dev = FindDevice();
        if (dev == null)
        {
            error = "DualSense audio device with 4 channels not found (USB needed; Sound settings: DualSense speaker = Quadraphonic).";
            return null;
        }
        try
        {
            var h = new Haptics(dev, lichiiPid);
            h._in?.Start();
            h._out.Play();
            h.Play(Fx.Hello);
            return h;
        }
        catch (Exception e) { error = e.Message + " @ " + string.Join(" < ", (e.StackTrace ?? "").Split('\n').Take(3).Select(l => l.Trim())); return null; }
    }

    void OnCapture(float[] buf, int frames)
    {
        lock (_lock)
        {
            for (int i = 0; i < frames; i++)
            {
                long k = (_w % (Rate)) * 2;
                _ring[k] = buf[2 * i]; _ring[k + 1] = buf[2 * i + 1];
                _w++;
            }
            if (_w - _r > Rate / 2) _r = _w - Rate / 2;
        }
    }

    // ---- procedural voices (HapticFx.cs) ---------------------------------------------------

    int _seed;
    public float FxScale = 1f;                                  // all effects (the dream world softens them)
    public static Action<Fx, float, int, float>? Recorder;      // every effect played, for "PulseOfTime replay"
    readonly Dictionary<Fx, (long t, int n)> _habit = new();

    /// strength 0.1..1.5; n: repeat count / variant; pan -1 left .. 1 right.
    public void Play(Fx fx, float strength = 1f, int n = 1, float pan = 0)
    {
        Recorder?.Invoke(fx, strength, n, pan);
        float g = Math.Clamp(Tuning.Of(fx), 0, 3) * FxScale * Habit(fx);
        _pending.Enqueue(new HVoice(fx, Math.Clamp(strength, 0.1f, 1.5f), Math.Clamp(n, 1, 6), Math.Clamp(pan, -1, 1), Interlocked.Increment(ref _seed)) { G = g });
    }

    /// The same effect again within 2 s is 8 % softer each time (down to 65 %), so a long fight stays crisp
    /// instead of numbing; rhythms and meters (steps, grips, ticks, notes, beats) always stay at full strength.
    float Habit(Fx fx)
    {
        if (fx is Fx.Grip or Fx.Footstep or Fx.Puddle or Fx.NoteOn or Fx.MenuTick or Fx.ItemTick or Fx.TextTick or Fx.Heartbeat or Fx.Ping
            or Fx.Sparkle or Fx.Coin or Fx.ProfileTick or Fx.TimerTick or Fx.Detent or Fx.AimTick or Fx.Swing or Fx.SwingHeavy or Fx.ChainJump or Fx.Peck or Fx.Debris or Fx.Hello
            or Fx.EnemyHit or Fx.Hit or Fx.ShieldBlock or Fx.Blocked or Fx.Finisher) // combat stays punchy
            return 1;
        long now = Environment.TickCount64;
        lock (_habit)
        {
            int k = _habit.TryGetValue(fx, out var h) && now - h.t < 2000 ? h.n + 1 : 0;
            _habit[fx] = (now, k);
            return Math.Max(0.65f, 1 - 0.08f * k);
        }
    }

    /// The audio device went away (controller unplugged): the daemon starts a new one.
    public volatile bool Stopped;

    // ---- sound -> haptics ----------------------------------------------------------------

    sealed class Side
    {
        public readonly Biquad Lp1 = Biquad.LowPass(140), Lp2 = Biquad.LowPass(140);
        public readonly Biquad MHp = Biquad.HighPass(180), MLp = Biquad.LowPass(1200);
        public readonly Biquad Hp1 = Biquad.HighPass(1800), Hp2 = Biquad.HighPass(1800);
        public readonly Biquad GrainDull = Biquad.BandPass(190, 1.4), GrainBright = Biquad.BandPass(330, 1.6);
        public readonly Follower LowF = new(0.002, 0.040), MidF = new(0.001, 0.030), HighF = new(0.0007, 0.018);
        // slow envelopes rise in 150 ms and fall in 600 ms, so a steady sound keeps its gate shut
        public readonly Follower LowS = new(0.150, 0.600), MidS = new(0.150, 0.600), HighS = new(0.150, 0.600);
        public readonly Follower GateL = new(0.001, 0.060), GateM = new(0.001, 0.050), GateH = new(0.0005, 0.035);
        public double Phase;
    }

    // Long-term peak per band (shared by both sides). It only ever turns loud games down, never quiet
    // ones up: boosting quiet ambience was what made the coils hum all the time.
    float _normLow, _normMid, _normHigh;
    const float RefLow = 0.12f, RefMid = 0.08f, RefHigh = 0.04f;     // full-scale transient per band
    const float FloorLow = 0.012f, FloorMid = 0.008f, FloorHigh = 0.004f; // below this: silence

    float SoundSide(Side s, float x, float bright, bool right)
    {
        float low = s.Lp2.Run(s.Lp1.Run(x));
        float mid = s.MLp.Run(s.MHp.Run(x));
        float high = s.Hp2.Run(s.Hp1.Run(x));

        float eL = s.LowF.Run(Math.Abs(low)), eM = s.MidF.Run(Math.Abs(mid)), eH = s.HighF.Run(Math.Abs(high));
        float sL = s.LowS.Run(eL), sM = s.MidS.Run(eM), sH = s.HighS.Run(eH);

        float thr = 1.4f + 0.8f * MusicSuppress; // a transient must be this many times the recent level
        float gL = s.GateL.Run(Onset(eL, sL, thr, FloorLow)), gM = s.GateM.Run(Onset(eM, sM, thr, FloorMid)), gH = s.GateH.Run(Onset(eH, sH, thr, FloorHigh));

        const float decay = 0.999997f; // ~7 s
        _normLow = Math.Max(_normLow * decay, eL); _normMid = Math.Max(_normMid * decay, eM); _normHigh = Math.Max(_normHigh * decay, eH);

        float outLow = low / Math.Max(_normLow, RefLow) * gL * 0.9f * LowBoost;
        s.Phase += 95.0 / Rate;
        float body = (float)(Math.Sin(2 * Math.PI * s.Phase) + 0.25 * Math.Sin(4 * Math.PI * s.Phase));
        float outMid = eM / Math.Max(_normMid, RefMid) * gM * body * 0.6f;
        float white = (float)(_rng.NextDouble() * 2 - 1);
        float noise = (s.GrainDull.Run(white) * (1 - bright) + s.GrainBright.Run(white) * bright) * 4.0f;
        float outHigh = eH / Math.Max(_normHigh, RefHigh) * gH * noise * 0.6f;
        // impact meter for the daemon (shield blocks): how hard a sudden mid/high sound just hit
        float imp = gM * eM / Math.Max(_normMid, RefMid) + gH * eH / Math.Max(_normHigh, RefHigh);
        if (imp > Impact) Impact = imp;
        if (right) { if (imp > ImpactR) ImpactR = imp; } else if (imp > ImpactL) ImpactL = imp;
        float lowImp = gL * eL / Math.Max(_normLow, RefLow);
        if (lowImp > LowImpact) LowImpact = lowImp; // big booms: the daemon shakes the triggers too
        if (Muffle) { outHigh *= 0.2f; outMid *= 0.55f; outLow *= 1.3f; } // underwater: everything dull and heavy
        return (outLow + outMid + outHigh) * SoundGain;
    }

    /// 0 for steady sound or below the floor, rising to 1 as the band jumps well above its recent level.
    static float Onset(float fast, float slow, float thr, float floor)
    {
        if (fast < floor) return 0;
        float ratio = fast / (slow + 1e-6f);
        return Math.Clamp((ratio - thr) / thr, 0, 1);
    }

    // ---- render ----------------------------------------------------------------------------

    void Render(float[] buf, int offset, int frames, int ch)
    {
        while (_pending.TryDequeue(out var v)) _voices.Add(v);
        while (_voices.Count > 14) _voices.RemoveAt(0); // the oldest goes first: never a mush of too many at once
        for (int i = 0; i < _loopGain.Length; i++) _loopGain[i] = Tuning.Of((Loop)i);
        UpdateDuck();
        float creakGain = Tuning.Get("bow.creak"), chargeGain = Tuning.Get("spin.charge");
        // no texture wanted and none fading out: skip the whole texture engine for this block
        bool loopsIdle = _grains.Count == 0;
        if (loopsIdle) for (int i = 0; i < _loopLvl.Length; i++) if (Loops[i] != 0 || _loopLvl[i] > 0.001f) { loopsIdle = false; break; }
        lock (_lock)
        {
            // keep latency low: never more than ~25 ms behind the game
            if (_w - _r > Rate / 40 + frames) _r = _w - Rate / 40 - frames;
            for (int i = 0; i < frames; i++)
            {
                float inL = 0, inR = 0;
                if (_r < _w) { long k = (_r % Rate) * 2; inL = _ring[k]; inR = _ring[k + 1]; _r++; }

                // brightness: share of high band in the mix (metal/gravel crisp, earth/wood dull)
                float bright = Math.Clamp(_l.HighF.Value / (_l.MidF.Value + _l.HighF.Value + 1e-6f), 0, 1);
                float l = SoundSide(_l, inL, bright, false) * _duckLvl, r = SoundSide(_r2, inR, bright, true) * _duckLvl;

                // bow draw: creaking string under the right hand, tension builds while aiming
                _t += 1.0 / Rate;
                // tension follows the real R2 pull when known (Pull >= 0), else builds over 0.9 s
                float target = Pull >= 0 ? Pull : 1f;
                _tension = Aiming ? (_tension < target ? Math.Min(target, _tension + 1f / (0.25f * Rate)) : Math.Max(target, _tension - 1f / (0.05f * Rate)))
                                  : Math.Max(0, _tension - 1f / (0.08f * Rate));
                if (_tension > 0)
                {
                    _creakPhase += (14 + 22 * _tension) / Rate;
                    double g = _creakPhase - Math.Floor(_creakPhase);
                    // bow string creaks at 230 Hz; the slingshot's rubber stretches lower and smoother
                    double pitch = Rubber ? 140 + 60 * _tension : 230;
                    float grain = g < 0.12 ? (float)(Math.Sin(Math.PI * g / 0.12) * Math.Sin(2 * Math.PI * pitch * _t)) : 0;
                    r += grain * (0.12f + 0.4f * _tension) * creakGain; l += grain * 0.08f * _tension * creakGain;
                }

                // spin attack charging: a buzz that climbs in pitch and strength, trembling faster as it fills
                float c0 = Charge;
                if (c0 > 0)
                {
                    _chargePhase += (60 + 140 * c0) / Rate;
                    float buzz = (float)Math.Sin(2 * Math.PI * _chargePhase) * (0.08f + 0.35f * c0) * (0.75f + 0.25f * (float)Math.Sin(2 * Math.PI * (8 + 14 * c0) * _t)) * chargeGain;
                    l += buzz; r += buzz;
                }

                if (!loopsIdle) RenderLoops(ref l, ref r);

                for (int v = _voices.Count - 1; v >= 0; v--)
                {
                    var (vl, vr, _) = _voices[v].Next(out bool done); // the effects' speaker part is not played: controller speaker removed on request
                    l += vl; r += vr;
                    if (done) _voices.RemoveAt(v);
                }

                l = SoftClip(l * Gain) * 0.8f; r = SoftClip(r * Gain) * 0.8f; // 0.8: keeps the coils cool (as Lichii)
                PeakIn = Math.Max(PeakIn, Math.Max(Math.Abs(inL), Math.Abs(inR)));
                PeakOut = Math.Max(PeakOut, Math.Max(Math.Abs(l), Math.Abs(r)));
                SumSq += l * l + r * r; SumN += 2;
                int b = offset + i * ch;
                for (int c = 0; c < ch; c++) buf[b + c] = 0;
                // channels 1/2 (speaker / headphone jack) stay silent
                buf[b + 2] = l; buf[b + 3] = r;
            }
        }
    }

    static float SoftClip(float x) => MathF.Tanh(x);

    sealed class Provider : IWaveProvider
    {
        readonly Haptics _h;
        float[] _f = new float[4096];
        public WaveFormat WaveFormat { get; }
        public Provider(Haptics h, WaveFormat mix) { _h = h; WaveFormat = mix; }
        public int Read(byte[] buffer, int offset, int count)
        {
            int ch = WaveFormat.Channels, samples = count / 4;
            if (_f.Length < samples) _f = new float[samples];
            _h.Render(_f, 0, samples / ch, ch);
            Buffer.BlockCopy(_f, 0, buffer, offset, samples * 4);
            return count;
        }
    }

    public void Dispose()
    {
        try { _out.Stop(); } catch { }
        _out.Dispose();
        _in?.Dispose();
    }
}

sealed class Follower
{
    readonly float _a, _r;
    public float Value;
    public Follower(double attackS, double releaseS)
    {
        _a = (float)(1 - Math.Exp(-1.0 / (attackS * ProcessLoopback.Rate)));
        _r = (float)(1 - Math.Exp(-1.0 / (releaseS * ProcessLoopback.Rate)));
    }
    // values decaying in silence would sink into denormal floats, which the CPU handles ~100x slower: flush them to 0
    public float Run(float x) { Value += (x - Value) * (x > Value ? _a : _r); if (Math.Abs(Value) < 1e-15f) Value = 0; return Value; }
}

/// RBJ cookbook biquad.
sealed class Biquad
{
    readonly float b0, b1, b2, a1, a2;
    float z1, z2;
    Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    { this.b0 = (float)(b0 / a0); this.b1 = (float)(b1 / a0); this.b2 = (float)(b2 / a0); this.a1 = (float)(a1 / a0); this.a2 = (float)(a2 / a0); }

    static (double cos, double alpha) W(double f, double q)
    {
        double w = 2 * Math.PI * f / ProcessLoopback.Rate;
        return (Math.Cos(w), Math.Sin(w) / (2 * q));
    }
    public static Biquad LowPass(double f, double q = 0.7071) { var (c, a) = W(f, q); return new((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + a, -2 * c, 1 - a); }
    public static Biquad HighPass(double f, double q = 0.7071) { var (c, a) = W(f, q); return new((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + a, -2 * c, 1 - a); }
    public static Biquad BandPass(double f, double q) { var (c, a) = W(f, q); return new(a, 0, -a, 1 + a, -2 * c, 1 - a); }

    public float Run(float x)
    {
        float y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        if (Math.Abs(z1) < 1e-15f) z1 = 0; // no denormals (see Follower)
        if (Math.Abs(z2) < 1e-15f) z2 = 0;
        return y;
    }
}
