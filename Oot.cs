namespace PulseOfTime;

/// Reads Link's state from OoT (Switch 2) build E0F3CCFA (1.1.1) inside Lichii.
/// Offsets: github.com/Kabosune/oot-remake-modding (docs/, mods/slopped-up-sloperina/src/z64hd_builds.h).
/// All guest pointers are guest VAs; main is loaded at a random address each boot, found via its MOD0 tag.
sealed class Oot
{
    // main offsets, build E0F3CCFA (1.1.1)
    const ulong Mod0 = 0x3C4705C;
    const ulong VtLinkObj = 0x4B25E18, VtParam = 0x4B63438;
    const ulong CameraPtr = 0x4E98068;
    static readonly (ulong global, ulong member)[] LinkPaths =
        { (0x4E371D0, 0x480), (0x4E94858, 0x2E0), (0x4E935B8, 0xEF8), (0x4E94860, 0x328), (0x4E94368, 0xD70) };

    readonly GuestMemory _m;
    public ulong Main { get; private set; }
    public ulong Link { get; private set; } // Link's actor after the last Read

    /// Link's current action verbs, one per BSA layer 0..4 (QueryPlayerGetCurrentActionVerb, decompiled):
    /// slot = [[[actor+0x270]+0x188]+0x1C0+8*layer]; verb = [slot+0x28], or [slot+0x4C] when that is 282
    /// (out of range). Index into Verbs.Names (the game's verb table at main+0x4B4ABC8). -1 = none.
    public int Verb(int layer)
    {
        ulong bsa = P(P(Link + 0x270) + 0x188);
        ulong slot = bsa == 0 ? 0 : P(bsa + 0x1C0 + 8 * (ulong)layer);
        if (slot == 0) return -1;
        var b = new byte[0x50];
        if (!_m.ReadVa(slot, b, b.Length)) return -1;
        int v = BitConverter.ToInt32(b, 0x28);
        if (v == 0x11A) v = BitConverter.ToInt32(b, 0x4C);
        return v;
    }

    /// Link's playing animation commands per AS slot (QueryASGetCurrentCommand / GetCurrentFrame, decompiled):
    /// as = [[[[actor+0x270]+0x40]+0x18]+0x18]; slots: int count at as+0x18, cmd = [[as+0x20] + i*0x48],
    /// name = char* at cmd+0x10, frame = float at cmd+0x10C.
    public List<(string name, float frame)> Anims(int max = 4)
    {
        var list = new List<(string, float)>();
        ulong a = P(P(P(P(Link + 0x270) + 0x40) + 0x18) + 0x18);
        if (a == 0) return list;
        var b = new byte[0x28];
        if (!_m.ReadVa(a, b, 0x28)) return list;
        int count = Math.Min(BitConverter.ToInt32(b, 0x18), max);
        ulong arr = BitConverter.ToUInt64(b, 0x20);
        var nb = new byte[64]; var fb = new byte[4];
        for (int i = 0; i < count; i++)
        {
            ulong cmd = P(arr + (ulong)i * 0x48);
            if (cmd == 0) continue;
            ulong name = P(cmd + 0x10);
            string s = name != 0 && _m.ReadVa(name, nb, 64) ? System.Text.Encoding.ASCII.GetString(nb, 0, Math.Max(0, Array.IndexOf(nb, (byte)0) is int z && z >= 0 ? z : 64)) : "";
            float f = _m.ReadVa(cmd + 0x10C, fb, 4) ? BitConverter.ToSingle(fb) : float.NaN;
            list.Add((s, f));
        }
        return list;
    }

    /// The running event's flowchart name ("Npc_Hylia_035_Child_C0_N", "Dm_SY_Ocarina_SunSong", "Dm_SY_Get", ...):
    /// char* at [[main+0x4E94840]+0x338] (EventMgr; found live with the objstr command). "" = none.
    public string EventName()
    {
        ulong mgr = P(Main + 0x4E94840);
        ulong p = mgr != 0 ? P(mgr + 0x338) : 0;
        var b = new byte[64];
        if (p == 0 || !_m.ReadVa(p, b, 64)) return "";
        int n = 0; while (n < 64 && b[n] >= 0x20 && b[n] < 0x7F) n++;
        return n < 64 && b[n] != 0 ? "" : System.Text.Encoding.ASCII.GetString(b, 0, n);
    }

    /// The rendered camera's eye (camera manager [[main+0x4C86BC0]], camera0 = [[mgr+0x28]], view = [camera0+0x78],
    /// eye f64[3] at view+0x60; docs/structures.md). Includes the game's camera shake.
    public (double x, double y, double z)? CameraEye()
    {
        ulong mgr = P(P(Main + 0x4C86BC0));
        ulong cams = mgr != 0 ? P(mgr + 0x28) : 0, cam0 = cams != 0 ? P(cams) : 0, view = cam0 != 0 ? P(cam0 + 0x78) : 0;
        var b = new byte[24];
        if (view == 0 || !_m.ReadVa(view + 0x60, b, 24)) return null;
        double x = BitConverter.ToDouble(b, 0), y = BitConverter.ToDouble(b, 8), z = BitConverter.ToDouble(b, 16);
        return double.IsFinite(x + y + z) ? (x, y, z) : null;
    }

    public readonly record struct WorldState(int UsingItem, int SelectedItem, bool Indoor, bool UnderRoof, int Seconds);

    const ulong WorldMgr = 0x4E7EAB0; // main+, the world manager pointer (QueryWeather / QueryWorldGetCurrentTime)

    /// Item in use / selected (QueryPlayerGetUsingItem/SelectedItem: player = [[actor+0x270]+0x358], +0x7D8,
    /// +0x7C0+(bit0 of +0x7C1 ? 0x1C : 0x10)), indoor / under a roof (env = [[actor+0x270]+0x460], [env+0xA8]
    /// bits 14 / 5), time of day in seconds (t = [[world]+0x578]+0xD48], u32 at t+(bit1 of [t+9] ? 0x50 : 0x20)+8).
    public WorldState World()
    {
        int use = -1, sel = -1; bool indoor = false, roof = false; int sec = -1;
        ulong comps = Link != 0 ? P(Link + 0x270) : 0;
        ulong player = comps != 0 ? P(comps + 0x358) : 0;
        var b = new byte[0x20];
        if (player != 0 && _m.ReadVa(player + 0x7C0, b, 0x20))
        {
            sel = BitConverter.ToInt32(b, (b[1] & 1) != 0 ? 0x1C : 0x10);
            use = BitConverter.ToInt32(b, 0x18);
        }
        ulong env = comps != 0 ? P(comps + 0x460) : 0;
        if (env != 0 && _m.ReadVa(env + 0xA8, b, 4)) { uint f = BitConverter.ToUInt32(b, 0); indoor = (f >> 14 & 1) != 0; roof = (f >> 5 & 1) != 0; }
        ulong t = P(P(P(Main + WorldMgr) + 0x578) + 0xD48);
        if (t != 0 && _m.ReadVa(t, b, 0x20)) { }
        if (t != 0)
        {
            var c = new byte[0x60];
            if (_m.ReadVa(t, c, 0x60)) sec = BitConverter.ToInt32(c, ((c[9] >> 1 & 1) != 0 ? 0x50 : 0x20) + 8);
        }
        return new WorldState(use, sel, indoor, roof, sec);
    }

    /// Is main still where it was? (False after the game was closed or rebooted.)
    public bool StillThere()
    {
        var b = new byte[4];
        return Main != 0 && _m.ReadVa(Main + Mod0, b, 4) && b[0] == 'M' && b[1] == 'O' && b[2] == 'D' && b[3] == '0';
    }

    public Oot(GuestMemory m) { _m = m; }

    public readonly record struct State(bool InGame, int Life, int LifeMax, float FovDeg, float Vx, float Vy, float Vz, float X = 0, float Y = 0, float Z = 0)
    {
        public float SpeedH => MathF.Sqrt(Vx * Vx + Vz * Vz);
    }

    const ulong VtCctProperty = 0x4C34F10, VtCctBody = 0x4C34658;

    /// Link's physics body velocity (m/s): body = [[[[[actor+0x270]+0x50]+0x20]+0x10]+8]+0x20, velocity at +0xE0.
    (float, float, float) Velocity(ulong actor)
    {
        ulong p = P(actor + 0x270);
        foreach (var off in new ulong[] { 0x50, 0x20, 0x10, 0x8 }) p = P(p == 0 ? 0 : p + off);
        if (p == 0 || P(p) != Main + VtCctProperty) return default;
        ulong body = P(p + 0x20);
        if (body == 0 || P(body) != Main + VtCctBody) return default;
        var b = new byte[12];
        if (!_m.ReadVa(body + 0xE0, b, 12)) return default;
        float x = BitConverter.ToSingle(b, 0), y = BitConverter.ToSingle(b, 4), z = BitConverter.ToSingle(b, 8);
        return float.IsFinite(x + y + z) && MathF.Abs(x) + MathF.Abs(y) + MathF.Abs(z) < 1000 ? (x, y, z) : default;
    }

    /// Finds main by its MOD0 tag at the build's offset. False = not this game/build or not loaded yet.
    public bool FindMain()
    {
        var b = new byte[4];
        foreach (var (va, size) in _m.GuestRegions())
            if (size >= 0x100000 && _m.ReadVa(va + Mod0, b, 4) && b[0] == 'M' && b[1] == 'O' && b[2] == 'D' && b[3] == '0')
                return (Main = va) != 0;
        Main = 0;
        return false;
    }

    ulong P(ulong va) => va == 0 ? 0 : _m.ReadU64(va);

    ulong LinkActor()
    {
        foreach (var (g, mem) in LinkPaths)
        {
            ulong holder = P(Main + g), actor = P(holder == 0 ? 0 : holder + mem);
            if (actor != 0 && P(actor) == Main + VtLinkObj) return actor;
        }
        return 0;
    }

    int Param(ulong p)
    {
        if (p == 0 || P(p) != Main + VtParam) return -1;
        var b = new byte[4];
        return _m.ReadVa(p + 8, b, 4) ? BitConverter.ToInt32(b) : -1;
    }

    public State Read()
    {
        ulong link = LinkActor();
        int life = -1, max = -1;
        if (link != 0)
        {
            ulong state = P(link + 8), pars = P(state == 0 ? 0 : state + 0x6A0);
            if (pars != 0) { life = Param(P(pars + 0x1C10)); max = Param(P(pars + 0x1C18)); }
        }
        float fov = 0;
        ulong mgr = P(Main + CameraPtr), cam = P(mgr == 0 ? 0 : mgr + 0x68);
        var b = new byte[4];
        if (cam != 0 && _m.ReadVa(cam + 0x130, b, 4)) fov = BitConverter.ToSingle(b) * (180f / MathF.PI);
        Link = link;
        var (vx, vy, vz) = link != 0 ? Velocity(link) : default;
        float px = 0, py = 0, pz = 0;
        if (link != 0 && _m.ReadVa(link + 0x31C, b, 4)) { var pb = new byte[12]; if (_m.ReadVa(link + 0x31C, pb, 12)) { px = BitConverter.ToSingle(pb, 0); py = BitConverter.ToSingle(pb, 4); pz = BitConverter.ToSingle(pb, 8); } }
        return new State(link != 0 && max > 0, life, max, fov, vx, vy, vz, px, py, pz);
    }
}
