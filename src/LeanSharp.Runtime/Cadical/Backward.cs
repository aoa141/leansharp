// Port of 'backward.cpp' (eager backward subsumption during elimination). The queue part
// ('Eliminator::enqueue/dequeue') is in 'Elim.cs'.

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    public void elim_backward_clause(Eliminator eliminator, Clause c)
    {
        if (c.garbage) return;
        long len = uint.MaxValue;
        uint size = 0;
        int best = 0;
        bool satisfied_ = false;
        var clits = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int lit = clits[i];
            sbyte tmp = val(lit);
            if (tmp > 0)
            {
                satisfied_ = true;
                break;
            }
            if (tmp < 0) continue;
            long l = occs(lit).n;
            if (l < len)
            {
                best = lit;
                len = l;
            }
            mark(lit);
            size++;
        }
        if (satisfied_)
        {
            elim_update_removed_clause(eliminator, c);
            mark_garbage(c);
        }
        else if (len > opts.elimocclim) { }
        else
        {
            var os = occs(best);
            for (int k = 0; k < os.n; k++)
            {
                Clause d = os[k];
                if (d == c) continue;
                if (d.garbage) continue;
                if ((uint)d.size < size) continue;
                int negated = 0;
                uint found = 0;
                var dlits = d.literals;
                for (int i = 0; i < d.size; i++)
                {
                    int lit = dlits[i];
                    int tmp = val(lit);
                    if (tmp > 0)
                    {
                        satisfied_ = true;
                        break;
                    }
                    if (tmp < 0) continue;
                    tmp = marked(lit);
                    if (tmp == 0) continue;
                    if (tmp < 0)
                    {
                        if (negated != 0)
                        {
                            size = uint.MaxValue;
                            break;
                        }
                        else negated = lit;
                    }
                    if (++found == size) break;
                }
                if (satisfied_)
                {
                    elim_update_removed_clause(eliminator, d);
                    mark_garbage(d);
                }
                else if (found == size)
                {
                    if (negated == 0)
                    {
                        elim_update_removed_clause(eliminator, d);
                        mark_garbage(d);
                        stats.subsumed++;
                        stats.elimbwsub++;
                    }
                    else
                    {
                        int unit = 0;
                        for (int i = 0; i < d.size; i++)
                        {
                            int lit = dlits[i];
                            sbyte tmp = val(lit);
                            if (tmp < 0)
                            {
                                if (!lrat) continue;
                                ref Flags f = ref flags(lit);
                                if (f.seen) continue;
                                f.seen = true;
                                analyzed.push_back(lit);
                                continue;
                            }
                            if (tmp > 0)
                            {
                                satisfied_ = true;
                                break;
                            }
                            if (lit == negated) continue;
                            if (unit != 0)
                            {
                                unit = int.MinValue;
                                break;
                            }
                            else unit = lit;
                        }
                        if (lrat && !satisfied_)
                        {
                            for (int i = 0; i < c.size; i++)
                            {
                                int lit = clits[i];
                                sbyte tmp = val(lit);
                                if (tmp >= 0) continue;
                                ref Flags f = ref flags(lit);
                                if (f.seen && unit != 0 && unit == int.MinValue)
                                {
                                    f.seen = false;
                                    continue;
                                }
                                else if (!f.seen)
                                {
                                    f.seen = true;
                                    analyzed.push_back(lit);
                                }
                            }
                            if (unit == int.MinValue)
                            {
                                for (int i = 0; i < d.size; i++) flags(dlits[i]).seen = false;
                            }
                            for (int i = 0; i < analyzed.n; i++)
                            {
                                int lit = analyzed[i];
                                ref Flags f = ref flags(lit);
                                if (!f.seen)
                                {
                                    f.seen = true;
                                    continue;
                                }
                                uint uidx = vlit(-lit);
                                ulong id = unit_clauses(uidx);
                                lrat_chain.push_back(id);
                            }
                            clear_analyzed_literals();
                            lrat_chain.push_back(d.id);
                            lrat_chain.push_back(c.id);
                        }
                        if (satisfied_)
                        {
                            mark_garbage(d);
                            elim_update_removed_clause(eliminator, d);
                        }
                        else if (unit != 0 && unit != int.MinValue)
                        {
                            assign_unit(unit);
                            elim_propagate(eliminator, unit);
                            lrat_chain.clear();
                            break;
                        }
                        else if (occs(negated).n <= opts.elimocclim)
                        {
                            strengthen_clause(d, negated);
                            WatchOps.remove_occs(occs(negated), d);
                            elim_update_removed_lit(eliminator, negated);
                            stats.elimbwstr++;
                            eliminator.enqueue(d);
                        }
                        lrat_chain.clear();
                    }
                }
            }
        }
        mini_chain.clear();
        unmark(c);
    }

    public void elim_backward_clauses(Eliminator eliminator)
    {
        if (opts.elimbackward == 0) return;
        Clause c;
        while (!unsat && (c = eliminator.dequeue()) != null)
            elim_backward_clause(eliminator, c);
    }
}
