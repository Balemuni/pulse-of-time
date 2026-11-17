namespace PulseOfTime;

/// Continuous textures that last as long as a state does (the daemon sets each one's level every frame,
/// 0 = off; they fade in and out on their own). Each is placed across the two coils so it moves: waves
/// wash from one hand to the other, rain lands anywhere between them, bubbles rise on alternating sides.
public enum Loop
{
    Swim,       // water flow + one wave per stroke washing across the hands (SwimRate = strokes/s)
    Underwater, // pressure hum + bubbles rising at random spots
    Rain,       // droplets scattered over both hands (RainRoof: on a roof instead: dull drumming)
    FallWind,   // air rushing past while falling, grows with speed
    Burn,       // crackling fire on Link
    Shock,      // electric buzz in bursts
    Frozen,     // ice: stiff, cracking
    Squeeze,    // swallowed by a Like Like: slow peristaltic waves
    Strain,     // hanging from a ledge: muscle tremor
    Grind,      // pushing / pulling a block: stone grinding
    Slide,      // sliding down a slope: scraping
    Wind,       // a gust pushing Link: noise swaying between hands
    Hooves,     // Epona: da-da-DUM, rate from speed (HoofRate)
    Note,       // an ocarina note held (NoteHz)
    Crawl,      // crawling through a tunnel: scraping hand over hand
    Fuse,       // a lit bomb in the hand: the fuse sizzling in the right palm
    Motor,      // riding a platform / lift: a motor hum, higher going up (MotorUp)
    Warmth,     // near fire: slow warm swells from its side (WarmPan)
    Danger,     // something heavy rolling at you: a rumble from its side (DangerPan), louder as it nears
    Mist,       // near a waterfall: fine spray from its side (MistPan)
    Sink,       // sinking in quicksand: a slow pull downward
    Quake,      // the ground trembles under both hands, wandering side to side (castle collapse, Ganon's tower)
    Swarm,      // angry cuccos: pecks from every side, ever faster
    Flap,       // holding a cucco and gliding: wing beats, one hand then the other
    Rock,       // on the Shadow Temple's ship: a slow sway with creaking wood
    Ghost,      // a Poe close by: a cold shimmer from its side (GhostPan)
    Drag,       // a hooked fish pulls the line: a rumble from where it swims (DragPan)
    Count,
}

sealed partial class Haptics
{
    public readonly float[] Loops = new float[(int)Loop.Count];   // targets, set by the daemon
    readonly float[] _loopLvl = new float[(int)Loop.Count];       // smoothed
    readonly float[] _loopGain = new float[(int)Loop.Count];      // Tuning factors, refreshed per audio block
    public volatile float SwimRate = 0.8f, HoofRate = 2f, NoteHz = 200f, LoopSpeed;
    public volatile bool RainRoof, Muffle;                         // Muffle: underwater, the game sound layer goes dull
    public double HoofPhase, SwimPhase;                            // cycles so far (the triggers are synced to them)
    public volatile float MotorUp, WarmPan, DangerPan, MistPan;    // direction of the directional textures
    public volatile float GhostPan, DragPan, HoofPan;              // HoofPan: Epona leans out of a turn
    double _swarmNext, _flapPh, _creakNext;
    readonly Biquad _wMist = Biquad.BandPass(260, 1.2), _wRumL = Biquad.BandPass(38, 0.9), _wRumR = Biquad.BandPass(38, 0.9);
    double _motorPh, _mistNext;

    readonly Biquad _wFlowL = Biquad.BandPass(55, 0.9), _wFlowR = Biquad.BandPass(55, 0.9);
    readonly Biquad _wAirL = Biquad.BandPass(120, 0.8), _wAirR = Biquad.BandPass(120, 0.8);
    readonly Biquad _wGrind = Biquad.BandPass(70, 1.2), _wSlide = Biquad.BandPass(180, 1.0), _wWind = Biquad.BandPass(100, 0.7);
    double _swimPh, _hoofPh, _notePh, _sqPh, _rainNext, _bubNext, _crackNext, _fireNext, _crawlPh;
    readonly List<(double t0, double f, float amp, float pan, double len)> _grains = new();

    float N() => (float)(_rng.NextDouble() * 2 - 1);
    double Exp(double rate) => -Math.Log(1 - _rng.NextDouble()) / Math.Max(rate, 0.01);

    /// Adds the loops' sound for one sample to l / r.
    void RenderLoops(ref float l, ref float r)
    {
        const float up = 1f / (0.06f * Rate), down = 1f / (0.25f * Rate);
        bool any = false;
        for (int i = 0; i < _loopLvl.Length; i++)
        {
            float tgt = Math.Clamp(Loops[i] * _loopGain[i], 0, 2.5f);
            _loopLvl[i] += Math.Clamp(tgt - _loopLvl[i], -down, up);
            any |= _loopLvl[i] > 0.001f;
        }
        if (!any && _grains.Count == 0) return;
        double t = _t;
        float L = 0, R = 0;

        float sw = _loopLvl[(int)Loop.Swim];
        if (sw > 0)
        {
            float flowL = _wFlowL.Run(N()) * 2.2f, flowR = _wFlowR.Run(N()) * 2.2f;
            _swimPh += SwimRate / Rate;
            SwimPhase = _swimPh;
            double ph = _swimPh % 1; bool odd = (int)_swimPh % 2 == 1;
            float wave = (float)Math.Pow(Math.Sin(Math.PI * ph), 2) * (0.3f + 0.15f * (float)Math.Sin(2 * Math.PI * 65 * t));
            float pos = (float)(odd ? 1 - ph : ph); // the wave washes from one hand to the other
            L += sw * (flowL * 0.35f + wave * (1 - pos) * 0.9f);
            R += sw * (flowR * 0.35f + wave * pos * 0.9f);
        }

        float uw = _loopLvl[(int)Loop.Underwater];
        if (uw > 0)
        {
            float hum = (float)Math.Sin(2 * Math.PI * 34 * t) * 0.22f * (0.8f + 0.2f * (float)Math.Sin(2 * Math.PI * 0.3 * t));
            L += uw * hum; R += uw * hum;
            if (t >= _bubNext) { _grains.Add((t, -1, 0.3f * uw, N(), 0.07)); _bubNext = t + Exp(7); } // f<0: rising chirp
        }

        float rn = _loopLvl[(int)Loop.Rain];
        if (rn > 0 && t >= _rainNext)
        {
            float pan = Math.Clamp(N() * 1.15f, -1, 1);
            if (RainRoof) _grains.Add((t, 110 + 30 * _rng.NextDouble(), (0.12f + 0.12f * (float)_rng.NextDouble()) * rn, pan, 0.012));
            else
            {
                bool big = _rng.NextDouble() < 0.08;
                _grains.Add((t, 230 + 110 * _rng.NextDouble(), (big ? 0.45f : 0.08f + 0.18f * (float)_rng.NextDouble()) * rn, pan, big ? 0.008 : 0.003));
            }
            _rainNext = t + Exp((RainRoof ? 18 : 30) + 70 * rn);
        }

        float fw = _loopLvl[(int)Loop.FallWind];
        if (fw > 0)
        {
            float wob = 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * 1.7 * t);
            L += fw * _wAirL.Run(N()) * 3f * (0.6f + 0.4f * wob); R += fw * _wAirR.Run(N()) * 3f * (1 - 0.4f * wob);
        }

        float bu = _loopLvl[(int)Loop.Burn];
        if (bu > 0)
        {
            if (t >= _fireNext) { _grains.Add((t, 200 + 120 * _rng.NextDouble(), 0.5f * bu, N(), 0.004)); _fireNext = t + Exp(45); }
            float hum = (float)Math.Sin(2 * Math.PI * 70 * t) * 0.18f * bu * (0.7f + 0.3f * N());
            L += hum; R += hum;
        }

        float sh = _loopLvl[(int)Loop.Shock];
        if (sh > 0)
        {
            float chop = ((int)(t * 15) % 2 == 0 ? 1f : 0.15f);
            float z = sh * chop * ((float)Math.Sin(2 * Math.PI * 290 * t) * 0.5f + (float)Math.Sin(2 * Math.PI * 50 * t) * 0.3f);
            L += z; R += z * ((int)(t * 7) % 2 == 0 ? 1f : 0.6f);
        }

        float fr = _loopLvl[(int)Loop.Frozen];
        if (fr > 0)
        {
            if (t >= _crackNext) { _grains.Add((t, 330 + 160 * _rng.NextDouble(), 0.4f * fr, N(), 0.01)); _crackNext = t + Exp(3); }
            float stiff = (float)Math.Sin(2 * Math.PI * 25 * t) * 0.12f * fr;
            L += stiff; R += stiff;
        }

        float sq = _loopLvl[(int)Loop.Squeeze];
        if (sq > 0)
        {
            _sqPh += 1.2 / Rate;
            double p = _sqPh % 1;
            float push = (float)Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * p)), 2) * (float)Math.Sin(2 * Math.PI * (55 + 15 * Math.Sin(2 * Math.PI * 9 * t)) * t) * 0.6f;
            L += sq * push * (float)(1 - p); R += sq * push * (float)p;
        }

        float st = _loopLvl[(int)Loop.Strain];
        if (st > 0)
        {
            float trem = (0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * 11 * t)) * (float)Math.Sin(2 * Math.PI * 140 * t) * 0.14f * st;
            L += trem; R += trem * 0.9f;
        }

        float gr = _loopLvl[(int)Loop.Grind];
        if (gr > 0)
        {
            float g = _wGrind.Run(N()) * 3.5f * gr * (0.5f + LoopSpeed);
            L += g; R += g;
        }

        float sl = _loopLvl[(int)Loop.Slide];
        if (sl > 0) { float g = _wSlide.Run(N()) * 2.5f * sl; L += g; R += g * 0.9f; }

        float wi = _loopLvl[(int)Loop.Wind];
        if (wi > 0)
        {
            float g = _wWind.Run(N()) * 3f * wi, a = 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * 0.4 * t);
            L += g * (1 - a); R += g * a;
        }

        float ho = _loopLvl[(int)Loop.Hooves];
        if (ho > 0)
        {
            _hoofPh += HoofRate / Rate;
            HoofPhase = _hoofPh;
            double p = _hoofPh % 1;
            // canter: hind left (left palm), hind right (right palm), the fore hooves land in the triggers
            // (the daemon reads HoofPhase and pulses L2 at 0.18, R2 at 0.36); the palms only echo them
            float h1 = Hann(p, 0.07) * 0.6f, h2 = Hann(p - 0.18, 0.07) * 0.5f, h3 = Hann(p - 0.36, 0.08) * 0.3f;
            float body = (float)Math.Sin(2 * Math.PI * 58 * t);
            float lean = Math.Clamp(HoofPan, -1, 1) * 0.45f; // the weight goes to the outside of a turn
            L += ho * body * (h1 + h3 * 0.5f) * (1 - lean); R += ho * body * (h2 + h3) * (1 + lean);
        }

        float nt = _loopLvl[(int)Loop.Note];
        if (nt > 0)
        {
            _notePh += NoteHz * (1 + 0.012 * Math.Sin(2 * Math.PI * 5.5 * t)) / Rate; // a little vibrato, like breath
            float v = (float)Math.Sin(2 * Math.PI * _notePh) * 0.35f * nt;
            L += v; R += v;
        }

        float cr = _loopLvl[(int)Loop.Crawl];
        if (cr > 0)
        {
            _crawlPh += (0.8 + 1.5 * LoopSpeed) / Rate;
            double p = _crawlPh % 1;
            float scrape = Hann(p, 0.3) * _wSlide.Run(N()) * 2f * cr;
            if ((int)_crawlPh % 2 == 0) L += scrape; else R += scrape;
        }

        float fu = _loopLvl[(int)Loop.Fuse];
        if (fu > 0)
        {
            if (t >= _fireNext) { _grains.Add((t, 280 + 120 * _rng.NextDouble(), 0.35f * fu, 0.7f + 0.3f * N(), 0.003)); _fireNext = t + Exp(90); }
            float hiss = _wSlide.Run(N()) * 1.2f * fu;
            R += hiss; L += hiss * 0.25f;
        }

        float mo = _loopLvl[(int)Loop.Motor];
        if (mo > 0)
        {
            _motorPh += (55 + 22 * MotorUp) / Rate;
            float hum = ((float)Math.Sin(2 * Math.PI * _motorPh) + 0.3f * (float)Math.Sin(4 * Math.PI * _motorPh)) * 0.3f * mo * (0.85f + 0.15f * (float)Math.Sin(2 * Math.PI * 7 * t));
            L += hum; R += hum;
        }

        float wa = _loopLvl[(int)Loop.Warmth];
        if (wa > 0)
        {
            float swell = (0.55f + 0.45f * (float)Math.Sin(2 * Math.PI * 0.7 * t)) * (float)Math.Sin(2 * Math.PI * 36 * t) * 0.3f * wa;
            if (t >= _fireNext && _rng.NextDouble() < 0.3) { _grains.Add((t, 210 + 80 * _rng.NextDouble(), 0.2f * wa, WarmPan, 0.004)); _fireNext = t + Exp(6); }
            L += swell * Math.Clamp(1 - WarmPan, 0.2f, 1); R += swell * Math.Clamp(1 + WarmPan, 0.2f, 1);
        }

        float da = _loopLvl[(int)Loop.Danger];
        if (da > 0)
        {
            float rl = _wRumL.Run(N()) * 4f * da, rr = _wRumR.Run(N()) * 4f * da;
            L += rl * Math.Clamp(1 - DangerPan, 0.15f, 1); R += rr * Math.Clamp(1 + DangerPan, 0.15f, 1);
        }

        float mi = _loopLvl[(int)Loop.Mist];
        if (mi > 0 && t >= _mistNext)
        {
            _grains.Add((t, 240 + 120 * _rng.NextDouble(), (0.06f + 0.1f * (float)_rng.NextDouble()) * mi, Math.Clamp(MistPan + 0.4f * N(), -1, 1), 0.003));
            _mistNext = t + Exp(25 + 50 * mi);
        }

        float si = _loopLvl[(int)Loop.Sink];
        if (si > 0)
        {
            float pull = (float)Math.Sin(2 * Math.PI * 28 * t) * 0.25f * si * (0.6f + 0.4f * (float)Math.Sin(2 * Math.PI * 0.5 * t)) + _wSlide.Run(N()) * 0.6f * si;
            L += pull; R += pull;
        }

        float qk = _loopLvl[(int)Loop.Quake];
        if (qk > 0)
        {
            float wob = 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * 3.3 * t);
            float body = (float)Math.Sin(2 * Math.PI * (34 + 6 * Math.Sin(2 * Math.PI * 9 * t)) * t) * 0.5f;
            L += qk * (_wRumL.Run(N()) * 3f + body) * (0.6f + 0.4f * wob); R += qk * (_wRumR.Run(N()) * 3f + body) * (1 - 0.4f * wob);
        }

        float sm = _loopLvl[(int)Loop.Swarm];
        if (sm > 0)
        {   // pecks land anywhere, faster as the swarm grows; wings flutter all around
            if (t >= _swarmNext) { _grains.Add((t, 250 + 40 * _rng.NextDouble(), 0.4f * sm, N(), 0.006)); _swarmNext = t + Exp(6 + 16 * sm); }
            float flutter = _wAirL.Run(N()) * 1.2f * sm * (0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * 13 * t));
            L += flutter; R += _wAirR.Run(N()) * 1.2f * sm * (0.5f - 0.5f * (float)Math.Sin(2 * Math.PI * 13 * t));
        }

        float fl = _loopLvl[(int)Loop.Flap];
        if (fl > 0)
        {   // wing beats, one hand then the other
            _flapPh += 4.5 / Rate;
            float beat = Hann(_flapPh % 1, 0.35) * (_wWind.Run(N()) * 2.5f + (float)Math.Sin(2 * Math.PI * 60 * t) * 0.2f) * fl;
            if ((int)_flapPh % 2 == 0) L += beat; else R += beat;
        }

        float rk = _loopLvl[(int)Loop.Rock];
        if (rk > 0)
        {   // the deck tilts slowly from side to side; the planks creak at the turn
            float a = (float)Math.Sin(2 * Math.PI * 0.22 * t);
            float body = (_wRumL.Run(N()) * 2f + (float)Math.Sin(2 * Math.PI * 30 * t) * 0.15f) * rk;
            L += body * (0.5f - 0.5f * a); R += body * (0.5f + 0.5f * a);
            if (t >= _creakNext && Math.Abs(a) > 0.9f) { _grains.Add((t, 170 + 30 * _rng.NextDouble(), 0.25f * rk, a, 0.012)); _creakNext = t + 0.6 + _rng.NextDouble(); }
        }

        float gh = _loopLvl[(int)Loop.Ghost];
        if (gh > 0)
        {
            float v = (float)Math.Sin(2 * Math.PI * (300 * t + 30 / (2 * Math.PI * 3) * Math.Sin(2 * Math.PI * 3 * t))) * 0.12f * gh * (0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * 0.7 * t))
                    + _wMist.Run(N()) * 0.4f * gh;
            L += v * Math.Clamp(1 - GhostPan, 0.15f, 1); R += v * Math.Clamp(1 + GhostPan, 0.15f, 1);
        }

        float dr = _loopLvl[(int)Loop.Drag];
        if (dr > 0)
        {
            float v = (_wRumR.Run(N()) * 3f + (float)Math.Sin(2 * Math.PI * 45 * t) * 0.2f) * dr * (0.6f + 0.4f * (float)Math.Sin(2 * Math.PI * 1.3 * t));
            L += v * Math.Clamp(1 - DragPan, 0.15f, 1); R += v * Math.Clamp(1 + DragPan, 0.15f, 1);
        }

        // short grains scattered across the hands (droplets, bubbles, crackles, ice cracks)
        for (int i = _grains.Count - 1; i >= 0; i--)
        {
            var g = _grains[i];
            double u = t - g.t0;
            if (u > g.len) { _grains.RemoveAt(i); continue; }
            float env = Hann(u, g.len);
            float s = g.f < 0 ? (float)Math.Sin(2 * Math.PI * (90 * u + 900 * u * u)) : (float)Math.Sin(2 * Math.PI * g.f * u);
            float v = s * env * g.amp;
            L += v * Math.Clamp(1 - g.pan, 0, 1); R += v * Math.Clamp(1 + g.pan, 0, 1);
        }
        l += L * _duckLvl; r += R * _duckLvl;
    }

    // ducking: while something important lands, textures and the sound layer step back so it cuts through
    long _duckUntil; float _duckLvl = 1;
    public void Duck(int ms) => _duckUntil = Math.Max(_duckUntil, Environment.TickCount64 + ms);
    /// Silence before a big moment: textures and game sound fade almost to nothing, so what comes next hits harder.
    public void Hush(int ms) => _hushUntil = Math.Max(_hushUntil, Environment.TickCount64 + ms);
    long _hushUntil;
    void UpdateDuck()
    {
        long now = Environment.TickCount64;
        float tgt = now < _hushUntil ? 0.03f : now < _duckUntil ? 0.45f : 1f;
        _duckLvl += (tgt - _duckLvl) * 0.15f;
    }

    static float Hann(double t, double len) => t < 0 || t > len ? 0 : (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * t / len));
}
