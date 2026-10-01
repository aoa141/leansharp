// Port of 'lookahead.cpp' (without 'generate_cubes', which Lean does not use).

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    struct literal_occ
    {
        public int lit;
        public int count;
    }

    public Vec<int> lookahead_populate_locc()
    {
        var loccs = new literal_occ[max_var + 1];
        for (int lit = 0; lit < loccs.Length; ++lit) loccs[lit].lit = lit;
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (c.redundant) continue;
            for (int i = 0; i < c.size; i++)
            {
                int lit = c.literals[i];
                if (is_active(lit)) ++loccs[Math.Abs(lit)].count;
            }
        }
        Array.Sort(loccs, (a, b) =>
        {
            if (a.count > b.count || (a.count == b.count && a.lit < b.lit)) return -1;
            if (b.count > a.count || (b.count == a.count && b.lit < a.lit)) return 1;
            return 0;
        });
        var locc_map = new Vec<int>(max_var);
        foreach (var locc in loccs) locc_map.push_back(locc.lit);
        return locc_map;
    }

    public int lookahead_locc(Vec<int> loccs)
    {
        for (int k = 0; k < loccs.n; k++)
        {
            int lit = loccs[k];
            if (is_active(Math.Abs(lit)) && !assumed(lit) && !assumed(-lit) && val(lit) == 0) return lit;
        }
        return 0;
    }

    public int most_occurring_literal()
    {
        init_noccs();
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (c.redundant) continue;
            for (int i = 0; i < c.size; i++)
            {
                int lit = c.literals[i];
                if (is_active(lit)) noccs(lit)++;
            }
        }
        long max_noccs = 0;
        int res = 0;

        if (unsat) return int.MinValue;

        propagate();
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (!is_active(idx) || assumed(idx) || assumed(-idx) || val(idx) != 0) continue;
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                int lit = sgn * idx;
                if (!is_active(lit)) continue;
                long tmp = noccs(lit);
                if (tmp <= max_noccs) continue;
                max_noccs = tmp;
                res = lit;
            }
        }
        MSG($"maximum occurrence {max_noccs} of literal {res}");
        reset_noccs();
        return res;
    }

    void lookahead_flush_probes()
    {
        count_binary_noccs();
        int eop = probes.n;
        int j = 0;
        for (int i = 0; i != eop; i++)
        {
            int lit = probes[i];
            if (!is_active(lit)) continue;
            bool have_pos_bin_occs = noccs(lit) > 0;
            bool have_neg_bin_occs = noccs(-lit) > 0;
            if (have_pos_bin_occs == have_neg_bin_occs) continue;
            if (have_pos_bin_occs) lit = -lit;
            if (propfixed(lit) >= stats.all.fixed_) continue;
            MSG($"keeping probe {lit} negated occs {noccs(-lit)}");
            probes[j++] = lit;
        }
        int remain = j;
        int flushed = probes.n - remain;
        probes.resize(remain);
        sort_probes_by_negated_noccs();
        reset_noccs();
        probes.shrink_to_fit();
        PHASE("probe-round", stats.probingrounds,
              $"flushed {flushed} literals {CUtil.percent(flushed, remain + flushed):F0}% remaining {remain}");
    }

    void lookahead_generate_probes()
    {
        count_binary_noccs();
        for (int idx = 1; idx <= max_var; idx++)
        {
            bool have_pos_bin_occs = noccs(idx) > 0;
            bool have_neg_bin_occs = noccs(-idx) > 0;
            if (have_pos_bin_occs)
            {
                int probe = -idx;
                if (propfixed(probe) >= stats.all.fixed_) continue;
                MSG($"scheduling probe {probe} negated occs {noccs(-probe)}");
                probes.push_back(probe);
            }
            if (have_neg_bin_occs)
            {
                int probe = idx;
                if (propfixed(probe) >= stats.all.fixed_) continue;
                MSG($"scheduling probe {probe} negated occs {noccs(-probe)}");
                probes.push_back(probe);
            }
        }
        sort_probes_by_negated_noccs();
        reset_noccs();
        probes.shrink_to_fit();
        PHASE("probe-round", stats.probingrounds,
              $"scheduled {probes.n} literals {CUtil.percent(probes.n, 2.0 * max_var):F0}%");
    }

    int lookahead_next_probe()
    {
        int generated = 0;
        for (; ; )
        {
            if (probes.empty())
            {
                if (generated++ != 0) return 0;
                lookahead_generate_probes();
            }
            while (!probes.empty())
            {
                int probe = probes.back();
                probes.pop_back();
                if (!is_active(probe) || assumed(probe) || assumed(-probe)) continue;
                if (propfixed(probe) >= stats.all.fixed_) continue;
                return probe;
            }
        }
    }

    public bool terminating_asked()
    {
        if (external.terminator != null && external.terminator())
        {
            MSG("connected terminator forces termination");
            return true;
        }
        if (termination_forced)
        {
            MSG("termination forced");
            return true;
        }
        return false;
    }

    public int lookahead_probing()
    {
        if (active() == 0) return 0;

        MSG($"lookahead-probe-round {stats.probingrounds} without propagations limit and {assumptions.n} assumptions");

        termination_forced = false;

        long old_failed = stats.failed;
        long old_probed = stats.probed;
        long old_hbrs = stats.hbrs;

        if (unsat) return int.MinValue;
        if (level != 0) backtrack();
        if (!propagate())
        {
            MSG("empty clause before probing");
            learn_empty_clause();
            return int.MinValue;
        }

        if (terminating_asked()) return most_occurring_literal();

        decompose();

        if (ternary()) decompose();

        mark_duplicated_binary_clauses_as_garbage();

        lim.conflicts = -1;

        if (!probes.empty()) lookahead_flush_probes();

        for (int idx = 1; idx <= max_var; idx++)
        {
            propfixed(idx) = -1;
            propfixed(-idx) = -1;
        }

        propagated = propagated2 = trail.n;

        int probe;
        int res = most_occurring_literal();
        int max_hbrs = -1;

        set_mode(PROBE);

        MSG($"unsat = {(unsat ? 1 : 0)}, terminating_asked () = {(terminating_asked() ? 1 : 0)} ");
        init_probehbr_lrat();
        while (!unsat && !terminating_asked() && (probe = lookahead_next_probe()) != 0)
        {
            stats.probed++;
            int hbrs;
            probe_assign_decision(probe);
            if (probe_propagate())
            {
                hbrs = trail.n;
                backtrack();
            }
            else
            {
                hbrs = 0;
                failed_literal(probe);
            }
            clean_probehbr_lrat();
            if (max_hbrs < hbrs || (max_hbrs == hbrs && bumped(probe) > bumped(res)))
            {
                res = probe;
                max_hbrs = hbrs;
            }
        }

        reset_mode(PROBE);

        if (unsat)
        {
            MSG("probing derived empty clause");
            res = int.MinValue;
        }
        else if (propagated < trail.n)
        {
            MSG($"probing produced {trail.n - propagated} units");
            if (!propagate())
            {
                MSG("propagating units after probing results in empty clause");
                learn_empty_clause();
                res = int.MinValue;
            }
            else sort_watches();
        }

        long failed = stats.failed - old_failed;
        long probed = stats.probed - old_probed;
        long hbrs_ = stats.hbrs - old_hbrs;

        MSG($"lookahead-probe-round {stats.probingrounds} probed {probed} and found {failed} failed literals");

        if (hbrs_ != 0)
            PHASE("lookahead-probe-round", stats.probingrounds, $"found {hbrs_} hyper binary resolvents");

        MSG($"lookahead literal {res} with {max_hbrs}\n");

        return res;
    }
}
