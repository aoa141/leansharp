// Port of 'elim.hpp' and 'elim.cpp' (bounded variable elimination).

namespace LeanSharp.Runtime.Cadical;

public sealed class Eliminator
{
    readonly Internal internal_;
    public readonly Heap schedule;
    public readonly Queue<Clause> backward = new();
    public readonly Vec<Clause> gates = new();
    public readonly Vec<int> marked = new();

    public Eliminator(Internal i)
    {
        internal_ = i;
        schedule = new Heap(i.elim_more);
    }

    // 'backward.cpp'
    public void enqueue(Clause c)
    {
        if (internal_.opts.elimbackward == 0) return;
        if (c.enqueued) return;
        backward.Enqueue(c);
        c.enqueued = true;
    }

    public Clause dequeue()
    {
        if (backward.Count == 0) return null;
        Clause res = backward.Dequeue();
        res.enqueued = false;
        return res;
    }

    /// <summary>Destructor of 'Eliminator' in C++.</summary>
    public void dispose()
    {
        while (dequeue() != null) { }
    }
}

public sealed unsafe partial class Internal
{
    public double compute_elim_score(uint lit)
    {
        uint uidx = 2 * lit;
        double pos = ntab[uidx];
        double neg = ntab[uidx + 1];
        if (pos == 0) return -neg;
        if (neg == 0) return -pos;
        double sum = 0, prod = 0;
        if (opts.elimsum != 0) sum = opts.elimsum * (pos + neg);
        if (opts.elimprod != 0) prod = opts.elimprod * (pos * neg);
        return prod + sum;
    }

    public bool elim_more(uint a, uint b)
    {
        double s = compute_elim_score(a);
        double t = compute_elim_score(b);
        if (s > t) return true;
        if (s < t) return false;
        return a > b;
    }

    public bool eliminating()
    {
        if (opts.elim == 0) return false;
        if (!preprocessing && opts.inprocessing == 0) return false;
        if (lim.elim >= stats.conflicts) return false;
        if (last.elim.fixed_ < stats.all.fixed_) return true;
        if (last.elim.marked < stats.mark.elim) return true;
        return false;
    }

    public void elim_update_added_clause(Eliminator eliminator, Clause c)
    {
        var schedule = eliminator.schedule;
        var lits = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int lit = lits[i];
            if (!is_active(lit)) continue;
            occs(lit).push_back(c);
            if (frozen(lit)) continue;
            noccs(lit)++;
            int idx = Math.Abs(lit);
            if (schedule.contains((uint)idx)) schedule.update((uint)idx);
        }
    }

    public void elim_update_removed_lit(Eliminator eliminator, int lit)
    {
        if (!is_active(lit)) return;
        if (frozen(lit)) return;
        ref long score_ = ref noccs(lit);
        score_--;
        int idx = Math.Abs(lit);
        var schedule = eliminator.schedule;
        if (schedule.contains((uint)idx)) schedule.update((uint)idx);
        else schedule.push_back((uint)idx);
    }

    public void elim_update_removed_clause(Eliminator eliminator, Clause c, int except = 0)
    {
        var lits = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int lit = lits[i];
            if (lit == except) continue;
            elim_update_removed_lit(eliminator, lit);
        }
    }

    public void elim_propagate(Eliminator eliminator, int root)
    {
        var work = new Vec<int>();
        int i = 0;
        work.push_back(root);
        while (i < work.n)
        {
            int lit = work[i++];
            var ns = occs(-lit);
            for (int k = 0; k < ns.n; k++)
            {
                Clause c = ns[k];
                if (c.garbage) continue;
                int unit = 0, satisfied_ = 0;
                var cl = c.literals;
                for (int q = 0; q < c.size; q++)
                {
                    int other = cl[q];
                    sbyte tmp = val(other);
                    if (tmp < 0) continue;
                    if (tmp > 0)
                    {
                        satisfied_ = other;
                        break;
                    }
                    if (unit != 0) unit = int.MinValue;
                    else unit = other;
                }
                if (satisfied_ != 0)
                {
                    elim_update_removed_clause(eliminator, c, satisfied_);
                    mark_garbage(c);
                }
                else if (unit == 0)
                {
                    conflict = c;
                    learn_empty_clause();
                    conflict = null;
                    break;
                }
                else if (unit != int.MinValue)
                {
                    build_chain_for_units(unit, c, false);
                    assign_unit(unit);
                    work.push_back(unit);
                }
            }
            if (unsat) break;
            var ps = occs(lit);
            for (int k = 0; k < ps.n; k++)
            {
                Clause c = ps[k];
                if (c.garbage) continue;
                elim_update_removed_clause(eliminator, c, lit);
                mark_garbage(c);
            }
        }
    }

    public void elim_on_the_fly_self_subsumption(Eliminator eliminator, Clause c, int pivot)
    {
        stats.elimotfstr++;
        stats.strengthened++;
        var lits = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int lit = lits[i];
            if (lit == pivot) continue;
            sbyte tmp = val(lit);
            if (tmp < 0) continue;
            clause.push_back(lit);
        }
        Clause r = new_resolved_irredundant_clause();
        elim_update_added_clause(eliminator, r);
        clause.clear();
        lrat_chain.clear();
        elim_update_removed_clause(eliminator, c, pivot);
        mark_garbage(c);
    }

    public bool resolve_clauses(Eliminator eliminator, Clause c, int pivot, Clause d, bool propagate_eagerly)
    {
        stats.elimres++;
        if (c.garbage || d.garbage) return false;
        if (c.size > d.size)
        {
            pivot = -pivot;
            (c, d) = (d, c);
        }

        int satisfied_ = 0;
        int tautological = 0;
        int s = 0;
        int t = 0;

        var clits = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int lit = clits[i];
            if (lit == pivot)
            {
                s++;
                continue;
            }
            sbyte tmp = val(lit);
            if (tmp > 0)
            {
                satisfied_ = lit;
                break;
            }
            else if (tmp < 0)
            {
                if (!lrat) continue;
                ref Flags f = ref flags(lit);
                if (f.seen) continue;
                analyzed.push_back(lit);
                f.seen = true;
                uint uidx = vlit(-lit);
                ulong id = unit_clauses(uidx);
                lrat_chain.push_back(id);
                continue;
            }
            else
            {
                mark(lit);
                clause.push_back(lit);
                s++;
            }
        }
        if (satisfied_ != 0)
        {
            elim_update_removed_clause(eliminator, c, satisfied_);
            mark_garbage(c);
            clause.clear();
            lrat_chain.clear();
            clear_analyzed_literals();
            unmark(c);
            return false;
        }

        var dlits = d.literals;
        for (int i = 0; i < d.size; i++)
        {
            int lit = dlits[i];
            if (lit == -pivot)
            {
                t++;
                continue;
            }
            int tmp = val(lit);
            if (tmp > 0)
            {
                satisfied_ = lit;
                break;
            }
            else if (tmp < 0)
            {
                if (!lrat) continue;
                ref Flags f = ref flags(lit);
                if (f.seen) continue;
                analyzed.push_back(lit);
                f.seen = true;
                uint uidx = vlit(-lit);
                ulong id = unit_clauses(uidx);
                lrat_chain.push_back(id);
                continue;
            }
            else if ((tmp = marked(lit)) < 0)
            {
                tautological = lit;
                break;
            }
            else if (tmp == 0)
            {
                clause.push_back(lit);
                t++;
            }
            else t++;
        }

        clear_analyzed_literals();
        unmark(c);
        long size = clause.n;

        if (lrat)
        {
            lrat_chain.push_back(d.id);
            lrat_chain.push_back(c.id);
        }

        if (satisfied_ != 0)
        {
            elim_update_removed_clause(eliminator, d, satisfied_);
            mark_garbage(d);
            clause.clear();
            lrat_chain.clear();
            return false;
        }

        if (tautological != 0)
        {
            clause.clear();
            lrat_chain.clear();
            return false;
        }

        if (size == 0)
        {
            clause.clear();
            learn_empty_clause();
            return false;
        }

        if (size == 1)
        {
            int unit = clause[0];
            clause.clear();
            assign_unit(unit);
            if (propagate_eagerly) elim_propagate(eliminator, unit);
            return false;
        }

        if (s > size && t > size)
        {
            clause.clear();
            elim_on_the_fly_self_subsumption(eliminator, c, pivot);
            stats.elimotfsub++;
            stats.subsumed++;
            elim_update_removed_clause(eliminator, d, -pivot);
            mark_garbage(d);
            return false;
        }

        if (s > size)
        {
            clause.clear();
            elim_on_the_fly_self_subsumption(eliminator, c, pivot);
            return false;
        }

        if (t > size)
        {
            clause.clear();
            elim_on_the_fly_self_subsumption(eliminator, d, -pivot);
            return false;
        }

        if (propagate_eagerly) lrat_chain.clear();
        return true;
    }

    public bool elim_resolvents_are_bounded(Eliminator eliminator, int pivot)
    {
        bool substitute = !eliminator.gates.empty();
        stats.elimtried++;
        var ps = occs(pivot);
        var ns = occs(-pivot);
        long pos = ps.n;
        long neg = ns.n;
        if (pos == 0 || neg == 0) return lim.elimbound >= 0;
        long bound = pos + neg + lim.elimbound;
        long resolvents = 0;
        for (int i = 0; i < ps.n; i++)
        {
            Clause c = ps[i];
            if (c.garbage) continue;
            for (int j = 0; j < ns.n; j++)
            {
                Clause d = ns[j];
                if (d.garbage) continue;
                if (substitute && c.gate == d.gate) continue;
                stats.elimrestried++;
                if (resolve_clauses(eliminator, c, pivot, d, true))
                {
                    resolvents++;
                    int size = clause.n;
                    clause.clear();
                    if (size > opts.elimclslim) return false;
                    if (resolvents > bound) return false;
                }
                else if (unsat) return false;
                else if (val(pivot) != 0) return false;
            }
        }
        return true;
    }

    void elim_add_resolvents(Eliminator eliminator, int pivot)
    {
        bool substitute = !eliminator.gates.empty();
        if (substitute) stats.elimsubst++;
        var ps = occs(pivot);
        var ns = occs(-pivot);
        for (int i = 0; i < ps.n; i++)
        {
            if (unsat) break;
            Clause c = ps[i];
            if (c.garbage) continue;
            for (int j = 0; j < ns.n; j++)
            {
                if (unsat) break;
                Clause d = ns[j];
                if (d.garbage) continue;
                if (substitute && c.gate == d.gate) continue;
                if (!resolve_clauses(eliminator, c, pivot, d, false)) continue;
                Clause r = new_resolved_irredundant_clause();
                elim_update_added_clause(eliminator, r);
                eliminator.enqueue(r);
                lrat_chain.clear();
                clause.clear();
            }
        }
    }

    public void mark_eliminated_clauses_as_garbage(Eliminator eliminator, int pivot, ref bool deleted_binary_clause)
    {
        long substitute = eliminator.gates.n;
        var ps = occs(pivot);
        for (int i = 0; i < ps.n; i++)
        {
            Clause c = ps[i];
            if (c.garbage) continue;
            if (substitute == 0 || c.gate)
            {
                if (proof != null) proof.weaken_minus(c);
                if (c.size == 2) deleted_binary_clause = true;
                external.push_clause_on_extension_stack(c, pivot);
            }
            mark_garbage(c);
            elim_update_removed_clause(eliminator, c, pivot);
        }
        ps.erase();

        var ns = occs(-pivot);
        for (int i = 0; i < ns.n; i++)
        {
            Clause d = ns[i];
            if (d.garbage) continue;
            if (substitute == 0 || d.gate)
            {
                if (proof != null) proof.weaken_minus(d);
                if (d.size == 2) deleted_binary_clause = true;
                external.push_clause_on_extension_stack(d, -pivot);
            }
            mark_garbage(d);
            elim_update_removed_clause(eliminator, d, -pivot);
        }
        ns.erase();
    }

    static int clause_smaller_size(Clause a, Clause b) => a.size.CompareTo(b.size);

    void try_to_eliminate_variable(Eliminator eliminator, int pivot, ref bool deleted_binary_clause)
    {
        if (!is_active(pivot)) return;
        long pos = flush_occs(pivot);
        long neg = flush_occs(-pivot);
        if (pos > neg)
        {
            pivot = -pivot;
            (pos, neg) = (neg, pos);
        }
        if (pos != 0 && neg > opts.elimocclim) return;

        var ps = occs(pivot);
        CUtil.stable_sort(ps.AsSpan(), clause_smaller_size);
        var ns = occs(-pivot);
        CUtil.stable_sort(ns.AsSpan(), clause_smaller_size);

        if (pos != 0) find_gate_clauses(eliminator, pivot);

        if (!unsat && val(pivot) == 0)
        {
            if (elim_resolvents_are_bounded(eliminator, pivot))
            {
                elim_add_resolvents(eliminator, pivot);
                if (!unsat) mark_eliminated_clauses_as_garbage(eliminator, pivot, ref deleted_binary_clause);
                if (is_active(pivot)) mark_eliminated(pivot);
            }
        }

        unmark_gate_clauses(eliminator);
        elim_backward_clauses(eliminator);
    }

    public void mark_redundant_clauses_with_eliminated_variables_as_garbage()
    {
        for (int k = 0; k < clauses.n; k++)
        {
            Clause c = clauses[k];
            if (c.garbage || !c.redundant) continue;
            bool clean = true;
            var lits = c.literals;
            for (int i = 0; i < c.size; i++)
            {
                ref Flags f = ref flags(lits[i]);
                if (f.eliminated()) { clean = false; break; }
                if (f.pure()) { clean = false; break; }
            }
            if (!clean) mark_garbage(c);
        }
    }

    int elim_round(out bool completed, ref bool deleted_binary_clause)
    {
        if (!preprocessing && !lookingahead) reset_mode(SEARCH);
        set_mode(SIMPLIFY);
        set_mode(ELIM);
        stats.elimrounds++;
        last.elim.marked = stats.mark.elim;

        long resolution_limit;
        if (opts.elimlimited != 0)
        {
            long delta = stats.propagations.search;
            delta = (long)(delta * (1e-3 * opts.elimreleff));
            if (delta < opts.elimineff) delta = opts.elimineff;
            if (delta > opts.elimaxeff) delta = opts.elimaxeff;
            delta = Math.Max(delta, 2L * active());
            PHASE("elim-round", stats.elimrounds, $"limit of {delta} resolutions");
            resolution_limit = stats.elimres + delta;
        }
        else
        {
            PHASE("elim-round", stats.elimrounds, "resolutions unlimited");
            resolution_limit = long.MaxValue;
        }

        init_noccs();

        for (int k = 0; k < clauses.n; k++)
        {
            Clause c = clauses[k];
            if (c.garbage || c.redundant) continue;
            bool satisfied_ = false, falsified = false;
            var lits = c.literals;
            for (int i = 0; i < c.size; i++)
            {
                sbyte tmp = val(lits[i]);
                if (tmp > 0) satisfied_ = true;
                else if (tmp < 0) falsified = true;
            }
            if (satisfied_) mark_garbage(c);
            else
            {
                for (int i = 0; i < c.size; i++)
                {
                    int lit = lits[i];
                    if (!is_active(lit)) continue;
                    if (falsified) mark_elim(lit);
                    noccs(lit)++;
                }
            }
        }

        init_occs();

        var eliminator = new Eliminator(this);
        var schedule = eliminator.schedule;

        for (int idx = 1; idx <= max_var; idx++)
        {
            if (!is_active(idx)) continue;
            if (frozen(idx)) continue;
            if (!flags(idx).elim) continue;
            flags(idx).elim = false;
            schedule.push_back((uint)idx);
        }

        schedule.shrink();

        long scheduled = schedule.size();
        PHASE("elim-round", stats.elimrounds,
              $"scheduled {scheduled} variables {CUtil.percent(scheduled, active()):F0}% for elimination");

        for (int k = 0; k < clauses.n; k++)
        {
            Clause c = clauses[k];
            if (c.garbage || c.redundant) continue;
            var lits = c.literals;
            for (int i = 0; i < c.size; i++)
                if (is_active(lits[i])) occs(lits[i]).push_back(c);
        }

        long old_resolutions = stats.elimres;
        long old_eliminated = stats.all.eliminated;
        long old_fixed = stats.all.fixed_;

        long garbage_limit = (2 * stats.irrlits / 3) + (1 << 20);

        long tried = 0;
        while (!unsat && !terminated_asynchronously() && stats.elimres <= resolution_limit && !schedule.empty())
        {
            int idx = (int)schedule.front();
            schedule.pop_front();
            flags(idx).elim = false;
            try_to_eliminate_variable(eliminator, idx, ref deleted_binary_clause);
            tried++;
            if (stats.garbage.literals <= garbage_limit) continue;
            mark_redundant_clauses_with_eliminated_variables_as_garbage();
            garbage_collection();
        }

        completed = schedule.size() == 0;

        PHASE("elim-round", stats.elimrounds,
              $"tried to eliminate {tried} variables {CUtil.percent(tried, scheduled):F0}% ({schedule.size()} remain)");

        schedule.erase();

        var instantiator = new Instantiator();
        if (!unsat && !terminated_asynchronously() && opts.instantiate != 0)
            collect_instantiation_candidates(instantiator);

        reset_occs();
        reset_noccs();

        if (!unsat) mark_redundant_clauses_with_eliminated_variables_as_garbage();

        int eliminated = (int)(stats.all.eliminated - old_eliminated);
        long resolutions = stats.elimres - old_resolutions;
        PHASE("elim-round", stats.elimrounds,
              $"eliminated {eliminated} variables {CUtil.percent(eliminated, scheduled):F0}% in {resolutions} resolutions");

        last.elim.subsumephases = stats.subsumephases;
        int units = (int)(stats.all.fixed_ - old_fixed);
        report('e', (opts.reportall == 0 && (eliminated + units) == 0) ? 1 : 0);
        reset_mode(ELIM);
        reset_mode(SIMPLIFY);
        if (!preprocessing && !lookingahead) set_mode(SEARCH);

        if (!unsat && !terminated_asynchronously() && instantiator.any())
            instantiate(instantiator);

        eliminator.dispose();
        return eliminated;
    }

    public void increase_elimination_bound()
    {
        if (lim.elimbound >= opts.elimboundmax) return;
        if (lim.elimbound < 0) lim.elimbound = 0;
        else if (lim.elimbound == 0) lim.elimbound = 1;
        else lim.elimbound *= 2;
        if (lim.elimbound > opts.elimboundmax) lim.elimbound = opts.elimboundmax;
        PHASE("elim-phase", stats.elimphases, $"new elimination bound {lim.elimbound}");
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (!is_active(idx)) continue;
            if (flags(idx).elim) continue;
            mark_elim(idx);
        }
        report('^');
    }

    public void elim(bool update_limits = true)
    {
        if (unsat) return;
        if (level != 0) backtrack();
        if (!propagate())
        {
            learn_empty_clause();
            return;
        }

        stats.elimphases++;
        PHASE("elim-phase", stats.elimphases, $"starting at most {opts.elimrounds} elimination rounds");

        if (external_prop) private_steps = true;

        int old_active_variables = active();
        long old_eliminated = stats.all.eliminated;

        if (last.elim.subsumephases == stats.subsumephases) subsume(update_limits);

        reset_watches();

        bool phase_complete = false, deleted_binary_clause = false;
        int round = 1;

        while (!unsat && !phase_complete && !terminated_asynchronously())
        {
            int eliminated = elim_round(out bool round_complete, ref deleted_binary_clause);

            if (!round_complete)
            {
                PHASE("elim-phase", stats.elimphases,
                      $"last round {round} incomplete {(eliminated != 0 ? "but successful" : "and unsuccessful")}");
                break;
            }

            if (round++ >= opts.elimrounds)
            {
                PHASE("elim-phase", stats.elimphases,
                      $"round limit {round - 1} hit ({(eliminated != 0 ? "though last round successful" : "last round unsuccessful anyhow")})");
                break;
            }

            if (subsume_round()) continue;
            if (block()) continue;
            if (cover()) continue;

            PHASE("elim-phase", stats.elimphases, "no new variable elimination candidates");
            phase_complete = true;
        }

        if (phase_complete)
        {
            stats.elimcompleted++;
            PHASE("elim-phase", stats.elimphases,
                  $"fully completed elimination {stats.elimcompleted} at elimination bound {lim.elimbound}");
        }
        else
        {
            PHASE("elim-phase", stats.elimphases,
                  $"incomplete elimination {stats.elimcompleted + 1} at elimination bound {lim.elimbound}");
        }

        if (deleted_binary_clause) delete_garbage_clauses();
        init_watches();
        connect_watches();

        if (unsat) { }
        else if (propagated < trail.n)
        {
            if (!propagate()) learn_empty_clause();
        }

        if (phase_complete) increase_elimination_bound();

        {
            long eliminated = stats.all.eliminated - old_eliminated;
            PHASE("elim-phase", stats.elimphases,
                  $"eliminated {eliminated} variables {CUtil.percent(eliminated, old_active_variables):F2}%");
        }

        if (external_prop) private_steps = false;

        if (!update_limits) return;

        long delta_ = (long)scale(opts.elimint * (stats.elimphases + 1));
        lim.elim = stats.conflicts + delta_;
        PHASE("elim-phase", stats.elimphases, $"new limit at {lim.elim} conflicts after {delta_} conflicts");
        last.elim.fixed_ = stats.all.fixed_;
    }
}
