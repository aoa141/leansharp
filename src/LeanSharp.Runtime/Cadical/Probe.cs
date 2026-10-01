// Port of 'probe.cpp' (failed literal probing with on-the-fly hyper binary resolution).

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    public bool probing()
    {
        if (opts.probe == 0) return false;
        if (!preprocessing && opts.inprocessing == 0) return false;
        if (stats.probingphases != 0 && last.probe.reductions == stats.reductions) return false;
        return lim.probe <= stats.conflicts;
    }

    /*------------------------------------------------------------------------*/

    int get_parent_reason_literal(int lit)
    {
        int idx = vidx(lit);
        int res = parents[idx];
        if (lit < 0) res = -res;
        return res;
    }

    void set_parent_reason_literal(int lit, int reason)
    {
        int idx = vidx(lit);
        if (lit < 0) reason = -reason;
        parents[idx] = reason;
    }

    /*------------------------------------------------------------------------*/
    // LRAT chains for 'opts.probehbr == false'. The C++ code keeps a quadratic
    // table 'probehbr_chains[vlit (lit)][vlit (uip)]'; we use a sparse map with the
    // same semantics (missing entries are empty chains).

    readonly Dictionary<ulong, ulong[]> probehbr_chain_map = new();

    static ulong probehbr_key(uint a, uint b) => ((ulong)a << 32) | b;

    void clean_probehbr_lrat()
    {
        if (!lrat || opts.probehbr != 0) return;
        probehbr_chain_map.Clear();
    }

    void init_probehbr_lrat()
    {
        if (!lrat || opts.probehbr != 0) return;
        probehbr_chain_map.Clear();
    }

    void get_probehbr_lrat(int lit, int uip)
    {
        if (!lrat || opts.probehbr != 0) return;
        lrat_chain.clear();
        if (probehbr_chain_map.TryGetValue(probehbr_key(vlit(lit), vlit(uip)), out var chain))
            foreach (var id in chain) lrat_chain.push_back(id);
        lrat_chain.push_back(unit_clauses(vlit(-uip)));
    }

    void set_probehbr_lrat(int lit, int uip)
    {
        if (!lrat || opts.probehbr != 0) return;
        probehbr_chain_map[probehbr_key(vlit(lit), vlit(uip))] = lrat_chain.ToArray();
        lrat_chain.clear();
    }

    void probe_dominator_lrat(int dom, Clause reason)
    {
        if (!lrat || dom == 0) return;
        var lits = reason.literals;
        int size = reason.size;
        for (int i = 0; i < size; i++)
        {
            int lit = lits[i];
            if (val(lit) >= 0) continue;
            int other = -lit;
            if (other == dom) continue;
            ref Flags f = ref flags(other);
            if (f.seen) continue;
            f.seen = true;
            analyzed.push_back(other);
            Var u = @var(other);
            if (u.level != 0)
            {
                if (u.reason == null) continue;
                probe_dominator_lrat(dom, u.reason);
                continue;
            }
            uint uidx = vlit(other);
            ulong id = unit_clauses(uidx);
            lrat_chain.push_back(id);
        }
        lrat_chain.push_back(reason.id);
    }

    /*------------------------------------------------------------------------*/

    int probe_dominator(int a, int b)
    {
        int l = a, k = b;
        int ul = l, vk = k; // indices of the 'Var' references
        while (l != k)
        {
            if (@var(ul).trail > @var(vk).trail)
            {
                (l, k) = (k, l);
                (ul, vk) = (vk, ul);
            }
            if (get_parent_reason_literal(l) == 0) return l;
            int parent = get_parent_reason_literal(k);
            vk = k = parent;
        }
        return l;
    }

    int hyper_binary_resolve(Clause reason)
    {
        int end = reason.size;
        int[] lits = reason.literals;
        stats.hbrs++;
        stats.hbrsizes += reason.size;
        int lit = lits[1];
        int dom = -lit, non_root_level_literals = 0;
        for (int k = 2; k != end; k++)
        {
            int other = -lits[k];
            if (@var(other).level == 0) continue;
            dom = probe_dominator(dom, other);
            non_root_level_literals++;
        }
        probe_reason = reason;
        if (non_root_level_literals != 0 && opts.probehbr != 0)
        {
            bool contained = false;
            for (int k = 1; !contained && k != end; k++) contained = lits[k] == -dom;
            bool red = !contained || reason.redundant;
            if (red) stats.hbreds++;
            clause.push_back(-dom);
            clause.push_back(lits[0]);
            probe_dominator_lrat(dom, reason);
            if (lrat) clear_analyzed_literals();
            Clause c = new_hyper_binary_resolved_clause(red, 2);
            probe_reason = c;
            if (red) c.hyper = true;
            clause.clear();
            lrat_chain.clear();
            if (contained)
            {
                stats.hbrsubs++;
                mark_garbage(reason);
            }
        }
        else if (non_root_level_literals != 0 && lrat)
        {
            probe_dominator_lrat(dom, reason);
            clear_analyzed_literals();
            set_probehbr_lrat(dom, lits[0]);
        }
        return dom;
    }

    /*------------------------------------------------------------------------*/

    void probe_assign(int lit, int parent)
    {
        int idx = vidx(lit);
        ref Var v = ref @var(idx);
        v.level = level;
        v.trail = trail.n;
        num_assigned++;
        v.reason = level != 0 ? probe_reason : null;
        probe_reason = null;
        set_parent_reason_literal(lit, parent);
        if (level == 0) learn_unit_clause(lit);
        sbyte tmp = (sbyte)CUtil.sign(lit);
        set_val(idx, tmp);
        trail.push_back(lit);
        if (level != 0) propfixed(lit) = (int)stats.all.fixed_;
    }

    void probe_assign_decision(int lit)
    {
        level++;
        control.push_back(new Level(lit, trail.n));
        probe_assign(lit, 0);
    }

    void probe_assign_unit(int lit)
    {
        probe_assign(lit, 0);
    }

    /*------------------------------------------------------------------------*/

    void probe_lrat_for_units(int lit)
    {
        if (!lrat) return;
        if (level != 0) return;
        var reason = probe_reason;
        var lits = reason.literals;
        for (int i = 0; i < reason.size; i++)
        {
            int reason_lit = lits[i];
            if (lit == reason_lit) continue;
            if (val(reason_lit) == 0) continue;
            uint uidx = vlit(val(reason_lit) * reason_lit);
            ulong id = unit_clauses(uidx);
            lrat_chain.push_back(id);
        }
        lrat_chain.push_back(reason.id);
    }

    /*------------------------------------------------------------------------*/

    void probe_propagate2()
    {
        while (propagated2 != trail.n)
        {
            int lit = -trail[propagated2++];
            var ws = watches(lit);
            // 'probe_assign' does not change watch lists.
            for (int i = 0; i < ws.n; i++)
            {
                Watch w = ws.a[i];
                if (w.size != 2) continue;
                sbyte b = val(w.blit);
                if (b > 0) continue;
                if (b < 0) conflict = w.clause;
                else
                {
                    probe_reason = w.clause;
                    probe_lrat_for_units(w.blit);
                    probe_assign(w.blit, -lit);
                    lrat_chain.clear();
                }
            }
        }
    }

    bool probe_propagate()
    {
        long before = propagated2 = propagated;
        while (conflict == null)
        {
            if (propagated2 != trail.n) probe_propagate2();
            else if (propagated != trail.n)
            {
                int lit = -trail[propagated++];
                var ws = watches(lit);
                // Hyper binary resolution may add watches to 'ws' (indices are used).
                int i = 0, j = 0;
                while (i != ws.n)
                {
                    Watch w = ws[j++] = ws[i++];
                    if (w.size == 2) continue;
                    sbyte b = val(w.blit);
                    if (b > 0) continue;
                    Clause c = w.clause;
                    if (c.garbage) continue;
                    int[] lits = c.literals;
                    int other = lits[0] ^ lits[1] ^ lit;
                    sbyte u = val(other);
                    if (u > 0) ws[j - 1].blit = other;
                    else
                    {
                        int size = c.size;
                        int middle = c.pos;
                        int k = middle;
                        int r = 0;
                        sbyte v = -1;
                        while (k != size && (v = val(r = lits[k])) < 0) k++;
                        if (v < 0)
                        {
                            k = 2;
                            while (k != middle && (v = val(r = lits[k])) < 0) k++;
                        }
                        c.pos = k;
                        if (v > 0) ws[j - 1].blit = r;
                        else if (v == 0)
                        {
                            lits[k] = lit;
                            lits[0] = other;
                            lits[1] = r;
                            watch_literal(r, lit, c);
                            j--;
                        }
                        else if (u == 0)
                        {
                            if (level == 1)
                            {
                                lits[0] = other;
                                lits[1] = lit;
                                int dom = hyper_binary_resolve(c);
                                probe_assign(other, dom);
                            }
                            else
                            {
                                probe_reason = c;
                                probe_lrat_for_units(other);
                                probe_assign_unit(other);
                                lrat_chain.clear();
                            }
                            probe_propagate2();
                        }
                        else conflict = c;
                    }
                }
                if (j != i)
                {
                    while (i != ws.n) ws[j++] = ws[i++];
                    ws.shrink(j);
                }
            }
            else break;
        }
        long delta = propagated2 - before;
        stats.propagations.probe += delta;
        return conflict == null;
    }

    /*------------------------------------------------------------------------*/

    void failed_literal(int failed)
    {
        stats.failed++;
        stats.probefailed++;

        int uip = 0;
        {
            var lits = conflict.literals;
            for (int i = 0; i < conflict.size; i++)
            {
                int other = -lits[i];
                if (@var(other).level == 0) continue;
                uip = uip != 0 ? probe_dominator(uip, other) : other;
            }
        }
        probe_dominator_lrat(uip, conflict);
        if (lrat) clear_analyzed_literals();

        var work = new Vec<int>();
        int parent = uip;
        while (parent != failed)
        {
            int next = get_parent_reason_literal(parent);
            parent = next;
            work.push_back(parent);
        }

        backtrack();
        conflict = null;

        probe_assign_unit(-uip);
        lrat_chain.clear();

        if (!probe_propagate()) learn_empty_clause();

        int j = 0;
        while (!unsat && j < work.n)
        {
            int p = work[j++];
            sbyte tmp = val(p);
            if (tmp > 0)
            {
                get_probehbr_lrat(p, uip);
                learn_empty_clause();
            }
            else if (tmp == 0)
            {
                get_probehbr_lrat(p, uip);
                probe_assign_unit(-p);
                lrat_chain.clear();
                if (!probe_propagate()) learn_empty_clause();
            }
            uip = p;
        }
    }

    /*------------------------------------------------------------------------*/

    public bool is_binary_clause(Clause c, out int a, out int b)
    {
        a = b = 0;
        if (c.garbage) return false;
        int first = 0, second = 0;
        var lits = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int lit = lits[i];
            sbyte tmp = val(lit);
            if (tmp > 0) return false;
            if (tmp < 0) continue;
            if (second != 0) return false;
            if (first != 0) second = lit;
            else first = lit;
        }
        if (second == 0) return false;
        a = first; b = second;
        return true;
    }

    void count_binary_noccs()
    {
        init_noccs();
        for (int k = 0; k < clauses.n; k++)
        {
            if (!is_binary_clause(clauses[k], out int a, out int b)) continue;
            noccs(a)++;
            noccs(b)++;
        }
    }

    void sort_probes_by_negated_noccs()
    {
        var nt = ntab;
        CUtil.rsort(probes.AsSpan(), a => (ulong)nt[vlit(-a)]);
    }

    void generate_probes()
    {
        count_binary_noccs();
        for (int idx = 1; idx <= max_var; idx++)
        {
            bool have_pos_bin_occs = noccs(idx) > 0;
            bool have_neg_bin_occs = noccs(-idx) > 0;
            if (have_pos_bin_occs == have_neg_bin_occs) continue;
            int probe = have_neg_bin_occs ? idx : -idx;
            if (propfixed(probe) >= stats.all.fixed_) continue;
            probes.push_back(probe);
        }
        sort_probes_by_negated_noccs();
        reset_noccs();
        probes.shrink_to_fit();
        PHASE("probe-round", stats.probingrounds,
              $"scheduled {probes.n} literals {CUtil.percent(probes.n, 2.0 * max_var):F0}%");
    }

    void flush_probes()
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

    int next_probe()
    {
        int generated = 0;
        for (; ; )
        {
            if (probes.empty())
            {
                if (generated++ != 0) return 0;
                generate_probes();
            }
            while (!probes.empty())
            {
                int probe = probes.back();
                probes.pop_back();
                if (!is_active(probe)) continue;
                if (propfixed(probe) >= stats.all.fixed_) continue;
                return probe;
            }
        }
    }

    void START_SIMPLIFIER_G4(int m)
    {
        if (!preprocessing && !lookingahead) reset_mode(SEARCH);
        set_mode(SIMPLIFY);
        set_mode(m);
    }

    void STOP_SIMPLIFIER_G4(int m)
    {
        reset_mode(m);
        reset_mode(SIMPLIFY);
        if (!preprocessing && !lookingahead) set_mode(SEARCH);
    }

    bool probe_round()
    {
        if (unsat) return false;
        if (terminated_asynchronously()) return false;

        START_SIMPLIFIER_G4(PROBE);
        stats.probingrounds++;

        long delta = stats.propagations.search;
        delta -= last.probe.propagations;
        delta = (long)(delta * (1e-3 * opts.probereleff));
        if (delta < opts.probemineff) delta = opts.probemineff;
        if (delta > opts.probemaxeff) delta = opts.probemaxeff;
        delta += 2L * active();

        PHASE("probe-round", stats.probingrounds, $"probing limit of {delta} propagations ");

        long limit = stats.propagations.probe + delta;

        long old_failed = stats.failed;
        long old_probed = stats.probed;
        long old_hbrs = stats.hbrs;

        if (!probes.empty()) flush_probes();

        for (int idx = 1; idx <= max_var; idx++)
        {
            propfixed(idx) = -1;
            propfixed(-idx) = -1;
        }

        propagated = propagated2 = trail.n;

        int probe;
        init_probehbr_lrat();
        while (!unsat && !terminated_asynchronously() && stats.propagations.probe < limit &&
               (probe = next_probe()) != 0)
        {
            stats.probed++;
            probe_assign_decision(probe);
            if (probe_propagate()) backtrack();
            else failed_literal(probe);
            clean_probehbr_lrat();
        }

        if (unsat) { }
        else if (propagated < trail.n)
        {
            if (!propagate()) learn_empty_clause();
            else sort_watches();
        }

        long failed = stats.failed - old_failed;
        long probed = stats.probed - old_probed;
        long hbrs = stats.hbrs - old_hbrs;

        PHASE("probe-round", stats.probingrounds, $"probed {probed} and found {failed} failed literals");
        if (hbrs != 0) PHASE("probe-round", stats.probingrounds, $"found {hbrs} hyper binary resolvents");

        STOP_SIMPLIFIER_G4(PROBE);

        report('p', (opts.reportall == 0 && (unsat ? 1 : 0) + failed + hbrs == 0) ? 1 : 0);

        return !unsat && failed != 0;
    }

    /*------------------------------------------------------------------------*/

    public void probe(bool update_limits = true)
    {
        if (unsat) return;
        if (level != 0) backtrack();
        if (!propagate())
        {
            learn_empty_clause();
            return;
        }

        stats.probingphases++;
        int before = active();

        decompose();

        if (ternary()) decompose();

        mark_duplicated_binary_clauses_as_garbage();

        for (int round = 1; round <= opts.proberounds; round++)
            if (!probe_round()) break;

        decompose();

        last.probe.propagations = stats.propagations.search;

        if (!update_limits) return;

        int after = active();
        int removed = before - after;

        if (removed != 0)
        {
            stats.probesuccess++;
            PHASE("probe-phase", stats.probingphases,
                  $"successfully removed {removed} active variables {CUtil.percent(removed, before):F0}%");
        }
        else
            PHASE("probe-phase", stats.probingphases, "could not remove any active variable");

        long delta = opts.probeint * (stats.probingphases + 1);
        lim.probe = stats.conflicts + delta;

        PHASE("probe-phase", stats.probingphases, $"new limit at {lim.probe} conflicts after {delta} conflicts");

        last.probe.reductions = stats.reductions;
    }
}
