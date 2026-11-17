namespace PulseOfTime;

/// Scripted moments by the running event's name (EventMgr, see Oot.EventName): every ocarina song, warps,
/// boss intros, sages, game over, map changes. Fires when the name changes or a new event starts, at most
/// once per name every few seconds.
sealed class Events
{
    string? _last; bool _wasEvent; readonly Dictionary<string, long> _fired = new();
    public (float R, float G, float B) Light; public long LightFrom, LightUntil; public bool LightSweepDay;
    public long HoovesUntil, HoovesFrom;
    public string Current = "";

    public void Update(long now, string name, bool inEvent, Haptics? hx, Quad q, Actions act, Action<string> log)
    {
        if (_last == null) { _last = name; _wasEvent = inEvent; return; } // the name left over from before we started: not a new event
        bool started = name.Length > 0 && (name != _last || inEvent && !_wasEvent);
        _wasEvent = inEvent; Current = name;
        if (!started) { _last = name; return; }
        _last = name;
        if (_fired.TryGetValue(name, out var t) && now - t < 4000) return;
        _fired[name] = now;
        log($"EVENT {name}");
        if (hx == null) return;
        bool Has(params string[] k) => k.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));
        void Glow(float r, float g, float b, int ms) { Light = (r, g, b); LightFrom = now; LightUntil = now + ms; LightSweepDay = false; }

        if (Has("Ocarina", "Melody", "Warp"))
        {
            // every song has its own feel; warp songs carry you off in their temple's colour
            if (Has("SunSong", "Sun")) { hx.Play(Fx.Sunrise); LightSweepDay = true; LightFrom = now; LightUntil = now + 3500; q.Pulse(now, 3, 3, 25, 2500, 0, false, true); return; }
            if (Has("Storm")) { hx.Play(Fx.Thunder); act.StartRain(now, 90000); Glow(200, 210, 255, 400); q.Pulse(now, 7, 7, 30, 500, 0, true, true); return; }
            if (Has("Epona")) { HoovesFrom = now; HoovesUntil = now + 4500; Glow(255, 170, 90, 4500); return; }   // she comes galloping from afar
            if (Has("Saria")) { hx.Play(Fx.Chime); hx.Play(Fx.Farore, 0.6f); Glow(60, 255, 90, 2500); return; }
            if (Has("Zelda", "Lullaby")) { hx.Play(Fx.Nayru); hx.Play(Fx.Chime); Glow(255, 215, 90, 3000); return; }
            if (Has("SongOfTime", "TimeSong", "Time")) { hx.Play(Fx.Appear, 1.2f); Glow(170, 210, 255, 3000); q.Pulse(now, 5, 5, 25, 1200, 0, true, true); return; }
            if (Has("Minuet", "Forest")) { hx.Play(Fx.Farore); Glow(40, 255, 70, 3000); return; }
            if (Has("Bolero", "Fire")) { hx.Play(Fx.DinFire); Glow(255, 70, 0, 3000); q.Pulse(now, 6, 6, 40, 500); return; }
            if (Has("Serenade", "Water")) { hx.Play(Fx.Splash, 1.2f); hx.Play(Fx.Farore, 0.7f); Glow(40, 120, 255, 3000); return; }
            if (Has("Nocturne", "Shadow")) { hx.Play(Fx.AmbEerie, 1.3f); Glow(110, 20, 170, 3000); return; }
            if (Has("Requiem", "Spirit")) { hx.Play(Fx.AmbSand, 1.3f); hx.Play(Fx.Farore, 0.6f); Glow(255, 150, 40, 3000); return; }
            if (Has("Prelude", "Light")) { hx.Play(Fx.Chime); hx.Play(Fx.Nayru); Glow(255, 250, 200, 3000); return; }
            if (Has("Scarecrow")) { hx.Play(Fx.Chime); return; }
            if (Has("Warp")) { hx.Play(Fx.Farore); Glow(120, 200, 255, 2500); return; }
            return;
        }
        if (Has("Boss") && Has("Appear", "Demo", "Start", "Encount", "Opening"))
        {   // a boss arrives: three slow heavy heartbeats, the light goes red, the fingers tense
            for (int i = 0; i < 3; i++) { q.Pulse(now, 6, 6, 30, 90, i * 650, false, true); }
            hx.Play(Fx.Heartbeat, 1.3f); Glow(200, 0, 20, 3000); return;
        }
        if (Has("SageOf", "Sage_")) { hx.Play(Fx.MedalGet); Glow(255, 255, 255, 3000); return; }
        if (Has("GameOver")) { hx.Play(Fx.Death); Glow(60, 0, 0, 3000); return; }
        if (Has("FallDown")) { hx.Play(Fx.Abyss); Glow(0, 0, 0, 1500); return; }
        if (Has("BancChange")) { hx.Play(Fx.MenuOpen, 0.8f); Glow(0, 0, 0, 700); return; }   // through a door / exit: the world fades
        if (Has("TBox", "TreasureBox", "Dm_SY_Get")) { hx.Play(Fx.Lift, 0.8f); return; }       // the chest: anticipation before the item
        if (Has("GossipStone")) { hx.Play(Fx.AmbEerie, 0.8f); return; }
        if (Has("Defeat_")) { Glow(255, 220, 120, 3000); return; }
    }

    /// Light for the current moment (null when none), with the Sun's Song racing through a whole day.
    public (float, float, float)? LightNow(long now)
    {
        if (now >= LightUntil) return null;
        if (LightSweepDay)
        {
            float p = (now - LightFrom) / (float)Math.Max(1, LightUntil - LightFrom);
            return p < 0.33f ? (30, 40, 140) : p < 0.66f ? (255, 120, 60) : (255, 245, 210);
        }
        return Light;
    }
}
