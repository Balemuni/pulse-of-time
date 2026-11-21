namespace PulseOfTime;

/// Masterplan v2: story, minigames and places read from GameData (polled every 100 ms) plus a few heuristics
/// from the actor walk. Time travel, minigame timers and scores, water levels, the collapsing castle, Ganon's
/// third eye and the lost Master Sword, angry cuccos, the fishing pond, bottles, masks, Ingo's race, the
/// Shadow Temple's ship, the Trial of the Hero (dream world), Gerudo guards, Poes.
/// Outputs are read by the daemon each frame: trigger overrides, a player-LED bar, a light, the mute LED.
sealed class Moments
{
    static uint H(string s) => GameData.Hash(s);
    static readonly uint HChild = H("Sequence_IsChildhood"), HScore = H("MiniGame_Score"), HScoreMax = H("MiniGame_ScoreMax"),
        HTimer = H("MiniGame_TimerValue"), HLimit = H("MiniGame_TimerValueLimit"), HTimerOn = H("MiniGame_IsTimerCounting"),
        HTimerDown = H("MiniGame_IsTimerCountDown"), HWater = H("WaterTemple_Room00_WaterLevel"), HCucco = H("Cucco_IsAngry"),
        HPondRain = H("MiniGame_Fishing_Rainy"), HFishA = H("MiniGame_Fishing_RecordFishSize_Adult"), HFishC = H("MiniGame_Fishing_RecordFishSize_Child"),
        HEye = H("Enemy_DungeonBossGanon_IsOpenedThirdEye"), HZelda = H("Enemy_DungeonBossGanon_IsZeldaMagicChargeFinish"),
        HHead = H("PlayerStatus.IsActiveHeadArmor");
    const uint HEponaSeq = 0x4969f026, HShadowSeq = 0x7a91dbd5; // unnamed sequence enums (sloperina field list)
    static readonly uint VRace = H("GambleRacePlaying"), VComplete = H("Complete"), VShip = H("BattleOnShip"), VAfterShip = H("BeforeBossRoom");
    static readonly uint[] WaterDown = { H("BottomOfTheWell_Room_01_WaterLevel_Down"), H("InsideTheDekuTree_RoomG_WaterLevel") };
    static readonly uint[] Melts = { H("Gimmick_RedIce_Melted"), H("ForestTemple_Room14_MeltedIce"), H("ForestTemple_Room27_MeltedIce") };
    static readonly uint[] Escape = new[] { "2F_FallDown", "4F_BreakRoom", "AfterStep03", "AfterStep05", "AfterStep08", "AfterStep10", "StartFireTrap_3F", "OpenLastGate", "SetBackRubble" }
        .Select(n => H("EscapeFromCrumblingGanonCastle_" + n)).ToArray();
    static readonly uint[] Bottles = "ABCDE".Select(c => H($"PlayerStatus.AssignableItem.BottleInfo.Bottle{c}.BottleItem")).ToArray();
    /// Bottle content -> feel: 1 fairy, 2 bug, 3 fish, 4 poe, 5 blue fire, 6 liquid, 7 letter, 0 empty.
    static readonly Dictionary<uint, int> Content = new()
    {
        [H("Empty")] = 0, [H("Fairy")] = 1, [H("Bug")] = 2, [H("Fish")] = 3, [H("Poe")] = 4, [H("BigPoe")] = 4, [H("BlueFire")] = 5,
        [H("RedPotion")] = 6, [H("GreenPotion")] = 6, [H("BluePotion")] = 6, [H("MaxMilk")] = 6, [H("HalfMilk")] = 6, [H("Letter")] = 7,
    };

    // ---- outputs ----
    public byte[]? R, L;            // trigger overrides this frame
    public float Bar = -1;          // player-LED bar while a minigame / the line / a grab needs it (0..1), -1 = none
    public byte Mic;                // 1 on, 2 blinking
    public bool Child, Dream, Escaping, Racing, OnShip, Hooked, Masked;
    public string Debug = "";

    // ---- state ----
    readonly Dictionary<uint, bool> _flag = new();
    readonly Dictionary<uint, uint> _enum = new();
    readonly List<(long at, Action run)> _later = new();
    readonly Random _rng = new(7);
    bool? _child; int _score = -1, _water = -1, _fishA = -1, _fishC = -1, _lastSecond = -1;
    bool _timerOn; float _timerLeft = -1, _timerFrac = -1; long _scoreGameUntil, _shotAt, _nextPoll, _escapeUntil, _nextDebris;
    int _scoreAtShot;
    (float r, float g, float b) _light; long _lightFrom, _lightUntil; float _lightMix; bool _lightFlicker;
    long _micUntil, _hookT = -1, _lastPull, _nextTug, _nextGuardBeat, _caughtUntil, _eventStart = -1, _guardSeen = -10000;
    long _swordGoneSince = -1, _bunnyStep; double _bunnyPh; float _runBase = 4f;
    float _lastX, _lastZ; bool _wasEvent; string _weapon = "";
    readonly HashSet<string> _loggedGuards = new();
    long _now, _bunnyT, _nextPeck, _timerMoved = -100000; float _timerVal = float.NaN;

    bool Rise(uint h, bool v) { bool had = _flag.TryGetValue(h, out var o); _flag[h] = v; return had && v && !o; }
    bool Fell(uint h, bool v) { bool had = _flag.TryGetValue(h, out var o); _flag[h] = v; return had && !v && o; }
    void Glow((float, float, float) c, int ms, float mix = 0.85f, bool flicker = false, int delay = 0)
    { _light = c; _lightFrom = _now + delay; _lightUntil = _lightFrom + ms; _lightMix = mix; _lightFlicker = flicker; }
    void Later(long now, int ms, Action a) => _later.Add((now + ms, a));

    /// A shot left the bow / slingshot (for "missed" in the shooting games).
    public void Shot(long now) { if (now < _scoreGameUntil) { _shotAt = now; _scoreAtShot = _score; } }

    /// The ground under Link by place (impacts take its texture).
    public static Mat MaterialOf(string map, bool indoor, bool water)
    {
        bool Has(params string[] k) => k.Any(x => map.Contains(x, StringComparison.OrdinalIgnoreCase));
        if (water) return Mat.Water;
        if (Has("IceCavern", "Ice_")) return Mat.Ice;
        if (Has("JabuJabu")) return Mat.Flesh;
        if (Has("DekuTree", "ShadowTemple_Ship")) return Mat.Wood;
        if (Has("Desert", "Wasteland", "Colossus", "Hounted")) return Mat.Sand;
        if (indoor && Has("Market", "Kakariko", "Kokiri", "LonLon", "House", "Shop")) return Mat.Wood;
        if (Has("Temple", "Cavern", "Well", "Castle", "Ganon", "Tower", "Dungeon", "Crater", "Graveyard", "Tomb", "Dream", "Training")) return Mat.Stone;
        if (Has("Kokiri", "LostWood", "SacredForest", "Field", "LonLon", "Lake", "Zora", "Forest", "Gerudo")) return Mat.Grass;
        return Mat.Stone;
    }

    public void Update(long now, GameData gd, Haptics? hx, Quad q, Actions act, Oot.State s, string map, Combat? cb, float pull,
                       string weapon, string eventName, bool paused, int buttons, int prevButtons)
    {
        _now = now; R = L = null; Bar = -1; Mic = now < _micUntil ? (byte)2 : (byte)0;
        for (int i = _later.Count - 1; i >= 0; i--) if (now >= _later[i].at) { var a = _later[i].run; _later.RemoveAt(i); a(); }
        bool Map(params string[] k) => k.Any(x => map.Contains(x, StringComparison.OrdinalIgnoreCase));
        var L0 = hx?.Loops;

        if (now >= _nextPoll && !paused)
        {
            _nextPoll = now + 100;
            Poll(now, gd, hx, q, s, map, cb);
        }

        // ---- the minigame timer: soft seconds, the last ten harder, the last three a heartbeat ----
        if (_timerOn && _timerLeft >= 0 && hx != null && !paused)
        {
            int sec = (int)Math.Ceiling(_timerLeft);
            if (sec != _lastSecond && _lastSecond >= 0 && sec >= 0)
            {
                if (sec <= 3) { hx.Play(Fx.Heartbeat, 1.1f); q.Pulse(now, 6, 6, 30, 70, 0, false, true); }
                else if (sec <= 10) { hx.Play(Fx.TimerTick, 0.8f + 0.05f * (10 - sec), 2); q.Pulse(now, 2, 2, 200, 25); }
                else if (sec % 5 == 0) hx.Play(Fx.TimerTick, 0.5f);
            }
            _lastSecond = sec;
            if (_timerFrac >= 0) Bar = _timerFrac;
        }
        else _lastSecond = -1;
        if (!_timerOn && now < _scoreGameUntil && _timerFrac >= 0) Bar = _timerFrac; // a score game without a clock: the score
        if (now < _scoreGameUntil && _shotAt > 0 && now - _shotAt > 1500)
        {   // the shot hit nothing
            if (_score == _scoreAtShot) hx?.Play(Fx.Miss);
            _shotAt = 0;
        }

        // ---- the collapsing castle: the ground shakes, rubble falls all around ----
        Escaping = now < _escapeUntil && Map("Ganon");
        if (Escaping && hx != null && !paused)
        {
            L0![(int)Loop.Quake] = Math.Max(L0[(int)Loop.Quake], 0.35f);
            if (now >= _nextDebris)
            {
                float side = (float)(_rng.NextDouble() * 2 - 1), front = (float)(_rng.NextDouble() * 2 - 1);
                var (palm, pan, tl, tr) = Quad.Place(side, front, 0.8f);
                hx.Play(Fx.Debris, Math.Max(0.4f, palm), 1, pan); q.Pulse(now, tl, tr, 35, 90);
                _nextDebris = now + _rng.Next(500, 2000);
            }
            Mic = Math.Max(Mic, (byte)1);
        }

        // ---- a ship rocks ----
        if (L0 != null) L0[(int)Loop.Rock] = OnShip || Map("ShadowTemple") && act.Carried ? 0.8f : 0;
        if (OnShip && !paused && (cb == null || cb.NearestDistance > 8)) // not while fighting: R2 keeps the sword
        {   // the deck tilts: the weight shifts from one trigger to the other with the sway
            float a = (float)Math.Sin(2 * Math.PI * 0.22 * now / 1000.0);
            L = Effects.Feedback(0, 2 + (int)Math.Round(3 * (0.5f - 0.5f * a))); R = Effects.Feedback(0, 2 + (int)Math.Round(3 * (0.5f + 0.5f * a)));
        }

        // ---- the race: hooves a notch stronger, the light warm ----
        if (Racing && L0 != null) L0[(int)Loop.Hooves] = Math.Min(1.3f, L0[(int)Loop.Hooves] * 1.3f);

        // ---- angry cuccos ----
        if (L0 != null) L0[(int)Loop.Swarm] = _flag.GetValueOrDefault(HCucco) ? 1 : 0;
        if (_flag.GetValueOrDefault(HCucco) && hx != null && !paused && now >= _nextPeck)
        {   // single hard pecks on top of the swarm, from any side
            hx.Play(Fx.Peck, 0.6f + 0.4f * (float)_rng.NextDouble(), 1, (float)(_rng.NextDouble() * 2 - 1)); _nextPeck = now + _rng.Next(150, 500);
        }
        // ---- holding a cucco and gliding ----
        if (L0 != null) L0[(int)Loop.Flap] = act.HasVerb("AnimalGlide") ? 0.9f : 0;

        // ---- Poes: a cold shimmer from where they float ----
        if (cb != null && L0 != null && hx != null)
        {
            Combat.Foe? poe = null;
            foreach (var f in cb.Live) if (f.Name.Contains("Poe") && f.Distance < 15 && (poe == null || f.Distance < poe.Value.Distance)) poe = f;
            L0[(int)Loop.Ghost] = poe is { } p ? 1 - p.Distance / 15 : 0;
            if (poe is { } p2) hx.GhostPan = p2.Pan;
        }

        // ---- fishing: a fish on the line pulls from where it swims, R2 is the line's brake ----
        bool fishing = act.HasVerb("Fishing") || act.HasVerb("ChangeLure");
        Hooked = false;
        if (fishing && cb != null)
        {
            Combat.Seen? fish = null;
            foreach (var o in cb.Objects) if (o.Kind == Combat.ObjKind.Fish && o.Distance < 25 && (fish == null || o.Distance < fish.Value.Distance)) fish = o;
            // shortcut: "hooked" = a fish coming closer while you reel (R2); the game's own bite flag is not found yet
            bool reeling = fish is { } f0 && f0.Closing > 0.25f && pull > 0.15f;
            if (reeling) { if (_hookT < 0) { _hookT = now; } _lastPull = now; }
            else if (_hookT >= 0 && now - _lastPull > 1500) _hookT = -1;
            Hooked = _hookT >= 0 && now - _hookT > 250;
            if (Hooked && fish is { } f && hx != null)
            {
                if (now - _hookT < 350 && _nextTug == 0)
                {   // the bite
                    hx.Play(Fx.Bite, 1, 1, f.Pan); q.Pulse(now, 0, 8, 60, 90); Glow((120, 230, 255), 200, 0.9f);
                }
                if (now >= _nextTug)
                {   // it pulls this way and that
                    hx.DragPan = Math.Clamp(f.Pan + (float)(_rng.NextDouble() - 0.5), -1, 1);
                    q.Pulse(now, hx.DragPan < 0 ? 4 : 1, hx.DragPan > 0 ? 4 : 1, 45, 140, 0, true, true);
                    _nextTug = now + _rng.Next(250, 700);
                }
                L0![(int)Loop.Drag] = 0.4f + 0.6f * Math.Max(0, pull);
                R = Effects.Feedback(1, Math.Clamp(3 + (int)(5 * Math.Max(0, pull)), 1, 8));
                Bar = Math.Clamp(pull, 0, 1);
            }
        }
        if (!Hooked) { _nextTug = 0; if (L0 != null) L0[(int)Loop.Drag] = 0; }
        if (fishing && _flag.GetValueOrDefault(HPondRain)) act.StartRain(now, 1500);

        // ---- the Master Sword knocked away (Ganon): R2 goes slack and empty until it is back ----
        bool ganonNear = cb != null && cb.Live.Any(f => f.Name.Contains("Ganon") && f.Distance < 40);
        if (_weapon.Length > 0 && weapon.Length == 0 && ganonNear && hx != null) { hx.Play(Fx.SwordLost); _swordGoneSince = now; Glow((60, 60, 80), 600, 0.6f); }
        if (_swordGoneSince >= 0)
        {
            if (weapon.Length > 0) { hx?.Play(Fx.EquipSword, 1.2f); hx?.Play(Fx.Detent); q.Pulse(now, 0, 6, 90, 80); _swordGoneSince = -1; }
            else { R = Effects.Off(); if (now % 900 < 30) q.Pulse(now, 0, 1.5f, 250, 25); }
        }
        _weapon = weapon;
        // Ganon's third eye open: a window to strike, the triggers glow with a faint tremor
        if (ganonNear && _flag.GetValueOrDefault(HEye) && now % 600 < 20) q.Pulse(now, 2, 2, 120, 200, 0, false, true);

        // ---- Gerudo guards: a heartbeat that quickens as the nearest guard comes closer; caught = alarm ----
        bool inEvent = act.InEvent;
        if (inEvent && !_wasEvent) _eventStart = now;
        _wasEvent = inEvent;
        if (Map("Gerudo") && cb != null && hx != null && !paused)
        {
            Combat.Seen? guard = null;
            foreach (var o in cb.Objects)
                if (o.Kind == Combat.ObjKind.Guard && o.Distance < 14 && (guard == null || o.Distance < guard.Value.Distance)) guard = o;
            if (guard is { } g)
            {
                if (_loggedGuards.Add(g.Name)) Log.Write($"GUARD {g.Name} ({g.Distance:F1} m)", false);
                _guardSeen = now;
                if (now >= _nextGuardBeat && !inEvent)
                {
                    float k = 1 - g.Distance / 14;
                    var (palm, pan, tl, tr) = Quad.Place(g.Pan, g.Front, 0.3f + 0.5f * k);
                    hx.Play(Fx.Heartbeat, Math.Max(0.3f, palm), 1, pan); q.Pulse(now, tl * 0.5f, tr * 0.5f, 30, 70, 0, false, true);
                    _nextGuardBeat = now + (long)(380 + 1000 * (1 - k));
                }
            }
            // caught: a scene starts near a guard, then Link is somewhere else (the cell / the entrance)
            float jump = MathF.Abs(s.X - _lastX) + MathF.Abs(s.Z - _lastZ);
            if (jump > 3 && _eventStart >= 0 && now - _eventStart < 6000 && _eventStart - _guardSeen < 2500 && now >= _caughtUntil)
            {
                hx.Play(Fx.Caught); Glow((255, 0, 0), 1500, 0.9f, true); _micUntil = now + 2000; _caughtUntil = now + 5000;
                Log.Write("CAUGHT by a Gerudo guard", false);
            }
            if (now < _caughtUntil - 4300) R = L = Effects.Feedback(0, 8);
        }
        _lastX = s.X; _lastZ = s.Z;

        // ---- bunny hood: springy steps when the mask makes you faster than you usually run ----
        float speed = act.PosSpeed; long bunnyLast = _bunnyT; _bunnyT = now;
        bool grounded = Math.Abs(s.Vy) < 0.5f && !act.HasVerb("Swim");
        if (!Masked && grounded && speed > 1) _runBase = Math.Max(_runBase * 0.9995f, speed);
        if (Masked && Child && grounded && speed > _runBase * 1.12f && hx != null && !paused)
        {
            double before = _bunnyPh; _bunnyPh += speed * 0.5 * Math.Clamp((now - bunnyLast) / 1000.0, 0, 0.1);
            if (Math.Floor(_bunnyPh) != Math.Floor(before) && now - _bunnyStep > 120)
            { hx.Play(Fx.ChainJump, 0.45f, 2, (long)Math.Floor(_bunnyPh) % 2 == 0 ? -0.6f : 0.6f); _bunnyStep = now; }
        }

        Debug = $"{(Child ? "child" : "adult")}{(Dream ? " DREAM" : "")}{(_timerOn ? $" timer {_timerLeft:F0}s" : "")}{(_score > 0 ? $" score {_score}" : "")}" +
                $"{(_water >= 0 ? $" water {_water}" : "")}{(Escaping ? " ESCAPE" : "")}{(Racing ? " RACE" : "")}{(OnShip ? " SHIP" : "")}{(Hooked ? " HOOKED" : "")}{(Masked ? " MASK" : "")}";
    }

    void Poll(long now, GameData gd, Haptics? hx, Quad q, Oot.State s, string map, Combat? cb)
    {
        bool Map(params string[] k) => k.Any(x => map.Contains(x, StringComparison.OrdinalIgnoreCase));

        // ---- time travel: the Master Sword in the Temple of Time. Silence, a heartbeat fading out, white light,
        //      and you wake up in the other body (impacts heavier as an adult, lighter as a child) ----
        bool child = gd.Bool(HChild) ?? true;
        if (_child is { } was && was != child && Map("TempleOfTime", "ShrineOfTime", "DoorOfTime") && hx != null)
        {
            hx.Hush(1000);
            Later(now, 800, () => { hx.Play(Fx.TimeShift); q.Pulse(now + 800, 5, 5, 30, 1800, 0, true, true); });
            Glow((255, 255, 255), 3500, 0.95f, false, 800);
        }
        _child = child; Child = child;
        HVoice.BodyRate = child ? 1.15f : 0.92f; HVoice.BodyWeight = child ? 0.88f : 1.08f;

        // ---- the dream world (Trial of the Hero of Time): everything a little softer, the light violet ----
        Dream = Map("Dream");
        if (hx != null) hx.FxScale = Dream ? 0.85f : 1f;

        // ---- minigames: start shot, timer, score, records ----
        bool flag = gd.Bool(HTimerOn) ?? false, down = gd.Bool(HTimerDown) ?? false;
        float val = gd.Float(HTimer) ?? 0, lim = gd.Float(HLimit) ?? 0;
        if (lim > 1000) { val /= 1000; lim /= 1000; } // shortcut: assumes seconds; a limit over 1000 must be ms
        // a clock only counts as running while its value moves: a flag left set after a minigame (or read at boot)
        // must not tick or fire a starting shot
        if (float.IsNaN(_timerVal)) _timerVal = val; // first read: whatever is there already is not a running clock
        else if (val != _timerVal) { _timerVal = val; _timerMoved = now; }
        bool on = flag && now - _timerMoved < 1500;
        float left = down ? val : lim > 0 ? lim - val : -1;
        if (on && !_timerOn) { hx?.Play(Fx.StartShot); q.Pulse(now, 0, 8, 50, 90); Glow((255, 255, 255), 250, 0.9f); _scoreGameUntil = now + 120000; }
        if (!on && _timerOn && _timerLeft >= 0 && _timerLeft < 0.6f) { hx?.Play(Fx.TimeUp); _micUntil = now + 1500; Glow((255, 40, 0), 800, 0.8f); }
        _timerOn = on; _timerLeft = on ? left : -1;
        _timerFrac = on && lim > 0 && left >= 0 ? Math.Clamp(left / lim, 0, 1) : -1;

        int score = gd.Int(HScore) ?? -1, max = gd.Int(HScoreMax) ?? 0;
        if (_score >= 0 && score > _score && score - _score < 1000)
        {
            int d = score - _score;
            hx?.Play(Fx.TargetHit, Math.Min(1.4f, 0.8f + d / 200f)); q.Pulse(now, 4, 5, 150, 50); Glow((255, 220, 60), 180, 0.7f);
            if (_score < 750 && score >= 750 || _score < 1400 && score >= 1400) { hx?.Play(Fx.Fanfare); Glow((255, 200, 40), 1500, 0.9f); }
            _scoreGameUntil = now + 60000; _shotAt = 0;
        }
        _score = score;
        if (now < _scoreGameUntil && !_timerOn && max > 0 && score >= 0) _timerFrac = Math.Clamp(score / (float)max, 0, 1);

        // the fishing pond: a new record lands with a fanfare, by size
        int fa = gd.Int(HFishA) ?? -1, fc = gd.Int(HFishC) ?? -1;
        if (_fishA >= 0 && fa > _fishA || _fishC >= 0 && fc > _fishC)
        {
            int size = Math.Max(fa, fc);
            hx?.Play(Fx.Catch, 1, size >= 60 ? 3 : size >= 50 ? 2 : 1); Later(now, 900, () => hx?.Play(Fx.Fanfare, size >= 50 ? 1.3f : 1f));
            Glow((255, 210, 60), 2000, 0.9f, false, 900);
        }
        _fishA = fa; _fishC = fc;
        Rise(HPondRain, gd.Bool(HPondRain) ?? false);

        // ---- water levels: the water rises from the palms into the fingers, or drains away downward ----
        int water = gd.Int(HWater) ?? -1;
        if (_water >= 0 && water >= 0 && water != _water && hx != null) WaterChange(now, hx, q, water > _water);
        _water = water;
        foreach (var h in WaterDown) if (Rise(h, gd.Bool(h) ?? false) && hx != null) WaterChange(now, hx, q, false);

        // ---- ice melts ----
        foreach (var h in Melts) if (Rise(h, gd.Bool(h) ?? false)) { hx?.Play(Fx.Melt); Glow((120, 200, 255), 1200, 0.6f); }

        // ---- the castle collapses: each stage of the escape is a blow, then the shaking goes on ----
        foreach (var h in Escape)
            if (Rise(h, gd.Bool(h) ?? false) && hx != null)
            {
                hx.Play(Fx.Collapse); q.Pulse(now, 8, 8, 30, 300, 0, true, true); hx.Duck(800);
                Glow((255, 80, 0), 1500, 0.8f, true); _escapeUntil = now + 90000;
            }

        // ---- Ganon ----
        if (Rise(HEye, gd.Bool(HEye) ?? false)) { hx?.Play(Fx.Chime, 1.2f); Glow((255, 215, 90), 2500, 0.6f); }
        if (Rise(HZelda, gd.Bool(HZelda) ?? false)) { hx?.Play(Fx.Nayru, 1.3f); q.Pulse(now, 5, 5, 60, 900, 0, false, true); Glow((255, 250, 220), 2000, 0.9f); }

        // ---- cuccos ----
        if (Rise(HCucco, gd.Bool(HCucco) ?? false)) { hx?.Play(Fx.Rage, 1.2f); Glow((255, 255, 255), 6000, 0.5f, true); }

        // ---- masks ----
        bool head = gd.Bool(HHead) ?? false;
        if (Rise(HHead, head)) hx?.Play(Fx.MaskOn);
        else if (_flag.ContainsKey(HHead) && Masked && !head) hx?.Play(Fx.MaskOn, 1, 2);
        Masked = head;

        // ---- bottles: what goes in, what is drunk ----
        foreach (var h in Bottles)
        {
            uint v = gd.Enum(h) ?? 0;
            if (_enum.TryGetValue(h, out var old) && old != v && hx != null)
            {
                int prev = Content.GetValueOrDefault(old, -1), cur = Content.GetValueOrDefault(v, -1);
                if (prev == 0 && cur is > 0 and < 7) hx.Play(Fx.BottleCatch, 1, cur);                 // caught something
                else if (prev == 6 && (cur == 0 || cur == 6)) hx.Play(Fx.Gulp, 1, 3);                 // drank it (or half the milk)
                else if (prev is 1 && cur == 0) { } // a fairy let go: the heal / revive effect plays already
                else if (prev is 2 or 3 or 4 or 5 && cur == 0) hx.Play(Fx.Bottle, 0.8f);               // let it out
            }
            _enum[h] = v;
        }

        // ---- Ingo's race and the Shadow Temple's ship (sequence enums) ----
        uint ep = gd.Enum(HEponaSeq) ?? 0;
        if (_enum.TryGetValue(HEponaSeq, out var epOld) && epOld != ep)
        {
            if (ep == VRace) { hx?.Play(Fx.StartShot); q.Pulse(now, 0, 8, 50, 90); Glow((255, 160, 60), 1200, 0.6f); }
            if (epOld == VRace && ep == VComplete) { hx?.Hush(500); Later(now, 400, () => { hx?.Play(Fx.Fanfare, 1.3f); hx?.Play(Fx.KillMidBoss, 0.8f); q.Pulse(now + 400, 8, 8, 35, 250); }); Glow((255, 200, 40), 2500, 0.9f, false, 400); }
        }
        _enum[HEponaSeq] = ep; Racing = ep == VRace;
        uint sh = gd.Enum(HShadowSeq) ?? 0;
        if (_enum.TryGetValue(HShadowSeq, out var shOld) && shOld == VShip && sh == VAfterShip && hx != null)
        { hx.Play(Fx.ShipSink); q.Pulse(now, 4, 4, 28, 2000, 0, true, true); }
        _enum[HShadowSeq] = sh; OnShip = sh == VShip && Map("Shadow");
    }

    void WaterChange(long now, Haptics hx, Quad q, bool up)
    {
        hx.Play(Fx.WaterRise, 1, up ? 1 : 2);
        if (up) { q.Pulse(now, 3, 3, 30, 600, 900, false, true); q.Pulse(now, 6, 6, 30, 700, 1600, true, true); } // reaches the fingers last
        else { q.Pulse(now, 6, 6, 30, 700, 0, true, true); q.Pulse(now, 3, 3, 30, 600, 700, true, true); }      // leaves the fingers first
        Glow((30, 110, 255), 2400, 0.7f);
    }

    /// The light for this moment, or null.
    public ((float, float, float) c, float mix)? Light(long now)
    {
        if (Dream && now >= _lightUntil) return ((120, 70, 200), 0.35f);
        if (now < _lightFrom || now >= _lightUntil) return null;
        var c = _light;
        if (_lightFlicker && _rng.NextDouble() < 0.35) c = (c.r * 0.35f, c.g * 0.35f, c.b * 0.35f);
        float fade = Math.Clamp((_lightUntil - now) / 400f, 0, 1);
        return (c, _lightMix * fade);
    }
}
