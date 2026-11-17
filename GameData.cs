namespace PulseOfTime;

/// Read-only access to OoT's GameData (the save fields), keyed by MurmurHash3_32 (seed 0) of the name.
/// Layout from sloperinaGZ (gamedata.c / z64hd.h): manager = [[main+0x4C7E180]]; per type an entry
/// { vtable, u32 count @+8, objects @+0x10, (hash,index) table @+0x18, u32 table_size @+0x24 };
/// Int entry at manager+0xC8, Bool/Float/Enum found by vtable in manager+0..0x800.
sealed class GameData
{
    public enum Kind { Int, Bool, Float, Enum }
    const ulong MgrPtr = 0x4C7E180, IntEntry = 0xC8, ScanBytes = 0x800;
    static readonly ulong[] Vtables = { 0, 0x4C1F140, 0x4C1F3C0, 0x4C1F500 }; // int found by offset
    static readonly (int stride, int off)[] Layout = { (0x28, 0x1C), (0x18, 0x16), (0x28, 0x1C), (0x38, 0x1C) };

    readonly GuestMemory _m; readonly ulong _main;
    readonly Dictionary<uint, uint>?[] _index = new Dictionary<uint, uint>?[4];
    readonly ulong[] _objects = new ulong[4];
    ulong _mgr; DateTime _built;

    public GameData(GuestMemory m, ulong main) { _m = m; _main = main; }

    public static uint Hash(string s)
    {
        const uint c1 = 0xCC9E2D51, c2 = 0x1B873593;
        var b = System.Text.Encoding.ASCII.GetBytes(s);
        uint h = 0; int i = 0;
        for (; i + 4 <= b.Length; i += 4)
        {
            uint k = BitConverter.ToUInt32(b, i);
            k = uint.RotateLeft(k * c1, 15) * c2;
            h = uint.RotateLeft(h ^ k, 13) * 5 + 0xE6546B64;
        }
        uint t = 0;
        switch (b.Length & 3)
        {
            case 3: t ^= (uint)b[i + 2] << 16; goto case 2;
            case 2: t ^= (uint)b[i + 1] << 8; goto case 1;
            case 1: t ^= b[i]; h ^= uint.RotateLeft(t * c1, 15) * c2; break;
        }
        h ^= (uint)b.Length;
        h = (h ^ (h >> 16)) * 0x85EBCA6B;
        h = (h ^ (h >> 13)) * 0xC2B2AE35;
        return h ^ (h >> 16);
    }

    ulong P(ulong va) => va == 0 ? 0 : _m.ReadU64(va);
    uint U32(ulong va) { var b = new byte[4]; return _m.ReadVa(va, b, 4) ? BitConverter.ToUInt32(b) : 0; }

    /// (Re)reads the hash tables. They don't move during play; rebuilt every 10 s or when the manager changes.
    bool Ensure()
    {
        ulong mgr = P(P(_main + MgrPtr));
        if (mgr == 0) return false;
        if (mgr == _mgr && _index[0] != null && DateTime.Now - _built < TimeSpan.FromSeconds(10)) return true;
        _mgr = mgr; _built = DateTime.Now;
        var scan = new byte[ScanBytes];
        _m.ReadVa(mgr, scan, scan.Length);
        for (int k = 0; k < 4; k++)
        {
            _index[k] = null;
            ulong entry = 0;
            if (k == 0) entry = mgr + IntEntry;
            else
                for (int o = 0; o + 0x28 <= scan.Length; o += 8)
                    if (BitConverter.ToUInt64(scan, o) == _main + Vtables[k]) { entry = mgr + (ulong)o; break; }
            if (entry == 0) continue;
            uint count = U32(entry + 8), size = U32(entry + 0x24);
            ulong objects = P(entry + 0x10), table = P(entry + 0x18);
            if (count == 0 || count > 0x10000 || size == 0 || size > 0x100000 || objects == 0 || table == 0) continue;
            var raw = new byte[size * 8];
            if (!_m.ReadVa(table, raw, raw.Length)) continue;
            var d = new Dictionary<uint, uint>((int)count);
            for (int i = 0; i < size; i++)
            {
                uint h = BitConverter.ToUInt32(raw, i * 8), idx = BitConverter.ToUInt32(raw, i * 8 + 4);
                if (h != 0 && idx < count) d[h] = idx;
            }
            _index[k] = d; _objects[k] = objects;
        }
        return _index[0] != null;
    }

    ulong Addr(Kind k, uint hash)
    {
        if (!Ensure() || _index[(int)k] is not { } d || !d.TryGetValue(hash, out uint idx)) return 0;
        var (stride, off) = Layout[(int)k];
        return _objects[(int)k] + (ulong)idx * (ulong)stride + (ulong)off;
    }

    public bool Has(Kind k, uint hash) => Addr(k, hash) != 0;
    public int? Int(uint hash) { ulong a = Addr(Kind.Int, hash); return a == 0 ? null : (int)U32(a); }
    public uint? Enum(uint hash) { ulong a = Addr(Kind.Enum, hash); return a == 0 ? null : U32(a); }
    public bool? Bool(uint hash)
    {
        ulong a = Addr(Kind.Bool, hash); var b = new byte[1];
        return a == 0 || !_m.ReadVa(a, b, 1) ? null : b[0] != 0;
    }
    public float? Float(uint hash) { ulong a = Addr(Kind.Float, hash); return a == 0 ? null : BitConverter.UInt32BitsToSingle(U32(a)); }

    /// String64 field (entry at manager+0x608, objects 0x108 apart, char* at +0x68), or null.
    public string? Str(uint hash)
    {
        if (!Ensure()) return null;
        ulong e = _mgr + 0x608;
        uint count = U32(e + 8), size = U32(e + 0x24);
        ulong objects = P(e + 0x10), table = P(e + 0x18);
        if (count == 0 || count > 0x10000 || size == 0 || size > 0x100000 || objects == 0 || table == 0) return null;
        for (uint i = 0, slot = hash % size; i < size; i++, slot = slot + 1 >= size ? 0 : slot + 1)
        {
            uint h = U32(table + slot * 8UL);
            if (h == 0) return null;
            if (h != hash) continue;
            uint idx = U32(table + slot * 8UL + 4);
            if (idx >= count) return null;
            ulong text = P(objects + idx * 0x108UL + 0x68);
            var b = new byte[64];
            if (text == 0 || !_m.ReadVa(text, b, 64)) return null;
            int n = Array.IndexOf(b, (byte)0);
            return System.Text.Encoding.ASCII.GetString(b, 0, n < 0 ? 64 : n);
        }
        return null;
    }

    public int Count(Kind k) => Ensure() && _index[(int)k] is { } d ? d.Count : 0;

    public string Debug()
    {
        ulong holder = P(_main + MgrPtr), mgr = P(holder), e = mgr + IntEntry;
        return $"holder 0x{holder:X} mgr 0x{mgr:X} int: vt 0x{P(e):X} count {U32(e + 8)} objects 0x{P(e + 0x10):X} table 0x{P(e + 0x18):X} size {U32(e + 0x24)}";
    }

    /// All fields of a kind with their raw 32-bit values (for finding unknown fields by diffing).
    public Dictionary<uint, uint> Snapshot(Kind k)
    {
        var r = new Dictionary<uint, uint>();
        if (!Ensure() || _index[(int)k] is not { } d) return r;
        var (stride, off) = Layout[(int)k];
        foreach (var (h, idx) in d)
        {
            ulong a = _objects[(int)k] + (ulong)idx * (ulong)stride + (ulong)off;
            r[h] = k == Kind.Bool ? (uint)(_m.ReadVa(a, new byte[1], 1) ? ReadByte(a) : 0) : U32(a);
        }
        return r;
    }
    byte ReadByte(ulong a) { var b = new byte[1]; _m.ReadVa(a, b, 1); return b[0]; }
}
