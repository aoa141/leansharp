// Port of 'walk.cpp' (ProbSAT style random walk local search).

namespace LeanSharp.Runtime.Cadical;

public sealed class Walker
{
    public CRandom random;       // local random number generator
    public long propagations;    // number of propagations
    public long limit;           // limit on number of propagations
    public readonly Vec<Clause> broken = new(); // currently unsatisfied clauses
    public double epsilon;       // smallest considered score
    public readonly Vec<double> table = new();  // break value to score table
    public readonly Vec<double> scores = new(); // scores of candidate literals

    // These are in essence the CB values from Adrian Balint's thesis.
    static readonly double[,] cbvals = {
        { 0.0, 2.00 }, { 3.0, 2.50 }, { 4.0, 2.85 }, { 5.0, 3.70 },
        { 6.0, 5.10 }, { 7.0, 7.40 },
    };

    static double fitcbval(double size)
    {
        int ncbvals = cbvals.GetLength(0);
        int i = 0;
        while (i + 2 < ncbvals && (cbvals[i, 0] > size || cbvals[i + 1, 0] < size)) i++;
        double x2 = cbvals[i + 1, 0], x1 = cbvals[i, 0];
        double y2 = cbvals[i + 1, 1], y1 = cbvals[i, 1];
        double dx = x2 - x1, dy = y2 - y1;
        return dy * (size - x1) / dx + y1;
    }

    public Walker(Internal internal_, double size, long l)
    {
        random = new CRandom((ulong)internal_.opts.seed);
        propagations = 0;
        limit = l;
        random.add((ulong)internal_.stats.walk.count);
        bool use_size_based_cb = (internal_.stats.walk.count & 1) != 0;
        double cb = use_size_based_cb ? fitcbval(size) : 2.0;
        double @base = 1 / cb;
        double next = 1;
        for (epsilon = next; next != 0; next = epsilon * @base) table.push_back(epsilon = next);
        internal_.PHASE("walk", internal_.stats.walk.count,
            $"CB {cb:F2} with inverse {@base:F2} as base and table size {table.n}");
    }

    public double score(uint i) => i < (uint)table.n ? table[(int)i] : epsilon;
}

public sealed unsafe partial class Internal
{
    Clause walk_pick_clause(Walker walker)
    {
        long size = walker.broken.n;
        if (size > int.MaxValue) size = int.MaxValue;
        int pos = walker.random.pick_int(0, (int)size - 1);
        return walker.broken[pos];
    }

    uint walk_break_value(int lit)
    {
        uint res = 0;
        var ws = watches(lit);
        var wa = ws.a;
        for (int k = 0; k < ws.n; k++)
        {
            ref Watch w = ref wa[k];
            if (val(w.blit) > 0) continue;
            if (w.size == 2)
            {
                res++;
                continue;
            }
            Clause c = w.clause;
            int[] lits = c.literals;
            int begin = 1;
            int end = c.size;
            int i = begin;
            int prev = 0;
            while (i != end)
            {
                int other = lits[i];
                lits[i++] = prev;
                prev = other;
                if (val(other) < 0) continue;
                w.blit = other;
                lits[begin] = other;
                break;
            }
            if (i != end) continue;
            while (i != begin)
            {
                int other = lits[--i];
                lits[i] = prev;
                prev = other;
            }
            res++;
        }
        return res;
    }

    int walk_pick_lit(Walker walker, Clause c)
    {
        double sum = 0;
        long propagations = 0;
        int[] lits = c.literals;
        int size = c.size;
        for (int k = 0; k < size; k++)
        {
            int lit = lits[k];
            if (@var(lit).level == 1) continue;
            propagations++;
            uint tmp = walk_break_value(-lit);
            double score = walker.score(tmp);
            walker.scores.push_back(score);
            sum += score;
        }
        walker.propagations += propagations;
        stats.propagations.walk += propagations;
        double lim_ = sum * walker.random.generate_double();
        int end = size;
        int i = 0;
        int j = 0;
        int res;
        for (; ; )
        {
            res = lits[i++];
            if (@var(res).level > 1) break;
        }
        sum = walker.scores[j++];
        while (sum <= lim_ && i != end)
        {
            res = lits[i++];
            if (@var(res).level == 1) continue;
            sum += walker.scores[j++];
        }
        walker.scores.clear();
        return res;
    }

    void walk_flip_lit(Walker walker, int lit)
    {
        int tmp = CUtil.sign(lit);
        int idx = Math.Abs(lit);
        set_val(idx, (sbyte)tmp);

        {
            double ratio = clause_variable_ratio();
            var broken = walker.broken;
            var ba = broken.a;
            int eou = broken.n;
            int j = 0, i = 0;
            long count = 0;
            while (i != eou)
            {
                Clause d = ba[j++] = ba[i++];
                int[] literals = d.literals;
                int prev = 0;
                int size = d.size;
                for (int k = 0; k < size; k++)
                {
                    int other = literals[k];
                    literals[k] = prev;
                    prev = other;
                    if (other == lit) break;
                }
                if (prev == lit)
                {
                    literals[0] = lit;
                    watch_literal(literals[0], literals[1], d);
                    j--;
                }
                else
                {
                    for (int k = size - 1; k >= 0; k--)
                    {
                        int other = literals[k];
                        literals[k] = prev;
                        prev = other;
                    }
                }
                if (count-- != 0) continue;
                count = (long)ratio;
                walker.propagations++;
                stats.propagations.walk++;
            }
            broken.shrink(j);
        }

        {
            walker.propagations++;
            stats.propagations.walk++;
            var ws = watches(-lit);
            var wa = ws.a;
            for (int k = 0; k < ws.n; k++)
            {
                Clause d = wa[k].clause;
                int[] literals = d.literals;
                int replacement = 0, prev = -lit;
                int size = d.size;
                for (int i = 1; i < size; i++)
                {
                    int other = literals[i];
                    literals[i] = prev;
                    prev = other;
                    sbyte t = val(other);
                    if (t < 0) continue;
                    replacement = other;
                    break;
                }
                if (replacement != 0)
                {
                    literals[1] = -lit;
                    literals[0] = replacement;
                    watch_literal(replacement, -lit, d);
                }
                else
                {
                    for (int i = size - 1; i > 0; i--)
                    {
                        int other = literals[i];
                        literals[i] = prev;
                        prev = other;
                    }
                    walker.broken.push_back(d);
                }
            }
            ws.clear();
        }
    }

    void walk_save_minimum(Walker walker)
    {
        long broken = walker.broken.n;
        if (broken >= stats.walk.minimum) return;
        VERBOSE(3, $"new global minimum {broken}");
        stats.walk.minimum = broken;
        for (int i = 1; i <= max_var; i++)
        {
            sbyte tmp = vals[i];
            if (tmp != 0) phases.min[i] = phases.saved[i] = tmp;
        }
    }

    public int walk_round(long limit, bool prev)
    {
        backtrack();
        if (propagated < trail.n && !propagate())
        {
            learn_empty_clause();
            return 20;
        }

        stats.walk.count++;

        clear_watches();

        if (last.collect.fixed_ < stats.all.fixed_) garbage_collection();

        if (localsearching) force_phase_messages = true;

        PHASE("walk", stats.walk.count, $"random walk limit of {limit} propagations");

        double size = 0;
        long n = 0;
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (c.garbage) continue;
            if (c.redundant)
            {
                if (opts.walkredundant == 0) continue;
                if (!likely_to_be_kept_clause(c)) continue;
            }
            size += c.size;
            n++;
        }
        double average_size = CUtil.relative(size, n);

        PHASE("walk", stats.walk.count,
              $"{n} clauses average size {average_size:F2} over {active()} variables");

        var walker = new Walker(this, average_size, limit);

        bool failed_ = false;

        level = 1;

        if (!assumptions.empty())
        {
            for (int k = 0; k < assumptions.n; k++)
            {
                int lit = assumptions[k];
                sbyte tmp = val(lit);
                if (tmp > 0) continue;
                if (tmp < 0)
                {
                    failed_ = true;
                    break;
                }
                if (!is_active(lit)) continue;
                tmp = (sbyte)CUtil.sign(lit);
                int idx = Math.Abs(lit);
                set_val(idx, tmp);
                @var(idx).level = 1;
            }
        }

        level = 2;

        if (!failed_)
        {
            for (int idx = 1; idx <= max_var; idx++)
            {
                if (!is_active(idx)) continue;
                if (vals[idx] != 0) continue;
                int tmp = 0;
                if (prev) tmp = phases.prev[idx];
                if (tmp == 0) tmp = CUtil.sign(decide_phase(idx, true));
                set_val(idx, (sbyte)tmp);
                @var(idx).level = 2;
            }

            for (int k = 0; k < clauses.n; k++)
            {
                var c = clauses[k];
                if (c.garbage) continue;
                if (c.redundant)
                {
                    if (opts.walkredundant == 0) continue;
                    if (!likely_to_be_kept_clause(c)) continue;
                }
                bool satisfiable = false;
                int satisfied_ = 0;
                int[] lits = c.literals;
                int csize = c.size;
                for (int i = 0; satisfied_ < 2 && i < csize; i++)
                {
                    int lit = lits[i];
                    if (val(lit) > 0)
                    {
                        (lits[satisfied_], lits[i]) = (lits[i], lits[satisfied_]);
                        satisfied_++;
                    }
                    else if (!satisfiable && @var(lit).level > 1)
                    {
                        satisfiable = true;
                    }
                }
                if (satisfied_ == 0 && !satisfiable)
                {
                    failed_ = true;
                    break;
                }
                if (satisfied_ != 0) watch_literal(lits[0], lits[1], c);
                else walker.broken.push_back(c);
            }
        }

        long old_global_minimum = stats.walk.minimum;

        int res;

        if (!failed_)
        {
            long broken = walker.broken.n;

            PHASE("walk", stats.walk.count,
                  $"starting with {broken} unsatisfied clauses ({CUtil.percent(broken, stats.current.irredundant):F0}% out of {stats.current.irredundant})");

            walk_save_minimum(walker);

            long minimum = broken;
            long flips = 0;
            double started = time();
            while (!terminated_asynchronously() && !walker.broken.empty() && walker.propagations < walker.limit)
            {
                flips++;
                stats.walk.flips++;
                stats.walk.broken += broken;
                Clause c = walk_pick_clause(walker);
                int lit = walk_pick_lit(walker, c);
                walk_flip_lit(walker, lit);
                broken = walker.broken.n;
                if (broken >= minimum) continue;
                minimum = broken;
                VERBOSE(3, $"new phase minimum {minimum} after {flips} flips");
                walk_save_minimum(walker);
            }

            if (minimum < old_global_minimum)
                PHASE("walk", stats.walk.count,
                      $"new global minimum {minimum} in {flips} flips and {walker.propagations} propagations");
            else
                PHASE("walk", stats.walk.count,
                      $"best phase minimum {minimum} in {flips} flips and {walker.propagations} propagations");

            double elapsed = time() - started;
            PHASE("walk", stats.walk.count,
                  $"{CUtil.relative(1e-6 * walker.propagations, elapsed):F2} million propagations per second");
            PHASE("walk", stats.walk.count,
                  $"{CUtil.relative(1e-3 * flips, elapsed):F2} thousand flips per second");

            if (minimum > 0) res = 0;
            else res = 10;
        }
        else
        {
            res = 20;
            PHASE("walk", stats.walk.count, "aborted due to inconsistent assumptions");
        }

        copy_phases(phases.prev);

        for (int idx = 1; idx <= max_var; idx++)
            if (is_active(idx)) set_val(idx, 0);

        level = 0;

        clear_watches();
        connect_watches();

        if (localsearching) force_phase_messages = false;

        return res;
    }

    public void walk()
    {
        set_mode(WALK);
        long limit = stats.propagations.search;
        limit = (long)(limit * (1e-3 * opts.walkreleff));
        if (limit < opts.walkmineff) limit = opts.walkmineff;
        if (limit > opts.walkmaxeff) limit = opts.walkmaxeff;
        walk_round(limit, false);
        reset_mode(WALK);
    }
}
