// Port of 'block.hpp' / 'block.cpp' (blocked clause elimination).

namespace LeanSharp.Runtime.Cadical;

public sealed class Blocker
{
    public readonly Vec<Clause> candidates = new();
    public readonly Vec<Clause> reschedule = new();
    public readonly Heap schedule;

    public Blocker(Internal i)
    {
        schedule = new Heap((a, b) => i.block_more_occs_size(a, b));
    }

    public void erase()
    {
        candidates.erase();
        reschedule.erase();
        schedule.erase();
    }
}

public sealed unsafe partial class Internal
{
    public bool block_more_occs_size(uint a, uint b)
    {
        ulong s = (ulong)noccs(-u2i(a));
        ulong t = (ulong)noccs(-u2i(b));
        if (s > t) return true;
        if (s < t) return false;
        s = (ulong)noccs(u2i(a));
        t = (ulong)noccs(u2i(b));
        if (s > t) return true;
        if (s < t) return false;
        return a > b;
    }

    /*------------------------------------------------------------------------*/

    public bool is_blocked_clause(Clause c, int lit)
    {
        mark(c);
        var os = occs(-lit);
        bool res = true;
        int end_of_os = os.n;
        int i = 0;
        Clause prev_d = null;
        for (; i != end_of_os; i++)
        {
            Clause d = os.a[i];
            os.a[i] = prev_d;
            prev_d = d;
            stats.blockres++;
            int prev_other = 0;
            int[] dl = d.literals;
            int end_of_d = d.size;
            int l;
            for (l = 0; l != end_of_d; l++)
            {
                int other = dl[l];
                dl[l] = prev_other;
                prev_other = other;
                if (other == -lit) continue;
                if (marked(other) < 0)
                {
                    dl[0] = other;
                    break;
                }
            }
            if (l == end_of_d)
            {
                while (l-- != 0)
                {
                    int other = dl[l];
                    dl[l] = prev_other;
                    prev_other = other;
                }
                res = false;
                os.a[0] = d;
                break;
            }
        }
        unmark(c);
        if (res)
        {
            while (i != 0)
            {
                Clause d = os.a[--i];
                os.a[i] = prev_d;
                prev_d = d;
            }
        }
        return res;
    }

    /*------------------------------------------------------------------------*/

    void block_schedule(Blocker blocker)
    {
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (c.garbage) continue;
            if (c.redundant) continue;
            if (c.size <= opts.blockmaxclslim) continue;
            for (int i = 0; i < c.size; i++) mark_skip(-c.literals[i]);
        }
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (c.garbage) continue;
            if (c.redundant) continue;
            for (int i = 0; i < c.size; i++) occs(c.literals[i]).push_back(c);
        }
        for (int idx = 1; idx <= max_var; idx++)
        {
            for (int s = 0; s < 2; s++)
            {
                int lit = s == 0 ? -idx : idx;
                if (!is_active(lit)) continue;
                noccs(lit) = occs(lit).n;
            }
        }
        int skipped = 0;
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (!is_active(idx)) continue;
            if (frozen(idx))
            {
                skipped += 2;
                continue;
            }
            for (int sign = -1; sign <= 1; sign += 2)
            {
                int lit = sign * idx;
                if (marked_skip(lit))
                {
                    skipped++;
                    continue;
                }
                if (!marked_block(lit)) continue;
                unmark_block(lit);
                blocker.schedule.push_back(vlit(lit));
            }
        }
        PHASE("block", stats.blockings,
              $"scheduled {blocker.schedule.size()} candidate literals {CUtil.percent(blocker.schedule.size(), 2.0 * active()):F2}% ({skipped} skipped {CUtil.percent(skipped, 2.0 * active()):F2}%)");
    }

    /*------------------------------------------------------------------------*/

    void block_pure_literal(Blocker blocker, int lit)
    {
        if (frozen(lit)) return;
        var pos = occs(lit);
        var nos = occs(-lit);
        stats.blockpurelits++;
        for (int k = 0; k < pos.n; k++)
        {
            var c = pos[k];
            if (c.garbage) continue;
            blocker.reschedule.push_back(c);
            if (proof != null) proof.weaken_minus(c);
            external.push_clause_on_extension_stack(c, lit);
            stats.blockpured++;
            mark_garbage(c);
        }
        pos.erase();
        nos.erase();
        mark_pure(lit);
        stats.blockpured++;
    }

    /*------------------------------------------------------------------------*/

    void block_literal_with_one_negative_occ(Blocker blocker, int lit)
    {
        var nos = occs(-lit);
        Clause d = null;
        for (int k = 0; k < nos.n; k++)
        {
            var c = nos[k];
            if (c.garbage) continue;
            d = c;
        }
        nos.resize(1);
        nos[0] = d;

        if (d != null && d.size > opts.blockmaxclslim) return;

        mark(d);
        long blocked = 0;
        var pos = occs(lit);
        int eop = pos.n;
        int j = 0;
        for (int i = 0; i != eop; i++)
        {
            Clause c = pos.a[j++] = pos.a[i];
            if (c.garbage)
            {
                j--;
                continue;
            }
            if (c.size > opts.blockmaxclslim) continue;
            if (c.size < opts.blockminclslim) continue;
            int prev_other = 0;
            int[] cl = c.literals;
            int end_of_c = c.size;
            int l;
            for (l = 0; l != end_of_c; l++)
            {
                int other = cl[l];
                cl[l] = prev_other;
                prev_other = other;
                if (other == lit) continue;
                if (marked(other) < 0)
                {
                    cl[0] = other;
                    break;
                }
            }
            if (l == end_of_c)
            {
                while (l-- != 0)
                {
                    int other = cl[l];
                    cl[l] = prev_other;
                    prev_other = other;
                }
                continue;
            }
            blocked++;
            if (proof != null) proof.weaken_minus(c);
            external.push_clause_on_extension_stack(c, lit);
            blocker.reschedule.push_back(c);
            mark_garbage(c);
            j--;
        }
        if (j == 0) pos.erase();
        else pos.resize(j);
        stats.blocked += blocked;
        unmark(d);
    }

    /*------------------------------------------------------------------------*/

    int block_candidates(Blocker blocker, int lit)
    {
        var pos = occs(lit);
        var nos = occs(-lit);
        for (int k = 0; k < nos.n; k++) mark2(nos[k]);
        int eop = pos.n;
        int j = 0;
        for (int i = 0; i != eop; i++)
        {
            Clause c = pos.a[j++] = pos.a[i];
            if (c.garbage)
            {
                j--;
                continue;
            }
            if (c.size > opts.blockmaxclslim) continue;
            if (c.size < opts.blockminclslim) continue;
            int eoc = c.size;
            int l;
            for (l = 0; l != eoc; l++)
            {
                int other = c.literals[l];
                if (other == lit) continue;
                if (marked2(-other)) break;
            }
            if (l != eoc) blocker.candidates.push_back(c);
        }
        if (j == 0) pos.erase();
        else pos.resize(j);
        for (int k = 0; k < nos.n; k++) unmark(nos[k]);
        return blocker.candidates.n;
    }

    /*------------------------------------------------------------------------*/

    Clause block_impossible(Blocker blocker, int lit)
    {
        for (int k = 0; k < blocker.candidates.n; k++) mark2(blocker.candidates[k]);
        var nos = occs(-lit);
        Clause res = null;
        for (int k = 0; k < nos.n; k++)
        {
            var c = nos[k];
            int eoc = c.size;
            int l;
            for (l = 0; l != eoc; l++)
            {
                int other = c.literals[l];
                if (other == -lit) continue;
                if (marked2(-other)) break;
            }
            if (l == eoc) res = c;
        }
        for (int k = 0; k < blocker.candidates.n; k++) unmark(blocker.candidates[k]);
        if (res != null) blocker.candidates.clear();
        return res;
    }

    /*------------------------------------------------------------------------*/

    void block_literal_with_at_least_two_negative_occs(Blocker blocker, int lit)
    {
        var nos = occs(-lit);
        int max_size = 0;
        int eon = nos.n;
        int j = 0;
        for (int i = 0; i != eon; i++)
        {
            Clause c = nos.a[j++] = nos.a[i];
            if (c.garbage) j--;
            else if (c.size > max_size) max_size = c.size;
        }
        if (j == 0) nos.erase();
        else nos.resize(j);

        if (max_size > opts.blockmaxclslim) return;

        int candidates = block_candidates(blocker, lit);
        if (candidates == 0) return;

        if (candidates > 1 && block_impossible(blocker, lit) != null) return;

        long blocked = 0;
        for (int k = 0; k < blocker.candidates.n; k++)
        {
            var c = blocker.candidates[k];
            if (!is_blocked_clause(c, lit)) continue;
            blocked++;
            if (proof != null) proof.weaken_minus(c);
            external.push_clause_on_extension_stack(c, lit);
            blocker.reschedule.push_back(c);
            mark_garbage(c);
        }
        blocker.candidates.clear();
        stats.blocked += blocked;
        if (blocked != 0) flush_occs(lit);
    }

    /*------------------------------------------------------------------------*/

    void block_reschedule_clause(Blocker blocker, int lit, Clause c)
    {
        for (int i = 0; i < c.size; i++)
        {
            int other = c.literals[i];
            ref long n = ref noccs(other);
            n--;
            if (blocker.schedule.contains(vlit(-other)))
                blocker.schedule.update(vlit(-other));
            else if (is_active(other) && !frozen(other) && !marked_skip(-other))
                blocker.schedule.push_back(vlit(-other));
            if (blocker.schedule.contains(vlit(other)))
                blocker.schedule.update(vlit(other));
        }
    }

    void block_reschedule(Blocker blocker, int lit)
    {
        while (!blocker.reschedule.empty())
        {
            Clause c = blocker.reschedule.back();
            blocker.reschedule.pop_back();
            block_reschedule_clause(blocker, lit, c);
        }
    }

    /*------------------------------------------------------------------------*/

    void block_literal(Blocker blocker, int lit)
    {
        if (!is_active(lit)) return;
        if (frozen(lit)) return;
        if (noccs(-lit) > opts.blockocclim) return;
        stats.blockcands++;
        if (noccs(-lit) == 0) block_pure_literal(blocker, lit);
        else if (noccs(lit) == 0) { }
        else if (noccs(-lit) == 1) block_literal_with_one_negative_occ(blocker, lit);
        else block_literal_with_at_least_two_negative_occs(blocker, lit);
        unmark_block(lit);
    }

    /*------------------------------------------------------------------------*/

    public bool block()
    {
        if (opts.block == 0) return false;
        if (unsat) return false;
        if (stats.current.irredundant == 0) return false;
        if (terminated_asynchronously()) return false;

        if (propagated < trail.n)
        {
            init_watches();
            connect_watches();
            if (!propagate())
            {
                learn_empty_clause();
            }
            clear_watches();
            reset_watches();
            if (unsat) return false;
        }

        // START_SIMPLIFIER (block, BLOCK)
        if (!preprocessing && !lookingahead) reset_mode(SEARCH);
        set_mode(SIMPLIFY);
        set_mode(BLOCK);

        stats.blockings++;

        mark_satisfied_clauses_as_garbage();

        init_occs();
        init_noccs();

        var blocker = new Blocker(this);
        block_schedule(blocker);

        long blocked = stats.blocked;
        long resolutions = stats.blockres;
        long purelits = stats.blockpurelits;
        long pured = stats.blockpured;

        while (!terminated_asynchronously() && !blocker.schedule.empty())
        {
            int lit = u2i(blocker.schedule.front());
            blocker.schedule.pop_front();
            block_literal(blocker, lit);
            block_reschedule(blocker, lit);
        }

        blocker.erase();
        reset_noccs();
        reset_occs();

        resolutions = stats.blockres - resolutions;
        blocked = stats.blocked - blocked;

        PHASE("block", stats.blockings, $"blocked {blocked} clauses in {resolutions} resolutions");

        pured = stats.blockpured - pured;
        purelits = stats.blockpurelits - purelits;

        if (pured != 0) mark_redundant_clauses_with_eliminated_variables_as_garbage();

        if (purelits != 0)
            PHASE("block", stats.blockings, $"found {purelits} pure literals in {pured} clauses");
        else
            PHASE("block", stats.blockings, "no pure literals found");

        report('b', opts.reportall == 0 && blocked == 0 ? 1 : 0);

        // STOP_SIMPLIFIER (block, BLOCK)
        reset_mode(BLOCK);
        reset_mode(SIMPLIFY);
        if (!preprocessing && !lookingahead) set_mode(SEARCH);

        return blocked != 0;
    }
}
