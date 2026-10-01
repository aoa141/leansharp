// Port of 'subsume.cpp' (global forward subsumption and strengthening).

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    public bool subsuming()
    {
        if (opts.subsume == 0 && opts.vivify == 0) return false;
        if (!preprocessing && opts.inprocessing == 0) return false;
        if (opts.reduce != 0 && stats.conflicts != last.reduce.conflicts) return false;
        if (stats.conflicts < lim.subsume) return false;
        return true;
    }

    // Checks whether 'subsuming' subsumes (result INT_MIN) or strengthens
    // (result is the negation of the removable literal) the marked clause.
    // As in C++ the literals of 'subsuming' are rotated as a side effect.
    int subsume_check(Clause subsuming, Clause subsumed)
    {
        stats.subchecks++;
        if (subsuming.size == 2) stats.subchecks2++;
        int flipped = 0, prev = 0;
        bool failed = false;
        int[] lits = subsuming.literals;
        int eoc = subsuming.size;
        for (int i = 0; !failed && i != eoc; i++)
        {
            int lit = lits[i];
            lits[i] = prev;
            prev = lit;
            int tmp = marked(lit);
            if (tmp == 0) failed = true;
            else if (tmp > 0) continue;
            else if (flipped != 0) failed = true;
            else flipped = lit;
        }
        lits[0] = prev;
        if (failed) return 0;
        if (flipped == 0) return int.MinValue;
        else if (opts.subsumestr == 0) return 0;
        else return flipped;
    }

    public void subsume_clause(Clause subsuming, Clause subsumed)
    {
        stats.subsumed++;
        if (subsumed.redundant) stats.subred++;
        else stats.subirr++;
        if (subsumed.redundant || !subsuming.redundant)
        {
            mark_garbage(subsumed);
            return;
        }
        subsuming.redundant = false;
        if (proof != null) proof.strengthen(subsuming.id);
        mark_garbage(subsumed);
        stats.current.irredundant++;
        stats.added.irredundant++;
        stats.irrlits += subsuming.size;
        stats.current.redundant--;
        stats.added.redundant--;
    }

    public void strengthen_clause(Clause c, int lit)
    {
        stats.strengthened++;
        if (proof != null) proof.strengthen_clause(c, lit, lrat_chain);
        if (!c.redundant) mark_removed(lit);
        // 'std::remove' of 'lit' (preserving order).
        int[] lits = c.literals;
        int j = 0;
        for (int i = 0; i < c.size; i++)
            if (lits[i] != lit) lits[j++] = lits[i];
        shrink_clause(c, c.size - 1);
        c.used = 0;
    }

    int try_to_subsume_clause(Clause c, Vec<Clause> shrunken)
    {
        stats.subtried++;
        mark(c);
        Clause d = null;
        int flipped = 0;
        int[] clits = c.literals;
        for (int k = 0; k < c.size; k++)
        {
            int lit = clits[k];
            if (!flags(lit).subsume) continue;
            for (int sign = -1; d == null && sign <= 1; sign += 2)
            {
                var bs = bins(sign * lit);
                for (int b = 0; b < bs.n; b++)
                {
                    ref Bin bin = ref bs.a[b];
                    int other = bin.lit;
                    int tmp = marked(other);
                    if (tmp == 0) continue;
                    if (tmp < 0 && sign < 0) continue;
                    if (tmp < 0)
                    {
                        if (sign < 0) continue;
                        dummy_binary.literals[0] = lit;
                        dummy_binary.literals[1] = other;
                        flipped = other;
                    }
                    else
                    {
                        dummy_binary.literals[0] = sign * lit;
                        dummy_binary.literals[1] = other;
                        flipped = (sign < 0) ? -lit : int.MinValue;
                    }
                    dummy_binary.id = bin.id;
                    d = dummy_binary;
                    break;
                }
                if (d != null) break;
                var os = occs(sign * lit);
                for (int q = 0; q < os.n; q++)
                {
                    Clause e = os.a[q];
                    if (e.garbage) continue;
                    flipped = subsume_check(e, c);
                    if (flipped == 0) continue;
                    d = e;
                    break;
                }
            }
            if (d != null) break;
        }
        unmark(c);
        if (flipped == int.MinValue)
        {
            subsume_clause(d, c);
            return 1;
        }
        if (flipped != 0)
        {
            if (lrat)
            {
                lrat_chain.push_back(c.id);
                lrat_chain.push_back(d.id);
            }
            strengthen_clause(c, -flipped);
            lrat_chain.clear();
            shrunken.push_back(c);
            return -1;
        }
        return 0;
    }

    struct ClauseSize
    {
        public long size;
        public Clause clause;
        public ClauseSize(int s, Clause c) { size = s; clause = c; }
    }

    int subsume_less_noccs(int a, int b)
    {
        if (a == b) return 0;
        sbyte u = val(a), v = val(b);
        if (u == 0 && v != 0) return -1;
        if (u != 0 && v == 0) return 1;
        long m = noccs(a), n = noccs(b);
        if (m < n) return -1;
        if (m > n) return 1;
        int aa = Math.Abs(a), ab = Math.Abs(b);
        if (aa < ab) return -1;
        if (aa > ab) return 1;
        return 0;
    }

    public bool subsume_round()
    {
        if (opts.subsume == 0) return false;
        if (unsat) return false;
        if (terminated_asynchronously()) return false;
        if (stats.current.redundant == 0 && stats.current.irredundant == 0) return false;

        set_mode(SUBSUME);
        stats.subsumerounds++;

        long check_limit;
        if (opts.subsumelimited != 0)
        {
            long delta = stats.propagations.search;
            delta = (long)(delta * (1e-3 * opts.subsumereleff));
            if (delta < opts.subsumemineff) delta = opts.subsumemineff;
            if (delta > opts.subsumemaxeff) delta = opts.subsumemaxeff;
            delta = Math.Max(delta, 2L * active());
            PHASE("subsume-round", stats.subsumerounds, $"limit of {delta} subsumption checks");
            check_limit = stats.subchecks + delta;
        }
        else
        {
            PHASE("subsume-round", stats.subsumerounds, "unlimited subsumption checks");
            check_limit = long.MaxValue;
        }

        long old_marked_candidate_variables_for_elimination = stats.mark.elim;

        var schedule = new Vec<ClauseSize>();
        init_noccs();

        long left_over_from_last_subsumption_round = 0;

        for (int k = 0; k < clauses.n; k++)
        {
            Clause c = clauses.a[k];
            if (c.garbage) continue;
            if (c.size > opts.subsumeclslim) continue;
            if (!likely_to_be_kept_clause(c)) continue;
            bool is_fixed = false;
            int subsume_count = 0;
            int[] lits = c.literals;
            for (int i = 0; i < c.size; i++)
            {
                int lit = lits[i];
                if (val(lit) != 0) is_fixed = true;
                else if (flags(lit).subsume) subsume_count++;
            }
            if (is_fixed) continue;
            if (subsume_count < 2) continue;
            if (c.subsume) left_over_from_last_subsumption_round++;
            schedule.push_back(new ClauseSize(c.size, c));
            for (int i = 0; i < c.size; i++) noccs(lits[i])++;
        }

        CUtil.rsort(schedule.AsSpan(), cs => (ulong)cs.size);

        if (left_over_from_last_subsumption_round == 0)
            for (int k = 0; k < schedule.n; k++)
                if (schedule.a[k].clause.size > 2) schedule.a[k].clause.subsume = true;

        long scheduled = schedule.n;
        long total = stats.current.irredundant + stats.current.redundant;
        PHASE("subsume-round", stats.subsumerounds,
              $"scheduled {scheduled} clauses {CUtil.percent(scheduled, total):F0}% out of {total} clauses");

        long subsumed = 0, strengthened = 0, checked_ = 0;

        var shrunken = new Vec<Clause>();
        init_occs();
        init_bins();

        for (int k = 0; k < schedule.n; k++)
        {
            if (terminated_asynchronously()) break;
            if (stats.subchecks >= check_limit) break;

            Clause c = schedule.a[k].clause;
            checked_++;

            if (c.size > 2 && c.subsume)
            {
                c.subsume = false;
                int tmp = try_to_subsume_clause(c, shrunken);
                if (tmp > 0)
                {
                    subsumed++;
                    continue;
                }
                if (tmp < 0) strengthened++;
            }

            int minlit = 0;
            long minoccs = 0;
            long minsize = 0;
            bool subsume_ = true;
            bool binary = c.size == 2 && !c.redundant;
            int[] lits = c.literals;
            for (int i = 0; i < c.size; i++)
            {
                int lit = lits[i];
                if (!flags(lit).subsume) subsume_ = false;
                long size = binary ? bins(lit).n : occs(lit).n;
                if (minlit != 0 && minsize <= size) continue;
                long tmp = noccs(lit);
                if (minlit != 0 && minsize == size && tmp <= minoccs) continue;
                minlit = lit; minsize = size; minoccs = tmp;
            }

            if (!subsume_) continue;

            if (!binary)
            {
                if (minsize > opts.subsumeocclim) continue;
                occs(minlit).push_back(c);
                new Span<int>(c.literals, 0, c.size).Sort(subsume_less_noccs);
            }
            else
            {
                if (minsize > opts.subsumebinlim) continue;
                int minlit_pos = c.literals[1] == minlit ? 1 : 0;
                int other = c.literals[minlit_pos == 0 ? 1 : 0];
                bins(minlit).push_back(new Bin { lit = other, id = c.id });
            }
        }

        PHASE("subsume-round", stats.subsumerounds,
              $"subsumed {subsumed} and strengthened {strengthened} out of {scheduled} clauses {CUtil.percent(subsumed + strengthened, scheduled):F0}%");

        long remain = schedule.n - checked_;
        bool completed = remain == 0;

        if (completed)
            PHASE("subsume-round", stats.subsumerounds, $"checked all {checked_} scheduled clauses");
        else
            PHASE("subsume-round", stats.subsumerounds,
                  $"checked {checked_} clauses {CUtil.percent(checked_, scheduled):F0}% of scheduled ({remain} remain)");

        schedule.erase();
        reset_noccs();
        reset_occs();
        reset_bins();

        if (completed) reset_subsume_bits();

        for (int k = 0; k < shrunken.n; k++) mark_added(shrunken.a[k]);
        shrunken.erase();

        report('s', (opts.reportall == 0 && (subsumed + strengthened) == 0) ? 1 : 0);

        reset_mode(SUBSUME);

        return old_marked_candidate_variables_for_elimination < stats.mark.elim;
    }

    public void subsume(bool update_limits = true)
    {
        stats.subsumephases++;

        if (stats.current.redundant == 0 && stats.current.irredundant == 0) goto UPDATE_LIMITS;

        if (unsat) return;

        backtrack();
        if (!propagate())
        {
            learn_empty_clause();
            return;
        }

        if (external_prop) private_steps = true;

        if (opts.subsume != 0)
        {
            reset_watches();
            subsume_round();
            init_watches();
            connect_watches();
            if (!unsat && !propagate())
            {
                learn_empty_clause();
            }
        }

        if (opts.vivify != 0) vivify();
        if (opts.transred != 0) transred();

        if (external_prop) private_steps = false;

    UPDATE_LIMITS:

        if (!update_limits) return;

        long delta = (long)scale(opts.subsumeint * (stats.subsumephases + 1));
        lim.subsume = stats.conflicts + delta;

        PHASE("subsume-phase", stats.subsumephases, $"new subsume limit {lim.subsume} after {delta} conflicts");
    }
}
