using System.Collections.Concurrent;

namespace PulseOfTime;

/// Enemies near Link and the damage they take (layout: sloperinaGZ actors.c, build 1.1.1).
/// Actor manager [main+0x4E90D90]: red-black tree at +0x38, nodes sit at entry+8 (left +0, right +8),
/// entry +0x28 = actor, +0x38 = next entry with the same key. Actor: name char* +0x20 ("Enemy_Wolfos"),
/// state u32 +0x2C (4-5 running), position f32[3] +0x31C, life [[[actor+0x270]+0x2B8]+0x1C10]+8 (max +0x1C18).
/// A background thread finds the enemies (the walk is a few thousand reads); Poll reads only their life.
sealed class Combat : IDisposable
{
    const ulong ActorMgr = 0x4E90D90, CameraPtr = 0x4E98068;
    const float Near = 25f;

    public sealed class Enemy
    {
        public ulong Actor, LifePtr, MaxPtr; public string Name = "";
        public int Life = -1, Max; public float X, Y, Z; public int Verb = -1;
    }
    public enum ObjKind { None, Treasure, Heat, Water, Danger, Wind, Lock, Fish, Guard }
    /// Objects that vanished near Link since the last poll (a chain lock opened, treasure picked up).
    public readonly List<Seen> Gone = new();
    public sealed class Obj { public ulong Actor; public string Name = ""; public ObjKind Kind; public float LastDist = -1; }
    /// A world object relative to Link: Closing = m/s it comes nearer (positive: approaching).
    public readonly record struct Seen(ObjKind Kind, string Name, float Pan, float Front, float Distance, float Closing);
    public readonly List<Seen> Objects = new();
    /// Every enemy near Link this frame (for target / boss life on the LEDs).
    public readonly record struct Foe(string Name, int Life, int Max, float Pan, float Front, float Distance, bool Boss);
    public readonly List<Foe> Live = new();
    const float ObjNear = 20f;
    volatile List<Obj> _objFound = new();
    readonly Dictionary<ulong, Obj> _objTracked = new();
    long _lastPollTicks;

    static ObjKind KindOf(string n)
    {
        bool Has(params string[] k) => k.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase));
        // shortcut: guards and fish by name only (names not verified in play; check the ENEMY/ANIM log at the fortress / pond)
        if (Has("Gerudo") && (n.StartsWith("Npc_") || n.StartsWith("Enemy_")) && !Has("MiddleBoss", "Shop")) return ObjKind.Guard; // characters only, not props
        if (Has("Fish") && !Has("Fishing", "Rod", "Lure", "Bottle", "BGM_")) return ObjKind.Fish;
        if (n.StartsWith("BGM_") || n.StartsWith("Npc_") || n.StartsWith("EftLoc_CameraEnv") || n.StartsWith("Shop_")) return ObjKind.None;
        if (Has("ChainLock", "KeyLock", "LockedDoor", "Door_Lock")) return ObjKind.Lock;
        if (Has("Heart_Piece", "HeartPiece", "HeartContainer", "Obj_Rupee", "Rupee_Gold", "Rupee_Silver")) return ObjKind.Treasure;
        if (Has("RollingRock", "Boulder", "Spike", "Blade", "Guillotine", "IronBall", "Trap")) return ObjKind.Danger;
        if (Has("Fire", "Torch", "Flame", "Lava", "Brazier", "Bonfire", "Magma", "Burner", "Candle")) return ObjKind.Heat;
        if (Has("Waterfall", "Geyser", "WaterSpout")) return ObjKind.Water;
        if (Has("Fan", "Windmill", "Tornado", "Whirlwind")) return ObjKind.Wind;
        return ObjKind.None;
    }

    /// An enemy started a new action (its main BSA verb changed), with where it is.
    public readonly record struct Act(string Name, string Verb, float Pan, float Front, float Distance, bool Boss);
    public readonly List<Act> Acts = new();

    /// An actor's main action verb (layer 0), same layout as Link's (Oot.Verb).
    int VerbOf(ulong actor)
    {
        ulong bsa = P(P(actor + 0x270) + 0x188);
        ulong slot = bsa == 0 ? 0 : P(bsa + 0x1C0);
        if (slot == 0) return -1;
        var b = new byte[0x50];
        if (!_m.ReadVa(slot, b, b.Length)) return -1;
        int v = BitConverter.ToInt32(b, 0x28);
        return v == 0x11A ? BitConverter.ToInt32(b, 0x4C) : v;
    }
    /// Pan: -1 left .. 1 right of the camera; Front: -1 behind .. 1 ahead.
    public readonly record struct Hit(string Name, int Damage, int Max, bool Killed, float Pan, float Front, float Distance, bool Boss);
    public float NearestSide { get; private set; }
    public float NearestFront { get; private set; } = 1;

    readonly GuestMemory _m; readonly ulong _main;
    volatile List<Enemy> _found = new();
    readonly Dictionary<ulong, Enemy> _tracked = new();
    readonly Thread _thread; volatile bool _stop;
    public ulong Link; // written by the main loop, read by the walk (64-bit stores are atomic on x64)
    public int Nearby { get; private set; }          // enemies within Near
    public float NearestDistance { get; private set; } = 999;
    public bool BossNear { get; private set; }

    public Combat(GuestMemory m, ulong main)
    {
        _m = m; _main = main;
        _thread = new Thread(Discover) { IsBackground = true, Name = "enemies", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    ulong P(ulong va) => va == 0 || (va & 7) != 0 ? 0 : _m.ReadU64(va);
    int I32(ulong va) { var b = new byte[4]; return _m.ReadVa(va, b, 4) ? BitConverter.ToInt32(b) : int.MinValue; }
    bool InMain(ulong p) => p >= _main && p < _main + 0x5000000;

    (float x, float y, float z) Pos(ulong actor)
    {
        var b = new byte[12];
        return _m.ReadVa(actor + 0x31C, b, 12) ? (BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8)) : default;
    }

    void Discover()
    {
        var entry = new byte[0x40]; var head = new byte[0x10]; var nm = new byte[48];
        while (!_stop)
        {
            try
            {
                var list = new List<Enemy>(); var seenNames = new List<string>(); var objs = new List<Obj>();
                ulong link = Link;
                var lp = link != 0 ? Pos(link) : default;
                ulong mgr = P(_main + ActorMgr), root = mgr != 0 ? P(mgr + 0x38) : 0;
                var stack = new Stack<ulong>(); ulong node = root; int visits = 0;
                while ((node != 0 || stack.Count > 0) && visits < 6000)
                {
                    while (node != 0 && stack.Count < 64) { stack.Push(node); node = P(node); }
                    if (node != 0) break; // deeper than a balanced tree: changed under us
                    node = stack.Pop();
                    for (ulong e = node - 8; e != 0 && visits < 6000; e = BitConverter.ToUInt64(entry, 0x38))
                    {
                        visits++;
                        if ((e & 7) != 0 || !_m.ReadVa(e, entry, 0x40)) break;
                        ulong actor = BitConverter.ToUInt64(entry, 0x28);
                        if (actor == 0 || actor == link || !_m.ReadVa(actor + 0x20, head, 0x10)) continue;
                        uint state = BitConverter.ToUInt32(head, 0xC);
                        if (state < 4 || state > 5) continue;
                        ulong name = BitConverter.ToUInt64(head, 0);
                        if (name == 0 || !_m.ReadVa(name, nm, nm.Length)) continue;
                        int n = Array.IndexOf(nm, (byte)0);
                        string s = System.Text.Encoding.ASCII.GetString(nm, 0, n < 0 ? nm.Length : n);
                        if (Debug && seenNames.Count < 400) seenNames.Add(s);
                        var (x, y, z) = Pos(actor);
                        float dx = x - lp.x, dy = y - lp.y, dz = z - lp.z;
                        float d2 = dx * dx + dy * dy + dz * dz;
                        if (!s.StartsWith("Enemy_"))
                        {   // things in the world worth feeling (treasure, heat, water, danger, wind)
                            var kind = KindOf(s);
                            if (kind != ObjKind.None && (link == 0 || d2 < ObjNear * ObjNear) && objs.Count < 48) objs.Add(new Obj { Actor = actor, Name = s, Kind = kind });
                            continue;
                        }
                        if (s.Contains("Skulltula_Gold") && (link == 0 || d2 < ObjNear * ObjNear) && objs.Count < 48) objs.Add(new Obj { Actor = actor, Name = s, Kind = ObjKind.Treasure });
                        else if (KindOf(s) is ObjKind.Guard or ObjKind.Fish && (link == 0 || d2 < ObjNear * ObjNear) && objs.Count < 48) objs.Add(new Obj { Actor = actor, Name = s, Kind = KindOf(s) });
                        if (link != 0 && d2 > Near * Near) continue;
                        ulong comp = P(P(actor + 0x270) + 0x2B8);
                        ulong cur = comp != 0 ? P(comp + 0x1C10) : 0, max = comp != 0 ? P(comp + 0x1C18) : 0;
                        if (cur == 0 || max == 0 || !InMain(P(cur)) || !InMain(P(max))) continue;
                        list.Add(new Enemy { Actor = actor, LifePtr = cur, MaxPtr = max, Name = s });
                    }
                    node = P(node + 8);
                }
                _found = list; _objFound = objs; Visits = visits; if (Debug) Names = seenNames;
            }
            catch { }
            Thread.Sleep(350);
        }
    }

    /// Reads the life of the enemies found so far; returns the hits since the last call.
    public List<Hit> Poll()
    {
        var hits = new List<Hit>();
        Acts.Clear(); Live.Clear(); Objects.Clear(); Gone.Clear();
        var found = _found;
        var seen = new HashSet<ulong>();
        ulong link = Link;
        var lp = link != 0 ? Pos(link) : default;
        // camera right vector (gameplay camera eye +0x38, look-at +0x44) for left/right
        float rx = 1, rz = 0, ux = 0, uz = 1; // camera right and forward on the ground plane
        ulong cam = P(P(_main + CameraPtr) + 0x68);
        if (cam != 0)
        {
            var b = new byte[24];
            if (_m.ReadVa(cam + 0x38, b, 24))
            {
                float fx = BitConverter.ToSingle(b, 12) - BitConverter.ToSingle(b, 0), fz = BitConverter.ToSingle(b, 20) - BitConverter.ToSingle(b, 8);
                float len = MathF.Sqrt(fx * fx + fz * fz);
                if (len > 1e-3f) { fx /= len; fz /= len; rx = -fz; rz = fx; ux = fx; uz = fz; } // right = forward x up
            }
        }
        int nearby = 0; float nearest = 999; bool boss = false;
        foreach (var f in found)
        {
            seen.Add(f.Actor);
            if (!_tracked.TryGetValue(f.Actor, out var e)) _tracked[f.Actor] = e = f;
            int life = I32(e.LifePtr + 8), max = I32(e.MaxPtr + 8);
            if (life == int.MinValue || max <= 0) continue;
            (e.X, e.Y, e.Z) = Pos(e.Actor);
            float dx = e.X - lp.x, dz = e.Z - lp.z, dist = MathF.Sqrt(dx * dx + dz * dz);
            float pan = dist > 0.3f ? Math.Clamp((dx * rx + dz * rz) / dist, -1, 1) : 0;
            float front = dist > 0.3f ? Math.Clamp((dx * ux + dz * uz) / dist, -1, 1) : 1;
            nearby++;
            if (dist < nearest) { nearest = dist; NearestSide = pan; NearestFront = front; }
            bool isBoss = e.Name.Contains("Boss");
            boss |= isBoss;
            if (e.Life >= 0 && life < e.Life)
                hits.Add(new Hit(e.Name, e.Life - life, max, life <= 0, pan, front, dist, isBoss));
            if (dist < 14)
            {
                int v = VerbOf(e.Actor);
                if (v != e.Verb && e.Verb != -1 && v >= 0) Acts.Add(new Act(e.Name, Verbs.Name(v), pan, front, dist, isBoss));
                e.Verb = v;
            }
            e.Life = life; e.Max = max;
            if (life > 0) Live.Add(new Foe(e.Name, life, max, pan, front, dist, isBoss));
        }
        foreach (var k in _tracked.Keys.Where(k => !seen.Contains(k)).ToList()) _tracked.Remove(k);
        Nearby = nearby; NearestDistance = nearest; BossNear = boss;

        // world objects: where they are and whether they are coming nearer
        long nowT = Environment.TickCount64; float dt = Math.Clamp((nowT - _lastPollTicks) / 1000f, 0.005f, 0.2f); _lastPollTicks = nowT;
        var objSeen = new HashSet<ulong>();
        foreach (var o in _objFound)
        {
            objSeen.Add(o.Actor);
            if (!_objTracked.TryGetValue(o.Actor, out var t)) _objTracked[o.Actor] = t = o;
            var (x, y, z) = Pos(t.Actor);
            float dx = x - lp.x, dy = y - lp.y, dz = z - lp.z;
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            float h = MathF.Sqrt(dx * dx + dz * dz);
            float pan = h > 0.3f ? Math.Clamp((dx * rx + dz * rz) / h, -1, 1) : 0;
            float front = h > 0.3f ? Math.Clamp((dx * ux + dz * uz) / h, -1, 1) : 1;
            float closing = t.LastDist >= 0 ? (t.LastDist - dist) / dt : 0;
            t.LastDist = dist;
            Objects.Add(new Seen(t.Kind, t.Name, pan, front, dist, Math.Clamp(closing, -30, 30)));
        }
        foreach (var k in _objTracked.Keys.Where(k => !objSeen.Contains(k)).ToList())
        {
            var o = _objTracked[k];
            if (o.LastDist >= 0 && o.LastDist < 5) Gone.Add(new Seen(o.Kind, o.Name, 0, 1, o.LastDist, 0));
            _objTracked.Remove(k);
        }
        return hits;
    }

    public IReadOnlyList<Enemy> Found => _found;
    public bool Debug; public volatile int Visits; public List<string> Names = new();

    public void Dispose() { _stop = true; }
}
