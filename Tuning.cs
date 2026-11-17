using System.Text.Json;

namespace PulseOfTime;

/// One place for how strong everything is: a factor per effect ("fx.Jump"), per texture ("loop.Rain") and a
/// few global ones. Kept in tuning.json next to the exe, written with every key on first start, re-read while
/// running when the file changes, so strengths can be tuned live without a restart.
static class Tuning
{
    static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "tuning.json");
    static Dictionary<string, float> _v = new();
    static DateTime _stamp; static long _nextCheck;

    // calibrated by hand: punchy where an action is quick and small (ticks, grips, swings), calmer where an
    // effect is long or heavy anyway (jump, flips, falls); a factor above 1 drives the soft clipper harder
    static readonly Dictionary<string, float> Defaults = new()
    {
        ["fx.Jump"] = 0.45f, ["fx.Flip"] = 0.7f, ["fx.Land"] = 0.85f, ["fx.Roll"] = 0.95f, ["fx.Hit"] = 1.15f, ["fx.Heal"] = 1.3f,
        ["fx.Heartbeat"] = 1.25f, ["fx.Swing"] = 1.4f, ["fx.SpinRelease"] = 1.25f, ["fx.ShieldBlock"] = 1.25f, ["fx.LockOn"] = 1.6f,
        ["fx.Grip"] = 1.9f, ["fx.Spur"] = 1.1f, ["fx.BowRelease"] = 1.25f, ["fx.SlingRelease"] = 1.35f, ["fx.DryFire"] = 1.6f,
        ["fx.EnemyHit"] = 1.3f, ["fx.KillPop"] = 1.3f, ["fx.KillCrunch"] = 1.3f, ["fx.KillBones"] = 1.3f, ["fx.KillArmor"] = 1.2f,
        ["fx.KillSquish"] = 1.25f, ["fx.KillGhost"] = 1.3f, ["fx.KillHeavy"] = 1.1f, ["fx.Token"] = 1.35f, ["fx.Coin"] = 1.45f,
        ["fx.CoinSpend"] = 1.6f, ["fx.AmmoGet"] = 1.6f, ["fx.MagicUse"] = 1.35f, ["fx.MagicFill"] = 1.35f, ["fx.SongGet"] = 1.3f,
        ["fx.KeyGet"] = 1.35f, ["fx.MapGet"] = 1.7f, ["fx.Thrust"] = 1.35f, ["fx.DoorOpen"] = 1.15f, ["fx.BootsClunk"] = 1.25f,
        ["fx.Lift"] = 1.25f, ["fx.Throw"] = 1.35f, ["fx.Put"] = 1.5f, ["fx.Bottle"] = 1.35f, ["fx.Nayru"] = 1.35f, ["fx.Farore"] = 1.2f,
        ["fx.Perfect"] = 1.25f, ["fx.Repelled"] = 1.35f, ["fx.Shatter"] = 1.25f, ["fx.Stagger"] = 1.25f, ["fx.Struggle"] = 1.15f,
        ["fx.Boomerang"] = 1.35f, ["fx.BoomerangCatch"] = 1.6f, ["fx.HookshotFire"] = 1.35f, ["fx.NoteOn"] = 1.6f, ["fx.Chime"] = 1.35f,
        ["fx.SwordDraw"] = 1.45f, ["fx.Sheathe"] = 1.6f, ["fx.EquipSword"] = 1.45f, ["fx.EquipShieldWood"] = 1.5f, ["fx.EquipShieldMetal"] = 1.4f,
        ["fx.EquipCloth"] = 1.5f, ["fx.EquipHeavy"] = 1.2f, ["fx.EquipHover"] = 1.4f, ["fx.ItemTick"] = 1.9f, ["fx.MenuOpen"] = 1.45f,
        ["fx.MenuClose"] = 1.45f, ["fx.MenuTick"] = 2.1f, ["fx.MenuConfirm"] = 1.7f, ["fx.MenuBack"] = 1.8f, ["fx.PageFlip"] = 1.7f,
        ["fx.TextTick"] = 1.9f, ["fx.Sunrise"] = 1.25f, ["fx.Nightfall"] = 1.25f, ["fx.Ping"] = 1.45f, ["fx.Puddle"] = 1.45f,
        ["fx.HeatPulse"] = 1.2f, ["fx.AirGasp"] = 1.35f, ["fx.Telegraph"] = 1.25f, ["fx.Alert"] = 1.6f, ["fx.Appear"] = 1.15f,
        ["fx.Stun"] = 1.45f, ["fx.Blocked"] = 1.35f, ["fx.NutFlash"] = 1.15f, ["fx.StickSnap"] = 1.6f, ["fx.ChuRoll"] = 1.25f,
        ["fx.Splash"] = 1.15f, ["fx.Footstep"] = 1.3f,
        ["loop.Swim"] = 1.15f, ["loop.Underwater"] = 1.0f, ["loop.Rain"] = 1.45f, ["loop.FallWind"] = 0.75f, ["loop.Burn"] = 1.0f,
        ["loop.Shock"] = 0.9f, ["loop.Frozen"] = 1.2f, ["loop.Squeeze"] = 1.1f, ["loop.Strain"] = 1.5f, ["loop.Grind"] = 1.1f,
        ["loop.Quake"] = 1.2f, ["loop.Motor"] = 1.1f, ["loop.Warmth"] = 1.2f, ["loop.Danger"] = 1.2f, ["loop.Mist"] = 1.3f, ["loop.Sink"] = 1.2f,
        ["fx.Sparkle"] = 1.4f, ["fx.Finisher"] = 1.2f, ["fx.BladeRing"] = 1.3f, ["fx.SwingHeavy"] = 1.3f, ["fx.Arrive"] = 1.2f, ["fx.Unlock"] = 1.4f, ["fx.ProfileTick"] = 1.8f,
        ["loop.Slide"] = 1.1f, ["loop.Wind"] = 1.0f, ["loop.Hooves"] = 1.35f, ["loop.Note"] = 1.3f, ["loop.Crawl"] = 1.45f, ["loop.Fuse"] = 1.25f,
        ["bow.creak"] = 1.25f, ["spin.charge"] = 1.0f, ["triggers.tremor"] = 1.15f, ["sound.layer"] = 1.0f, ["master"] = 1.0f,
    };

    public static float Get(string key, float def = 1f)
    {
        Refresh();
        return _v.TryGetValue(key, out var v) ? v : Defaults.TryGetValue(key, out var d) ? d : def;
    }
    /// Changes one value and writes the file (keeps every other key as it is).
    public static void Set(string key, float value)
    {
        Refresh();
        try
        {
            var all = File.Exists(FilePath) ? JsonSerializer.Deserialize<SortedDictionary<string, float>>(File.ReadAllText(FilePath)) ?? new() : new();
            all[key] = value; _v = new Dictionary<string, float>(_v) { [key] = value }; // copy-on-write: the audio thread reads _v at the same time
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            _stamp = File.GetLastWriteTimeUtc(FilePath);
        }
        catch { _v = new Dictionary<string, float>(_v) { [key] = value }; }
    }

    // keys built once: the audio thread asks for every texture's factor 100 times a second, without allocating
    static readonly string[] FxKeys = Enum.GetNames<Fx>().Select(n => "fx." + n).ToArray(), LoopKeys = Enum.GetNames<Loop>().Select(n => "loop." + n).ToArray();
    public static float Of(Fx fx) => Get(FxKeys[(int)fx]);
    public static float Of(Loop lp) => Get(LoopKeys[(int)lp]);

    static void Refresh()
    {
        long now = Environment.TickCount64;
        if (now < _nextCheck) return;
        _nextCheck = now + 2000;
        try
        {
            if (!File.Exists(FilePath))
            {
                var all = new SortedDictionary<string, float>(Defaults);
                foreach (Fx f in Enum.GetValues<Fx>()) all.TryAdd("fx." + f, 1f);
                foreach (Loop l in Enum.GetValues<Loop>()) if (l != Loop.Count) all.TryAdd("loop." + l, 1f);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            var stamp = File.GetLastWriteTimeUtc(FilePath);
            if (stamp == _stamp) return;
            _stamp = stamp;
            _v = JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* a half-written file while editing: keep the last good values */ }
    }
}
