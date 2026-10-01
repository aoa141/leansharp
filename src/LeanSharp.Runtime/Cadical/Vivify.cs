// Port of 'vivify.hpp' / 'vivify.cpp' (clause vivification with LRAT support).

namespace LeanSharp.Runtime.Cadical;

public struct VivifyLratItem
{
    public int lit;
    public Clause reason;
    public bool finished;
    public VivifyLratItem(int l, Clause r, bool f) { lit = l; reason = r; finished = f; }
}

public sealed class Vivifier
{
    public readonly Vec<Clause> schedule = new(), stack = new();
    public readonly Vec<int> sorted = new();
    public readonly bool redundant_mode;
    public readonly Vec<VivifyLratItem> lrat_stack = new();

    public Vivifier(bool mode) { redundant_mode = mode; }

    public void erase()
    {
        schedule.erase();
        sorted.erase();
        stack.erase();
    }
}

public sealed unsafe partial class Internal
{
    void vivify_assign(int lit, Clause reason)
    {
        int idx = vidx(lit);
        ref Var v = ref @var(idx);
        v.level = level;
        v.trail = trail.n;
        num_assigned++;
        v.reason = level != 0 ? reason : null;
        if (level == 0) learn_unit_clause(lit);
        sbyte tmp = (sbyte)CUtil.sign(lit);
        set_val(idx, tmp);
        trail.push_back(lit);
    }

    void vivify_assume(int lit)
    {
        level++;
        control.push_back(new Level(lit, trail.n));
        vivify_assign(lit, null);
    }

    bool vivify_propagate()
    {
        long before = propagated2 = propagated;
        for (; ; )
        {
            if (propagated2 != trail.n)
            {
                int lit = -trail[propagated2++];
                var ws = watches(lit);
                // 'vivify_assign' does not touch watch lists, so iterating by index is safe.
                for (int k = 0; k < ws.n; k++)
                {
                    Watch w = ws.a[k];
                    if (w.size != 2) continue;
                    sbyte b = val(w.blit);
                    if (b > 0) continue;
                    if (b < 0) conflict = w.clause;
                    else
                    {
                        build_chain_for_units(w.blit, w.clause, false);
                        vivify_assign(w.blit, w.clause);
                        lrat_chain.clear();
                    }
                }
            }
            else if (conflict == null && propagated != trail.n)
            {
                int lit = -trail[propagated++];
                var ws = watches(lit);
                Watch[] wa = ws.a;
                int eow = ws.n;
                int i = 0, j = 0;
                while (i != eow)
                {
                    Watch w = wa[j++] = wa[i++];
                    if (w.size == 2) continue;
                    if (val(w.blit) > 0) continue;
                    Clause c = w.clause;
                    if (c.garbage)
                    {
                        j--;
                        continue;
                    }
                    if (c == ignore) continue;
                    int[] lits = c.literals;
                    int other = lits[0] ^ lits[1] ^ lit;
                    sbyte u = val(other);
                    if (u > 0) wa[j - 1].blit = other;
                    else
                    {
                        int size = c.size;
                        int end = size;
                        int middle = c.pos;
                        int k = middle;
                        sbyte v = -1;
                        int r = 0;
                        while (k != end && (v = val(r = lits[k])) < 0) k++;
                        if (v < 0)
                        {
                            k = 2;
                            while (k != middle && (v = val(r = lits[k])) < 0) k++;
                        }
                        c.pos = k;
                        if (v > 0) wa[j - 1].blit = r;
                        else if (v == 0)
                        {
                            lits[0] = other;
                            lits[1] = r;
                            lits[k] = lit;
                            watch_literal(r, lit, c);
                            j--;
                        }
                        else if (u == 0)
                        {
                            vivify_chain_for_units(other, c);
                            vivify_assign(other, c);
                            lrat_chain.clear();
                        }
                        else
                        {
                            conflict = c;
                            break;
                        }
                    }
                }
                if (j != i)
                {
                    while (i != eow) wa[j++] = wa[i++];
                    ws.shrink(j);
                }
            }
            else break;
        }
        long delta = propagated2 - before;
        stats.propagations.vivify += delta;
        return conflict == null;
    }

    /*------------------------------------------------------------------------*/

    bool vivify_more_noccs(int a, int b)
    {
        long n = noccs(a);
        long m = noccs(b);
        if (n > m) return true;
        if (n < m) return false;
        if (a == -b) return a > 0;
        return Math.Abs(a) < Math.Abs(b);
    }

    int vivify_more_noccs_cmp(int a, int b)
    {
        if (vivify_more_noccs(a, b)) return -1;
        if (vivify_more_noccs(b, a)) return 1;
        return 0;
    }

    bool vivify_clause_later(Clause a, Clause b)
    {
        if (a == b) return false;
        if (!a.vivify && b.vivify) return true;
        if (a.vivify && !b.vivify) return false;
        if (a.redundant)
        {
            if (a.glue > b.glue) return true;
            if (a.glue < b.glue) return false;
        }
        if (a.size > b.size) return true;
        if (a.size < b.size) return false;
        int eoa = a.size, eob = b.size;
        int i = 0, j = 0;
        for (; i != eoa && j != eob; i++, j++)
            if (a.literals[i] != b.literals[j])
                return vivify_more_noccs(b.literals[j], a.literals[i]);
        return j == eob;
    }

    static bool vivify_flush_smaller(Clause a, Clause b)
    {
        int eoa = a.size, eob = b.size;
        int i = 0, j = 0;
        for (; i != eoa && j != eob; i++, j++)
            if (a.literals[i] != b.literals[j]) return a.literals[i] < b.literals[j];
        return j == eob && i != eoa;
    }

    void flush_vivification_schedule(Vivifier vivifier)
    {
        var schedule = vivifier.schedule;
        CUtil.stable_sort(schedule.AsSpan(), (a, b) =>
            vivify_flush_smaller(a, b) ? -1 : vivify_flush_smaller(b, a) ? 1 : 0);
        int end = schedule.n;
        int j = 0, i = 0;
        Clause prev = null;
        long subsumed = 0;
        var sa = schedule.a;
        for (; i != end; i++)
        {
            Clause c = sa[j++] = sa[i];
            if (prev == null || c.size < prev.size)
            {
                prev = c;
                continue;
            }
            int eop = prev.size;
            int k = 0;
            for (int l = 0; k != eop; k++, l++)
                if (prev.literals[k] != c.literals[l]) break;
            if (k == eop)
            {
                mark_garbage(c);
                subsumed++;
                j--;
            }
            else prev = c;
        }
        if (subsumed != 0)
            PHASE("vivify", stats.vivifications, $"flushed {subsumed} subsumed scheduled clauses");
        stats.vivifysubs += subsumed;
        if (subsumed != 0)
        {
            schedule.shrink(j);
            schedule.shrink_to_fit();
        }
    }

    bool consider_to_vivify_clause(Clause c, bool redundant_mode)
    {
        if (c.garbage) return false;
        if (c.redundant != redundant_mode) return false;
        if (opts.vivifyonce >= 1 && c.redundant && c.vivified) return false;
        if (opts.vivifyonce >= 2 && !c.redundant && c.vivified) return false;
        if (c.redundant && !likely_to_be_kept_clause(c)) return false;
        return true;
    }

    void vivify_analyze_redundant(Vivifier vivifier, Clause start, ref bool only_binary_reasons)
    {
        only_binary_reasons = true;
        var stack = vivifier.stack;
        stack.clear();
        stack.push_back(start);
        while (!stack.empty())
        {
            Clause c = stack.back();
            if (c.size > 2) only_binary_reasons = false;
            stack.pop_back();
            int[] lits = c.literals;
            for (int k = 0; k < c.size; k++)
            {
                int lit = lits[k];
                ref Var v = ref @var(lit);
                if (v.level == 0) continue;
                ref Flags f = ref flags(lit);
                if (f.seen) continue;
                f.seen = true;
                analyzed.push_back(lit);
                if (v.reason != null) stack.push_back(v.reason);
            }
        }
    }

    bool vivify_all_decisions(Clause c, int subsume)
    {
        int[] lits = c.literals;
        for (int k = 0; k < c.size; k++)
        {
            int other = lits[k];
            if (other == subsume) continue;
            if (val(other) >= 0) return false;
            ref Var v = ref @var(other);
            if (v.level == 0) continue;
            if (v.reason != null) return false;
            if (!flags(other).seen) return false;
        }
        return true;
    }

    void vivify_post_process_analysis(Clause c, int subsume)
    {
        if (vivify_all_decisions(c, subsume))
        {
            clause.clear();
        }
        else
        {
            int[] lits = c.literals;
            for (int k = 0; k < c.size; k++)
            {
                int other = lits[k];
                bool keep;
                if (other == subsume) keep = true;
                else if (val(other) >= 0) keep = false;
                else
                {
                    ref Var v = ref @var(other);
                    if (v.level == 0) keep = false;
                    else if (v.reason != null) keep = false;
                    else if (flags(other).seen) keep = true;
                    else keep = false;
                }
                if (keep) clause.push_back(other);
            }
        }
    }

    bool vivify_better_watch(int a, int b)
    {
        sbyte av = val(a), bv = val(b);
        if (av >= 0 && bv < 0) return true;
        if (av < 0 && bv >= 0) return false;
        return @var(a).trail > @var(b).trail;
    }

    void vivify_strengthen(Clause c)
    {
        stats.vivifystrs++;
        if (clause.n == 1)
        {
            backtrack();
            int unit = clause[0];
            assign_unit(unit);
            stats.vivifyunits++;
            bool ok = propagate();
            if (!ok) learn_empty_clause();
        }
        else
        {
            CUtil.stable_sort(clause.AsSpan(), (a, b) => vivify_better_watch(a, b) ? -1 : vivify_better_watch(b, a) ? 1 : 0);
            int new_level = level;
            int lit0 = clause[0];
            sbyte val0 = val(lit0);
            if (val0 < 0)
            {
                int level0 = @var(lit0).level;
                new_level = level0 - 1;
            }
            int lit1 = clause[1];
            sbyte val1 = val(lit1);
            if (val1 < 0 && !(val0 > 0 && @var(lit0).level <= @var(lit1).level))
            {
                int level1 = @var(lit1).level;
                new_level = level1 - 1;
            }
            if (new_level < level) backtrack(new_level);
            new_clause_as(c);
        }
        clause.clear();
        mark_garbage(c);
        lrat_chain.clear();
    }

    void vivify_clause(Vivifier vivifier, Clause c)
    {
        bool redundant_mode = vivifier.redundant_mode;

        c.vivify = false;
        c.vivified = true;

        if (c.garbage) return;

        var lrat_stack = vivifier.lrat_stack;

        int satisfied_ = 0;
        var sorted = vivifier.sorted;
        sorted.clear();

        int[] clits = c.literals;
        for (int k = 0; k < c.size; k++)
        {
            int lit = clits[k];
            int tmp = @fixed(lit);
            if (tmp > 0)
            {
                satisfied_ = lit;
                break;
            }
            else if (tmp == 0) sorted.push_back(lit);
        }

        if (satisfied_ != 0)
        {
            mark_garbage(c);
            return;
        }

        sorted.AsSpan().Sort(vivify_more_noccs_cmp);

        stats.vivifychecks++;

        if (level != 0)
        {
            int forced = 0;
            for (int k = 0; k < c.size; k++)
            {
                int lit = clits[k];
                sbyte tmp = val(lit);
                if (tmp < 0) continue;
                if (tmp > 0 && @var(lit).reason == c) forced = lit;
                break;
            }
            if (forced != 0) backtrack(@var(forced).level - 1);

            if (level != 0)
            {
                int l = 1;
                for (int k = 0; k < sorted.n; k++)
                {
                    int lit = sorted[k];
                    if (@fixed(lit) != 0) continue;
                    int decision = control[l].decision;
                    if (-lit == decision)
                    {
                        stats.vivifyreused++;
                        if (++l > level) break;
                    }
                    else
                    {
                        backtrack(l - 1);
                        break;
                    }
                }
            }
        }

        ignore = c;

        int subsume = 0;
        int remove = 0;

        bool only_binary_reasons = false;

        for (int k = 0; k < sorted.n; k++)
        {
            int lit = sorted[k];
            if (subsume != 0) break;

            sbyte tmp = val(lit);

            if (tmp != 0)
            {
                ref Var v = ref @var(lit);
                if (v.level == 0) continue;
                if (v.reason == null) continue;

                if (tmp > 0)
                {
                    subsume = lit;
                    if (redundant_mode)
                    {
                        flags(lit).seen = true;
                        analyzed.push_back(-lit);
                        Clause vreason = v.reason;
                        vivify_analyze_redundant(vivifier, vreason, ref only_binary_reasons);
                        if (!only_binary_reasons)
                        {
                            vivify_post_process_analysis(c, subsume);
                            if (!clause.empty()) stats.vivifystred2++;
                        }
                        clear_analyzed_literals();
                        if (lrat)
                        {
                            vivify_build_lrat(lit, vreason, lrat_stack);
                            clear_analyzed_literals();
                        }
                        backtrack(level - 1);
                        break;
                    }
                }
                else
                {
                    remove = lit;
                }
            }
            else
            {
                stats.vivifydecs++;
                vivify_assume(-lit);

                if (vivify_propagate()) continue;

                subsume = int.MinValue;

                if (redundant_mode)
                {
                    vivify_analyze_redundant(vivifier, conflict, ref only_binary_reasons);
                    if (!only_binary_reasons)
                    {
                        vivify_post_process_analysis(c, subsume);
                        if (!clause.empty()) stats.vivifystred3++;
                    }
                    clear_analyzed_literals();
                    if (lrat)
                    {
                        vivify_build_lrat(0, conflict, lrat_stack);
                        clear_analyzed_literals();
                    }
                }

                backtrack(level - 1);
                conflict = null;
                break;
            }
        }

        if (opts.vivifyinst != 0 && subsume == 0)
        {
            int lit = sorted.back();
            if (remove != lit)
            {
                backtrack(level - 1);
                stats.vivifydecs++;
                vivify_assume(lit);
                bool ok = vivify_propagate();
                if (!ok)
                {
                    stats.vivifyinst++;
                    if (lrat)
                    {
                        vivify_build_lrat(0, c, lrat_stack);
                        vivify_build_lrat(0, conflict, lrat_stack);
                        clear_analyzed_literals();
                    }
                    remove = lit;
                    backtrack(level - 1);
                    conflict = null;
                }
            }
        }

        ignore = null;

        if (subsume != 0)
        {
            if (redundant_mode && !only_binary_reasons)
            {
                if (!clause.empty())
                {
                    vivify_strengthen(c);
                }
                else
                {
                    if (c.redundant) { }
                    else c.vivify = true;
                }
            }
            else
            {
                stats.vivifysubs++;
                mark_garbage(c);
            }
        }
        else if (remove != 0)
        {
            for (int k = 0; k < c.size; k++)
            {
                int other = clits[k];
                ref Var v = ref @var(other);
                if (v.level == 0) continue;
                if (v.reason != null) continue;
                if (other == remove) continue;
                clause.push_back(other);
            }
            if (redundant_mode) stats.vivifystred1++;
            else stats.vivifystrirr++;
            if (lrat && lrat_chain.empty())
            {
                vivify_build_lrat(0, c, lrat_stack);
                clear_analyzed_literals();
            }
            vivify_strengthen(c);
        }
        lrat_chain.clear();
    }

    void vivify_build_lrat(int lit, Clause reason, Vec<VivifyLratItem> stack)
    {
        stack.push_back(new VivifyLratItem(lit, reason, false));
        while (!stack.empty())
        {
            var item = stack.back();
            int l = item.lit;
            Clause r = item.reason;
            bool finished = item.finished;
            stack.pop_back();
            if (l != 0 && flags(l).seen) continue;
            if (finished)
            {
                lrat_chain.push_back(r.id);
                if (l != 0 && r != null)
                {
                    ref Flags f = ref flags(l);
                    f.seen = true;
                    analyzed.push_back(l);
                }
                continue;
            }
            else stack.push_back(new VivifyLratItem(l, r, true));
            int[] lits = r.literals;
            for (int k = 0; k < r.size; k++)
            {
                int other = lits[k];
                if (other == l) continue;
                ref Var v = ref @var(other);
                ref Flags f = ref flags(other);
                if (f.seen) continue;
                if (v.level == 0)
                {
                    uint uidx = vlit(-other);
                    ulong id = unit_clauses(uidx);
                    lrat_chain.push_back(id);
                    f.seen = true;
                    analyzed.push_back(other);
                    continue;
                }
                if (v.reason != null)
                {
                    stack.push_back(new VivifyLratItem(other, v.reason, false));
                }
            }
        }
        stack.clear();
    }

    void vivify_chain_for_units(int lit, Clause reason)
    {
        if (!lrat) return;
        if (level != 0) return;
        int[] lits = reason.literals;
        for (int k = 0; k < reason.size; k++)
        {
            int reason_lit = lits[k];
            if (lit == reason_lit) continue;
            uint uidx = vlit(val(reason_lit) * reason_lit);
            ulong id = unit_clauses(uidx);
            lrat_chain.push_back(id);
        }
        lrat_chain.push_back(reason.id);
    }

    /*------------------------------------------------------------------------*/

    void vivify_round(bool redundant_mode, long propagation_limit)
    {
        if (unsat) return;
        if (terminated_asynchronously()) return;

        PHASE("vivify", stats.vivifications,
              $"starting {(redundant_mode ? "redundant" : "irredundant")} vivification round propagation limit {propagation_limit}");

        if (watching()) clear_watches();

        init_noccs();

        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (!consider_to_vivify_clause(c, redundant_mode)) continue;
            int shift = 12 - c.size;
            long score = shift < 1 ? 1 : (1L << shift);
            int[] lits = c.literals;
            for (int i = 0; i < c.size; i++) noccs(lits[i]) += score;
        }

        var vivifier = new Vivifier(redundant_mode);

        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (c.size == 2) continue;
            if (!consider_to_vivify_clause(c, redundant_mode)) continue;
            c.lits.Sort(vivify_more_noccs_cmp);
            vivifier.schedule.push_back(c);
        }
        vivifier.schedule.shrink_to_fit();

        flush_vivification_schedule(vivifier);

        CUtil.stable_sort(vivifier.schedule.AsSpan(), (a, b) =>
            vivify_clause_later(a, b) ? -1 : vivify_clause_later(b, a) ? 1 : 0);

        long @checked = stats.vivifychecks;
        long subsumed = stats.vivifysubs;
        long strengthened = stats.vivifystrs;
        long units = stats.vivifyunits;

        long scheduled = vivifier.schedule.n;
        stats.vivifysched += scheduled;

        PHASE("vivify", stats.vivifications,
              $"scheduled {scheduled} clauses to be vivified {CUtil.percent(scheduled, stats.current.irredundant):F0}%");

        long limit = stats.propagations.vivify + propagation_limit;

        connect_watches(!redundant_mode);

        propagated2 = propagated = 0;

        if (!unsat && !propagate())
        {
            learn_empty_clause();
        }

        while (!unsat && !terminated_asynchronously() && !vivifier.schedule.empty() &&
               stats.propagations.vivify < limit)
        {
            Clause c = vivifier.schedule.pop_back();
            vivify_clause(vivifier, c);
        }

        if (level != 0) backtrack();

        if (!unsat)
        {
            reset_noccs();

            long still_need_to_be_vivified = 0;
            for (int k = 0; k < vivifier.schedule.n; k++)
                if (vivifier.schedule[k].vivify) still_need_to_be_vivified++;

            if (still_need_to_be_vivified != 0)
                PHASE("vivify", stats.vivifications,
                      $"still need to vivify {still_need_to_be_vivified} clauses {CUtil.percent(still_need_to_be_vivified, scheduled):F2}% of {scheduled} scheduled");
            else
            {
                PHASE("vivify", stats.vivifications, "no previously not yet vivified clause left");
                for (int k = 0; k < vivifier.schedule.n; k++) vivifier.schedule[k].vivify = true;
            }

            vivifier.erase();
        }

        clear_watches();
        connect_watches();

        if (!unsat)
        {
            propagated2 = propagated = 0;
            if (!propagate())
            {
                learn_empty_clause();
            }
        }

        @checked = stats.vivifychecks - @checked;
        subsumed = stats.vivifysubs - subsumed;
        strengthened = stats.vivifystrs - strengthened;
        units = stats.vivifyunits - units;

        PHASE("vivify", stats.vivifications,
              $"checked {@checked} clauses {CUtil.percent(@checked, scheduled):F2}% of {scheduled} scheduled");
        if (units != 0)
            PHASE("vivify", stats.vivifications,
                  $"found {units} units {CUtil.percent(units, @checked):F2}% of {@checked} checked");
        if (subsumed != 0)
            PHASE("vivify", stats.vivifications,
                  $"subsumed {subsumed} clauses {CUtil.percent(subsumed, @checked):F2}% of {@checked} checked");
        if (strengthened != 0)
            PHASE("vivify", stats.vivifications,
                  $"strengthened {strengthened} clauses {CUtil.percent(strengthened, @checked):F2}% of {@checked} checked");

        stats.subsumed += subsumed;
        stats.strengthened += strengthened;

        last.vivify.propagations = stats.propagations.search;

        bool unsuccessful = (subsumed + strengthened + units) == 0;
        report(redundant_mode ? 'w' : 'v', unsuccessful ? 1 : 0);
    }

    public void vivify()
    {
        if (unsat) return;
        if (terminated_asynchronously()) return;
        if (stats.current.irredundant == 0) return;

        if (!preprocessing && !lookingahead) reset_mode(SEARCH);
        set_mode(SIMPLIFY);
        set_mode(VIVIFY);
        stats.vivifications++;

        long limit = stats.propagations.search;
        limit -= last.vivify.propagations;
        limit = (long)(limit * (1e-3 * opts.vivifyreleff));
        if (limit < opts.vivifymineff) limit = opts.vivifymineff;
        if (limit > opts.vivifymaxeff) limit = opts.vivifymaxeff;

        PHASE("vivify", stats.vivifications, $"vivification limit of twice {limit} propagations");

        vivify_round(false, limit);

        limit = (long)(limit * (1e-3 * opts.vivifyredeff));

        vivify_round(true, limit);

        reset_mode(VIVIFY);
        reset_mode(SIMPLIFY);
        if (!preprocessing && !lookingahead) set_mode(SEARCH);

        last.vivify.propagations = stats.propagations.search;
    }
}
