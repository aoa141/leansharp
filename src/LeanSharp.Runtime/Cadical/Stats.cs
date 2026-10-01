// Port of 'stats.hpp' and 'limit.hpp'.

namespace LeanSharp.Runtime.Cadical;

public sealed class Stats
{
    public long vars, conflicts, decisions;

    public sealed class Propagations { public long cover, instantiate, probe, search, transred, vivify, walk; }
    public readonly Propagations propagations = new();

    public sealed class ExtProp { public long ext_cb, eprop_call, eprop_prop, eprop_conf, eprop_expl, elearn_call, elearned, elearn_prop, elearn_conf, echeck_call; }
    public readonly ExtProp ext_prop = new();

    public long condassinit, condassirem, condassrem, condassvars, condautinit, condautrem, condcands,
        condcondinit, condcondrem, conditioned, conditionings, condprops;

    public sealed class Mark { public long block, elim, subsume, ternary; }
    public readonly Mark mark = new();

    public sealed class Clauses { public long total, redundant, irredundant; }
    public readonly Clauses current = new(), added = new();

    public sealed class Time { public double process, real; }
    public readonly Time time = new();

    public sealed class Cover { public long count, asymmetric, blocked, total; }
    public readonly Cover cover = new();

    public sealed class OneZero { public long one, zero; }
    public sealed class PosNeg { public long positive, negative; }
    public sealed class Lucky { public long tried, succeeded; public readonly OneZero constant = new(), forward = new(), backward = new(); public readonly PosNeg horn = new(); }
    public readonly Lucky lucky = new();

    public sealed class Rephased { public long total, best, flipped, inverted, original, random, walk; }
    public readonly Rephased rephased = new();

    public sealed class Walk { public long count, broken, flips, minimum; }
    public readonly Walk walk = new();

    public sealed class Flush { public long count, learned, hyper; }
    public readonly Flush flush = new();

    public long compacts, shuffled, restarts, restartlevels, restartstable, stabphases, stabconflicts, rescored,
        reused, reusedlevels, reusedstable, sections, chrono, backtracks, improvedglue, promoted1, promoted2,
        bumped, recomputed, searched, reductions, reduced, collected, collections, hbrs, hbrsizes, hbreds,
        hbrsubs, instried, instantiated, instrounds, subsumed, deduplicated, deduplications, strengthened,
        elimotfstr, subirr, subred, subtried, subchecks, subchecks2, elimotfsub, subsumerounds, subsumephases,
        eagertried, eagersub, elimres, elimrestried, elimrounds, elimphases, elimcompleted, elimtried, elimsubst,
        elimgates, elimequivs, elimands, elimites, elimxors, elimbwsub, elimbwstr, ternary, ternres, htrs,
        htrs2, htrs3, decompositions, vivifications, vivifychecks, vivifydecs, vivifyreused, vivifysched,
        vivifysubs, vivifystrs, vivifystrirr, vivifystred1, vivifystred2, vivifystred3, vivifyunits,
        vivifyinst, transreds, transitive;

    public sealed class Learned { public long literals, clauses; }
    public readonly Learned learned = new();

    public long minimized, shrunken, minishrunken, irrlits;

    public sealed class Garbage { public long bytes, clauses, literals; }
    public readonly Garbage garbage = new();

    public long units, binaries, probingphases, probingrounds, probesuccess, probed, failed, hyperunary,
        probefailed, transredunits, blockings, blocked, blockres, blockcands, blockpured, blockpurelits,
        extensions, extended, weakened, weakenedlen, restorations, restored, reactivated, restoredlits,
        preprocessings, ilbtriggers, ilbsuccess, levelsreused, literalsreused, assumptionsreused;

    public sealed class Vars { public long fixed_, eliminated, substituted, pure; }
    public readonly Vars all = new(), now = new();

    public sealed class Otfs { public long strengthened, subsumed; }
    public readonly Otfs otfs = new();

    public long unused, active, inactive;

    public Stats()
    {
        time.real = Resources.absolute_real_time();
        time.process = Resources.absolute_process_time();
        walk.minimum = long.MaxValue;
    }
}

public sealed class Limit
{
    public bool initialized;
    public long conflicts, decisions, preprocessing, localsearch;
    public long compact, condition, elim, flush, probe, reduce, rephase, report, restart, stabilize, subsume;
    public int keptsize, keptglue;
    public readonly long[] rephased = new long[2];
    public long elimbound;
    public struct Terminate { public int check, forced; }
    public Terminate terminate;
}

public sealed class Last
{
    public struct Props { public long propagations; }
    public Props transred, vivify;
    public struct Elim { public long fixed_, subsumephases, marked; }
    public Elim elim;
    public struct Probe { public long propagations, reductions; }
    public Probe probe;
    public struct Confl { public long conflicts; }
    public Confl reduce, rephase;
    public struct Marked { public long marked; }
    public Marked ternary;
    public struct Fixed { public long fixed_; }
    public Fixed collect;
}

public sealed class Inc
{
    public long flush, stabilize, conflicts = -1, decisions = -1, preprocessing, localsearch;
}

/// <summary>'resources.cpp'</summary>
public static class Resources
{
    static readonly System.Diagnostics.Stopwatch s_watch = System.Diagnostics.Stopwatch.StartNew();
    public static double absolute_real_time() => s_watch.Elapsed.TotalSeconds;
    public static double absolute_process_time()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds; }
        catch { return absolute_real_time(); }
    }
    public static ulong maximum_resident_set_size()
    {
        try { return (ulong)System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64; } catch { return 0; }
    }
    public static ulong current_resident_set_size()
    {
        try { return (ulong)System.Diagnostics.Process.GetCurrentProcess().WorkingSet64; } catch { return 0; }
    }
}
