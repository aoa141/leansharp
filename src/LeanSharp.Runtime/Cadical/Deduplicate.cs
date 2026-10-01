// Port of 'deduplicate.cpp' (removing duplicated binary clauses and hyper unary resolution).

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    public void mark_duplicated_binary_clauses_as_garbage()
    {
        if (opts.deduplicate == 0) return;
        if (unsat) return;
        if (terminated_asynchronously()) return;

        set_mode(DEDUP);
        stats.deduplications++;

        var stack = new Vec<int>();
        long subsumed = 0;
        long units = 0;

        for (int idx = 1; idx <= max_var; idx++)
        {
            if (unsat) break;
            if (!is_active(idx)) continue;
            int unit = 0;

            for (int sign = -1; unit == 0 && sign <= 1; sign += 2)
            {
                int lit = sign * idx;
                var ws = watches(lit);
                Watch[] wa = ws.a;
                int end = ws.n;
                int j = 0;
                int i;
                for (i = j; unit == 0 && i != end; i++)
                {
                    Watch w = wa[j++] = wa[i];
                    if (w.size != 2) continue;
                    int other = w.blit;
                    int tmp = marked(other);
                    Clause c = w.clause;

                    if (tmp > 0)
                    {
                        if (c.garbage)
                        {
                            j--;
                            continue;
                        }
                        if (!c.redundant)
                        {
                            int k;
                            for (k = 0; ; k++)
                            {
                                if (wa[k].size != 2) continue;
                                if (wa[k].blit != other) continue;
                                Clause d = wa[k].clause;
                                if (d.garbage) continue;
                                c = d;
                                break;
                            }
                            wa[k] = w;
                        }
                        stats.subsumed++;
                        stats.deduplicated++;
                        subsumed++;
                        mark_garbage(c);
                        j--;
                    }
                    else if (tmp < 0)
                    {
                        unit = lit;
                        if (lrat)
                        {
                            lrat_chain.push_back(c.id);
                            for (int k = 0; ; k++)
                            {
                                if (wa[k].size != 2) continue;
                                if (wa[k].blit != -other) continue;
                                lrat_chain.push_back(wa[k].clause.id);
                                break;
                            }
                        }
                        j = 0; // Flush 'ws'.
                        units++;
                    }
                    else
                    {
                        if (c.garbage) continue;
                        mark(other);
                        stack.push_back(other);
                    }
                }

                if (j == 0) ws.erase();
                else if (j != end) ws.shrink(j);

                for (int k = 0; k < stack.n; k++) unmark(stack.a[k]);
                stack.clear();
            }

            if (unit != 0)
            {
                stats.failed++;
                stats.hyperunary++;
                assign_unit(unit);
                if (!propagate())
                {
                    learn_empty_clause();
                }
            }
        }
        reset_mode(DEDUP);

        report('2', (opts.reportall == 0 && (subsumed + units) == 0) ? 1 : 0);
    }
}
