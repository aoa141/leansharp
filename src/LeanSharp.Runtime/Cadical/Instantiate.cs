// Port of 'instantiate.cpp' (variable instantiation).

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    public void collect_instantiation_candidates(Instantiator instantiator)
    {
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (frozen(idx)) continue;
            if (!is_active(idx)) continue;
            if (flags(idx).elim) continue;
            for (int sign = -1; sign <= 1; sign += 2)
            {
                int lit = sign * idx;
                if (noccs(lit) > opts.instantiateocclim) continue;
                var os = occs(lit);
                for (int k = 0; k < os.n; k++)
                {
                    var c = os[k];
                    if (c.garbage) continue;
                    if (opts.instantiateonce != 0 && c.instantiated) continue;
                    if (c.size < opts.instantiateclslim) continue;
                    bool satisfied_ = false;
                    int unassigned = 0;
                    for (int i = 0; i < c.size; i++)
                    {
                        sbyte tmp = val(c.literals[i]);
                        if (tmp > 0) satisfied_ = true;
                        if (tmp == 0) unassigned++;
                    }
                    if (satisfied_) continue;
                    if (unassigned < 3) continue;
                    long negoccs = occs(-lit).n;
                    instantiator.candidate(lit, c, c.size, negoccs);
                }
            }
        }
    }

    /*------------------------------------------------------------------------*/

    void inst_assign(int lit)
    {
        num_assigned++;
        set_val(lit, 1);
        trail.push_back(lit);
    }

    bool inst_propagate()
    {
        long before = propagated;
        bool ok = true;
        while (ok && propagated != trail.n)
        {
            int lit = -trail[propagated++];
            var ws = watches(lit);
            var wa = ws.a;
            int eow = ws.n;
            int i = 0, j = 0;
            while (i != eow)
            {
                Watch w = wa[j++] = wa[i++];
                sbyte b = val(w.blit);
                if (b > 0) continue;
                if (w.size == 2)
                {
                    if (b < 0)
                    {
                        ok = false;
                        if (lrat) inst_chain.push_back(w.clause);
                        break;
                    }
                    else
                    {
                        if (lrat) inst_chain.push_back(w.clause);
                        inst_assign(w.blit);
                    }
                }
                else
                {
                    int[] lits = w.clause.literals;
                    int other = lits[0] ^ lits[1] ^ lit;
                    lits[0] = other; lits[1] = lit;
                    sbyte u = val(other);
                    if (u > 0) wa[j - 1].blit = other;
                    else
                    {
                        int size = w.clause.size;
                        int middle = w.clause.pos;
                        int k = middle;
                        sbyte v = -1;
                        int r = 0;
                        while (k != size && (v = val(r = lits[k])) < 0) k++;
                        if (v < 0)
                        {
                            k = 2;
                            while (k != middle && (v = val(r = lits[k])) < 0) k++;
                        }
                        w.clause.pos = k;
                        if (v > 0)
                        {
                            wa[j - 1].blit = r;
                        }
                        else if (v == 0)
                        {
                            lits[1] = r;
                            lits[k] = lit;
                            watch_literal(r, lit, w.clause);
                            j--;
                        }
                        else if (u == 0)
                        {
                            if (lrat) inst_chain.push_back(w.clause);
                            inst_assign(other);
                        }
                        else
                        {
                            if (lrat) inst_chain.push_back(w.clause);
                            ok = false;
                            break;
                        }
                    }
                }
            }
            if (j != i)
            {
                while (i != eow) wa[j++] = wa[i++];
                ws.shrink(j);
            }
        }
        long delta = propagated - before;
        stats.propagations.instantiate += delta;
        return ok;
    }

    /*------------------------------------------------------------------------*/

    bool instantiate_candidate(int lit, Clause c)
    {
        stats.instried++;
        if (c.garbage) return false;
        bool found = false, satisfied_ = false, inactive = false;
        int unassigned = 0;
        for (int i = 0; i < c.size; i++)
        {
            int other = c.literals[i];
            if (other == lit) found = true;
            sbyte tmp = val(other);
            if (tmp > 0)
            {
                satisfied_ = true;
                break;
            }
            if (tmp == 0 && !is_active(other))
            {
                inactive = true;
                break;
            }
            if (tmp == 0) unassigned++;
        }
        if (!found) return false;
        if (inactive) return false;
        if (satisfied_) return false;
        if (unassigned < 3) return false;
        int before = trail.n;
        c.instantiated = true;
        level++;
        inst_assign(lit);
        for (int i = 0; i < c.size; i++)
        {
            int other = c.literals[i];
            if (other == lit) continue;
            sbyte tmp = val(other);
            if (tmp != 0) continue;
            inst_assign(-other);
        }
        bool ok = inst_propagate();
        if (ok)
        {
            inst_chain.clear();
        }
        else if (lrat)
        {
            Clause reason = inst_chain.back();
            inst_chain.pop_back();
            lrat_chain.push_back(reason.id);
            for (int i = 0; i < reason.size; i++)
            {
                int other = reason.literals[i];
                ref Flags f = ref flags(other);
                f.seen = true;
                analyzed.push_back(other);
            }
        }
        while (trail.n > before)
        {
            int other = trail.back();
            trail.pop_back();
            num_assigned--;
            set_val(other, 0);
            if (!ok && inst_chain.n != 0 && lrat)
            {
                if (flags(other).seen)
                {
                    Clause reason = inst_chain.back();
                    lrat_chain.push_back(reason.id);
                    for (int i = 0; i < reason.size; i++)
                    {
                        int other2 = reason.literals[i];
                        ref Flags f2 = ref flags(other2);
                        if (f2.seen) continue;
                        f2.seen = true;
                        analyzed.push_back(other2);
                    }
                    flags(other).seen = false;
                }
                inst_chain.pop_back();
            }
        }
        if (!ok && lrat)
        {
            if (flags(lit).seen) lrat_chain.push_back(c.id);
            for (int i = 0; i < c.size; i++) flags(c.literals[i]).seen = false;
            for (int k = 0; k < analyzed.n; k++)
            {
                int other = analyzed[k];
                ref Flags f = ref flags(other);
                if (!f.seen)
                {
                    f.seen = true;
                    continue;
                }
                uint uidx = vlit(-other);
                ulong id = unit_clauses(uidx);
                lrat_chain.push_back(id);
            }
            clear_analyzed_literals();
            lrat_chain.reverse();
        }
        propagated = before;
        level = 0;
        if (ok) return false;
        unwatch_clause(c);
        strengthen_clause(c, lit);
        watch_clause(c);
        lrat_chain.clear();
        stats.instantiated++;
        return true;
    }

    /*------------------------------------------------------------------------*/

    public void instantiate(Instantiator instantiator)
    {
        stats.instrounds++;
        long candidates = instantiator.candidates.n;
        long tried = 0;
        long instantiated = 0;
        init_watches();
        connect_watches();
        if (propagated < trail.n)
        {
            if (!propagate())
            {
                learn_empty_clause();
            }
        }
        PHASE("instantiate", stats.instrounds,
              $"attempting to instantiate {candidates} candidate literal clause pairs");
        while (!unsat && !terminated_asynchronously() && !instantiator.candidates.empty())
        {
            var cand = instantiator.candidates.back();
            instantiator.candidates.pop_back();
            tried++;
            if (!is_active(cand.lit)) continue;
            if (!instantiate_candidate(cand.lit, cand.clause)) continue;
            instantiated++;
            VERBOSE(2, $"instantiation {tried} ({CUtil.percent(tried, candidates):F1}%) succeeded ({CUtil.percent(instantiated, tried):F1}%) with {cand.negoccs} negative occurrences in size {cand.size} clause");
        }
        PHASE("instantiate", stats.instrounds,
              $"instantiated {instantiated} candidate successfully out of {tried} tried {CUtil.percent(instantiated, tried):F1}%");
        report('I', instantiated == 0 ? 1 : 0);
        reset_watches();
    }
}
