// Port of 'clause.hpp', 'var.hpp', 'flags.hpp', 'watch.hpp', 'level.hpp', 'bins.hpp',
// 'occs.hpp', 'phases.hpp' and 'decompose.hpp'.

using System.Runtime.CompilerServices;

namespace LeanSharp.Runtime.Cadical;

/// <summary>A clause. In C++ the literals are allocated inline after the header; here they are
/// a separate array whose length can be larger than 'size' after shrinking.</summary>
public sealed class Clause
{
    public ulong id;
    public bool conditioned, covered, enqueued, frozen, garbage, gate, hyper, instantiated, keep,
        moved, reason, redundant, transred, subsume, vivified, vivify;
    public byte used; // 2 bits
    public int glue;
    public int size;
    public int pos;
    public int[] literals;

    public Clause() { }
    public Clause(int capacity) { literals = new int[capacity]; }

    public bool collect() => !reason && garbage;

    /// <summary>Approximation of 'Clause::bytes' (header + literals, aligned to 8).</summary>
    public static long bytes(int size)
    {
        long b = 32 + 4L * size;
        return (b + 7) & ~7L;
    }
    public long bytes() => bytes(size);

    public Span<int> lits => new Span<int>(literals, 0, size);

    public override string ToString() => $"[{id}]{(redundant ? "r" : "i")} " + string.Join(" ", literals.Take(size));
}

public struct Var
{
    public int level;   // decision level
    public int trail;   // trail height at assignment
    public Clause reason; // implication graph edge during search
}

public struct Flags
{
    public bool seen, keep, poison, removable, shrinkable, added, ignorepos, ignoreneg;
    public bool elim, subsume, ternary;
    public byte decompose, block, skip, assumed, failed; // 2 bits each
    public byte status; // 3 bits

    public const byte UNUSED = 0, ACTIVE = 1, FIXED = 2, ELIMINATED = 3, SUBSTITUTED = 4, PURE = 5;

    public static Flags Default()
    {
        Flags f = default;
        f.subsume = f.elim = f.ternary = true;
        f.block = 3;
        f.status = UNUSED;
        return f;
    }

    public bool unused() => status == UNUSED;
    public bool active() => status == ACTIVE;
    public bool @fixed() => status == FIXED;
    public bool eliminated() => status == ELIMINATED;
    public bool substituted() => status == SUBSTITUTED;
    public bool pure() => status == PURE;

    public void copy(ref Flags dst)
    {
        dst.elim = elim;
        dst.subsume = subsume;
        dst.ternary = ternary;
        dst.block = block;
    }
}

public struct Watch
{
    public Clause clause;
    public int blit;
    public int size;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Watch(int b, Clause c) { clause = c; blit = b; size = c.size; }

    public bool binary() => size == 2;
}

public struct Level
{
    public int decision; // decision literal of this level
    public int trail;    // trail start of this level
    public int seen_count; // how many variables seen during 'analyze'
    public int seen_trail; // smallest trail position seen on this level

    public void reset() { seen_count = 0; seen_trail = int.MaxValue; }
    public Level(int d, int t) { decision = d; trail = t; seen_count = 0; seen_trail = int.MaxValue; }
}

public struct Bin
{
    public int lit;
    public ulong id;
}

public sealed class Phases
{
    public sbyte[] best = new sbyte[0];
    public sbyte[] forced = new sbyte[0];
    public sbyte[] min = new sbyte[0];
    public sbyte[] prev = new sbyte[0];
    public sbyte[] saved = new sbyte[0];
    public sbyte[] target = new sbyte[0];
}

public struct DFS
{
    public uint idx;   // depth first search index
    public uint min;   // minimum reachable index
    public Clause parent; // for lrat
}

public static class WatchOps
{
    public static void remove_watch(Vec<Watch> ws, Clause clause)
    {
        var a = ws.a; int n = ws.n;
        int i = 0;
        for (int j = 0; j < n; j++)
        {
            var w = a[i++] = a[j];
            if (w.clause == clause) i--;
        }
        ws.shrink(i);
    }

    public static void update_watch_size(Vec<Watch> ws, int blit, Clause conflict)
    {
        int size = conflict.size;
        var a = ws.a;
        for (int k = 0; k < ws.n; k++)
        {
            ref Watch w = ref a[k];
            if (w.clause == conflict) { w.size = size; w.blit = blit; }
        }
    }

    public static void remove_occs(Vec<Clause> os, Clause c)
    {
        var a = os.a; int n = os.n;
        int i = 0;
        for (int j = 0; j < n; j++)
        {
            var d = a[i++] = a[j];
            if (c == d) i--;
        }
        os.shrink(i);
    }
}

/// <summary>Comparison used to sort clause literals ('clause_lit_less_than').</summary>
public static class ClauseLitLess
{
    public static int Compare(int a, int b)
    {
        int s = Math.Abs(a), t = Math.Abs(b);
        if (s < t || (s == t && a < b)) return -1;
        if (a == b) return 0;
        return 1;
    }
}
