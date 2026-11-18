using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PulseOfTime;

/// Read-only view of Lichii's emulated memory from outside the process.
/// Lichii (common/host_memory.cpp) maps guest DRAM as one big section view ("backing") and
/// reserves a 512 GiB arena where guest virtual address X lives at arena + X (fastmem).
sealed class GuestMemory : IDisposable
{
    public readonly IntPtr Handle;
    public readonly int Pid;
    public ulong Arena;          // host address of guest VA 0
    public ulong Backing;        // host address of guest physical DRAM
    public ulong BackingSize;

    GuestMemory(IntPtr h, int pid) { Handle = h; Pid = pid; }

    /// Lichii and its forks, by exe name (Fritchii renamed it from v6.1 on).
    static readonly string[] Emulators = { "lichii", "fritchii" };
    public static Process? FindEmulator() => Emulators.SelectMany(Process.GetProcessesByName).FirstOrDefault();

    public static GuestMemory? Attach()
    {
        var p = FindEmulator();
        if (p == null) return null;
        var h = OpenProcess(0x0410 /*QUERY_INFORMATION|VM_READ*/, false, p.Id);
        if (h == IntPtr.Zero) return null;
        var g = new GuestMemory(h, p.Id);
        g.FindLayout();
        return g;
    }

    public record Region(ulong Base, ulong AllocBase, ulong Size, uint State, uint Protect, uint Type);

    public IEnumerable<Region> Regions(ulong from = 0, ulong to = 0x7FFF_FFFF_FFFF)
    {
        ulong a = from;
        while (a < to && VirtualQueryEx(Handle, (IntPtr)a, out var m, (IntPtr)Marshal.SizeOf<MBI>()) != IntPtr.Zero)
        {
            yield return new Region((ulong)m.BaseAddress, (ulong)m.AllocationBase, (ulong)m.RegionSize, m.State, m.Protect, m.Type);
            ulong next = (ulong)m.BaseAddress + (ulong)m.RegionSize;
            if (next <= a) break;
            a = next;
        }
    }

    void FindLayout()
    {
        // Group regions by allocation base: the arena is the one spanning ~512 GiB,
        // the backing is the largest single MEM_MAPPED read-write view outside it.
        var groups = new Dictionary<ulong, (ulong lo, ulong hi)>();
        Region? bestMapped = null;
        foreach (var r in Regions())
        {
            if (r.State == 0x10000 /*FREE*/) continue;
            var key = r.AllocBase;
            groups[key] = groups.TryGetValue(key, out var g) ? (Math.Min(g.lo, r.Base), Math.Max(g.hi, r.Base + r.Size)) : (r.Base, r.Base + r.Size);
            if (r.Type == 0x40000 /*MAPPED*/ && r.State == 0x1000 /*COMMIT*/ && (r.Protect & 0x04) != 0
                && (bestMapped == null || r.Size > bestMapped.Size))
                bestMapped = r;
        }
        if (bestMapped != null) { Backing = bestMapped.Base; BackingSize = bestMapped.Size; }
        Arena = FindArena();
    }

    const ulong ArenaSize = 1UL << 39;
    const ulong HugePage = 0x200000;

    /// Placeholders split the arena reservation (2^39 + 2 MiB, host_memory.cpp) into many pieces with
    /// their own allocation bases, so find it by shape: a start S where S and S + size are both region
    /// boundaries, all address space in between is in use, and it holds mapped views (guest pages).
    /// virtual_base is S rounded up to 2 MiB.
    ulong FindArena()
    {
        var all = Regions().ToList();
        var bounds = new HashSet<ulong>();
        foreach (var r in all) { bounds.Add(r.Base); bounds.Add(r.Base + r.Size); }
        foreach (var r in all)
        {
            if (r.State == 0x10000 || r.Base == Backing) continue;
            ulong s = r.Base, e = s + ArenaSize + HugePage;
            if (!bounds.Contains(e)) continue;
            bool contiguous = true; int views = 0;
            foreach (var q in all)
            {
                if (q.Base + q.Size <= s || q.Base >= e) continue;
                if (q.State == 0x10000) { contiguous = false; break; }
                if (q.Type == 0x40000 && q.State == 0x1000) views++;
            }
            if (contiguous && views > 0) return (s + HugePage - 1) & ~(HugePage - 1);
        }
        return 0;
    }

    /// Committed, readable guest pages as (guest VA, size).
    public IEnumerable<(ulong va, ulong size)> GuestRegions()
    {
        if (Arena == 0) yield break;
        foreach (var r in Regions(Arena, Arena + ArenaSize))
            if (r.State == 0x1000 && (r.Protect & 0x66) != 0 /*R, RW, RX, RWX*/ && (r.Protect & 0x100) == 0 /*not GUARD*/)
                yield return (r.Base - Arena, r.Size);
    }

    public bool ReadVa(ulong va, byte[] buf, int len) => Read(Arena + va, buf, len);

    public ulong ReadU64(ulong va) { var b = new byte[8]; return ReadVa(va, b, 8) ? BitConverter.ToUInt64(b) : 0; }

    public bool Read(ulong host, byte[] buf, int len)
        => ReadProcessMemory(Handle, (IntPtr)host, buf, (IntPtr)len, out var n) && (long)n == len;

    public void Dispose() => CloseHandle(Handle);

    [StructLayout(LayoutKind.Sequential)]
    struct MBI
    {
        public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId;
        public IntPtr RegionSize; public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, IntPtr len);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] buf, IntPtr len, out IntPtr read);
}
