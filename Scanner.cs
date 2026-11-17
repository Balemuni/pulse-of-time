namespace PulseOfTime;

/// Cheat-Engine-style value scanner over the guest's mapped memory, state kept in files so each
/// command is one short process run:  start -> (do something in game) -> filter -> ... -> list.
static class Scanner
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "scan");
    static string Snap => Path.Combine(Dir, "snap.bin");     // [va u64][len u64][bytes] ...
    static string Cand => Path.Combine(Dir, "cand.bin");     // [va u64][value u64] ...
    static string TypeFile => Path.Combine(Dir, "type.txt");

    public static int Run(GuestMemory gm, string[] a)
    {
        Directory.CreateDirectory(Dir);
        switch (a[0])
        {
            case "start": return Start(gm, a.Length > 1 ? a[1] : "u8");
            case "list": return List(gm, a.Length > 1 ? int.Parse(a[1]) : 40);
            default: return Filter(gm, a[0], a.Length > 1 ? a[1] : null);
        }
    }

    static int Size(string t) => t switch { "u8" => 1, "u16" => 2, "u32" or "f32" => 4, _ => throw new ArgumentException("type: u8 u16 u32 f32") };

    static int Start(GuestMemory gm, string type)
    {
        int sz = Size(type);
        File.WriteAllText(TypeFile, type);
        File.Delete(Cand);
        using var w = new BinaryWriter(File.Create(Snap));
        ulong total = 0;
        foreach (var (va, size) in gm.GuestRegions())
        {
            for (ulong off = 0; off < size; off += 16UL << 20)
            {
                int len = (int)Math.Min(16UL << 20, size - off);
                var buf = new byte[len];
                if (!gm.ReadVa(va + off, buf, len)) continue;
                w.Write(va + off); w.Write((ulong)len); w.Write(buf);
                total += (ulong)len;
            }
        }
        Console.WriteLine($"snapshot: {total >> 20} MiB of guest memory, type {type} (step {sz})");
        return 0;
    }

    static bool Keep(string op, double old, double now, double? v) => op switch
    {
        "changed" => now != old,
        "unchanged" => now == old,
        "inc" => now > old,
        "dec" => now < old,
        "eq" => now == v,
        "ne" => now != v,
        "gt" => now > v,
        "lt" => now < v,
        _ => throw new ArgumentException("op: changed unchanged inc dec eq ne gt lt"),
    };

    static double Get(string t, byte[] b, int i) => t switch
    {
        "u8" => b[i],
        "u16" => BitConverter.ToUInt16(b, i),
        "u32" => BitConverter.ToUInt32(b, i),
        _ => BitConverter.ToSingle(b, i),
    };

    static int Filter(GuestMemory gm, string op, string? value)
    {
        string t = File.ReadAllText(TypeFile).Trim(); int sz = Size(t);
        double? v = value == null ? null : double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        string tmp = Cand + ".new";
        long kept = 0;
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            if (!File.Exists(Cand))
            {
                // First filter: compare the snapshot block by block with live memory.
                using var r = new BinaryReader(File.OpenRead(Snap));
                while (r.BaseStream.Position < r.BaseStream.Length)
                {
                    ulong va = r.ReadUInt64(); int len = (int)r.ReadUInt64(); var old = r.ReadBytes(len);
                    var now = new byte[len];
                    if (!gm.ReadVa(va, now, len)) continue;
                    for (int i = 0; i + sz <= len; i += sz)
                    {
                        double o = Get(t, old, i), n = Get(t, now, i);
                        if (t == "f32" && (double.IsNaN(n) || Math.Abs(n) > 1e7)) continue;
                        if (Keep(op, o, n, v)) { w.Write(va + (ulong)i); w.Write(BitConverter.DoubleToInt64Bits(n)); kept++; }
                    }
                }
            }
            else
            {
                using var r = new BinaryReader(File.OpenRead(Cand));
                var buf = new byte[8];
                while (r.BaseStream.Position < r.BaseStream.Length)
                {
                    ulong va = r.ReadUInt64(); double o = BitConverter.Int64BitsToDouble(r.ReadInt64());
                    if (!gm.ReadVa(va, buf, sz)) continue;
                    double n = Get(t, buf, 0);
                    if (Keep(op, o, n, v)) { w.Write(va); w.Write(BitConverter.DoubleToInt64Bits(n)); kept++; }
                }
            }
        }
        File.Move(tmp, Cand, true);
        Console.WriteLine($"{op} {value}: {kept} candidates");
        if (kept <= 40) List(gm, 40);
        return 0;
    }

    static int List(GuestMemory gm, int n)
    {
        string t = File.ReadAllText(TypeFile).Trim(); int sz = Size(t);
        using var r = new BinaryReader(File.OpenRead(Cand));
        var buf = new byte[8];
        for (int i = 0; i < n && r.BaseStream.Position < r.BaseStream.Length; i++)
        {
            ulong va = r.ReadUInt64(); double o = BitConverter.Int64BitsToDouble(r.ReadInt64());
            double now = gm.ReadVa(va, buf, sz) ? Get(t, buf, 0) : double.NaN;
            Console.WriteLine($"  0x{va:X10}  last {o,12}  now {now,12}");
        }
        return 0;
    }
}
