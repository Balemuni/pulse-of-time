using PulseOfTime;

// Usage:
//   PulseOfTime                 (or "run") game-aware triggers for OoT in Lichii; add --debug for state lines
//   PulseOfTime state [sec]     print the OoT state the daemon sees (no controller needed)
//   PulseOfTime test            cycles through each effect on R2 and L2 (3 s each), then turns them off
//   PulseOfTime off             both triggers back to normal
//   PulseOfTime meminfo | scan ... | watch ...   memory research tools
//   PulseOfTime calibrate [fx ...]   rate every effect on the controller (D-pad up/down), saved to tuning.json
//   PulseOfTime replay [file] [speed]  feel a recorded session again (pulse-fx.log)
//   run options: --no-haptics, --gain 1.0 (all haptics), --sound 1.0 (game sound layer), --music 1.0 (0 = don't hold back sustained sound)
if (args.Length == 0 || args[0] is "run" || args[0].StartsWith("--")) return Daemon.Run(args);

if (args[0] == "state")
{
    using var gm = GuestMemory.Attach();
    if (gm == null || gm.Arena == 0) { Console.Error.WriteLine("emulator (Lichii/Fritchii) or game not running, or guest arena not found."); return 1; }
    var oot = new Oot(gm);
    if (!oot.FindMain()) { Console.Error.WriteLine("OoT 1.1.1 (E0F3CCFA) main not found."); return 1; }
    Console.WriteLine($"main at 0x{oot.Main:X}");
    var until = DateTime.Now.AddSeconds(args.Length > 1 ? double.Parse(args[1]) : 1);
    string prev = "";
    do
    {
        var s = oot.Read();
        string line = $"ingame {s.InGame} life {s.Life}/{s.LifeMax} fov {s.FovDeg:F1} vel {s.Vx:F1} {s.Vy:F1} {s.Vz:F1} speed {s.SpeedH:F1}";
        if (line != prev) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        prev = line; Thread.Sleep(16);
    } while (DateTime.Now < until);
    return 0;
}

if (args[0] == "peek")
{
    // peek <doc address> [+off ...]: follows a pointer chain from a main variable (docs' 0x80020000 base);
    // prints each step as guest VA and as a doc address when it points into main
    using var gm = GuestMemory.Attach();
    var oot = gm == null || gm.Arena == 0 ? null : new Oot(gm);
    if (oot == null || !oot.FindMain()) { Console.Error.WriteLine("OoT not running."); return 1; }
    ulong Doc(ulong va) => va - oot.Main + 0x80020000;
    ulong p = oot.Main + (Convert.ToUInt64(args[1], 16) - 0x80020000);
    Console.WriteLine($"start 0x{p:X}");
    foreach (var a in args.Skip(2))
    {
        bool deref = !a.StartsWith("="); // "+off": p = [p+off]; "=+off": p = p+off
        ulong off = Convert.ToUInt64(a.TrimStart('=', '+'), 16);
        p = deref ? gm!.ReadU64(p + off) : p + off;
        Console.WriteLine($"{a,8} -> 0x{p:X}{(p >= oot.Main && p < oot.Main + 0x4EA0000 ? $"  (main 0x{Doc(p):X})" : "")}");
    }
    var b = new byte[64];
    if (gm!.ReadVa(p, b, 64)) Console.WriteLine("bytes: " + Convert.ToHexString(b));
    return 0;
}

if (args[0] == "strref")
{
    // strref <dump> <live main hex> <word...>: where each 0-terminated string sits and which 8-aligned
    // pointers in the dump point at it (the dump is taken from live memory, so pointers are live VAs);
    // prints the neighbouring pointer table entries as strings
    var bin = File.ReadAllBytes(args[1]); ulong live = Convert.ToUInt64(args[2], 16);
    string Str(ulong va) { if (va < live || va >= live + (ulong)bin.Length) return "?"; int o = (int)(va - live), e = o; while (e < bin.Length && bin[e] != 0 && e - o < 64) e++; return System.Text.Encoding.ASCII.GetString(bin, o, e - o); }
    foreach (var w in args.Skip(3))
    {
        var pat = System.Text.Encoding.ASCII.GetBytes(w + "\0");
        for (int i = bin.AsSpan().IndexOf(pat); i >= 0; )
        {
            if (i > 0 && bin[i - 1] == 0)
            {
                ulong va = live + (ulong)i; int refs = 0;
                for (int j = 0; j + 8 <= bin.Length; j += 8)
                    if (BitConverter.ToUInt64(bin, j) == va)
                    {
                        refs++;
                        Console.WriteLine($"{w} @0x{i:X} <- ptr @0x{j:X}: " + string.Join(" | ", Enumerable.Range(-4, 9).Select(k => j + k * 8 >= 0 ? Str(BitConverter.ToUInt64(bin, j + k * 8)) : "")));
                    }
                if (refs == 0) Console.WriteLine($"{w} @0x{i:X}: no pointers");
            }
            int n = bin.AsSpan(i + 1).IndexOf(pat); i = n < 0 ? -1 : i + 1 + n;
        }
    }
    return 0;
}

if (args[0] == "calibrate")
{
    // calibrate [fx ...]: plays each effect; D-pad up/down = stronger/weaker (written to tuning.json), Circle = again,
    // Cross = next, Square = back, Options = done. Without a controller: arrow keys, Enter, Backspace, Space, Esc.
    using var hx = Haptics.Start(0, out var err);
    if (hx == null) { Console.Error.WriteLine(err); return 1; }
    using var dsc = DualSense.Open(); dsc?.StartInput();
    var list = args.Length > 1 ? args.Skip(1).Select(a => Enum.TryParse<Fx>(a, true, out var f) ? f : (Fx?)null).OfType<Fx>().ToList() : Enum.GetValues<Fx>().Where(f => f != Fx.Hello).ToList();
    if (list.Count == 0) { Console.Error.WriteLine("no such effect; names: " + string.Join(" ", Enum.GetValues<Fx>())); return 1; }
    Thread.Sleep(400);
    int i = 0, prevB = 0, prevPad = 8;
    while (i >= 0 && i < list.Count)
    {
        var fx = list[i];
        Console.WriteLine($"[{i + 1}/{list.Count}] {fx,-16} strength {Tuning.Of(fx):F2}");
        hx.Play(fx);
        string? cmd = null;
        while (cmd == null)
        {
            if (!Console.IsInputRedirected && Console.KeyAvailable)
                cmd = Console.ReadKey(true).Key switch
                {
                    ConsoleKey.UpArrow => "up", ConsoleKey.DownArrow => "down", ConsoleKey.Enter => "next",
                    ConsoleKey.Backspace => "back", ConsoleKey.Spacebar => "again", ConsoleKey.Escape => "quit", _ => null,
                };
            else if (dsc != null)
            {
                int b = dsc.Buttons, pad = dsc.Dpad;
                bool Pressed(int bit) => (b & bit) != 0 && (prevB & bit) == 0;
                if (pad == 0 && prevPad != 0) cmd = "up";
                else if (pad == 4 && prevPad != 4) cmd = "down";
                else if (Pressed(2)) cmd = "next";
                else if (Pressed(1)) cmd = "back";
                else if (Pressed(4)) cmd = "again";
                else if (dsc.Options) cmd = "quit";
                prevB = b; prevPad = pad;
            }
            Thread.Sleep(10);
        }
        switch (cmd)
        {
            case "up": case "down":
            {
                float v = Math.Clamp(MathF.Round((Tuning.Of(fx) + (cmd == "up" ? 0.1f : -0.1f)) * 10) / 10, 0, 3);
                Tuning.Set("fx." + fx, v); break;
            }
            case "next": i++; break;
            case "back": i = Math.Max(0, i - 1); break;
            case "quit": i = -1; break;
        }
    }
    Console.WriteLine("Saved in tuning.json.");
    return 0;
}

if (args[0] == "replay")
{
    // replay [file] [speed]: plays a recorded session's effects again (pulse-fx.log: "ms fx strength n pan"),
    // to tune strengths in peace. The trigger and light parts are not recorded.
    string file = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "pulse-fx.log");
    double speed = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 1;
    if (!File.Exists(file)) { Console.Error.WriteLine($"{file} not found (it is written while \"PulseOfTime\" is going)."); return 1; }
    using var hx = Haptics.Start(0, out var err);
    if (hx == null) { Console.Error.WriteLine(err); return 1; }
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var sw = System.Diagnostics.Stopwatch.StartNew(); long first = -1;
    using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
    for (string? line; (line = reader.ReadLine()) != null;)
    {
        var p = line.Split(' ');
        if (p.Length < 5 || !long.TryParse(p[0], out long ms) || !Enum.TryParse<Fx>(p[1], out var fx)) continue;
        if (first < 0) first = ms;
        long due = (long)((ms - first) / speed);
        while (sw.ElapsedMilliseconds < due) { if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) return 0; Thread.Sleep(2); }
        hx.Play(fx, float.Parse(p[2], inv), int.Parse(p[3]), float.Parse(p[4], inv));
        Console.WriteLine($"{(ms - first) / 1000.0,8:F2}s {fx}");
    }
    Thread.Sleep(1500);
    return 0;
}

if (args[0] == "demo")
{
    // demo [name ...]: plays effects and textures on the controller, one after another, with their names
    using var hx = Haptics.Start(0, out var err);
    if (hx == null) { Console.Error.WriteLine(err); return 1; }
    Thread.Sleep(400);
    var only = args.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (only.Count == 0 || only.Contains("360"))
    {
        // the four points together: triggers (front) + palms (back)
        using var dsq = DualSense.Open();
        var q = new Quad(); var sw = System.Diagnostics.Stopwatch.StartNew(); long _lastBeat = -1;
        void Run(string name, int ms, Action<long, int> step)
        {
            Console.WriteLine($"360: {name}");
            long end = sw.ElapsedMilliseconds + ms; int frame = 0;
            while (sw.ElapsedMilliseconds < end)
            {
                long now = sw.ElapsedMilliseconds; step(now, frame++);
                var (r, l) = q.Apply(now, Effects.Feedback(0, 3), Effects.Feedback(0, 3));
                dsq?.SetTriggers(r, l); Thread.Sleep(16);
            }
            hx.Loops.AsSpan().Clear(); Thread.Sleep(400);
        }
        Run("spin attack once round: L2, R2, right palm, left palm", 900, (n, f) => { if (f == 0) { hx.Play(Fx.SpinRelease); q.Pulse(n, 8, 0, 60, 110); q.Pulse(n, 0, 8, 60, 110, 120); } });
        Run("sword arc left to right", 600, (n, f) => { if (f == 0) { hx.Play(Fx.Swing, 1, 1); q.Pulse(n, 3, 0, 90, 50); q.Pulse(n, 0, 4, 90, 60, 70); } });
        Run("enemy hit, front right", 600, (n, f) => { if (f == 0) { var (p, pan, a, b) = Quad.Place(0.7f, 0.7f, 1); hx.Play(Fx.EnemyHit, Math.Max(p, 0.5f), 1, pan); q.Pulse(n, a, b, 55, 90); } });
        Run("enemy behind left: sonar pings", 2600, (n, f) => { if (f % 40 == 0) { var (p, pan, a, b) = Quad.Place(-0.7f, -0.7f, 0.5f); hx.Play(Fx.Ping, p, 1, pan); } });
        Run("Epona: hind hooves in the palms, fore hooves in the triggers", 4000, (n, f) =>
        {
            hx.HoofRate = 2.4f; hx.Loops[(int)Loop.Hooves] = 1;
            double hp = hx.HoofPhase; long beat = (long)Math.Floor(hp);
            if (beat != _lastBeat && hp - beat > 0.18) { q.Pulse(n, 7, 0, 40, 60); q.Pulse(n, 0, 8, 40, 70, (int)(0.18 / 2.4 * 1000)); _lastBeat = beat; }
        });
        Run("heartbeat at one heart: through all four points", 3000, (n, f) => { if (f % 40 == 0) { hx.Play(Fx.Heartbeat, 1.1f); q.Pulse(n, 8, 8, 30, 75, 0, false, true); q.Pulse(n, 5, 5, 30, 65, 170, false, true); } });
        Run("rain: drops on palms and fingertips, puddle steps", 4000, (n, f) =>
        {
            hx.Loops[(int)Loop.Rain] = 0.9f;
            if (f % 20 == 0) { bool left = Random.Shared.NextDouble() < 0.5; q.Pulse(n, left ? 1.5f : 0, left ? 0 : 1.5f, 200, 30); }
            if (f % 22 == 0) hx.Play(Fx.Puddle, 1, 1, f / 22 % 2 == 0 ? -0.6f : 0.6f);
        });
        Run("explosion: everything shakes", 900, (n, f) => { if (f == 0) { hx.Play(Fx.KillHeavy); q.Pulse(n, 8, 8, 30, 220); } });
        Run("ocarina: D F A B D (palms, L2, R2, both, both high)", 3200, (n, f) =>
        {
            int i = f / 38; var notes = new (float hz, int al, int ar)[] { (150, 0, 0), (178, 5, 0), (224, 0, 5), (252, 3, 3), (300, 5, 5) };
            if (i < notes.Length) { hx.NoteHz = notes[i].hz; hx.Loops[(int)Loop.Note] = f % 38 < 30 ? 1 : 0; if (f % 38 < 30) q.Pulse(n, notes[i].al, notes[i].ar, (int)Math.Min(255, notes[i].hz), 40, 0, false); }
        });
        dsq?.SetTriggers(Effects.Off(), Effects.Off());
        if (only.Contains("360")) return 0;
    }
    foreach (Loop lp in Enum.GetValues<Loop>())
    {
        if (lp == Loop.Count || only.Count > 0 && !only.Contains(lp.ToString())) continue;
        Console.WriteLine($"texture {lp}");
        if (lp == Loop.Rain) { hx.RainRoof = false; hx.Loops[(int)lp] = 1; Thread.Sleep(2500); Console.WriteLine("  ...on a roof"); hx.RainRoof = true; Thread.Sleep(2000); hx.RainRoof = false; }
        else
        {
            hx.LoopSpeed = 1.5f; hx.SwimRate = 1.2f; hx.HoofRate = 3f; hx.NoteHz = 224;
            hx.Loops[(int)lp] = 1; Thread.Sleep(2500);
        }
        hx.Loops[(int)lp] = 0; Thread.Sleep(500);
    }
    foreach (Fx fx in Enum.GetValues<Fx>())
    {
        if (only.Count > 0 && !only.Contains(fx.ToString())) continue;
        Console.WriteLine($"effect {fx}");
        hx.Play(fx, 1, fx == Fx.Coin ? 3 : 1, fx is Fx.EnemyHit or Fx.Grip ? -0.8f : 0);
        Thread.Sleep(fx is Fx.KillBoss or Fx.MedalGet or Fx.Death or Fx.DeathFire or Fx.DeathDrown ? 2800 : 1300);
    }
    return 0;
}

if (args[0] == "verbs")
{
    // verbs [sec]: Link's action verbs per layer, the item in use, environment flags and the time, on change
    using var gm = GuestMemory.Attach();
    var oot = gm == null || gm.Arena == 0 ? null : new Oot(gm);
    if (oot == null || !oot.FindMain()) { Console.Error.WriteLine("OoT not running."); return 1; }
    var end = DateTime.Now.AddSeconds(args.Length > 1 ? double.Parse(args[1]) : 5);
    string last = "";
    while (DateTime.Now < end)
    {
        var s = oot.Read();
        var w = oot.World();
        string line = string.Join(" | ", Enumerable.Range(0, 5).Select(i => Verbs.Name(oot.Verb(i)))) + $"  item {w.UsingItem}/{w.SelectedItem} indoor {w.Indoor} roof {w.UnderRoof} time {w.Seconds / 3600:00}:{w.Seconds / 60 % 60:00}";
        string anims = string.Join(", ", oot.Anims().Select(a => a.name)) + $"] event [{oot.EventName()}] eye [{oot.CameraEye()}";
        if (line + anims != last) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}  anim [{anims}] frames [{string.Join(",", oot.Anims().Select(a => a.frame.ToString("F1")))}] pos {s.X:F2},{s.Y:F2},{s.Z:F2}");
        last = line + anims; Thread.Sleep(16);
    }
    return 0;
}

if (args[0] == "xref")
{
    // xref <dump> <doc address...>: ARM64 code loading that address via adrp + add/ldr (base 0x80020000);
    // prints each site and the start of its function (the nearest pacibsp before it)
    var bin = File.ReadAllBytes(args[1]); const long baseDoc = 0x80020000, textEnd = 0x3C461D0;
    foreach (var a in args.Skip(2))
    {
        long target = Convert.ToInt64(a, 16);
        for (long i = 0; i + 4 <= textEnd; i += 4)
        {
            uint ins = BitConverter.ToUInt32(bin, (int)i);
            if ((ins & 0x9F000000) != 0x90000000) continue; // adrp
            int rd = (int)(ins & 31);
            long immlo = (ins >> 29) & 3, immhi = (ins >> 5) & 0x7FFFF;
            long imm = ((immhi << 2) | immlo) << 12; if ((imm & (1L << 32)) != 0) imm -= 1L << 33;
            long page = ((baseDoc + i) & ~0xFFFL) + imm;
            if (page != (target & ~0xFFFL)) continue;
            for (long j = i + 4; j < i + 40 && j + 4 <= textEnd; j += 4)
            {
                uint n = BitConverter.ToUInt32(bin, (int)j);
                bool add = (n & 0xFFC00000) == 0x91000000 && ((n >> 5) & 31) == rd;
                long lo = add ? (n >> 10) & 0xFFF : -1;
                if (add && page + lo == target)
                {
                    long f = j; while (f > 0 && BitConverter.ToUInt32(bin, (int)f) != 0xD503237F && j - f < 0x20000) f -= 4;
                    Console.WriteLine($"{a}: loaded at 0x{baseDoc + j:X}, function 0x{baseDoc + f:X}");
                    break;
                }
            }
        }
    }
    return 0;
}

if (args[0] == "strtab")
{
    // strtab <dump> <live main hex> <offset of one entry hex>: the whole table of string pointers around it, with indices
    var bin = File.ReadAllBytes(args[1]); ulong live = Convert.ToUInt64(args[2], 16); int at = Convert.ToInt32(args[3], 16);
    bool IsStr(int o) { if (o + 8 > bin.Length) return false; ulong va = BitConverter.ToUInt64(bin, o); if (va < live || va >= live + (ulong)bin.Length) return false; int s = (int)(va - live); return bin[s] >= 0x20 && bin[s] < 0x7F && (s == 0 || bin[s - 1] == 0); }
    string Str(int o) { int s = (int)(BitConverter.ToUInt64(bin, o) - live), e = s; while (bin[e] != 0) e++; return System.Text.Encoding.ASCII.GetString(bin, s, e - s); }
    int start = at; while (IsStr(start - 8)) start -= 8;
    int i = 0; for (int o = start; IsStr(o); o += 8, i++) Console.WriteLine($"{i,4} 0x{i:X3} {Str(o)}");
    Console.WriteLine($"table @0x{start:X}, {i} entries");
    return 0;
}

if (args[0] == "objstr")
{
    // objstr <doc var> [bytes hex] [depth]: strings in the object a main variable points to, inline or behind
    // pointers (one level deeper with depth 2); for finding where the game keeps names like the current event
    using var gm = GuestMemory.Attach();
    var oot = gm == null || gm.Arena == 0 ? null : new Oot(gm);
    if (oot == null || !oot.FindMain()) { Console.Error.WriteLine("OoT not running."); return 1; }
    ulong obj = gm!.ReadU64(oot.Main + (Convert.ToUInt64(args[1], 16) - 0x80020000));
    int size = args.Length > 2 ? Convert.ToInt32(args[2], 16) : 0x1000, depth = args.Length > 3 ? int.Parse(args[3]) : 1;
    string? Str(ulong va) { var b = new byte[48]; if (va == 0 || !gm.ReadVa(va, b, 48)) return null; int n = 0; while (n < 48 && b[n] >= 0x20 && b[n] < 0x7F) n++; return n >= 4 && (n == 48 || b[n] == 0) ? System.Text.Encoding.ASCII.GetString(b, 0, n) : null; }
    void Scan(ulong o, int sz, string prefix, int d)
    {
        var buf = new byte[sz];
        if (!gm.ReadVa(o, buf, sz)) return;
        for (int i = 0; i + 8 <= sz; i += 8)
        {
            ulong p = BitConverter.ToUInt64(buf, i);
            if (p < 0x1000000 || p > 0x8000000000) continue;
            var s = Str(p);
            if (s != null) Console.WriteLine($"{prefix}+0x{i:X} -> \"{s}\"");
            else if (d > 1) Scan(p, 0x200, $"{prefix}+0x{i:X}->", d - 1);
        }
    }
    Console.WriteLine($"object 0x{obj:X}");
    Scan(obj, size, "", depth);
    return 0;
}

if (args[0] == "dumpmain")
{
    // dumpmain <file>: the game's main module as loaded (for Ghidra: base 0x80020000 = the docs' addresses)
    using var gm = GuestMemory.Attach();
    var oot = gm == null || gm.Arena == 0 ? null : new Oot(gm);
    if (oot == null || !oot.FindMain()) { Console.Error.WriteLine("OoT not running."); return 1; }
    const int size = 0x4EA0000, chunk = 0x10000;
    var buf = new byte[chunk]; int bad = 0;
    using (var f = File.Create(args[1]))
        for (int o = 0; o < size; o += chunk)
        {
            if (!gm!.ReadVa(oot.Main + (ulong)o, buf, chunk)) { Array.Clear(buf); bad++; }
            f.Write(buf, 0, chunk);
        }
    Console.WriteLine($"main 0x{oot.Main:X} -> {args[1]} ({size >> 20} MiB, {bad} unreadable 64K blocks zeroed)");
    return 0;
}

if (args[0] == "enemies")
{
    // enemies [sec]: the map, enemies near Link and hits on them
    using var gm = GuestMemory.Attach();
    var oot = gm == null || gm.Arena == 0 ? null : new Oot(gm);
    if (oot == null || !oot.FindMain()) { Console.Error.WriteLine("OoT not running."); return 1; }
    var gd = new GameData(gm!, oot.Main);
    Console.WriteLine($"map: {gd.Str(GameData.Hash("Sequence_CurrentBanc"))}  loading {gd.Bool(GameData.Hash("Sequence_IsLoadingBanc"))}");
    oot.Read();
    using var cb = new Combat(gm!, oot.Main) { Link = oot.Link, Debug = true };
    var end = DateTime.Now.AddSeconds(args.Length > 1 ? double.Parse(args[1]) : 3);
    Thread.Sleep(800);
    Console.WriteLine($"walk: {cb.Visits} entries; running actors: {string.Join(", ", cb.Names.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key).Take(60))}");
    foreach (var e in cb.Found) Console.WriteLine($"  {e.Name}");
    while (DateTime.Now < end)
    {
        oot.Read(); cb.Link = oot.Link;
        foreach (var h in cb.Poll()) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} HIT {h.Name} -{h.Damage}/{h.Max} killed {h.Killed} pan {h.Pan:F2} dist {h.Distance:F1}");
        Thread.Sleep(16);
    }
    Console.WriteLine($"nearby {cb.Nearby} nearest {cb.NearestDistance:F1} boss {cb.BossNear}");
    return 0;
}

if (args[0] == "gd")
{
    // gd probe | gd snap <file> | gd diff <file>   (GameData research)
    using var gm = GuestMemory.Attach();
    var oot = gm == null || gm.Arena == 0 ? null : new Oot(gm);
    if (oot == null || !oot.FindMain()) { Console.Error.WriteLine("OoT not running."); return 1; }
    var gd = new GameData(gm!, oot.Main);
    string mode = args.Length > 1 ? args[1] : "probe";
    if (mode == "probe")
    {
        Console.WriteLine($"murmur check: {GameData.Hash("Sequence_IsPause"):X8} (want 604AEBE1)");
        Console.WriteLine(gd.Debug());
        foreach (GameData.Kind k in Enum.GetValues<GameData.Kind>()) Console.WriteLine($"{k}: {gd.Count(k)} fields");
        var items = new[] { "Arrow", "Item_Arrow", "Bullet", "Item_Bullet", "Seed", "Item_Seed", "Bomb", "Item_Bomb", "Bombchu", "Item_Bombchu",
            "Nut", "Item_Nut", "Stick", "Item_Stick", "Rupee", "Item_Rupee", "Item_Arrow_Fire", "Magic", "Item_Magic", "Heart", "Item_Heart",
            "Item_Wallet_Small", "Item_Quiver_Small", "Item_BulletBag_Small", "Item_Bow", "Item_SlingShot", "Item_Hookshot", "Item_Boomerang" };
        var names = new List<string> { "Sequence_IsPause", "Sequence_GamePauseRequestCount", "Sequence_IsPreGamePause",
            "PlayerStatus.EquipWeapon", "PlayerStatus.EquipShield", "PlayerStatus.EquipArmor", "PlayerStatus.EquipBoot", "PlayerStatus.EquipOcarina",
            "PlayerStatus.Life", "PlayerStatus.MaxLife", "PlayerStatus.CurrentLife", "PlayerStatus.Magic", "PlayerStatus.MagicPoint", "PlayerStatus.MP",
            "PlayerStatus.MaxMagic", "PlayerStatus.CurrentMagic", "PlayerStatus.Rupee", "PlayerStatus.RupeeNum", "PlayerStatus.Money",
            "PlayerStatus.IsChild", "PlayerStatus.Age", "GetEpona_IsRideEpona", "Get_Epona_RidingEpona", "PlayerStatus.IsRideHorse" };
        foreach (var it in items) { names.Add($"PouchData.{it}.StockNum"); names.Add($"IsGet.{it}"); }
        foreach (var n in names)
        {
            uint h = GameData.Hash(n);
            foreach (GameData.Kind k in Enum.GetValues<GameData.Kind>())
                if (gd.Has(k, h))
                {
                    string v = k switch { GameData.Kind.Int => gd.Int(h).ToString()!, GameData.Kind.Bool => gd.Bool(h).ToString()!, GameData.Kind.Float => gd.Float(h).ToString()!, _ => $"0x{gd.Enum(h):X8}" };
                    Console.WriteLine($"  {k,-5} {n} = {v}");
                }
        }
        foreach (var n in new[] { "PouchData.Item_MagicBean.StockNum", "PouchData.Item_NayruLove.StockNum", "PouchData.Item_FaroreWind.StockNum", "PouchData.Item_DekuNut.StockNum", "PouchData.Item_DekuStick.StockNum",
            "PouchData.Item_Arrow_Normal.StockNum", "PouchData.Item_DekuSeed.StockNum", "PouchData.Weapon_Range_Bow.StockNum",
            "PouchData.Weapon_Range_SlingShot.StockNum", "PlayerStatus.CurrentRupee", "PlayerStatus.MPMax", "PouchData.Armor_Adult_HeavyBoot.StockNum" })
            Console.WriteLine($"  Int   {n} = {gd.Int(GameData.Hash(n))}");
        foreach (var n in new[] { "PlayerStatus.EquipShield", "PlayerStatus.EquipWeapon", "PlayerStatus.EquipArmor", "PlayerStatus.EquipBoot", "PlayerStatus.EquipBoots" })
            Console.WriteLine($"  Str   {n} = {gd.Str(GameData.Hash(n)) ?? "(none)"}");
        Console.WriteLine($"  Enum  PlayerDeadReason = 0x{gd.Enum(GameData.Hash("PlayerDeadReason")):X8}");
        // enum choice names for equipment
        foreach (var c in new[] { "Weapon_Shield_Deku", "Weapon_Shield_Hylian", "Weapon_Shield_Mirror", "Armor_Adult_HeavyBoot", "Armor_Adult_HoverBoot", "Armor_Adult_Goron", "Armor_Adult_Zora", "None", "Weapon_Sword_Kokiri", "Weapon_Sword_Master", "Weapon_Sword_Biggoron" })
            Console.WriteLine($"  choice {c} = 0x{GameData.Hash(c):X8}");
        return 0;
    }
    string file = args[2];
    if (mode == "snap")
    {
        using var w = new StreamWriter(file);
        foreach (GameData.Kind k in Enum.GetValues<GameData.Kind>())
            foreach (var (h, v) in gd.Snapshot(k)) w.WriteLine($"{k} {h:X8} {v}");
        Console.WriteLine($"saved {file}");
        return 0;
    }
    // diff: print fields whose value changed since the snapshot
    var old = File.ReadAllLines(file).Select(l => l.Split(' ')).ToDictionary(p => p[0] + " " + p[1], p => uint.Parse(p[2]));
    foreach (GameData.Kind k in Enum.GetValues<GameData.Kind>())
        foreach (var (h, v) in gd.Snapshot(k))
            if (old.TryGetValue($"{k} {h:X8}", out var o) && o != v)
                Console.WriteLine($"  {k,-5} {h:X8}: {(k == GameData.Kind.Float ? BitConverter.UInt32BitsToSingle(o) : (int)o)} -> {(k == GameData.Kind.Float ? BitConverter.UInt32BitsToSingle(v) : (int)v)}");
    return 0;
}

if (args.FirstOrDefault() is "scan" or "watch")
{
    using var gm = GuestMemory.Attach();
    if (gm == null || gm.Arena == 0) { Console.Error.WriteLine("emulator (Lichii/Fritchii) or game not running, or guest arena not found."); return 1; }
    if (args[0] == "scan") return Scanner.Run(gm, args[1..]);
    // watch <va hex> <u8|u16|u32|f32> [seconds]: print the value whenever it changes
    ulong va = Convert.ToUInt64(args[1], 16); string t = args.Length > 2 ? args[2] : "u32";
    var end = DateTime.Now.AddSeconds(args.Length > 3 ? double.Parse(args[3]) : 10);
    var b = new byte[8]; string last = "";
    while (DateTime.Now < end)
    {
        gm.ReadVa(va, b, 8);
        string s = t switch { "u8" => b[0].ToString(), "u16" => BitConverter.ToUInt16(b).ToString(), "f32" => BitConverter.ToSingle(b).ToString("F3"), _ => BitConverter.ToUInt32(b).ToString() };
        if (s != last) { Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); last = s; }
        Thread.Sleep(16);
    }
    return 0;
}

if (args.FirstOrDefault() == "meminfo")
{
    using var gm = GuestMemory.Attach();
    if (gm == null) { Console.Error.WriteLine("emulator (Lichii/Fritchii) not running / no access."); return 1; }
    Console.WriteLine($"pid {gm.Pid}  arena 0x{gm.Arena:X}  backing 0x{gm.Backing:X} size {gm.BackingSize >> 20} MiB");
    // Large regions overview
    foreach (var r in gm.Regions().Where(r => r.State != 0x10000 && r.Size >= (256UL << 20)))
        Console.WriteLine($"  0x{r.Base:X12} {r.Size >> 20,9} MiB state {r.State:X} prot {r.Protect:X} type {r.Type:X} alloc 0x{r.AllocBase:X}");
    if (args.Length > 2)
    {
        ulong f = Convert.ToUInt64(args[1], 16), t = Convert.ToUInt64(args[2], 16);
        foreach (var r in gm.Regions(f, t))
            Console.WriteLine($"  R 0x{r.Base:X12}-0x{r.Base + r.Size:X12} {r.Size >> 20,8} MiB st {r.State:X} pr {r.Protect:X} ty {r.Type:X} ab 0x{r.AllocBase:X}");
        return 0;
    }
    // Committed guest pages inside the arena, summarised
    ulong total = 0; int n = 0;
    foreach (var (va, size) in gm.GuestRegions())
    {
        total += size; n++;
        if (size >= (64UL << 20)) Console.WriteLine($"  guest VA 0x{va:X10} {size >> 20,7} MiB");
    }
    Console.WriteLine($"  arena: {n} readable guest regions, {total >> 20} MiB");
    return 0;
}

using var ds = DualSense.Open();
if (ds == null) { Console.Error.WriteLine("No DualSense found."); return 1; }
Console.WriteLine($"DualSense ({(ds.Bluetooth ? "Bluetooth" : "USB")}): {ds.Path}");

switch (args.FirstOrDefault() ?? "test")
{
    case "input": // print trigger travel and buttons for 6 s
        ds.StartInput();
        Console.WriteLine($"input report length {ds.InLen}");
        for (int i = 0; i < 30; i++) { Thread.Sleep(200); Console.WriteLine($"L2 {ds.L2,3} R2 {ds.R2,3} buttons {Convert.ToString(ds.Buttons, 2).PadLeft(8, '0')} {ds.InputError}"); }
        break;
    case "off":
        Check(ds.SetTriggers(Effects.Off(), Effects.Off()));
        break;
    case "test":
        var steps = new (string name, byte[] fx)[]
        {
            ("Feedback: constant resistance (strength 5)", Effects.Feedback(2, 5)),
            ("Slope: bow string, gets harder towards the end", Effects.Slope(1, 9, 1, 8)),
            ("Weapon: resistance that breaks like a trigger", Effects.Weapon(3, 6, 8)),
            ("Vibration: buzz at 30 Hz", Effects.Vibration(0, 6, 30)),
        };
        foreach (var (name, fx) in steps)
        {
            Console.WriteLine($"{name}  -> press R2 and L2");
            Check(ds.SetTriggers(fx, fx));
            Thread.Sleep(3000);
        }
        Check(ds.SetTriggers(Effects.Off(), Effects.Off()));
        Console.WriteLine("Off.");
        break;
}
return 0;

static void Check(bool ok)
{
    if (!ok) Console.Error.WriteLine($"HID write failed (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
}
