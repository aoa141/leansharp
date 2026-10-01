// Port of 'transred.cpp' (transitive reduction of the binary implication graph).

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    public void transred()
    {
        if (unsat) return;
        if (terminated_asynchronously()) return;
        if (stats.current.redundant == 0 && stats.current.irredundant == 0) return;

        set_mode(TRANSRED);
        stats.transreds++;

        long limit = stats.propagations.search;
        limit -= last.transred.propagations;
        limit = (long)(limit * (1e-3 * opts.transredreleff));
        if (limit < opts.transredmineff) limit = opts.transredmineff;
        if (limit > opts.transredmaxeff) limit = opts.transredmaxeff;

        PHASE("transred", stats.transreds, $"transitive reduction limit of {limit} propagations");

        int end = clauses.n;
        int i = 0;

        for (; i != end; i++)
        {
            Clause c = clauses.a[i];
            if (c.garbage) continue;
            if (c.size != 2) continue;
            if (c.redundant && c.hyper) continue;
            if (!c.transred) break;
        }

        if (i == end)
        {
            PHASE("transred", stats.transreds, "rescheduling all clauses since no clauses to check left");
            for (i = 0; i != end; i++)
            {
                Clause c = clauses.a[i];
                if (c.transred) c.transred = false;
            }
            i = 0;
        }

        sort_watches();

        var work = new Vec<int>();
        var parents_ = new Vec<int>();

        long propagations = 0, units = 0, removed = 0;

        while (!unsat && i != end && !terminated_asynchronously() && propagations < limit)
        {
            Clause c = clauses.a[i++];

            if (c.garbage) continue;
            if (c.size != 2) continue;
            if (c.redundant && c.hyper) continue;
            if (c.transred) continue;
            c.transred = true;

            int src = -c.literals[0];
            int dst = c.literals[1];
            if (val(src) != 0 || val(dst) != 0) continue;
            if (watches(-src).n < watches(dst).n)
            {
                int tmp = dst;
                dst = -src;
                src = -tmp;
            }

            bool irredundant = !c.redundant;

            mark(src);
            work.push_back(src);

            bool transitive = false;
            bool failed_ = false;

            int j = 0;

            parents_.clear();

            while (!transitive && !failed_ && j < work.n)
            {
                int lit = work.a[j++];
                propagations++;
                var ws = watches(-lit);
                Watch[] wa = ws.a;
                int eow = ws.n;
                for (int k = 0; !transitive && !failed_ && k != eow; k++)
                {
                    Watch w = wa[k];
                    if (w.size != 2) break;
                    Clause d = w.clause;
                    if (d == c) continue;
                    if (irredundant && d.redundant) continue;
                    if (d.garbage) continue;
                    int other = w.blit;
                    if (other == dst) transitive = true;
                    else
                    {
                        int tmp = marked(other);
                        if (tmp > 0) continue;
                        else if (tmp < 0)
                        {
                            if (lrat)
                            {
                                parents_.push_back(lit);
                                mini_chain.push_back(d.id);
                                work.push_back(other);
                            }
                            failed_ = true;
                        }
                        else
                        {
                            if (lrat)
                            {
                                parents_.push_back(lit);
                                mini_chain.push_back(d.id);
                            }
                            mark(other);
                            work.push_back(other);
                        }
                    }
                }
            }

            int failed_lit = work.back();
            int next_pos = 0;
            int next_neg = 0;

            while (!work.empty())
            {
                int lit = work.back();
                work.pop_back();
                if (lrat && failed_ && !work.empty())
                {
                    if (lit == failed_lit || lit == next_pos)
                    {
                        lrat_chain.push_back(mini_chain.back());
                        next_pos = parents_.back();
                    }
                    else if (lit == -failed_lit || lit == next_neg)
                    {
                        lrat_chain.push_back(mini_chain.back());
                        next_neg = parents_.back();
                    }
                    parents_.pop_back();
                    mini_chain.pop_back();
                }
                unmark(lit);
            }
            mini_chain.clear();
            if (lrat && failed_) lrat_chain.reverse();

            if (transitive)
            {
                removed++;
                stats.transitive++;
                mark_garbage(c);
            }
            else if (failed_)
            {
                units++;
                stats.failed++;
                stats.transredunits++;
                assign_unit(-src);
                if (!propagate())
                {
                    VERBOSE(1, "propagating new unit results in conflict");
                    learn_empty_clause();
                }
            }
            lrat_chain.clear();
        }

        last.transred.propagations = stats.propagations.search;
        stats.propagations.transred += propagations;
        work.erase();

        PHASE("transred", stats.transreds, $"removed {removed} transitive clauses, found {units} units");

        reset_mode(TRANSRED);
        report('t', (opts.reportall == 0 && (removed + units) == 0) ? 1 : 0);
    }
}
