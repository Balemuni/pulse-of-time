namespace PulseOfTime;

/// Every procedural effect, synthesised per sample for the two voice coils (left, right) and the
/// controller's speaker. Built from a few shapes the actuators render well: sine bodies at 30-80 Hz
/// (weight), 150-330 Hz (crisp detail), band-limited noise grains (texture), pitch sweeps (motion).
public enum Fx
{
    Hello,
    // Link
    Hit, Heal, Heartbeat, Land, Jump, Roll, Swing, SpinRelease, ShieldBlock, LockOn, Grip, Spur, Revive,
    Death, DeathFire, DeathDrown, DeathShock,
    // weapons
    BowRelease, SlingRelease, DryFire,
    // enemies: hit, and a kill by family
    EnemyHit, KillPop, KillCrunch, KillBones, KillArmor, KillSquish, KillGhost, KillHeavy, KillMidBoss, KillBoss, Token,
    // pickups
    Coin, CoinSpend, AmmoGet, MagicUse, MagicFill, ItemGet, SongGet, KeyGet, BossKeyGet, MapGet, MedalGet, HeartContainer,
    // ambience (sparse, quiet)
    AmbLava, AmbDrip, AmbOrganic, AmbEerie, AmbThunder, AmbSand, AmbCrystal,
    // actions and reactions (from the game's action verbs)
    Splash, Slam, Thrust, Flip, DoorOpen, BootsClunk, Lift, Throw, Put, Bottle,
    DinFire, Nayru, Farore, Perfect, Repelled, GuardBreak, Shatter, Stagger, Down, Struggle, Abyss, Thunder,
    Boomerang, BoomerangCatch, HookshotFire, NoteOn, Chime,
    // gear, menus, the day, the senses
    SwordDraw, Sheathe, EquipSword, EquipShieldWood, EquipShieldMetal, EquipCloth, EquipHeavy, EquipHover, ItemTick,
    MenuOpen, MenuClose, MenuTick, MenuConfirm, MenuBack, PageFlip, TextTick,
    Sunrise, Nightfall, Ping, Puddle, HeatPulse, AirGasp,
    // what enemies do
    Telegraph, Alert, Appear, Stun, Blocked,
    // items used up
    NutFlash, StickSnap, ChuRoll,
    Footstep, // an iron boot coming down
    SwingHeavy, BladeRing, Finisher, Sparkle, Arrive, Unlock, ProfileTick,
    // v2: time, minigames, world, story
    TimeShift, TimerTick, TimeUp, StartShot, TargetHit, Miss, Fanfare, Catch, Bite, Cast,
    WaterRise, Collapse, Debris, ShipSink, Melt, Ignite,
    // v2: the remake's moves and Link's accidents
    Dash, Dodge, WallKick, ChainJump, SlideLand, QuickTurn, Crash, Slip, Hammer, PickUp,
    Terror, Grabbed, Breakfree, ChainRattle, Detent, Nock, AimTick, Compass,
    // v2: enemies
    ArmorBreak, WeakSpot, Projectile, Rage, Relief, Caught, SwordLost,
    // v2: things in the hand
    MaskOn, BottleCatch, Gulp, Peck,
}

/// What Link stands on (set from the place by the daemon): impacts get that material's texture on top.
public enum Mat { Default, Grass, Stone, Wood, Sand, Water, Ice, Flesh, Metal }

sealed class HVoice
{
    const double Rate = ProcessLoopback.Rate;
    public readonly Fx Kind; readonly float S; readonly int N; readonly float Pan; // S: varied a little per voice
    public float G = 1; // output factor from Tuning
    double T;
    readonly Random _rng;
    readonly Biquad _bp = Biquad.BandPass(230, 1.2), _bp2 = Biquad.BandPass(140, 1.0);
    float _burst;
    double _nextClick;

    public HVoice(Fx k, float s, int n, float pan, int seed)
    {
        Kind = k; S = s; N = n; Pan = pan; _rng = new Random(seed);
        // every repetition a little different (±5 % speed and pitch, ±6 % strength): no machine-gun feel;
        // the musical ones (notes, songs, sunrise) stay exact
        bool exact = k is Fx.NoteOn or Fx.Sunrise or Fx.Nightfall or Fx.Chime or Fx.Heartbeat or Fx.Hello or Fx.TimerTick or Fx.TimeShift or Fx.Detent or Fx.AimTick;
        _speed = exact ? 1 : 0.95 + 0.1 * _rng.NextDouble();
        if (!exact) S *= (float)(0.94 + 0.12 * _rng.NextDouble());
        // the body: the child is lighter and quicker (higher, shorter), the adult heavier (lower, longer)
        if (IsBody(k)) { _speed *= BodyRate; S *= BodyWeight; }
        _mat = Material;
    }
    readonly double _speed;
    readonly Mat _mat;
    public static volatile float BodyRate = 1, BodyWeight = 1;
    public static volatile Mat Material = Mat.Default;
    static bool IsBody(Fx k) => k is Fx.Land or Fx.Jump or Fx.Roll or Fx.Flip or Fx.Footstep or Fx.Down or Fx.Slam or Fx.Stagger or Fx.Grip
        or Fx.Swing or Fx.SwingHeavy or Fx.Hit or Fx.ChainJump or Fx.SlideLand or Fx.Dash or Fx.Dodge or Fx.WallKick or Fx.Crash or Fx.Slip or Fx.Puddle or Fx.QuickTurn;

    /// The ground's own texture on top of an impact (grass rustles, stone clicks, wood knocks, metal rings).
    float MatTex(double t, float a) => a * (_mat switch
    {
        Mat.Grass => Hann(t, 0.09) * _bp2.Run(Noise()) * 0.9f,
        Mat.Stone => Env(t, 0.0005, 0.012) * Sin(t, 210) * 0.35f,
        Mat.Wood => Env(t, 0.001, 0.04) * Sin(t, 120) * 0.4f,
        Mat.Sand => Hann(t, 0.12) * Grain() * 0.3f,
        Mat.Water => Env(t, 0.001, 0.03) * Sin(t, 85) * 0.3f + Hann(t, 0.1) * Grain() * 0.2f,
        Mat.Ice => Env(t, 0.0003, 0.03) * Sin(t, 330) * 0.3f,
        Mat.Flesh => Hann(t, 0.15) * (float)Math.Sin(2 * Math.PI * (45 * t + 20 / (2 * Math.PI * 14) * Math.Sin(2 * Math.PI * 14 * t))) * 0.35f,
        Mat.Metal => Env(t, 0.0005, 0.08) * (Sin(t, 250) + 0.5f * Sin(t, 375)) * 0.3f,
        _ => 0f,
    });

    static float Env(double t, double a, double d) => t < 0 ? 0 : (float)((t < a ? t / a : 1) * Math.Exp(-(t - Math.Min(t, a)) / d));
    static float Sin(double t, double f) => (float)Math.Sin(2 * Math.PI * f * t);
    static float Hann(double t, double len) => t < 0 || t > len ? 0 : (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * t / len));
    static float Chirp(double t, double f0, double f1, double len) => (float)Math.Sin(2 * Math.PI * (f0 * t + (f1 - f0) * t * t / (2 * len)));
    float Noise() => (float)(_rng.NextDouble() * 2 - 1);
    float Grain() => _bp.Run(Noise()) * 4f;
    /// Random clicks at `rate` per second: 1 for the sample a click starts.
    bool Click(double t, double rate) { if (t < _nextClick) return false; _nextClick = t + -Math.Log(1 - _rng.NextDouble()) / Math.Max(rate, 0.1); return true; }

    /// (left, right, speaker); done when past its length.
    public (float, float, float) Next(out bool done)
    {
        double t = T; T += _speed / Rate;
        float l = 0, r = 0, spk = 0;
        float pan = Pan;
        switch (Kind)
        {
            case Fx.Hello:
                l = Hann(t, 0.03) * Sin(t, 180) * 0.6f; r = Hann(t - 0.12, 0.03) * Sin(t, 180) * 0.6f;
                done = t > 0.2; break;

            // ---- Link ----
            case Fx.Hit: // thud + crack
                l = r = S * (Env(t, 0.003, 0.09) * Sin(t, 48) * 0.95f + Env(t, 0.001, 0.018) * Sin(t, 170) * 0.6f);
                done = t > 0.45; break;
            case Fx.Heal:
                for (int i = 0; i < 3; i++) { double u = t - i * 0.075; l += Hann(u, 0.045) * Sin(u, 250 + 45 * i) * 0.38f; }
                r = l; done = t > 0.3; break;
            case Fx.Heartbeat:
                l = r = S * (Hann(t, 0.075) * Sin(t, 55) + Hann(t - 0.17, 0.065) * Sin(t, 47) * 0.7f) * 0.9f;
                done = t > 0.25; break;
            case Fx.Land: // weight + grit
                l = r = S * (Env(t, 0.002, 0.06) * Sin(t, 60) + Env(t, 0.001, 0.012) * Sin(t, 210) * 0.4f + Env(t, 0.001, 0.03) * Grain() * 0.25f + MatTex(t, 1));
                done = t > 0.3; break;
            case Fx.Jump: // push off the ground, then lift
                l = r = Env(t, 0.002, 0.035) * Sin(t, 70) * 0.65f + Hann(t, 0.1) * Chirp(t, 110, 210, 0.1) * 0.22f;
                done = t > 0.12; break;
            case Fx.Roll: // tumble: three thumps going round the hands, ground scraping under them
            {
                for (int i = 0; i < 3; i++)
                {
                    float th = Env(t - i * 0.15, 0.003, 0.05) * Sin(t, 55) * 0.75f;
                    if (i == 1) r += th; else l += th;
                }
                float scrape = Hann(t, 0.48) * Grain() * 0.18f + MatTex(t - 0.15, 0.8f);
                l += scrape; r += scrape; done = t > 0.5; break;
            }
            case Fx.Swing: // sword cuts the air across the hands (N 1: left to right, 2: right to left)
            {
                double p = Math.Clamp(t / 0.15, 0, 1);
                float g = Hann(t, 0.15) * Grain() * 0.6f * S;
                float toR = (float)(N == 2 ? 1 - p : p);
                l = g * (1 - toR); r = g * toR; done = t > 0.16; break;
            }
            case Fx.SpinRelease: // the spin goes once round both hands, ending in a crack
            {
                float g = Hann(t, 0.5) * Grain() * 0.75f + Hann(t, 0.5) * Sin(t, 85) * 0.25f;
                float a = (float)Math.Sin(2 * Math.PI * t / 0.5);
                l = g * (0.5f - 0.5f * a); r = g * (0.5f + 0.5f * a);
                float crack = Env(t - 0.48, 0.001, 0.03) * Sin(t, 190) * 0.7f;
                l += crack; r += crack; done = t > 0.6; break;
            }
            case Fx.ShieldBlock: // metal ring + body shove, on the shield hand
                l = r = Env(t, 0.0005, 0.08) * (Sin(t, 205) + 0.4f * Sin(t, 410)) * 0.8f + Env(t, 0.002, 0.05) * Sin(t, 55) * 0.8f;
                done = t > 0.3; break;
            case Fx.LockOn: // two quick ticks
                l = r = (Hann(t, 0.008) + Hann(t - 0.04, 0.008)) * Sin(t, 270) * 0.5f; done = t > 0.06; break;
            case Fx.Grip: // a hand closes on a ledge or vine
                l = r = Hann(t, 0.03) * Sin(t, 150) * 0.45f + Env(t, 0.001, 0.01) * Grain() * 0.15f; done = t > 0.05; break;
            case Fx.Spur: // Epona kicks off: thump and rush
                l = r = Env(t, 0.002, 0.06) * Sin(t, 55) * 0.9f + Hann(t, 0.35) * Grain() * 0.3f; done = t > 0.36; break;
            case Fx.Revive: // a fairy: sparkles raining down, warmth swelling up
            {
                float sparkle = 0;
                if (Click(t, 40 * (1.2 - t))) _burst = 1;
                _burst *= 0.993f;
                sparkle = _burst * Sin(t, 300 + 60 * Noise()) * 0.5f;
                l = r = sparkle + Hann(t, 1.2) * Sin(t, 90) * 0.3f;
                spk = _burst * Sin(t, 2600 + 900 * _burst) * 0.4f;
                done = t > 1.2; break;
            }
            case Fx.Death:
                l = r = Env(t, 0.02, 0.6) * Sin(t, 40) * 0.9f * (0.7f + 0.3f * Sin(t, 6));
                done = t > 2.0; break;
            case Fx.DeathFire:
                if (Click(t, 250)) _burst = 1;
                _burst *= 0.995f;
                l = r = Env(t, 0.01, 0.8) * (_burst * Sin(t, 230) * 0.8f + Sin(t, 75) * 0.35f);
                done = t > 2.0; break;
            case Fx.DeathDrown:
            {
                double ph = t * 5 % 1;
                float bub = Hann(ph, 0.25) * Sin(t, 90 + 120 * ph) * Env(t, 0.05, 1.0);
                if ((int)(t * 5) % 2 == 0) l = bub; else r = bub;
                done = t > 2.2; break;
            }
            case Fx.DeathShock:
                l = r = ((int)(t * 12) % 2 == 0 ? 1f : 0.2f) * Sin(t, 290) * Env(t, 0.005, 0.5) * 0.8f;
                done = t > 1.5; break;

            // ---- weapons ----
            case Fx.BowRelease: // twang with falling pitch, mostly the right hand; "thwip" from the speaker
            {
                double f = 160 + 70 * Math.Exp(-t / 0.05);
                float v = Env(t, 0.001, 0.07) * (float)Math.Sin(2 * Math.PI * f * t) * S;
                r = v * 0.95f; l = v * 0.35f;
                spk = Env(t, 0.0005, 0.02) * Chirp(t, 1800, 700, 0.06) * 0.35f;
                done = t > 0.3; break;
            }
            case Fx.SlingRelease:
            {
                float v = Env(t, 0.0005, 0.025) * Sin(t, 260) * 0.8f + Env(t - 0.02, 0.005, 0.06) * Sin(t, 110) * 0.35f;
                r = v * S; l = v * 0.25f * S; spk = Env(t, 0.0005, 0.015) * Sin(t, 1400) * 0.25f; done = t > 0.2; break;
            }
            case Fx.DryFire:
                r = Hann(t, 0.012) * Sin(t, 320) * 0.5f; l = r * 0.2f; done = t > 0.03; break;

            // ---- enemies ----
            case Fx.EnemyHit: // the blade lands: crisp contact + body, from the enemy's side; N 2 = heavy / boss
                l = r = S * (Env(t, 0.0005, 0.012) * Sin(t, 185) * 0.9f + Env(t, 0.002, N == 2 ? 0.12 : 0.06) * Sin(t, N == 2 ? 40 : 52) * 0.8f);
                done = t > (N == 2 ? 0.4 : 0.25); break;
            case Fx.KillPop: // keese, bubbles, larvae: a small pop
                l = r = Env(t, 0.0005, 0.02) * Sin(t, 240) * 0.7f + Env(t, 0.001, 0.03) * Sin(t, 90) * 0.3f; done = t > 0.12; break;
            case Fx.KillCrunch: // plants, spiders: a burst of snapping fibres
                if (t < 0.12 && Click(t, 60)) _burst = 1;
                _burst *= 0.97f;
                l = r = _burst * Noise() * 0.6f + Env(t, 0.002, 0.05) * Sin(t, 65) * 0.5f; done = t > 0.25; break;
            case Fx.KillBones: // stalfos, stalchildren: bones rattle apart, then fall
            {
                if (Click(t, 70 * Math.Exp(-t / 0.2))) { _burst = 1; _nextPanR = _rng.NextDouble() < 0.5; }
                _burst *= 0.985f;
                float c = _burst * Sin(t, 300) * 0.55f;
                if (_nextPanR) r += c; else l += c;
                float drop = Env(t - 0.32, 0.002, 0.06) * Sin(t, 48) * 0.6f;
                l += drop; r += drop; done = t > 0.55; break;
            }
            case Fx.KillArmor: // iron knuckles, lizalfos, moblins: a clang, then the armour hits the floor
                l = r = Env(t, 0.0005, 0.15) * (Sin(t, 210) + 0.5f * Sin(t, 317)) * 0.6f + Env(t - 0.35, 0.003, 0.12) * Sin(t, 40) * 1.0f;
                done = t > 0.75; break;
            case Fx.KillSquish: // like-likes, jellies, octoroks: a wobble that collapses
                l = r = Hann(t, 0.4) * Sin(t, 70 + 30 * Math.Sin(2 * Math.PI * 12 * t)) * 0.7f; done = t > 0.4; break;
            case Fx.KillGhost: // poes: a shimmer that sinks and fades, drifting across
            {
                float v = Hann(t, 0.9) * Chirp(t, 320, 90, 0.9) * (0.5f + 0.5f * Sin(t, 9)) * 0.45f;
                float a = (float)(t / 0.9);
                l = v * (1 - a); r = v * a; spk = Hann(t, 0.9) * Chirp(t, 1600, 500, 0.9) * 0.08f; done = t > 0.9; break;
            }
            case Fx.KillHeavy: // dodongos, leevers, beamos: the body drops
                l = r = Env(t, 0.004, 0.2) * Sin(t, 36) * 1.1f + Env(t, 0.001, 0.03) * Sin(t, 160) * 0.5f; done = t > 0.6; break;
            case Fx.KillMidBoss: // shockwave and three aftershocks
                for (int i = 0; i < 4; i++) { double u = t - i * 0.28; l += Env(u, 0.003, 0.12) * Sin(u, 42 + 6 * i) * (1.1f - 0.25f * i); }
                l += Hann(t - 0.9, 0.5) * Chirp(t - 0.9, 120, 260, 0.5) * 0.3f;
                r = l; done = t > 1.5; break;
            case Fx.KillBoss: // the floor drops away, three slow beats, then the light
            {
                l = Env(t, 0.004, 0.35) * Sin(t, 32) * 1.2f + Env(t, 0.001, 0.05) * Sin(t, 150) * 0.6f;
                for (int i = 0; i < 3; i++) { double u = t - 0.7 - i * 0.45; l += Hann(u, 0.09) * Sin(u, 50) * (0.8f - 0.2f * i); }
                for (int i = 0; i < 3; i++) { double u = t - 2.2 - i * 0.12; l += Hann(u, 0.07) * Sin(u, 230 + 40 * i) * 0.5f; spk += Hann(u, 0.07) * Sin(u, 1800 + 400 * i) * 0.25f; }
                r = l; done = t > 2.7; break;
            }
            case Fx.Token: // gold skulltula token: three bright ticks
                for (int i = 0; i < 3; i++) { double u = t - i * 0.07; float c = Hann(u, 0.025) * Sin(u, 290 + 30 * i) * 0.5f; if (i % 2 == 0) r += c; else l += c; spk += Hann(u, 0.03) * Sin(u, 2400 + 300 * i) * 0.3f; }
                done = t > 0.25; break;

            // ---- pickups ----
            case Fx.Coin:
                for (int i = 0; i < N; i++)
                {
                    double u = t - i * 0.06;
                    float c = Hann(u, 0.022) * Sin(t, 300 + 25 * i) * 0.55f;
                    if (i % 2 == 0) r += c; else l += c;
                    spk += Hann(u, 0.035) * (Sin(u, 2600) + 0.5f * Sin(u, 3900)) * 0.22f;
                }
                done = t > N * 0.06 + 0.04; break;
            case Fx.CoinSpend:
                l = r = Hann(t, 0.03) * Sin(t, 220 - 800 * t) * 0.35f; done = t > 0.04; break;
            case Fx.AmmoGet:
                l = r = (Hann(t, 0.015) + Hann(t - 0.05, 0.015) * 0.7f) * Sin(t, 180) * 0.45f; done = t > 0.08; break;
            case Fx.MagicUse:
                l = r = Hann(t, 0.35) * Sin(t, 270 + 40 * Math.Sin(2 * Math.PI * 18 * t)) * 0.4f; done = t > 0.36; break;
            case Fx.MagicFill:
                l = r = Hann(t, 0.3) * Chirp(t, 150, 450, 0.3) * 0.35f; done = t > 0.31; break;
            case Fx.ItemGet: // the chest: a swell rising in both hands, then three bright pulses
            {
                double ramp = Math.Clamp(t / 0.8, 0, 1);
                l = r = (float)(ramp * ramp) * Hann(t, 1.6) * Chirp(t, 50, 140, 0.8) * 0.5f;
                for (int i = 0; i < 3; i++)
                {
                    double u = t - 0.85 - i * 0.15;
                    l += Hann(u, 0.06) * Sin(u, 260) * 0.6f;
                    spk += Hann(u, 0.08) * Sin(u, 1600 * Math.Pow(1.26, i)) * 0.25f;
                }
                r = l; done = t > 1.4; break;
            }
            case Fx.SongGet: // five soft rising pulses
                for (int i = 0; i < 5; i++) { double u = t - i * 0.2; l += Hann(u, 0.14) * Sin(u, 180 + 25 * i) * 0.4f; }
                r = l; done = t > 1.2; break;
            case Fx.KeyGet:
            case Fx.BossKeyGet:
                for (int i = 0; i < 2; i++)
                {
                    double u = t - i * 0.12;
                    l += Env(u, 0.0005, 0.05) * (Sin(u, 300) + 0.5f * Sin(u, 470)) * 0.5f;
                    spk += Env(u, 0.0005, 0.06) * (Sin(u, 3200) + 0.6f * Sin(u, 4100)) * 0.2f;
                }
                if (Kind == Fx.BossKeyGet) l += Env(t - 0.3, 0.004, 0.25) * Sin(t, 38) * 1.0f + Hann(t - 0.3, 0.8) * Chirp(t - 0.3, 60, 120, 0.8) * 0.3f;
                r = l; done = t > (Kind == Fx.BossKeyGet ? 1.2 : 0.35); break;
            case Fx.MapGet:
                l = r = (Hann(t, 0.04) + Hann(t - 0.1, 0.04)) * _bp2.Run(Noise()) * 2.5f; done = t > 0.15; break;
            case Fx.MedalGet: // a stone or medallion: long deep swell with shimmer, two strong pulses
            {
                double ramp = Math.Clamp(t / 1.5, 0, 1);
                l = (float)ramp * Hann(t, 2.6) * (Chirp(t, 40, 80, 1.5) * 0.6f + Grain() * 0.15f * (float)ramp);
                for (int i = 0; i < 2; i++) { double u = t - 1.7 - i * 0.35; l += Env(u, 0.003, 0.15) * Sin(u, 45) * 1.0f; }
                r = l; done = t > 2.5; break;
            }
            case Fx.HeartContainer: // three heartbeats, each stronger
                for (int i = 0; i < 3; i++) { double u = t - i * 0.45; l += (Hann(u, 0.08) * Sin(u, 55) + Hann(u - 0.17, 0.07) * Sin(u, 47) * 0.7f) * (0.5f + 0.25f * i); }
                r = l; done = t > 1.6; break;

            // ---- ambience ----
            case Fx.AmbLava: // a lava bubble somewhere below
                l = r = S * (Hann(t, 0.35) * Sin(t, 32) * 0.6f + Env(t - 0.3, 0.001, 0.02) * Sin(t, 120) * 0.3f); done = t > 0.4; break;
            case Fx.AmbDrip:
                l = r = S * (Env(t, 0.0005, 0.006) * Sin(t, 260) * 0.6f + Env(t, 0.002, 0.05) * Sin(t, 120) * 0.15f); done = t > 0.08; break;
            case Fx.AmbOrganic: // inside Jabu-Jabu: a slow wet pulse
                l = r = S * Hann(t, 0.3) * Sin(t, 40) * 0.6f; done = t > 0.3; break;
            case Fx.AmbEerie:
                l = r = S * Hann(t, 1.4) * (Sin(t, 28) * 0.6f + _bp2.Run(Noise()) * 0.6f); done = t > 1.4; break;
            case Fx.AmbThunder: // distant, rolling
                l = r = S * Hann(t, 1.8) * (float)(0.6 + 0.4 * Math.Sin(2 * Math.PI * 3 * t)) * (_bp2.Run(Noise()) * 1.5f + Sin(t, 34) * 0.4f); done = t > 1.8; break;
            case Fx.AmbSand: // a gust of sand grains
                if (Click(t, 35 * Hann(t, 0.8))) _burst = 1;
                _burst *= 0.96f;
                l = r = S * _burst * Grain() * 0.4f; done = t > 0.8; break;
            case Fx.AmbCrystal:
                l = r = S * Env(t, 0.0005, 0.06) * (Sin(t, 330) + 0.4f * Sin(t, 495)) * 0.4f; done = t > 0.15; break;

            // ---- actions and reactions ----
            case Fx.Splash: // breaking the water surface: a slap, then spray scattered over the hands
            {
                if (t < 0.25 && Click(t, 90 * (1 - t * 4))) { _burst = 1; _nextPanR = _rng.NextDouble() < 0.5; }
                _burst *= 0.97f;
                float spray = _burst * Grain() * 0.4f;
                float slap = Env(t, 0.001, 0.04) * Sin(t, 75) * 0.7f * S;
                l = slap + (_nextPanR ? spray * 0.4f : spray); r = slap + (_nextPanR ? spray : spray * 0.4f);
                done = t > 0.3; break;
            }
            case Fx.Slam: // a jump attack / plunge comes down: rising, then the impact through both hands
                l = r = Hann(t, 0.2) * Chirp(t, 90, 200, 0.2) * 0.25f + Env(t - 0.2, 0.002, 0.14) * Sin(t, 38) * 1.1f + Env(t - 0.2, 0.0005, 0.02) * Sin(t, 190) * 0.6f;
                done = t > 0.55; break;
            case Fx.Thrust: // a stab: one sharp forward poke
                l = r = Env(t, 0.0005, 0.03) * Sin(t, 210) * 0.6f + Env(t, 0.002, 0.05) * Sin(t, 85) * 0.4f; done = t > 0.12; break;
            case Fx.Flip: // backflip / side hop: lift, a sweep over, and the landing
            {
                float a = (float)Math.Clamp(t / 0.35, 0, 1);
                float g = Hann(t, 0.35) * Grain() * 0.35f;
                l = g * (N == 2 ? a : 1 - a); r = g * (N == 2 ? 1 - a : a);
                float land = Env(t - 0.35, 0.002, 0.05) * Sin(t, 60) * 0.6f; l += land; r += land;
                done = t > 0.45; break;
            }
            case Fx.DoorOpen: // a heavy door grinds open, then settles
                l = r = Hann(t, 0.9) * _bp2.Run(Noise()) * 1.4f + Env(t - 0.85, 0.003, 0.12) * Sin(t, 42) * 0.8f; done = t > 1.1; break;
            case Fx.BootsClunk:
                l = r = Env(t, 0.001, 0.05) * (Sin(t, 120) + 0.5f * Sin(t, 260)) * 0.6f + Env(t - 0.12, 0.001, 0.05) * Sin(t, 50) * 0.6f; done = t > 0.25; break;
            case Fx.Lift: // picking something heavy up: the strain swells in both hands
                l = r = Hann(t, 0.45) * Sin(t, 48) * 0.6f * (float)Math.Clamp(t / 0.3, 0, 1); done = t > 0.45; break;
            case Fx.Throw: // released: a whoosh away from you
            {
                float a = (float)Math.Clamp(t / 0.2, 0, 1);
                float g = Hann(t, 0.2) * Grain() * 0.5f;
                l = g * (1 - a * 0.5f); r = g * (0.5f + a * 0.5f); done = t > 0.22; break;
            }
            case Fx.Put:
                l = r = Env(t, 0.002, 0.05) * Sin(t, 55) * 0.5f; done = t > 0.15; break;
            case Fx.Bottle: // swish into the bottle, cork pop
                l = r = Hann(t, 0.18) * Grain() * 0.35f + Env(t - 0.2, 0.0005, 0.02) * Sin(t, 280) * 0.5f;
                spk = Env(t - 0.2, 0.0005, 0.03) * Sin(t, 1500) * 0.3f; done = t > 0.3; break;
            case Fx.DinFire: // Din's Fire: a shockwave from the centre out to both edges, burning as it goes
            {
                float ring = Env(t, 0.002, 0.25) * Sin(t, 36) * 1.1f;
                if (Click(t, 120 * Math.Exp(-t / 0.4))) _burst = 1;
                _burst *= 0.99f;
                l = r = ring + _burst * Sin(t, 220) * 0.4f + Hann(t, 1.0) * Grain() * 0.2f; done = t > 1.0; break;
            }
            case Fx.Nayru: // Nayru's Love: a crystal forms around you, ringing
                l = r = Hann(t, 1.2) * (Sin(t, 300) * 0.25f + Sin(t, 450) * 0.12f) * (0.6f + 0.4f * Sin(t, 7));
                spk = Hann(t, 1.2) * (Sin(t, 2400) + 0.5f * Sin(t, 3600)) * 0.12f; done = t > 1.2; break;
            case Fx.Farore: // Farore's Wind / warp: a swirl going round and rising
            {
                float g = Hann(t, 1.0) * Chirp(t, 80, 300, 1.0) * 0.45f;
                float a = (float)(0.5 + 0.5 * Math.Sin(2 * Math.PI * 3 * t));
                l = g * a; r = g * (1 - a); done = t > 1.0; break;
            }
            case Fx.Perfect: // a perfect guard: a bright clean ring on the shield hand and a snap
                l = r = Env(t, 0.0003, 0.12) * (Sin(t, 260) + 0.6f * Sin(t, 520)) * 0.7f + Env(t, 0.001, 0.02) * Sin(t, 120) * 0.6f;
                spk = Env(t, 0.0003, 0.15) * Sin(t, 3200) * 0.25f; done = t > 0.35; break;
            case Fx.Repelled: // the blade bounces off something hard: ping and a kick back into the sword hand
                l = r = Env(t, 0.0003, 0.07) * (Sin(t, 310) + 0.5f * Sin(t, 470)) * 0.6f + Env(t, 0.002, 0.04) * Sin(t, 70) * 0.5f; done = t > 0.25; break;
            case Fx.GuardBreak: // the guard is smashed open
                l = r = Env(t, 0.002, 0.2) * Sin(t, 34) * 1.2f + Env(t, 0.0005, 0.05) * Grain() * 0.6f; done = t > 0.5; break;
            case Fx.Shatter: // ice breaks: a burst of crystal shards
                if (t < 0.2 && Click(t, 150)) { _burst = 1; _nextPanR = _rng.NextDouble() < 0.5; }
                _burst *= 0.985f;
                { float c = _burst * Sin(t, 330 + 150 * Noise()) * 0.55f; if (_nextPanR) r += c; else l += c; }
                l += Env(t, 0.001, 0.05) * Sin(t, 60) * 0.5f; r += Env(t, 0.001, 0.05) * Sin(t, 60) * 0.5f;
                spk = _burst * Sin(t, 3000 + 1500 * Noise()) * 0.15f; done = t > 0.35; break;
            case Fx.Stagger: // knocked off balance: a sway left, right, left
                l = Hann(t, 0.5) * Sin(t, 50) * 0.6f * (float)(0.5 + 0.5 * Math.Sin(2 * Math.PI * 4 * t));
                r = Hann(t, 0.5) * Sin(t, 50) * 0.6f * (float)(0.5 - 0.5 * Math.Sin(2 * Math.PI * 4 * t)); done = t > 0.5; break;
            case Fx.Down: // thrown to the ground: body thud and a skid
                l = r = Env(t, 0.003, 0.12) * Sin(t, 40) * 1.0f + Hann(t - 0.05, 0.3) * _bp2.Run(Noise()) * 1.2f; done = t > 0.4; break;
            case Fx.Struggle: // shaking free: hard alternating jerks
                l = ((int)(t * 9) % 2 == 0 ? 1f : 0) * Sin(t, 65) * 0.7f * Hann(t, 0.6);
                r = ((int)(t * 9) % 2 == 1 ? 1f : 0) * Sin(t, 65) * 0.7f * Hann(t, 0.6); done = t > 0.6; break;
            case Fx.Abyss: // falling into the void: a long sinking sweep that fades to nothing
                l = r = Hann(t, 1.6) * Chirp(t, 140, 30, 1.6) * 0.55f * (float)(1 - t / 1.6); done = t > 1.6; break;
            case Fx.Thunder: // a strike close by: crack, then the roll
                l = r = Env(t, 0.0005, 0.05) * Grain() * 0.9f + Hann(t - 0.05, 1.6) * (_bp2.Run(Noise()) * 1.6f + Sin(t, 32) * 0.5f) * (float)(0.6 + 0.4 * Math.Sin(2 * Math.PI * 3 * t));
                done = t > 1.7; break;
            case Fx.Boomerang: // thrown: it whirrs away from the right hand, swings round, comes back
            {
                double p = t / 1.2; float dist = (float)Math.Sin(Math.PI * Math.Clamp(p, 0, 1)); // 0 near, 1 far
                float whirr = Sin(t, 12) * 0.5f + 0.5f;
                float v = (float)Hann(t, 1.2) * _bp.Run(Noise()) * 3f * whirr * (1 - 0.6f * dist);
                r = v; l = v * dist; done = t > 1.2; break;
            }
            case Fx.BoomerangCatch:
                r = Env(t, 0.001, 0.05) * Sin(t, 90) * 0.7f; l = r * 0.3f; done = t > 0.15; break;
            case Fx.HookshotFire: // the chain ratchets out of the right hand, then bites in
            {
                float rat = ((int)(t * 40) % 2 == 0 ? 1f : 0.2f) * Sin(t, 240) * 0.4f * Hann(t, 0.4);
                r = rat + Env(t - 0.4, 0.001, 0.04) * Sin(t, 150) * 0.6f; l = rat * 0.3f;
                spk = ((int)(t * 40) % 2 == 0 ? 1f : 0) * Sin(t, 2000) * 0.08f * Hann(t, 0.4); done = t > 0.5; break;
            }
            case Fx.NoteOn: // an ocarina note starts: a soft breath tick
                l = r = Hann(t, 0.03) * Sin(t, 180) * 0.3f; done = t > 0.04; break;
            // ---- gear ----
            case Fx.SwordDraw: // the blade slides out of the scabbard over your back: a rising metallic hiss, then the ring
            {
                float hiss = Hann(t, 0.22) * Grain() * 0.4f * (float)Math.Clamp(t / 0.2, 0, 1);
                float ring = Env(t - 0.2, 0.0005, 0.18) * (Sin(t, 330) + 0.5f * Sin(t, 495)) * 0.45f;
                r = hiss + ring; l = hiss * 0.6f + ring * 0.4f;
                spk = Env(t - 0.2, 0.0005, 0.25) * (Sin(t, 2900) + 0.4f * Sin(t, 4350)) * 0.12f; done = t > 0.5; break;
            }
            case Fx.Sheathe: // back in: a short slide and a click
                r = Hann(t, 0.12) * Grain() * 0.3f + Env(t - 0.12, 0.0005, 0.015) * Sin(t, 260) * 0.6f; l = r * 0.6f; done = t > 0.18; break;
            case Fx.EquipSword:
                l = r = Env(t, 0.0005, 0.2) * (Sin(t, 300) + 0.5f * Sin(t, 450)) * 0.45f; spk = Env(t, 0.0005, 0.3) * Sin(t, 3100) * 0.12f; done = t > 0.4; break;
            case Fx.EquipShieldWood:
                l = Env(t, 0.001, 0.04) * Sin(t, 120) * 0.7f; r = l * 0.4f; done = t > 0.12; break;
            case Fx.EquipShieldMetal:
                l = Env(t, 0.0005, 0.09) * (Sin(t, 250) + 0.5f * Sin(t, 380)) * 0.6f; r = l * 0.4f; done = t > 0.25; break;
            case Fx.EquipCloth: // a tunic going on: a soft swish over both hands
            {
                float a = (float)Math.Clamp(t / 0.3, 0, 1);
                float g = Hann(t, 0.3) * _bp2.Run(Noise()) * 1.3f;
                l = g * (1 - a); r = g * a; done = t > 0.31; break;
            }
            case Fx.EquipHeavy: // iron boots: two heavy clanks, like stepping into them
                l = Env(t, 0.001, 0.08) * (Sin(t, 45) + 0.4f * Sin(t, 210)) * 0.9f; r = Env(t - 0.18, 0.001, 0.08) * (Sin(t, 45) + 0.4f * Sin(t, 210)) * 0.9f; done = t > 0.4; break;
            case Fx.EquipHover: // hover boots: an airy lift
                l = r = Hann(t, 0.4) * Chirp(t, 150, 320, 0.4) * 0.3f * (0.6f + 0.4f * Sin(t, 11)); done = t > 0.4; break;
            case Fx.ItemTick:
                l = r = Hann(t, 0.012) * Sin(t, 230) * 0.35f; done = t > 0.015; break;

            // ---- menus (the pause screen is a page you hold) ----
            case Fx.MenuOpen: // the page slides in from the right
            case Fx.MenuClose:
            {
                float a = (float)Math.Clamp(t / 0.22, 0, 1);
                if (Kind == Fx.MenuClose) a = 1 - a;
                float g = Hann(t, 0.22) * _bp2.Run(Noise()) * 1.6f;
                l = g * a; r = g * (1 - a);
                float settle = Env(t - 0.2, 0.001, 0.02) * Sin(t, 140) * 0.35f; l += settle; r += settle;
                done = t > 0.26; break;
            }
            case Fx.MenuTick: // the cursor moves: a crisp tick on the side it moved to
                l = r = Hann(t, 0.008) * Sin(t, 280) * 0.45f; done = t > 0.01; break;
            case Fx.MenuConfirm:
                l = r = Env(t, 0.0005, 0.02) * Sin(t, 220) * 0.6f + Env(t - 0.03, 0.0005, 0.02) * Sin(t, 300) * 0.45f; done = t > 0.08; break;
            case Fx.MenuBack:
                l = r = Hann(t, 0.03) * Chirp(t, 260, 160, 0.03) * 0.4f; done = t > 0.035; break;
            case Fx.PageFlip: // a page turned to the side you asked for (N 1 left, 2 right)
            {
                float a = (float)Math.Clamp(t / 0.15, 0, 1);
                float g = Hann(t, 0.15) * Grain() * 0.4f;
                float toR = N == 2 ? a : 1 - a;
                l = g * (1 - toR); r = g * toR; done = t > 0.16; break;
            }
            case Fx.TextTick: // the next line of dialogue
                l = r = Hann(t, 0.015) * Sin(t, 190) * 0.3f; done = t > 0.02; break;

            // ---- the day ----
            case Fx.Sunrise: // light spreads: a long warm swell rising
                l = r = Hann(t, 2.5) * Chirp(t, 40, 110, 2.5) * 0.4f; done = t > 2.5; break;
            case Fx.Nightfall: // the day sinks
                l = r = Hann(t, 2.5) * Chirp(t, 110, 35, 2.5) * 0.35f; done = t > 2.5; break;

            // ---- the senses ----
            case Fx.Ping: // something is there (sonar for enemies out of sight)
                l = r = (Hann(t, 0.03) + Hann(t - 0.09, 0.03) * 0.6f) * Sin(t, 120) * 0.5f * S; done = t > 0.13; break;
            case Fx.Puddle: // a foot lands in a puddle: a slap and a few drops
                if (t < 0.08 && Click(t, 120)) _burst = 1;
                _burst *= 0.96f;
                l = r = Env(t, 0.001, 0.025) * Sin(t, 85) * 0.5f + _burst * Grain() * 0.25f; done = t > 0.12; break;
            case Fx.HeatPulse: // heat without a Goron tunic: a slow wave of heat through the hands
                l = r = Hann(t, 0.7) * (Sin(t, 38) * 0.5f + Grain() * 0.15f) * S; done = t > 0.7; break;
            case Fx.AirGasp: // surfacing after a long dive: a big breath in
                l = r = Hann(t, 0.5) * Chirp(t, 60, 160, 0.5) * 0.45f; done = t > 0.5; break;

            // ---- what enemies do ----
            case Fx.Telegraph: // an enemy winds up: air drawn in towards you, then a held breath
            {
                float a = (float)Math.Clamp(t / 0.22, 0, 1);
                l = r = a * a * Hann(t, 0.26) * Grain() * 0.55f * S + Env(t - 0.22, 0.001, 0.02) * Sin(t, 150) * 0.4f * S;
                done = t > 0.3; break;
            }
            case Fx.Alert: // it has seen you
                l = r = (Hann(t, 0.012) + Hann(t - 0.06, 0.012)) * Sin(t, 310) * 0.4f * S; done = t > 0.08; break;
            case Fx.Appear: // something breaks out of the ground or drops down
                l = r = Hann(t, 0.5) * (_bp2.Run(Noise()) * 1.6f + Sin(t, 40) * 0.4f) * (float)Math.Clamp(t / 0.35, 0, 1) * S + Env(t - 0.45, 0.002, 0.08) * Sin(t, 45) * 0.8f * S;
                done = t > 0.65; break;
            case Fx.Stun: // a stunned enemy: dizzy stars circling
                l = Hann(t, 0.6) * Sin(t, 280) * 0.3f * (float)(0.5 + 0.5 * Math.Sin(2 * Math.PI * 6 * t)) * S;
                r = Hann(t, 0.6) * Sin(t, 280) * 0.3f * (float)(0.5 - 0.5 * Math.Sin(2 * Math.PI * 6 * t)) * S; done = t > 0.6; break;
            case Fx.Blocked: // your blade hits its guard: a dull clank that jars the sword hand
                r = Env(t, 0.0005, 0.06) * (Sin(t, 180) + 0.4f * Sin(t, 270)) * 0.7f + Env(t, 0.002, 0.05) * Sin(t, 60) * 0.6f; l = r * 0.4f;
                done = t > 0.2; break;

            // ---- items used up ----
            case Fx.NutFlash: // a Deku Nut bursts: a sharp crack and a bright fizz
                l = r = Env(t, 0.0003, 0.02) * Grain() * 0.9f + Hann(t, 0.35) * Sin(t, 320 + 80 * Noise()) * 0.25f;
                spk = Env(t, 0.0003, 0.03) * Sin(t, 2200) * 0.2f; done = t > 0.35; break;
            case Fx.StickSnap: // a Deku Stick breaks
                l = r = Env(t, 0.0003, 0.015) * Grain() * 0.8f + Env(t - 0.03, 0.0003, 0.01) * Grain() * 0.5f; done = t > 0.08; break;
            case Fx.ChuRoll: // a Bombchu scurries off ahead: its rattle fades away from you
                l = r = (float)Math.Max(0, 1 - t / 2.5) * ((int)(t * 30) % 2 == 0 ? 1f : 0.4f) * Sin(t, 45) * 0.45f; done = t > 2.5; break;

            case Fx.Footstep: // iron boot: a heavy stamp with a metal edge
                l = r = Env(t, 0.002, 0.07) * Sin(t, 42) * 0.9f + Env(t, 0.0005, 0.02) * Sin(t, 230) * 0.35f + MatTex(t, 0.7f); done = t > 0.2; break;

            case Fx.SwingHeavy: // a two-handed blade: a slow broad arc with weight behind it (N 1 left->right, 2 right->left)
            {
                double p = Math.Clamp(t / 0.28, 0, 1);
                float g = Hann(t, 0.28) * (Grain() * 0.45f + Sin(t, 70) * 0.35f) * S;
                float toR = (float)(N == 2 ? 1 - p : p);
                l = g * (1 - toR * 0.8f); r = g * (0.2f + toR * 0.8f); done = t > 0.3; break;
            }
            case Fx.BladeRing: // the Master Sword sings a little as it cuts
                spk = Env(t, 0.0005, 0.12) * (Sin(t, 3300) + 0.5f * Sin(t, 4950)) * 0.1f;
                l = r = Env(t, 0.0005, 0.05) * Sin(t, 330) * 0.15f; done = t > 0.25; break;
            case Fx.Finisher: // the last blow of a combo: body thump through both hands
                l = r = Env(t, 0.002, 0.08) * Sin(t, 48) * 0.9f + Env(t, 0.0005, 0.02) * Sin(t, 200) * 0.5f; done = t > 0.25; break;
            case Fx.Sparkle: // something precious nearby: a glint from its side
                l = r = (Hann(t, 0.02) + Hann(t - 0.07, 0.02) * 0.6f + Hann(t - 0.13, 0.02) * 0.35f) * Sin(t, 300) * 0.35f * S;
                spk = (Hann(t, 0.03) + Hann(t - 0.07, 0.03) * 0.6f) * Sin(t, 3800) * 0.08f * S; done = t > 0.17; break;
            case Fx.Arrive: // a flight / ride ends: you land on something solid
                l = r = Env(t, 0.002, 0.06) * Sin(t, 55) * 0.8f + Env(t, 0.0005, 0.015) * Sin(t, 180) * 0.4f; done = t > 0.2; break;
            case Fx.Unlock: // a key turns: two clicks, the bolt slides, a clunk
                l = r = Env(t, 0.0005, 0.01) * Sin(t, 300) * 0.5f + Env(t - 0.08, 0.0005, 0.01) * Sin(t, 280) * 0.5f + Hann(t - 0.15, 0.2) * _bp2.Run(Noise()) * 1.2f + Env(t - 0.35, 0.002, 0.06) * Sin(t, 60) * 0.7f;
                spk = Env(t, 0.0005, 0.02) * Sin(t, 2500) * 0.2f + Env(t - 0.08, 0.0005, 0.02) * Sin(t, 2300) * 0.2f; done = t > 0.45; break;
            case Fx.ProfileTick: // touchpad strength steps: one tick per step, higher means stronger
                l = r = Hann(t, 0.025) * Sin(t, 160 + 60 * N) * 0.5f; done = t > 0.03; break;

            // ---- v2: time, minigames, world, story ----
            case Fx.TimeShift: // seven years pass: four heartbeats fading away under a long white swell
                for (int i = 0; i < 4; i++) { double u = t - i * 0.65; l += (Hann(u, 0.09) * Sin(u, 50) + Hann(u - 0.17, 0.08) * Sin(u, 44) * 0.7f) * (0.9f - 0.2f * i); }
                l += Hann(t, 2.6) * Chirp(t, 35, 150, 2.6) * 0.3f * (float)Math.Clamp(t / 1.5, 0, 1);
                spk = Hann(t - 2.0, 0.6) * Sin(t, 2400) * 0.12f; r = l; done = t > 2.6; break;
            case Fx.TimerTick: // a second goes by (N 2: the last seconds, sharper)
                l = r = Hann(t, 0.02) * Sin(t, N == 2 ? 260 : 200) * (N == 2 ? 0.6f : 0.35f) * S;
                spk = Hann(t, 0.02) * Sin(t, N == 2 ? 2000 : 1500) * 0.12f; done = t > 0.03; break;
            case Fx.TimeUp: // the buzzer
                l = r = Hann(t, 0.6) * ((int)(t * 20) % 2 == 0 ? 1f : 0.3f) * Sin(t, 90) * 0.8f; spk = Hann(t, 0.6) * Sin(t, 600) * 0.15f; done = t > 0.6; break;
            case Fx.StartShot: // the starting shot
                l = r = Env(t, 0.0003, 0.03) * Grain() * 1.0f + Env(t, 0.002, 0.08) * Sin(t, 45) * 0.9f; spk = Env(t, 0.0003, 0.04) * Sin(t, 1100) * 0.25f; done = t > 0.3; break;
            case Fx.TargetHit: // a target hit: bright pop and a click in the hand
                l = r = (Env(t, 0.0003, 0.02) * Sin(t, 290) * 0.7f + Env(t, 0.001, 0.04) * Sin(t, 120) * 0.4f) * S;
                spk = Env(t, 0.0003, 0.05) * (Sin(t, 2800) + 0.5f * Sin(t, 4200)) * 0.25f; done = t > 0.15; break;
            case Fx.Miss: // nothing hit: a dull "pfft"
                l = r = Env(t, 0.003, 0.05) * Sin(t, 70) * 0.4f + Hann(t, 0.08) * _bp2.Run(Noise()) * 0.8f; done = t > 0.12; break;
            case Fx.Fanfare: // a record, a prize: three rising pulses and a held bright tone
                for (int i = 0; i < 3; i++) { double u = t - i * 0.13; l += Hann(u, 0.09) * Sin(u, 200 + 60 * i) * 0.6f; spk += Hann(u, 0.1) * Sin(u, 1600 * Math.Pow(1.26, i)) * 0.25f; }
                l += Hann(t - 0.4, 0.5) * Sin(t, 320) * 0.35f; r = l; done = t > 0.95; break;
            case Fx.Catch: // a fish on land: it flaps from hand to hand (N 1..3: its size)
            {
                double len = 0.6 + 0.3 * N;
                float flap = Hann(t * 9 % 1, 0.5) * Sin(t, 70) * 0.6f * (float)Math.Max(0, 1 - t / len);
                if ((int)(t * 9) % 2 == 0) l = flap; else r = flap; done = t > len; break;
            }
            case Fx.Bite: // a fish bites / the hook bites: a hard tug
                l = r = Env(t, 0.0005, 0.03) * Sin(t, 160) * 0.9f + Env(t, 0.002, 0.12) * Sin(t, 45) * 0.9f; done = t > 0.3; break;
            case Fx.Cast: // the rod swings forward, the line whistles out, the lure lands
            {
                float a = (float)Math.Clamp(t / 0.35, 0, 1), g = Hann(t, 0.35) * Grain() * 0.5f;
                l = g * (1 - a) + Env(t - 0.6, 0.001, 0.03) * Sin(t, 90) * 0.4f; r = g * a + Env(t - 0.6, 0.001, 0.03) * Sin(t, 90) * 0.4f;
                spk = Hann(t, 0.35) * Chirp(t, 3000, 1200, 0.35) * 0.1f; done = t > 0.7; break;
            }
            case Fx.WaterRise: // the water level changes: a long rush (N 1 rising, 2 sinking)
                l = r = Hann(t, 2.4) * (_bp2.Run(Noise()) * 1.6f + (N == 2 ? Chirp(t, 90, 40, 2.4) : Chirp(t, 40, 90, 2.4)) * 0.4f); done = t > 2.4; break;
            case Fx.Collapse: // a part of the castle comes down
                if (t < 0.8 && Click(t, 40)) _burst = 1;
                _burst *= 0.99f;
                l = r = Env(t, 0.004, 0.4) * Sin(t, 30) * 1.3f + Hann(t, 1.2) * _bp2.Run(Noise()) * 1.6f + _burst * Grain() * 0.4f; done = t > 1.3; break;
            case Fx.Debris: // a chunk of rubble lands nearby
                if (t < 0.25 && Click(t, 40)) _burst = 1;
                _burst *= 0.98f;
                l = r = Env(t, 0.002, 0.06) * Sin(t, 50) * 0.8f + Env(t, 0.0005, 0.015) * Grain() * 0.6f + _burst * Grain() * 0.25f; done = t > 0.35; break;
            case Fx.ShipSink: // the ship goes down under you, creaking
                if (Click(t, 6)) _burst = 1;
                _burst *= 0.995f;
                l = r = Hann(t, 2.5) * Chirp(t, 70, 25, 2.5) * 0.7f + _burst * Sin(t, 180) * 0.3f; done = t > 2.5; break;
            case Fx.Melt: // ice melts: crackling, then drops
                if (t < 0.6 && Click(t, 60)) _burst = 1;
                _burst *= 0.98f;
                l = r = _burst * Sin(t, 330 + 100 * Noise()) * 0.4f;
                foreach (double d in new[] { 0.8, 1.1, 1.5 }) l += Env(t - d, 0.0005, 0.006) * Sin(t, 260) * 0.6f;
                r = l; done = t > 1.7; break;
            case Fx.Ignite: // something catches fire: a whoomp and crackles
                if (Click(t, 80 * Math.Exp(-t / 0.3))) _burst = 1;
                _burst *= 0.99f;
                l = r = Hann(t, 0.25) * Chirp(t, 60, 180, 0.25) * 0.6f + _burst * Sin(t, 230) * 0.35f; done = t > 0.6; break;

            // ---- v2: moves and accidents ----
            case Fx.Dash: // pushing off into a sprint
                l = r = Env(t, 0.002, 0.05) * Sin(t, 60) * 0.7f + Hann(t, 0.3) * Grain() * 0.35f * (float)Math.Clamp(t / 0.15, 0, 1) + MatTex(t, 0.6f); done = t > 0.32; break;
            case Fx.Dodge: // a quick step aside (N 1 to the right, 2 to the left)
            {
                float a = (float)Math.Clamp(t / 0.12, 0, 1), g = Hann(t, 0.12) * (Grain() * 0.5f + Sin(t, 80) * 0.2f);
                float toR = N == 2 ? 1 - a : a; l = g * (1 - toR); r = g * toR; done = t > 0.14; break;
            }
            case Fx.WallKick: // kicking off a wall
                l = r = Env(t, 0.0005, 0.02) * Sin(t, 180) * 0.7f + Env(t, 0.002, 0.05) * Sin(t, 55) * 0.8f; done = t > 0.15; break;
            case Fx.ChainJump: // each jump of a chain a step higher (N 1..6)
                l = r = Env(t, 0.002, 0.035) * Sin(t, 70 + 15 * N) * 0.6f + Hann(t, 0.1) * Chirp(t, 110 + 30 * N, 210 + 50 * N, 0.1) * 0.25f;
                spk = Hann(t, 0.05) * Sin(t, 1200 * Math.Pow(1.12, N)) * 0.08f; done = t > 0.12; break;
            case Fx.SlideLand: // landing in a slide: skidding to a stop
                l = r = Hann(t, 0.35) * _bp2.Run(Noise()) * 1.4f * (float)(1 - t / 0.35) + Env(t, 0.002, 0.05) * Sin(t, 50) * 0.5f + MatTex(t, 0.8f); done = t > 0.36; break;
            case Fx.QuickTurn: // spinning round on the spot
            {
                float a = (float)Math.Clamp(t / 0.1, 0, 1), g = Hann(t, 0.1) * Grain() * 0.35f;
                l = g * (N == 2 ? a : 1 - a); r = g * (N == 2 ? 1 - a : a); done = t > 0.11; break;
            }
            case Fx.Crash: // running head-first into something
                l = r = Env(t, 0.0005, 0.03) * Grain() * 0.9f + Env(t, 0.003, 0.15) * Sin(t, 38) * 1.2f + MatTex(t, 0.6f); done = t > 0.45; break;
            case Fx.Slip: // the foot slides away (N 1 to the right, 2 to the left)
            {
                float a = (float)Math.Clamp(t / 0.3, 0, 1), g = Hann(t, 0.3) * _bp2.Run(Noise()) * 1.3f;
                float toR = N == 2 ? 1 - a : a; l = g * (1 - toR); r = g * toR; done = t > 0.31; break;
            }
            case Fx.Hammer: // the Megaton Hammer: the blow, then the shockwave rings out three times
                l = r = Env(t, 0.003, 0.25) * Sin(t, 34) * 1.3f + Env(t, 0.0005, 0.03) * Sin(t, 170) * 0.8f;
                for (int i = 1; i <= 3; i++) { double u = t - 0.15 * i; l += Env(u, 0.003, 0.08) * Sin(u, 40) * (0.7f - 0.18f * i); }
                r = l; done = t > 0.8; break;
            case Fx.PickUp:
                l = r = Env(t, 0.002, 0.03) * Sin(t, 140) * 0.4f; done = t > 0.06; break;
            case Fx.Terror: // a ReDead's scream: frozen in place, a cold high tremble
                l = r = Hann(t, 1.0) * Sin(t, 300 + 40 * Noise()) * 0.5f * (0.6f + 0.4f * Sin(t, 23)) + Env(t, 0.001, 0.08) * Sin(t, 45) * 0.8f; done = t > 1.0; break;
            case Fx.Grabbed: // something clamps onto you
                l = r = Env(t, 0.002, 0.1) * Sin(t, 42) * 1.0f + Hann(t, 0.4) * Sin(t, 65) * 0.4f * (0.5f + 0.5f * Sin(t, 6)); done = t > 0.5; break;
            case Fx.Breakfree: // free again: a snap and a release
                l = r = Env(t, 0.0005, 0.03) * Sin(t, 200) * 0.6f + Hann(t, 0.25) * Chirp(t, 80, 220, 0.25) * 0.4f; done = t > 0.3; break;
            case Fx.ChainRattle: // the hookshot found nothing: the chain rattles back in
                r = ((int)(t * 50) % 2 == 0 ? 1f : 0.2f) * Sin(t, 220) * 0.35f * Hann(t, 0.45); l = r * 0.3f;
                spk = ((int)(t * 50) % 2 == 0 ? 1f : 0) * Sin(t, 1800) * 0.06f * Hann(t, 0.45); done = t > 0.45; break;
            case Fx.Detent: // a click point under the finger (full draw, full charge)
                r = Hann(t, 0.006) * Sin(t, 300) * 0.6f; l = r * 0.3f; done = t > 0.01; break;
            case Fx.Nock: // an arrow laid on the string
                r = Env(t, 0.0003, 0.01) * Sin(t, 280) * 0.5f; l = r * 0.3f; spk = Env(t, 0.0003, 0.015) * Sin(t, 2600) * 0.25f; done = t > 0.04; break;
            case Fx.AimTick: // the aim moves a notch (gyro aiming)
                r = Hann(t, 0.004) * Sin(t, 320) * 0.3f; l = r * 0.5f; done = t > 0.006; break;
            case Fx.Compass: // Navi points the way: three pings from where it is
                for (int i = 0; i < 3; i++) { double u = t - i * 0.11; l += Hann(u, 0.03) * Sin(u, 200 + 40 * i) * 0.5f; spk += Hann(u, 0.03) * Sin(u, 2200 + 300 * i) * 0.1f; }
                r = l; done = t > 0.35; break;

            // ---- v2: enemies ----
            case Fx.ArmorBreak: // an Iron Knuckle's armour bursts: a clang and shards flying
                if (t < 0.3 && Click(t, 120 * Math.Exp(-t / 0.15))) { _burst = 1; _nextPanR = _rng.NextDouble() < 0.5; }
                _burst *= 0.985f;
                l = r = Env(t, 0.0005, 0.12) * (Sin(t, 230) + 0.5f * Sin(t, 345)) * 0.7f;
                { float c = _burst * Sin(t, 330 + 150 * Noise()) * 0.5f; if (_nextPanR) r += c; else l += c; }
                done = t > 0.5; break;
            case Fx.WeakSpot: // a weak point hit: a critical spark
                l = r = Env(t, 0.0003, 0.05) * (Sin(t, 320) + 0.6f * Sin(t, 480)) * 0.7f + Env(t, 0.002, 0.08) * Sin(t, 50) * 0.8f;
                spk = Env(t, 0.0003, 0.08) * Sin(t, 3500) * 0.2f; done = t > 0.3; break;
            case Fx.Projectile: // something flies at you and past
                l = r = Hann(t, 0.4) * Chirp(t, 260, 120, 0.4) * 0.4f; done = t > 0.4; break;
            case Fx.Rage: // an enemy roars / gets angry: a wave from its side
                l = r = Hann(t, 0.7) * (_bp2.Run(Noise()) * 1.5f + Sin(t, 36) * 0.5f) * (0.7f + 0.3f * Sin(t, 11)) * S; done = t > 0.7; break;
            case Fx.Relief: // the pressure lets go (an enemy flees, danger passes)
                l = r = Hann(t, 0.6) * Chirp(t, 110, 50, 0.6) * 0.3f * S; done = t > 0.6; break;
            case Fx.Caught: // a guard caught you: three hard alarm beats
                for (int i = 0; i < 3; i++) { double u = t - i * 0.18; l += Env(u, 0.001, 0.06) * Sin(u, 60) * 1.0f + Env(u, 0.0005, 0.02) * Sin(u, 250) * 0.5f; spk += Hann(u, 0.1) * Sin(u, 1800) * 0.2f; }
                r = l; done = t > 0.6; break;
            case Fx.SwordLost: // the sword is knocked out of your hand and flies away
                r = Env(t, 0.0005, 0.05) * Sin(t, 280) * 0.6f + Hann(t, 0.6) * Chirp(t, 300, 80, 0.6) * 0.35f;
                l = Hann(t, 0.6) * Chirp(t, 300, 80, 0.6) * 0.35f * (float)Math.Clamp(t / 0.6, 0, 1); done = t > 0.6; break;

            // ---- v2: things in the hand ----
            case Fx.MaskOn: // a mask goes on (N 2: comes off)
                l = r = Hann(t, 0.15) * _bp2.Run(Noise()) * 1.2f + Env(t - 0.15, 0.0005, 0.015) * Sin(t, N == 2 ? 180 : 240) * 0.5f; done = t > 0.2; break;
            case Fx.BottleCatch: // into the bottle, then what's inside moves (N 1 fairy, 2 bug, 3 fish, 4 poe, 5 blue fire, 6 liquid)
            {
                l = r = Hann(t, 0.15) * Grain() * 0.35f;
                double u = t - 0.15;
                if (u > 0 && u < 0.75)
                {
                    float c = 0;
                    switch (N)
                    {
                        case 1: if (Click(u, 40)) _burst = 1; _burst *= 0.993f; c = _burst * Sin(t, 300 + 60 * Noise()) * 0.5f; spk = _burst * Sin(t, 2600 + 900 * _burst) * 0.3f; break;
                        case 2: c = ((int)(u * 25) % 2 == 0 ? 1f : 0) * Sin(t, 260) * 0.25f; break;
                        case 3: c = Hann(u * 8 % 1, 0.5) * Sin(t, 70) * 0.5f; break;
                        case 4: c = Hann(u, 0.75) * Chirp(u, 320, 90, 0.75) * 0.35f; break;
                        case 5: if (Click(u, 80)) _burst = 1; _burst *= 0.99f; c = _burst * Sin(t, 230) * 0.4f; break;
                        default: c = Sin(t, 55) * (0.5f + 0.5f * Sin(t, 6)) * 0.4f * Hann(u, 0.75); break;
                    }
                    if ((int)(u * 8) % 2 == 0) l += c; else r += c;
                }
                float pop = Env(t - 0.9, 0.0005, 0.02) * Sin(t, 280) * 0.5f; l += pop; r += pop;
                spk += Env(t - 0.9, 0.0005, 0.03) * Sin(t, 1500) * 0.3f; done = t > 1.0; break;
            }
            case Fx.Gulp: // drinking: N gulps
                for (int i = 0; i < N; i++) { double u = t - i * 0.28; l += Hann(u, 0.12) * Chirp(u, 90, 50, 0.12) * 0.6f; }
                r = l; done = t > N * 0.28 + 0.05; break;
            case Fx.Peck: // a cucco's beak
                l = r = Env(t, 0.0003, 0.012) * Sin(t, 260) * 0.6f + Env(t, 0.001, 0.02) * Sin(t, 110) * 0.3f; done = t > 0.05; break;

            case Fx.Chime:
                for (int i = 0; i < 3; i++) { double u = t - i * 0.09; l += Hann(u, 0.08) * Sin(u, 220 + 50 * i) * 0.3f; spk += Hann(u, 0.1) * Sin(u, 1800 + 450 * i) * 0.15f; }
                r = l; done = t > 0.35; break;

            default: done = true; break;
        }
        if (pan != 0)
        {
            l *= Math.Clamp(1 - pan, 0.15f, 1f);
            r *= Math.Clamp(1 + pan, 0.15f, 1f);
        }
        return (l * G, r * G, spk);
    }

    bool _nextPanR;
}
