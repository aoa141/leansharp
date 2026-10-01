// Port of 'ternary.cpp' (hyper ternary resolution).

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    bool ternary_find_binary_clause(int a, int b)
    {
        int s = occs(a).n;
        int t = occs(b).n;
        int lit = s < t ? a : b;
        var os = occs(lit);
        if (opts.ternaryocclim < os.n) return true;
        for (int k = 0; k < os.n; k++)
        {
            Clause c = os.a[k];
            if (c.size != 2) continue;
            int[] lits = c.literals;
            if (lits[0] == a && lits[1] == b) return true;
            if (lits[0] == b && lits[1] == a) return true;
        }
        return false;
    }

    bool ternary_find_ternary_clause(int a, int b, int c)
    {
        int r = occs(a).n;
        int s = occs(b).n;
        int t = occs(c).n;
        int lit;
        if (r < s) lit = (t < r) ? c : a;
        else lit = (t < s) ? c : b;
        var os = occs(lit);
        if (opts.ternaryocclim < os.n) return true;
        for (int k = 0; k < os.n; k++)
        {
            Clause d = os.a[k];
            int[] lits = d.literals;
            if (d.size == 2)
            {
                if (lits[0] == a && lits[1] == b) return true;
                if (lits[0] == b && lits[1] == a) return true;
                if (lits[0] == a && lits[1] == c) return true;
                if (lits[0] == c && lits[1] == a) return true;
                if (lits[0] == b && lits[1] == c) return true;
                if (lits[0] == c && lits[1] == b) return true;
            }
            else
            {
                if (lits[0] == a && lits[1] == b && lits[2] == c) return true;
                if (lits[0] == a && lits[1] == c && lits[2] == b) return true;
                if (lits[0] == b && lits[1] == a && lits[2] == c) return true;
                if (lits[0] == b && lits[1] == c && lits[2] == a) return true;
                if (lits[0] == c && lits[1] == a && lits[2] == b) return true;
                if (lits[0] == c && lits[1] == b && lits[2] == a) return true;
            }
        }
        return false;
    }

    bool hyper_ternary_resolve(Clause c, int pivot, Clause d)
    {
        stats.ternres++;
        int[] cl = c.literals;
        for (int i = 0; i < c.size; i++)
            if (cl[i] != pivot) clause.push_back(cl[i]);
        int[] dl = d.literals;
        for (int i = 0; i < d.size; i++)
        {
            int lit = dl[i];
            if (lit == -pivot) continue;
            if (lit == clause[0]) continue;
            if (lit == -clause[0]) return false;
            if (lit == clause[1]) continue;
            if (lit == -clause[1]) return false;
            clause.push_back(lit);
        }
        int size = clause.n;
        if (size > 3) return false;
        if (size == 2 && ternary_find_binary_clause(clause[0], clause[1])) return false;
        if (size == 3 && ternary_find_ternary_clause(clause[0], clause[1], clause[2])) return false;
        return true;
    }

    void ternary_lit(int pivot, ref long steps, ref long htrs)
    {
        var pos_occs = occs(pivot);
        int pend = pos_occs.n;
        for (int pi = 0; pi < pend; pi++)
        {
            Clause c = pos_occs.a[pi];
            if (htrs < 0) break;
            if (c.garbage) continue;
            if (c.size != 3) continue;
            if (--steps < 0) break;
            bool assigned = false;
            for (int i = 0; i < c.size; i++)
                if (val(c.literals[i]) != 0)
                {
                    assigned = true;
                    break;
                }
            if (assigned) continue;
            var neg_occs = occs(-pivot);
            int nend = neg_occs.n;
            for (int ni = 0; ni < nend; ni++)
            {
                Clause d = neg_occs.a[ni];
                if (htrs < 0) break;
                if (d.garbage) continue;
                if (d.size != 3) continue;
                // As in the C++ code 'assigned' is not reset for each 'd'.
                for (int i = 0; i < d.size; i++)
                    if (val(d.literals[i]) != 0)
                    {
                        assigned = true;
                        break;
                    }
                if (assigned) continue;
                htrs--;
                if (hyper_ternary_resolve(c, pivot, d))
                {
                    int size = clause.n;
                    bool red = size == 3 || (c.redundant && d.redundant);
                    if (lrat)
                    {
                        lrat_chain.push_back(c.id);
                        lrat_chain.push_back(d.id);
                    }
                    Clause r = new_hyper_ternary_resolved_clause(red);
                    if (red) r.hyper = true;
                    lrat_chain.clear();
                    clause.clear();
                    stats.htrs++;
                    for (int i = 0; i < r.size; i++) occs(r.literals[i]).push_back(r);
                    if (size == 2)
                    {
                        mark_garbage(c);
                        mark_garbage(d);
                        stats.htrs2++;
                        break;
                    }
                    else
                    {
                        stats.htrs3++;
                    }
                }
                else
                {
                    clause.clear();
                }
            }
        }
    }

    void ternary_idx(int idx, ref long steps, ref long htrs)
    {
        if (!is_active(idx)) return;
        if (!flags(idx).ternary) return;
        int pos = occs(idx).n;
        int neg = occs(-idx).n;
        if (pos <= opts.ternaryocclim && neg <= opts.ternaryocclim)
        {
            int pivot = neg < pos ? -idx : idx;
            ternary_lit(pivot, ref steps, ref htrs);
        }
        flags(idx).ternary = false;
    }

    bool ternary_round(ref long steps_limit, ref long htrs_limit)
    {
        long bincon = 0;
        long terncon = 0;

        init_occs();

        for (int k = 0; k < clauses.n; k++)
        {
            Clause c = clauses.a[k];
            if (c.garbage) continue;
            if (c.size > 3) continue;
            bool assigned = false, marked_ = false;
            int[] lits = c.literals;
            for (int i = 0; i < c.size; i++)
            {
                int lit = lits[i];
                if (val(lit) != 0)
                {
                    assigned = true;
                    break;
                }
                if (flags(lit).ternary) marked_ = true;
            }
            if (assigned) continue;
            if (c.size == 2)
            {
                bincon++;
            }
            else
            {
                if (!marked_) continue;
                terncon++;
            }
            for (int i = 0; i < c.size; i++) occs(lits[i]).push_back(c);
        }

        PHASE("ternary", stats.ternary,
              $"connected {terncon} ternary {CUtil.percent(terncon, clauses.n):F0}% and {bincon} binary clauses {CUtil.percent(bincon, clauses.n):F0}%");

        for (int idx = 1; idx <= max_var; idx++)
        {
            if (terminated_asynchronously()) break;
            if (steps_limit < 0) break;
            if (htrs_limit < 0) break;
            ternary_idx(idx, ref steps_limit, ref htrs_limit);
        }

        int remain = 0;
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (!is_active(idx)) continue;
            if (!flags(idx).ternary) continue;
            remain++;
        }
        if (remain != 0)
            PHASE("ternary", stats.ternary, $"{remain} variables remain {CUtil.percent(remain, max_var):F0}%");
        else
            PHASE("ternary", stats.ternary, "completed hyper ternary resolution");

        reset_occs();
        return remain != 0;
    }

    public bool ternary()
    {
        if (opts.ternary == 0) return false;
        if (unsat) return false;
        if (terminated_asynchronously()) return false;
        if (last.ternary.marked == stats.mark.ternary) return false;

        set_mode(TERNARY);
        stats.ternary++;

        if (watching()) reset_watches();

        long steps_limit = stats.propagations.search;
        steps_limit = (long)(steps_limit * (1e-3 * opts.ternaryreleff));
        if (steps_limit < opts.ternarymineff) steps_limit = opts.ternarymineff;
        if (steps_limit > opts.ternarymaxeff) steps_limit = opts.ternarymaxeff;

        long htrs_limit = stats.current.redundant + stats.current.irredundant;
        htrs_limit *= opts.ternarymaxadd;
        htrs_limit /= 100;

        PHASE("ternary", stats.ternary,
              $"will run a maximum of {opts.ternaryrounds} rounds limited to {steps_limit} steps and {htrs_limit} clauses");

        bool resolved_binary_clause = false;
        bool completed = false;

        for (int round = 0; !terminated_asynchronously() && round < opts.ternaryrounds; round++)
        {
            if (htrs_limit < 0) break;
            if (steps_limit < 0) break;
            if (round != 0) stats.ternary++;
            int old_htrs2 = (int)stats.htrs2;
            int old_htrs3 = (int)stats.htrs3;
            completed = ternary_round(ref steps_limit, ref htrs_limit);
            int delta_htrs2 = (int)stats.htrs2 - old_htrs2;
            int delta_htrs3 = (int)stats.htrs3 - old_htrs3;
            PHASE("ternary", stats.ternary, $"derived {delta_htrs3} ternary and {delta_htrs2} binary resolvents");
            report('3', (opts.reportall == 0 && (delta_htrs2 + delta_htrs2) == 0) ? 1 : 0);
            if (delta_htrs2 != 0) resolved_binary_clause = true;
            if (delta_htrs3 == 0) break;
        }

        init_watches();
        connect_watches();
        if (!propagate())
        {
            learn_empty_clause();
        }

        if (completed) last.ternary.marked = stats.mark.ternary;

        reset_mode(TERNARY);

        return resolved_binary_clause;
    }
}
