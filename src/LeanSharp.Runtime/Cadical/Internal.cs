// Port of 'internal.hpp' / 'internal.cpp' (and the small helpers 'flags.cpp', 'var.cpp',
// 'queue.cpp', 'score.cpp', 'phases.cpp', 'limit.cpp', 'averages.cpp').
//
// Naming follows the C++ sources (snake_case) to keep the port easy to compare line by line.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LeanSharp.Runtime.Cadical;

public enum ConclusionType { CONFLICT = 1, ASSUMPTIONS = 2, CONSTRAINT = 4 }

public sealed unsafe partial class Internal
{
    // Modes.
    public const int BLOCK = 1 << 0, CONDITION = 1 << 1, COVER = 1 << 2, DECOMP = 1 << 3, DEDUP = 1 << 4,
        ELIM = 1 << 5, LUCKY = 1 << 6, PROBE = 1 << 7, SEARCH = 1 << 8, SIMPLIFY = 1 << 9, SUBSUME = 1 << 10,
        TERNARY = 1 << 11, TRANSRED = 1 << 12, VIVIFY = 1 << 13, WALK = 1 << 14;

    public bool in_mode(int m) => (mode & m) != 0;
    public void set_mode(int m) { mode |= m; }
    public void reset_mode(int m) { mode &= ~m; }
    public void require_mode(int m) { }

    // Pseudo reasons ('decision_reason' and 'external_reason' in C++).
    public static readonly Clause decision_reason = new Clause(0);
    public static readonly Clause external_reason = new Clause(0);

    public int mode = SEARCH;
    public bool unsat, iterating, localsearching, lookingahead, preprocessing, protected_reasons,
        force_saved_phase, searching_lucky_phases, stable, reported, external_prop, did_external_prop,
        external_prop_is_lazy = true, forced_backt_allowed, private_steps;
    public char rephased;
    public readonly Reluctant reluctant = new();
    public int vsize;
    public int max_var;
    public ulong clause_id, original_id, reserved_ids, conflict_id;
    public bool concluded;
    public readonly Vec<ulong> conclusion = new();
    public ulong[] unit_clauses_idx = new ulong[0];
    public readonly Vec<ulong> lrat_chain = new();
    public readonly Vec<ulong> mini_chain = new();
    public readonly Vec<ulong> minimize_chain = new();
    public readonly Vec<ulong> unit_chain = new();
    public readonly Vec<Clause> inst_chain = new();
    public List<List<List<ulong>>> probehbr_chains = new();
    public bool lrat, frat;
    public int level;
    public readonly Phases phases = new();
    sbyte[] vals_array;
    public sbyte* vals;               // assignment [-max_var,max_var]
    public sbyte[] marks = new sbyte[0];
    public readonly Vec<uint> frozentab = new();
    public readonly Vec<int> i2e = new();
    public readonly Vec<uint> relevanttab = new();
    public readonly CQueue queue = new();
    public Link[] links = new Link[0];
    public double score_inc = 1.0;
    public Heap scores;
    public double[] stab = new double[0];
    public Var[] vtab = new Var[0];
    public int[] parents = new int[0];
    public Flags[] ftab = new Flags[0];
    public long[] btab = new long[0];
    public long[] gtab = new long[0];
    public Vec<Clause>[] otab = new Vec<Clause>[0];
    public int[] ptab = new int[0];
    public long[] ntab = new long[0];
    public Vec<Bin>[] big = new Vec<Bin>[0];
    public Vec<Watch>[] wtab = new Vec<Watch>[0];
    public Clause conflict;
    public Clause ignore;
    public Clause dummy_binary;
    public Clause newest_clause;
    public bool force_no_backtrack, from_propagator, ext_clause_forgettable;
    public int tainted_literal;
    public int notified;
    public Clause probe_reason;
    public int propagated, propagated2, propergated;
    public int best_assigned, target_assigned, no_conflict_until;
    public readonly Vec<int> trail = new();
    public readonly Vec<int> clause = new();
    public readonly Vec<int> assumptions = new();
    public readonly Vec<int> constraint = new();
    public bool unsat_constraint;
    public bool marked_failed = true;
    public readonly Vec<int> original = new();
    public readonly Vec<int> levels = new();
    public readonly Vec<int> analyzed = new();
    public readonly Vec<int> unit_analyzed = new();
    public readonly Vec<int> decomposed = new();
    public readonly Vec<int> minimized = new();
    public readonly Vec<int> shrinkable = new();
    public readonly Reap reap = new();
    public int num_assigned;
    public readonly Vec<int> probes = new();
    public readonly Vec<Level> control = new();
    public readonly Vec<Clause> clauses = new();
    public readonly Averages averages = new();
    public readonly Limit lim = new();
    public readonly Last last = new();
    public readonly Inc inc = new();

    public Proof proof;
    public readonly List<Tracer> tracers = new();
    public readonly List<FileTracer> file_tracers = new();
    public readonly List<StatTracer> stat_tracers = new();

    public readonly Options opts;
    public readonly Stats stats = new();
    public bool force_phase_messages;
    public string error_message;
    public string prefix = "c ";

    public External external;

    public volatile bool termination_forced;

    /// <summary>Output for messages ('stdout' in C++).</summary>
    public TextWriter output;

    // Accumulated time spent in 'solve' (replaces the profiling based 'solve_time').
    double solve_time_accumulated;
    double solve_time_started = -1;

    public Internal(int reportdefault = 0, TextWriter output = null)
    {
        this.output = output ?? TextWriter.Null;
        opts = new Options(this, reportdefault);
        scores = new Heap(score_smaller);
        control.push_back(new Level(0, 0));
        dummy_binary = new Clause(2);
        dummy_binary.size = 2;
        vals_array = GC.AllocateArray<sbyte>(2, pinned: true);
        vals = (sbyte*)Unsafe.AsPointer(ref vals_array[0]);
        vals_array = null; // replaced on first 'enlarge'
        vals = null;
    }

    /*------------------------------------------------------------------------*/

    bool score_smaller(uint a, uint b)
    {
        double s = stab[a];
        double t = stab[b];
        if (s < t) return true;
        if (s > t) return false;
        return a > b;
    }

    public bool score_smaller_pub(uint a, uint b) => score_smaller(a, b);

    /*------------------------------------------------------------------------*/

    void enlarge_vals(int new_vsize)
    {
        var new_array = GC.AllocateArray<sbyte>(2 * new_vsize, pinned: true);
        sbyte* new_vals = (sbyte*)Unsafe.AsPointer(ref new_array[0]) + new_vsize;
        if (vals != null)
        {
            for (int i = -max_var; i <= max_var; i++) new_vals[i] = vals[i];
        }
        vals_array = new_array;
        vals = new_vals;
    }

    static void enlarge_init<T>(ref T[] v, int N, T init)
    {
        if (v.Length < N)
        {
            int old = v.Length;
            Array.Resize(ref v, N);
            for (int i = old; i < N; i++) v[i] = init;
        }
    }

    static void enlarge_only<T>(ref T[] v, int N)
    {
        if (v.Length < N) Array.Resize(ref v, N);
    }

    static void enlarge_vecs<T>(ref Vec<T>[] v, int N)
    {
        if (v.Length < N)
        {
            int old = v.Length;
            Array.Resize(ref v, N);
            for (int i = old; i < N; i++) v[i] = new Vec<T>();
        }
    }

    public void enlarge(int new_max_var)
    {
        long new_vsize = vsize != 0 ? 2L * vsize : 1L + new_max_var;
        while (new_vsize <= new_max_var) new_vsize *= 2;
        int nv = (int)new_vsize;
        if (lrat || frat) enlarge_only(ref unit_clauses_idx, 2 * nv);
        if (wtab.Length != 0 || true) enlarge_vecs(ref wtab, 2 * nv);
        enlarge_only(ref vtab, nv);
        enlarge_only(ref parents, nv);
        enlarge_only(ref links, nv);
        enlarge_only(ref btab, nv);
        enlarge_only(ref gtab, nv);
        enlarge_only(ref stab, nv);
        enlarge_init(ref ptab, 2 * nv, -1);
        if (ftab.Length < nv)
        {
            int old = ftab.Length;
            Array.Resize(ref ftab, nv);
            for (int i = old; i < nv; i++) ftab[i] = Flags.Default();
        }
        enlarge_vals(nv);
        vsize = nv;
        if (external != null)
        {
            if (relevanttab.n < nv) relevanttab.resize(nv);
        }
        sbyte val = (sbyte)(opts.phase != 0 ? 1 : -1);
        enlarge_init(ref phases.saved, nv, val);
        enlarge_only(ref phases.forced, nv);
        enlarge_only(ref phases.target, nv);
        enlarge_only(ref phases.best, nv);
        enlarge_only(ref phases.prev, nv);
        enlarge_only(ref phases.min, nv);
        enlarge_only(ref marks, nv);
    }

    public void init_vars(int new_max_var)
    {
        if (new_max_var <= max_var) return;
        if (new_max_var >= vsize) enlarge(new_max_var);
        int old_max_var = max_var;
        max_var = new_max_var;
        init_queue(old_max_var, new_max_var);
        init_scores(old_max_var, new_max_var);
        int initialized = new_max_var - old_max_var;
        stats.vars += initialized;
        stats.unused += initialized;
        stats.inactive += initialized;
    }

    public void add_original_lit(int lit)
    {
        if (lit != 0)
        {
            original.push_back(lit);
        }
        else
        {
            ulong id = original_id < reserved_ids ? ++original_id : ++clause_id;
            if (proof != null)
                proof.add_external_original_clause(id, false, external.eclause);
            add_new_original_clause(id);
            original.clear();
        }
    }

    public void finish_added_clause_with_id(ulong id, bool restore = false)
    {
        if (proof != null)
            proof.add_external_original_clause(id, false, external.eclause, restore);
        add_new_original_clause(id);
        original.clear();
    }

    public void reserve_ids(int number)
    {
        clause_id = reserved_ids = (ulong)number;
        if (proof != null) proof.begin_proof(reserved_ids);
    }

    /*------------------------------------------------------------------------*/

    public int active(int lit) => flags(lit).active() ? 1 : 0;
    public bool is_active(int lit) => flags(lit).active();
    public int active() => (int)stats.active;

    public long redundant() => stats.current.redundant;
    public long irredundant() => stats.current.irredundant;
    public double clause_variable_ratio() => CUtil.relative(irredundant(), active());

    public double scale(double v)
    {
        double ratio = clause_variable_ratio();
        double factor = (ratio <= 2) ? 1.0 : Math.Log(ratio) / Math.Log(2);
        double res = factor * v;
        if (res < 1) res = 1;
        return res;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int vidx(int lit) => lit < 0 ? -lit : lit;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint vlit(int lit) => (lit < 0 ? 1u : 0u) + 2u * (uint)(lit < 0 ? -lit : lit);

    public int u2i(uint u)
    {
        int res = (int)(u / 2);
        if ((u & 1) != 0) res = -res;
        return res;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref ulong unit_clauses(uint uidx) => ref unit_clauses_idx[uidx];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref ulong unit_clauses(int uidx) => ref unit_clauses_idx[uidx];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Var @var(int lit) => ref vtab[lit < 0 ? -lit : lit];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Link link(int lit) => ref links[lit < 0 ? -lit : lit];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Flags flags(int lit) => ref ftab[lit < 0 ? -lit : lit];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref long bumped(int lit) => ref btab[lit < 0 ? -lit : lit];
    public ref int propfixed(int lit) => ref ptab[vlit(lit)];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref double score(int lit) => ref stab[lit < 0 ? -lit : lit];

    public bool occurring() => otab.Length != 0;
    public bool watching() => wtab.Length != 0;

    public Vec<Bin> bins(int lit) => big[vlit(lit)];
    public Vec<Clause> occs(int lit) => otab[vlit(lit)];
    public ref long noccs(int lit) => ref ntab[vlit(lit)];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vec<Watch> watches(int lit) => wtab[vlit(lit)];

    public bool use_scores() => opts.score != 0 && stable;

    // Marking.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public sbyte marked(int lit)
    {
        sbyte res = marks[vidx(lit)];
        if (lit < 0) res = (sbyte)-res;
        return res;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void mark(int lit) { marks[vidx(lit)] = (sbyte)CUtil.sign(lit); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void unmark(int lit) { marks[vidx(lit)] = 0; }

    public sbyte marked67(int lit)
    {
        sbyte res = (sbyte)(marks[vidx(lit)] >> 6);
        if (lit < 0) res = (sbyte)-res;
        return res;
    }
    public void mark67(int lit)
    {
        ref sbyte m = ref marks[vidx(lit)];
        const sbyte mask = 0x3f;
        m = (sbyte)((m & mask) | (CUtil.sign(lit) << 6));
    }
    public void unmark67(int lit)
    {
        ref sbyte m = ref marks[vidx(lit)];
        m = (sbyte)(m & 0x3f);
    }

    public void unmark(Vec<int> lits)
    {
        for (int i = 0; i < lits.n; i++) unmark(lits.a[i]);
    }

    public bool getbit(int lit, int bit) => (marks[vidx(lit)] & (1 << bit)) != 0;
    public void setbit(int lit, int bit) { marks[vidx(lit)] |= (sbyte)(1 << bit); }
    public void unsetbit(int lit, int bit) { marks[vidx(lit)] &= (sbyte)~(1 << bit); }

    public bool marked2(int lit)
    {
        uint res = (uint)(byte)marks[vidx(lit)];
        uint bit = CUtil.bign(lit);
        return (res & bit) != 0;
    }
    public void mark2(int lit) { marks[vidx(lit)] |= (sbyte)CUtil.bign(lit); }

    public void mark_clause() { for (int i = 0; i < clause.n; i++) mark(clause.a[i]); }
    public void unmark_clause() { for (int i = 0; i < clause.n; i++) unmark(clause.a[i]); }
    public void mark(Clause c) { var l = c.literals; for (int i = 0; i < c.size; i++) mark(l[i]); }
    public void mark2(Clause c) { var l = c.literals; for (int i = 0; i < c.size; i++) mark2(l[i]); }
    public void unmark(Clause c) { var l = c.literals; for (int i = 0; i < c.size; i++) unmark(l[i]); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void watch_literal(int lit, int blit, Clause c)
    {
        wtab[vlit(lit)].push_back(new Watch(blit, c));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void watch_clause(Clause c)
    {
        int l0 = c.literals[0];
        int l1 = c.literals[1];
        watch_literal(l0, l1, c);
        watch_literal(l1, l0, c);
    }

    public void unwatch_clause(Clause c)
    {
        int l0 = c.literals[0];
        int l1 = c.literals[1];
        WatchOps.remove_watch(watches(l0), c);
        WatchOps.remove_watch(watches(l1), c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void update_queue_unassigned(int idx)
    {
        queue.unassigned = idx;
        queue.bumped = btab[idx];
    }

    public bool likely_to_be_kept_clause(Clause c)
    {
        if (!c.redundant) return true;
        if (c.keep) return true;
        if (c.glue > lim.keptglue) return false;
        if (c.size > lim.keptsize) return false;
        return true;
    }

    public void mark_subsume(int lit)
    {
        ref Flags f = ref flags(lit);
        if (f.subsume) return;
        stats.mark.subsume++;
        f.subsume = true;
    }
    public void mark_ternary(int lit)
    {
        ref Flags f = ref flags(lit);
        if (f.ternary) return;
        stats.mark.ternary++;
        f.ternary = true;
    }
    public bool marked_subsume(int lit) => flags(lit).subsume;

    public void mark_elim(int lit)
    {
        ref Flags f = ref flags(lit);
        if (f.elim) return;
        stats.mark.elim++;
        f.elim = true;
    }
    public void mark_block(int lit)
    {
        ref Flags f = ref flags(lit);
        uint bit = CUtil.bign(lit);
        if ((f.block & bit) != 0) return;
        stats.mark.block++;
        f.block |= (byte)bit;
    }
    public void mark_removed(int lit)
    {
        mark_elim(lit);
        mark_block(-lit);
    }
    public bool marked_block(int lit) => (flags(lit).block & CUtil.bign(lit)) != 0;
    public void unmark_block(int lit) { flags(lit).block &= (byte)~CUtil.bign(lit); }
    public void mark_skip(int lit)
    {
        ref Flags f = ref flags(lit);
        uint bit = CUtil.bign(lit);
        if ((f.skip & bit) != 0) return;
        f.skip |= (byte)bit;
    }
    public bool marked_skip(int lit) => (flags(lit).skip & CUtil.bign(lit)) != 0;
    public void mark_decomposed(int lit)
    {
        ref Flags f = ref flags(lit);
        uint bit = CUtil.bign(lit);
        decomposed.push_back(lit);
        f.decompose |= (byte)bit;
    }
    public void unmark_decompose(int lit) { flags(lit).decompose &= (byte)~CUtil.bign(lit); }
    public bool marked_decompose(int lit) => (flags(lit).decompose & CUtil.bign(lit)) != 0;

    public bool assumed(int lit) => (flags(lit).assumed & CUtil.bign(lit)) != 0;

    /// <summary>Value of an internal literal: -1=false, 0=unassigned, 1=true.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public sbyte val(int lit) => vals[lit];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void set_val(int lit, sbyte v)
    {
        vals[lit] = v;
        vals[-lit] = (sbyte)-v;
    }

    /// <summary>Root-level value of a literal ('fixed' in C++).</summary>
    public int @fixed(int lit)
    {
        int idx = vidx(lit);
        int res = vals[idx];
        if (res != 0 && vtab[idx].level != 0) res = 0;
        if (lit < 0) res = -res;
        return res;
    }

    public int externalize(int lit)
    {
        int idx = Math.Abs(lit);
        int res = i2e[idx];
        if (lit < 0) res = -res;
        return res;
    }

    public void freeze(int lit)
    {
        int idx = vidx(lit);
        if (idx >= frozentab.n)
        {
            long new_vsize = vsize != 0 ? 2L * vsize : 1L + max_var;
            while (new_vsize <= max_var) new_vsize *= 2;
            frozentab.resize((int)new_vsize);
        }
        ref uint r = ref frozentab[idx];
        if (r < uint.MaxValue) r++;
    }

    public void melt(int lit)
    {
        int idx = vidx(lit);
        ref uint r = ref frozentab[idx];
        if (r < uint.MaxValue)
        {
            if (--r == 0)
            {
                if (idx < relevanttab.n && relevanttab[idx] != 0) r++;
            }
        }
    }

    public bool frozen(int lit) => vidx(lit) < frozentab.n && frozentab[vidx(lit)] > 0;

    /*------------------------------------------------------------------------*/
    // 'flags.cpp'

    public void mark_fixed(int lit)
    {
        ref Flags f = ref flags(lit);
        f.status = Flags.FIXED;
        stats.all.fixed_++;
        stats.now.fixed_++;
        stats.inactive++;
        stats.active--;
    }

    public void mark_eliminated(int lit)
    {
        ref Flags f = ref flags(lit);
        f.status = Flags.ELIMINATED;
        stats.all.eliminated++;
        stats.now.eliminated++;
        stats.inactive++;
        stats.active--;
    }

    public void mark_pure(int lit)
    {
        ref Flags f = ref flags(lit);
        f.status = Flags.PURE;
        stats.all.pure++;
        stats.now.pure++;
        stats.inactive++;
        stats.active--;
    }

    public void mark_substituted(int lit)
    {
        ref Flags f = ref flags(lit);
        f.status = Flags.SUBSTITUTED;
        stats.all.substituted++;
        stats.now.substituted++;
        stats.inactive++;
        stats.active--;
    }

    public void mark_active(int lit)
    {
        ref Flags f = ref flags(lit);
        f.status = Flags.ACTIVE;
        stats.inactive--;
        stats.unused--;
        stats.active++;
    }

    public void reactivate(int lit)
    {
        ref Flags f = ref flags(lit);
        switch (f.status)
        {
            default:
            case Flags.ELIMINATED: stats.now.eliminated--; break;
            case Flags.SUBSTITUTED: stats.now.substituted--; break;
            case Flags.PURE: stats.now.pure--; break;
        }
        f.status = Flags.ACTIVE;
        stats.reactivated++;
        stats.inactive--;
        stats.active++;
    }

    // 'var.cpp'
    public void reset_subsume_bits()
    {
        for (int idx = 1; idx <= max_var; idx++) flags(idx).subsume = false;
    }
    public void check_var_stats() { }

    /*------------------------------------------------------------------------*/
    // 'queue.cpp'

    void init_enqueue(int idx)
    {
        ref Link l = ref links[idx];
        if (opts.reverse != 0)
        {
            l.prev = 0;
            if (queue.first != 0)
            {
                links[queue.first].prev = idx;
                btab[idx] = btab[queue.first] - 1;
            }
            else
            {
                queue.last = idx;
                btab[idx] = 0;
            }
            l.next = queue.first;
            queue.first = idx;
            if (queue.unassigned == 0) update_queue_unassigned(queue.last);
        }
        else
        {
            l.next = 0;
            if (queue.last != 0) links[queue.last].next = idx;
            else queue.first = idx;
            btab[idx] = ++stats.bumped;
            l.prev = queue.last;
            queue.last = idx;
            update_queue_unassigned(queue.last);
        }
    }

    public void init_queue(int old_max_var, int new_max_var)
    {
        for (int idx = old_max_var; idx < new_max_var; idx++) init_enqueue(idx + 1);
    }

    public void shuffle_queue()
    {
        if (opts.shuffle == 0) return;
        if (opts.shufflequeue == 0) return;
        stats.shuffled++;
        var shuffle = new Vec<int>();
        if (opts.shufflerandom != 0)
        {
            for (int idx = max_var; idx != 0; idx--) shuffle.push_back(idx);
            var random = new CRandom((ulong)opts.seed);
            random.add((ulong)stats.shuffled);
            for (int i = 0; i <= max_var - 2; i++)
            {
                int j = random.pick_int(i, max_var - 1);
                (shuffle[i], shuffle[j]) = (shuffle[j], shuffle[i]);
            }
        }
        else
        {
            for (int idx = queue.last; idx != 0; idx = links[idx].prev) shuffle.push_back(idx);
        }
        queue.first = queue.last = 0;
        for (int k = 0; k < shuffle.n; k++) queue.enqueue(links, shuffle[k]);
        long bumped_ = queue.bumped;
        for (int idx = queue.last; idx != 0; idx = links[idx].prev) btab[idx] = bumped_--;
        queue.unassigned = queue.last;
    }

    // 'score.cpp'
    public void init_scores(int old_max_var, int new_max_var)
    {
        for (int i = old_max_var; i < new_max_var; i++) scores.push_back((uint)(i + 1));
    }

    public void shuffle_scores()
    {
        if (opts.shuffle == 0) return;
        if (opts.shufflescores == 0) return;
        stats.shuffled++;
        var shuffle = new Vec<int>();
        if (opts.shufflerandom != 0)
        {
            scores.erase();
            for (int idx = max_var; idx != 0; idx--) shuffle.push_back(idx);
            var random = new CRandom((ulong)opts.seed);
            random.add((ulong)stats.shuffled);
            for (int i = 0; i <= max_var - 2; i++)
            {
                int j = random.pick_int(i, max_var - 1);
                (shuffle[i], shuffle[j]) = (shuffle[j], shuffle[i]);
            }
        }
        else
        {
            while (!scores.empty())
            {
                int idx = (int)scores.front();
                scores.pop_front();
                shuffle.push_back(idx);
            }
        }
        score_inc = 0;
        for (int k = 0; k < shuffle.n; k++)
        {
            int idx = shuffle[k];
            stab[idx] = score_inc++;
            scores.push_back((uint)idx);
        }
    }

    // 'phases.cpp'
    public void copy_phases(sbyte[] dst)
    {
        var saved = phases.saved;
        for (int i = 1; i <= max_var; i++) dst[i] = saved[i];
    }

    public void clear_phases(sbyte[] dst)
    {
        for (int i = 1; i <= max_var; i++) dst[i] = 0;
    }

    public void phase(int lit)
    {
        int idx = vidx(lit);
        sbyte old_forced_phase = phases.forced[idx];
        sbyte new_forced_phase = (sbyte)CUtil.sign(lit);
        if (old_forced_phase == new_forced_phase) return;
        phases.forced[idx] = new_forced_phase;
    }

    public void unphase(int lit)
    {
        int idx = vidx(lit);
        sbyte old_forced_phase = phases.forced[idx];
        if (old_forced_phase == 0) return;
        phases.forced[idx] = 0;
    }

    // 'averages.cpp'
    public void init_averages()
    {
        averages.current.jump = EMA.Init(opts.emajump);
        averages.current.level = EMA.Init(opts.emalevel);
        averages.current.size = EMA.Init(opts.emasize);
        averages.current.glue_fast = EMA.Init(opts.emagluefast);
        averages.current.glue_slow = EMA.Init(opts.emaglueslow);
        averages.current.trail_fast = EMA.Init(opts.ematrailfast);
        averages.current.trail_slow = EMA.Init(opts.ematrailslow);
    }

    public void swap_averages()
    {
        (averages.current, averages.saved) = (averages.saved, averages.current);
        if (averages.swapped == 0) init_averages();
        averages.swapped++;
    }

    /*------------------------------------------------------------------------*/
    // 'limit.cpp'

    public void limit_terminate(int l)
    {
        if (l <= 0 && lim.terminate.forced == 0) { }
        else if (l <= 0) lim.terminate.forced = 0;
        else lim.terminate.forced = l;
    }

    public void limit_conflicts(int l)
    {
        if (l < 0 && inc.conflicts < 0) { }
        else if (l < 0) inc.conflicts = -1;
        else inc.conflicts = l;
    }

    public void limit_decisions(int l)
    {
        if (l < 0 && inc.decisions < 0) { }
        else if (l < 0) inc.decisions = -1;
        else inc.decisions = l;
    }

    public void limit_preprocessing(int l)
    {
        if (l < 0) { }
        else if (l == 0) inc.preprocessing = 0;
        else inc.preprocessing = l;
    }

    public void limit_local_search(int l)
    {
        if (l < 0) { }
        else if (l == 0) inc.localsearch = 0;
        else inc.localsearch = l;
    }

    public static bool is_valid_limit(string name) =>
        name == "terminate" || name == "conflicts" || name == "decisions" || name == "preprocessing" || name == "localsearch";

    public bool limit(string name, int l)
    {
        bool res = true;
        if (name == "terminate") limit_terminate(l);
        else if (name == "conflicts") limit_conflicts(l);
        else if (name == "decisions") limit_decisions(l);
        else if (name == "preprocessing") limit_preprocessing(l);
        else if (name == "localsearch") limit_local_search(l);
        else res = false;
        return res;
    }

    public void reset_limits()
    {
        limit_terminate(0);
        limit_conflicts(-1);
        limit_decisions(-1);
        limit_preprocessing(0);
        limit_local_search(0);
    }

    /*------------------------------------------------------------------------*/
    // Asynchronous termination.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool terminated_asynchronously(int factor = 1)
    {
        if (termination_forced) return true;
        if (lim.terminate.forced != 0)
        {
            if (lim.terminate.forced-- == 1)
            {
                termination_forced = true;
                return true;
            }
        }
        if (external.terminator != null && lim.terminate.check-- == 0)
        {
            lim.terminate.check = factor * opts.terminateint;
            if (external.terminator())
            {
                termination_forced = true;
                return true;
            }
        }
        return false;
    }

    public bool search_limits_hit()
    {
        if (lim.conflicts >= 0 && stats.conflicts >= lim.conflicts) return true;
        if (lim.decisions >= 0 && stats.decisions >= lim.decisions) return true;
        return false;
    }

    public void terminate() { termination_forced = true; }

    /*------------------------------------------------------------------------*/
    // External propagator support is not ported (no ExternalPropagator in Lean's API).
    // These are the no-op versions used when no propagator is connected.

    public bool external_propagate() => true;
    public bool external_check_solution() => true;
    public void notify_assignments() { }
    public void notify_decision() { }
    public void notify_backtrack(int new_level) { }
    public int ask_decision() => 0;
    /// <summary>'handle_external_clause' from 'external_propagate.cpp' (also needed without a
    /// propagator when clauses are added at a non-zero level, i.e., with 'opts.ilb').</summary>
    public void handle_external_clause(Clause res)
    {
        if (level == 0) return;
        if (res == null)
        {
            backtrack();
            return;
        }
        int pos0 = res.literals[0];
        int pos1 = res.literals[1];
        if (force_no_backtrack) return;
        int l1 = @var(pos1).level;
        if (val(pos0) < 0)
        {
            if (opts.chrono == 0) backtrack(l1);
            if (val(pos0) < 0)
            {
                conflict = res;
                if (!from_propagator)
                {
                    backtrack(l1 - 1);
                    conflict = null;
                }
            }
            else search_assign_driving(pos0, res);
            return;
        }
        if (val(pos1) < 0 && val(pos0) == 0)
        {
            if (opts.chrono == 0) backtrack(l1);
            search_assign_driving(pos0, res);
            return;
        }
    }
    public void renotify_trail_after_local_search() { }
    public void renotify_trail_after_ilb() { }
    public bool is_external_forgettable(ulong id) => false;
    public void mark_garbage_external_forgettable(ulong id) { }
    public Clause learn_external_reason_clause(int lit, int falsified_elit = 0, bool no_backtrack = false) =>
        throw new CadicalException("external propagation not supported");
    public Clause wrapped_learn_external_reason_clause(int lit) => learn_external_reason_clause(lit);
    public void explain_external_propagations() { }
    public bool is_decision(int ilit)
    {
        if (level == 0 || @fixed(ilit) != 0 || val(ilit) == 0) return false;
        int idx = vidx(ilit);
        ref Var v = ref vtab[idx];
        if (v.level == 0 || v.reason != null) return false;
        return true;
    }

    /// <summary>'move_literals_to_watch' from 'external_propagate.cpp': moves the two best
    /// literals of 'clause' to the front (needed when adding clauses at non-zero level).</summary>
    public void move_literals_to_watch()
    {
        if (level == 0) return;
        if (clause.n < 2) return;
        for (int i = 0; i < 2; i++)
        {
            int highest_position = i;
            int highest_literal = clause[i];
            int highest_level = @var(highest_literal).level;
            sbyte highest_value = val(highest_literal);
            for (int j = i + 1; j < clause.n; j++)
            {
                int other = clause[j];
                int other_level = @var(other).level;
                sbyte other_value = val(other);
                if (other_value < 0)
                {
                    if (highest_value >= 0) continue;
                    if (other_level <= highest_level) continue;
                }
                else if (other_value > 0)
                {
                    if (highest_value > 0 && other_level >= highest_level) continue;
                }
                else
                {
                    if (highest_value >= 0) continue;
                }
                highest_position = j;
                highest_literal = other;
                highest_level = other_level;
                highest_value = other_value;
            }
            if (highest_position == i) continue;
            (clause[i], clause[highest_position]) = (clause[highest_position], clause[i]);
        }
    }

    /*------------------------------------------------------------------------*/
    // Main CDCL loop.

    public int cdcl_loop_with_inprocessing()
    {
        int res = 0;
        if (stable) report('['); else report('{');
        while (res == 0)
        {
            if (unsat) res = 20;
            else if (unsat_constraint) res = 20;
            else if (!propagate()) analyze();
            else if (iterating) iterate();
            else if (!external_propagate() || unsat)
            {
                if (unsat) continue;
                else analyze();
            }
            else if (satisfied())
            {
                if (!external_check_solution() || unsat)
                {
                    if (unsat) continue;
                    else analyze();
                }
                else if (satisfied()) res = 10;
            }
            else if (search_limits_hit()) break;
            else if (terminated_asynchronously()) break;
            else if (restarting()) restart();
            else if (rephasing()) rephase();
            else if (reducing()) reduce();
            else if (probing()) probe();
            else if (subsuming()) subsume();
            else if (eliminating()) elim();
            else if (compacting()) compact();
            else if (conditioning()) condition();
            else res = decide();
        }
        if (stable) report(']'); else report('}');
        return res;
    }

    public void init_report_limits()
    {
        reported = false;
        lim.report = 0;
    }

    public void init_preprocessing_limits()
    {
        bool incremental = lim.initialized;
        if (!incremental) lim.subsume = stats.conflicts + (long)scale(opts.subsumeint);
        if (!incremental)
        {
            last.elim.marked = -1;
            lim.elim = stats.conflicts + (long)scale(opts.elimint);
        }
        lim.elimbound = opts.elimboundmin;
        if (!incremental)
        {
            last.ternary.marked = -1;
            lim.compact = stats.conflicts + opts.compactint;
        }
        if (!incremental) lim.probe = stats.conflicts + opts.probeint;
        if (!incremental) lim.condition = stats.conflicts + opts.conditionint;
        if (inc.preprocessing <= 0) lim.preprocessing = 0;
        else lim.preprocessing = inc.preprocessing;
    }

    public void init_search_limits()
    {
        bool incremental = lim.initialized;
        if (!incremental)
        {
            last.reduce.conflicts = -1;
            lim.reduce = stats.conflicts + opts.reduceint;
        }
        if (!incremental)
        {
            lim.flush = opts.flushint;
            inc.flush = opts.flushint;
        }
        lim.rephase = stats.conflicts + opts.rephaseint;
        lim.rephased[0] = lim.rephased[1] = 0;
        lim.restart = stats.conflicts + opts.restartint;
        if (!incremental)
        {
            stable = opts.stabilize != 0 && opts.stabilizeonly != 0;
            init_averages();
        }
        else if (opts.stabilize != 0 && opts.stabilizeonly != 0) { }
        else if (stable)
        {
            stable = false;
            swap_averages();
        }
        inc.stabilize = opts.stabilizeint;
        lim.stabilize = stats.conflicts + inc.stabilize;
        if (opts.stabilize != 0 && opts.reluctant != 0)
            reluctant.enable(opts.reluctant, opts.reluctantmax);
        else
            reluctant.disable();
        if (inc.conflicts < 0) lim.conflicts = -1;
        else lim.conflicts = stats.conflicts + inc.conflicts;
        if (inc.decisions < 0) lim.decisions = -1;
        else lim.decisions = stats.decisions + inc.decisions;
        if (inc.localsearch <= 0) lim.localsearch = 0;
        else lim.localsearch = inc.localsearch;
        lim.initialized = true;
    }

    public bool preprocess_round(int round)
    {
        if (unsat) return false;
        if (max_var == 0) return false;
        long before_vars = active();
        long before_clauses = stats.current.irredundant;
        stats.preprocessings++;
        preprocessing = true;
        PHASE("preprocessing", stats.preprocessings,
              $"starting round {round} with {before_vars} variables and {before_clauses} clauses");
        long old_elimbound = lim.elimbound;
        if (opts.probe != 0) probe(false);
        if (opts.elim != 0) elim(false);
        if (opts.condition != 0) condition(false);
        long after_vars = active();
        long after_clauses = stats.current.irredundant;
        preprocessing = false;
        PHASE("preprocessing", stats.preprocessings,
              $"finished round {round} with {after_vars} variables and {after_clauses} clauses");
        report('P');
        if (unsat) return false;
        if (after_vars < before_vars) return true;
        if (old_elimbound < lim.elimbound) return true;
        return false;
    }

    public int preprocess()
    {
        for (int i = 0; i < lim.preprocessing; i++)
            if (!preprocess_round(i)) break;
        if (unsat) return 20;
        return 0;
    }

    public int try_to_satisfy_formula_by_saved_phases()
    {
        force_saved_phase = true;
        int res = 0;
        while (res == 0)
        {
            if (satisfied()) res = 10;
            else if (decide() != 0) res = 20;
            else if (!propagate())
            {
                backtrack();
                conflict = null;
                break;
            }
        }
        force_saved_phase = false;
        return res;
    }

    public void produce_failed_assumptions()
    {
        while (!unsat)
        {
            notify_assignments();
            if (decide() != 0) break;
            while (!unsat && !propagate()) analyze();
        }
        notify_assignments();
    }

    public int local_search_round(int round)
    {
        if (unsat) return 0;
        if (max_var == 0) return 0;
        localsearching = true;
        long limit = opts.walkmineff;
        limit *= round;
        if (long.MaxValue / round > limit) limit *= round;
        else limit = long.MaxValue;
        int res = walk_round(limit, true);
        localsearching = false;
        report('L');
        return res;
    }

    public int local_search()
    {
        if (unsat) return 0;
        if (max_var == 0) return 0;
        if (opts.walk == 0) return 0;
        if (constraint.n != 0) return 0;
        int res = 0;
        for (int i = 1; res == 0 && i <= lim.localsearch; i++) res = local_search_round(i);
        if (res == 10) res = try_to_satisfy_formula_by_saved_phases();
        else if (res == 20) produce_failed_assumptions();
        return res;
    }

    public int solve(bool preprocess_only)
    {
        double start = time();
        if (proof != null) proof.solve_query();
        if (opts.ilb != 0)
        {
            if (opts.ilbassumptions != 0) sort_and_reuse_assumptions();
            stats.ilbtriggers++;
            stats.ilbsuccess += level > 0 ? 1 : 0;
            stats.levelsreused += level;
            if (level != 0) stats.literalsreused += num_assigned - control[1].trail;
        }
        init_report_limits();
        int res = already_solved();
        if (res == 0 && preprocess_only && level != 0) backtrack();
        if (res == 0) res = restore_clauses();
        if (res == 0)
        {
            init_preprocessing_limits();
            if (!preprocess_only) init_search_limits();
        }
        if (res == 0 && level == 0) res = preprocess();
        if (!preprocess_only)
        {
            if (res == 0 && level == 0) res = local_search();
            if (res == 0 && level == 0) res = lucky_phases();
            if (res == 0 || (res == 10 && external_prop))
            {
                if (res == 10 && external_prop && level != 0) backtrack();
                res = cdcl_loop_with_inprocessing();
            }
        }
        finalize(res);
        reset_solving();
        report_solving(res);
        solve_time_accumulated += time() - start;
        return res;
    }

    public int already_solved()
    {
        int res = 0;
        if (unsat || unsat_constraint) res = 20;
        else
        {
            if (level != 0 && opts.ilb == 0) backtrack();
            if (level == 0 && !propagate())
            {
                learn_empty_clause();
                res = 20;
            }
            if (max_var == 0 && res == 0) res = 10;
        }
        return res;
    }

    public void report_solving(int res)
    {
        if (res == 10) report('1');
        else if (res == 20) report('0');
        else report('?');
    }

    public void reset_solving()
    {
        if (termination_forced) termination_forced = false;
    }

    public int restore_clauses()
    {
        int res = 0;
        if (opts.restoreall <= 1 && external.tainted.empty())
        {
            report('*');
        }
        else
        {
            report('+');
            external.restore_clauses();
            report('r');
            if (!unsat && level == 0 && !propagate())
            {
                learn_empty_clause();
                res = 20;
            }
        }
        return res;
    }

    public int lookahead()
    {
        lookingahead = true;
        int tmp = already_solved();
        if (tmp == 0) tmp = restore_clauses();
        int res = 0;
        if (tmp == 0) res = lookahead_probing();
        if (res == int.MinValue) res = 0;
        reset_solving();
        report_solving(tmp);
        lookingahead = false;
        return res;
    }

    public void finalize(int res)
    {
        if (proof == null) return;
        if (frat)
        {
            for (int evar = 1; evar <= external.max_var; evar++)
            {
                int eidx = 2 * evar;
                int sgn = 1;
                ulong id = external.ext_units[eidx];
                if (id == 0)
                {
                    sgn = -1;
                    id = external.ext_units[eidx + 1];
                }
                if (id != 0) proof.finalize_external_unit(id, evar * sgn);
            }
            for (int idx = 1; idx <= max_var; idx++)
            {
                for (int s = 0; s < 2; s++)
                {
                    int lit = s == 0 ? idx : -idx;
                    int elit = externalize(lit);
                    if (elit != 0)
                    {
                        uint eidx = (elit < 0 ? 1u : 0u) + 2u * (uint)Math.Abs(elit);
                        ulong eid = external.ext_units[eidx];
                        if (eid != 0) continue;
                    }
                    uint uidx = vlit(lit);
                    ulong uid = unit_clauses(uidx);
                    if (uid == 0) continue;
                    proof.finalize_unit(uid, lit);
                }
            }
            for (int k = 0; k < clauses.n; k++)
            {
                var c = clauses[k];
                if (!c.garbage || c.size == 2) proof.finalize_clause(c);
            }
            if (conflict_id != 0) proof.finalize_clause(conflict_id, new Vec<int>());
        }
        proof.report_status(res, conflict_id);
        if (res == 10) external.conclude_sat();
        else if (res == 20) conclude_unsat();
    }

    public void print_statistics()
    {
        print_stats();
        foreach (var st in stat_tracers) st.print_stats();
    }

    public bool traverse_constraint(Func<Vec<int>, bool> it)
    {
        if (constraint.empty() && !unsat_constraint) return true;
        var eclause = new Vec<int>();
        if (unsat) return it(eclause);
        bool satisfied_ = false;
        for (int k = 0; k < constraint.n; k++)
        {
            int ilit = constraint[k];
            int tmp = @fixed(ilit);
            if (tmp > 0) { satisfied_ = true; break; }
            if (tmp < 0) continue;
            eclause.push_back(externalize(ilit));
        }
        if (!satisfied_ && !it(eclause)) return false;
        return true;
    }

    public bool traverse_clauses(Func<Vec<int>, bool> it)
    {
        var eclause = new Vec<int>();
        if (unsat) return it(eclause);
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (c.garbage) continue;
            if (c.redundant) continue;
            bool satisfied_ = false;
            for (int i = 0; i < c.size; i++)
            {
                int ilit = c.literals[i];
                int tmp = @fixed(ilit);
                if (tmp > 0) { satisfied_ = true; break; }
                if (tmp < 0) continue;
                eclause.push_back(externalize(ilit));
            }
            if (!satisfied_ && !it(eclause)) return false;
            eclause.clear();
        }
        return true;
    }

    public void dump()
    {
        long m = assumptions.n;
        for (int idx = 1; idx <= max_var; idx++) if (@fixed(idx) != 0) m++;
        for (int k = 0; k < clauses.n; k++) if (!clauses[k].garbage) m++;
        output.Write($"p cnf {max_var} {m}\n");
        for (int idx = 1; idx <= max_var; idx++)
        {
            int tmp = @fixed(idx);
            if (tmp != 0) output.Write($"{(tmp < 0 ? -idx : idx)} 0\n");
        }
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (c.garbage) continue;
            for (int i = 0; i < c.size; i++) output.Write($"{c.literals[i]} ");
            output.Write("0\n");
        }
        for (int k = 0; k < assumptions.n; k++) output.Write($"{assumptions[k]} 0\n");
        output.Flush();
    }

    /*------------------------------------------------------------------------*/

    public double solve_time() => solve_time_accumulated;
    public double process_time() => Resources.absolute_process_time() - stats.time.process;
    public double real_time() => Resources.absolute_real_time() - stats.time.real;
    public double time() => opts.realtime != 0 ? real_time() : process_time();
}
