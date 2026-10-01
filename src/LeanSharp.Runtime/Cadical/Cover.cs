// Port of 'cover.cpp' (covered clause elimination).

namespace LeanSharp.Runtime.Cadical;

public sealed class Coveror
{
    public readonly Vec<int> added = new();        // acts as trail
    public readonly Vec<int> extend = new();       // extension stack for witness
    public readonly Vec<int> covered = new();      // clause literals or added through CLA
    public readonly Vec<int> intersection = new(); // of literals in resolution candidates
    public long alas, clas;
    public int next_added, next_covered;
}

public sealed unsafe partial class Internal
{
    void cover_push_extension(int lit, Coveror coveror)
    {
        coveror.extend.push_back(0);
        coveror.extend.push_back(lit);
        for (int k = 0; k < coveror.covered.n; k++)
        {
            int other = coveror.covered[k];
            if (lit == other) { }
            else coveror.extend.push_back(other);
        }
    }

    void covered_literal_addition(int lit, Coveror coveror)
    {
        cover_push_extension(lit, coveror);
        for (int k = 0; k < coveror.intersection.n; k++)
        {
            int other = coveror.intersection[k];
            set_val(other, -1);
            coveror.covered.push_back(other);
            coveror.added.push_back(other);
            coveror.clas++;
        }
        coveror.next_covered = 0;
    }

    void asymmetric_literal_addition(int lit, Coveror coveror)
    {
        set_val(lit, -1);
        coveror.added.push_back(lit);
        coveror.alas++;
        coveror.next_covered = 0;
    }

    bool cover_propagate_asymmetric(int lit, Clause ignore, Coveror coveror)
    {
        stats.propagations.cover++;
        bool subsumed = false;
        var ws = watches(lit);
        var wa = ws.a;
        int eow = ws.n;
        int i = 0, j = 0;
        while (!subsumed && i != eow)
        {
            Watch w = wa[j++] = wa[i++];
            if (w.clause == ignore) continue;
            sbyte b = val(w.blit);
            if (b > 0) continue;
            if (w.clause.garbage) j--;
            else if (w.size == 2)
            {
                if (b < 0) subsumed = true;
                else asymmetric_literal_addition(-w.blit, coveror);
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
                    if (v > 0) wa[j - 1].blit = r;
                    else if (v == 0)
                    {
                        lits[1] = r;
                        lits[k] = lit;
                        watch_literal(r, lit, w.clause);
                        j--;
                    }
                    else if (u == 0)
                    {
                        asymmetric_literal_addition(-other, coveror);
                    }
                    else
                    {
                        subsumed = true;
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
        return subsumed;
    }

    bool cover_propagate_covered(int lit, Coveror coveror)
    {
        if (frozen(lit)) return false;
        stats.propagations.cover++;
        var os = occs(-lit);
        int end = os.n;
        bool first = true;
        for (int i = 0; i != end; i++)
        {
            Clause c = os.a[i];
            if (c.garbage) continue;
            bool blocked = false;
            for (int q = 0; q < c.size; q++)
            {
                int other = c.literals[q];
                if (other == -lit) continue;
                sbyte tmp = val(other);
                if (tmp < 0) continue;
                if (tmp > 0)
                {
                    blocked = true;
                    break;
                }
            }
            if (blocked) continue;
            if (first)
            {
                for (int q = 0; q < c.size; q++)
                {
                    int other = c.literals[q];
                    if (other == -lit) continue;
                    sbyte tmp = val(other);
                    if (tmp < 0) continue;
                    coveror.intersection.push_back(other);
                    mark(other);
                }
                first = false;
            }
            else
            {
                for (int q = 0; q < c.size; q++)
                {
                    int other = c.literals[q];
                    if (other == -lit) continue;
                    sbyte tmp = val(other);
                    if (tmp < 0) continue;
                    tmp = marked(other);
                    if (tmp > 0) unmark(other);
                }
                var inter = coveror.intersection;
                int iend = inter.n;
                int jj = 0;
                for (int k = 0; k != iend; k++)
                {
                    int other = inter.a[jj++] = inter.a[k];
                    int tmp = marked(other);
                    if (tmp != 0)
                    {
                        jj--;
                        unmark(other);
                    }
                    else mark(other);
                }
                inter.resize(jj);
                if (!inter.empty()) continue;
                // move to front
                int pos = i;
                while (pos != 0)
                {
                    os.a[pos] = os.a[pos - 1];
                    pos--;
                }
                os.a[0] = c;
                break;
            }
        }
        bool res = false;
        if (first)
        {
            cover_push_extension(lit, coveror);
            res = true;
        }
        else if (coveror.intersection.empty()) { }
        else
        {
            covered_literal_addition(lit, coveror);
            unmark(coveror.intersection);
            coveror.intersection.clear();
            coveror.next_covered = 0;
        }
        unmark(coveror.intersection);
        coveror.intersection.clear();
        return res;
    }

    // Adds the literals of 'c' not in 'coveror.covered' (keeping their relative order).
    void cover_add_missing_literals(Clause c, Coveror coveror)
    {
        for (int i = 0, j = 0; i < c.size; ++i, ++j)
        {
            int lit = c.literals[i];
            if (j >= coveror.covered.n || c.literals[i] != coveror.covered[j])
            {
                --j;
                clause.push_back(lit);
                external.push_clause_literal_on_extension_stack(lit);
            }
        }
    }

    bool cover_clause(Clause c, Coveror coveror)
    {
        bool satisfied_ = false;
        for (int i = 0; i < c.size; i++)
            if (val(c.literals[i]) > 0) satisfied_ = true;
        if (satisfied_)
        {
            mark_garbage(c);
            return false;
        }

        level = 1;
        for (int i = 0; i < c.size; i++)
        {
            int lit = c.literals[i];
            if (val(lit) != 0) continue;
            asymmetric_literal_addition(lit, coveror);
            coveror.covered.push_back(lit);
        }

        bool tautological = false;
        coveror.next_added = coveror.next_covered = 0;

        while (!tautological)
        {
            if (coveror.next_added < coveror.added.n)
            {
                int lit = coveror.added[coveror.next_added++];
                tautological = cover_propagate_asymmetric(lit, c, coveror);
            }
            else if (coveror.next_covered < coveror.covered.n)
            {
                int lit = coveror.covered[coveror.next_covered++];
                tautological = cover_propagate_covered(lit, coveror);
            }
            else break;
        }

        if (tautological)
        {
            if (coveror.extend.empty())
            {
                stats.cover.asymmetric++;
                stats.cover.total++;
            }
            else
            {
                stats.cover.blocked++;
                stats.cover.total++;
                int prev = int.MinValue;
                bool already_pushed = false;
                ulong last_id = 0;
                for (int k = 0; k < coveror.extend.n; k++)
                {
                    int other = coveror.extend[k];
                    if (prev == 0)
                    {
                        if (already_pushed) cover_add_missing_literals(c, coveror);
                        if (proof != null && already_pushed)
                        {
                            if (lrat) lrat_chain.push_back(c.id);
                            proof.add_derived_clause(last_id, false, clause, lrat_chain);
                            proof.weaken_plus(last_id, clause);
                            lrat_chain.clear();
                        }
                        last_id = ++clause_id;
                        external.push_zero_on_extension_stack();
                        external.push_witness_literal_on_extension_stack(other);
                        external.push_zero_on_extension_stack();
                        external.push_id_on_extension_stack(last_id);
                        external.push_zero_on_extension_stack();
                        clause.clear();
                        already_pushed = true;
                    }
                    if (other != 0)
                    {
                        external.push_clause_literal_on_extension_stack(other);
                        clause.push_back(other);
                    }
                    prev = other;
                }
                if (proof != null)
                {
                    cover_add_missing_literals(c, coveror);
                    if (lrat) lrat_chain.push_back(c.id);
                    proof.add_derived_clause(last_id, false, clause, lrat_chain);
                    proof.weaken_plus(last_id, clause);
                    lrat_chain.clear();
                }
                clause.clear();
                mark_garbage(c);
            }
        }

        for (int k = 0; k < coveror.added.n; k++) set_val(coveror.added[k], 0);
        level = 0;

        coveror.covered.clear();
        coveror.extend.clear();
        coveror.added.clear();

        return tautological;
    }

    static int cover_clause_covered_or_smaller(Clause a, Clause b)
    {
        // 'a' before 'b' if not tried before (covered) first, then smaller.
        bool ab = (a.covered && !b.covered) || (a.covered == b.covered && a.size < b.size);
        bool ba = (b.covered && !a.covered) || (a.covered == b.covered && b.size < a.size);
        return ab ? -1 : ba ? 1 : 0;
    }

    static int cover_clause_smaller_size(Clause a, Clause b) => a.size.CompareTo(b.size);

    long cover_round()
    {
        if (unsat) return 0;

        init_watches();
        connect_watches(true);

        long delta = stats.propagations.search;
        delta = (long)(delta * (1e-3 * opts.coverreleff));
        if (delta < opts.covermineff) delta = opts.covermineff;
        if (delta > opts.covermaxeff) delta = opts.covermaxeff;
        delta = Math.Max(delta, 2L * active());

        PHASE("cover", stats.cover.count, $"covered clause elimination limit of {delta} propagations");

        long limit = stats.propagations.cover + delta;

        init_occs();

        var schedule = new Vec<Clause>();
        var coveror = new Coveror();
        long untried = 0;

        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (c.garbage) continue;
            if (c.redundant) continue;
            bool satisfied_ = false, allfrozen = true;
            for (int i = 0; i < c.size; i++)
            {
                int lit = c.literals[i];
                if (val(lit) > 0)
                {
                    satisfied_ = true;
                    break;
                }
                else if (allfrozen && !frozen(lit)) allfrozen = false;
            }
            if (satisfied_)
            {
                mark_garbage(c);
                continue;
            }
            if (allfrozen)
            {
                c.frozen = true;
                continue;
            }
            for (int i = 0; i < c.size; i++) occs(c.literals[i]).push_back(c);
            if (c.size < opts.coverminclslim) continue;
            if (c.size > opts.covermaxclslim) continue;
            if (c.covered) continue;
            schedule.push_back(c);
            untried++;
        }

        if (schedule.empty())
        {
            PHASE("cover", stats.cover.count, "no previously untried clause left");
            for (int k = 0; k < clauses.n; k++)
            {
                var c = clauses[k];
                if (c.garbage) continue;
                if (c.redundant) continue;
                if (c.frozen)
                {
                    c.frozen = false;
                    continue;
                }
                if (c.size < opts.coverminclslim) continue;
                if (c.size > opts.covermaxclslim) continue;
                c.covered = false;
                schedule.push_back(c);
            }
        }
        else
        {
            for (int k = 0; k < clauses.n; k++)
            {
                var c = clauses[k];
                if (c.garbage) continue;
                if (c.redundant) continue;
                if (c.frozen)
                {
                    c.frozen = false;
                    continue;
                }
                if (c.size < opts.coverminclslim) continue;
                if (c.size > opts.covermaxclslim) continue;
                if (!c.covered) continue;
                schedule.push_back(c);
            }
        }

        CUtil.stable_sort(schedule.AsSpan(), cover_clause_covered_or_smaller);

        int scheduled = schedule.n;
        PHASE("cover", stats.cover.count,
              $"scheduled {scheduled} clauses {CUtil.percent(scheduled, stats.current.irredundant):F0}% with {untried} untried {CUtil.percent(untried, scheduled):F0}%");

        for (int idx = 1; idx <= max_var; idx++)
        {
            for (int s = 0; s < 2; s++)
            {
                int lit = s == 0 ? -idx : idx;
                if (!is_active(lit)) continue;
                var os = occs(lit);
                CUtil.stable_sort(os.AsSpan(), cover_clause_smaller_size);
            }
        }

        long covered = 0;
        while (!terminated_asynchronously() && !schedule.empty() && stats.propagations.cover < limit)
        {
            Clause c = schedule.back();
            schedule.pop_back();
            c.covered = true;
            if (cover_clause(c, coveror)) covered++;
        }

        int remain = schedule.n;
        int tried = scheduled - remain;
        PHASE("cover", stats.cover.count,
              $"eliminated {covered} covered clauses out of {tried} tried {CUtil.percent(covered, tried):F0}%");
        if (remain != 0)
            PHASE("cover", stats.cover.count, $"remaining {remain} clauses {CUtil.percent(remain, scheduled):F0}% untried");
        else
            PHASE("cover", stats.cover.count, "all scheduled clauses tried");
        reset_occs();
        reset_watches();
        return covered;
    }

    public bool cover()
    {
        if (opts.cover == 0) return false;
        if (unsat) return false;
        if (terminated_asynchronously()) return false;
        if (stats.current.irredundant == 0) return false;
        if (opts.restoreflush != 0) return false;

        // START_SIMPLIFIER (cover, COVER)
        if (!preprocessing && !lookingahead) reset_mode(SEARCH);
        set_mode(SIMPLIFY);
        set_mode(COVER);

        stats.cover.count++;

        if (propagated < trail.n)
        {
            init_watches();
            connect_watches();
            if (!propagate())
            {
                learn_empty_clause();
            }
            reset_watches();
        }

        long covered = cover_round();

        // STOP_SIMPLIFIER (cover, COVER)
        reset_mode(COVER);
        reset_mode(SIMPLIFY);
        if (!preprocessing && !lookingahead) set_mode(SEARCH);
        report('c', opts.reportall == 0 && covered == 0 ? 1 : 0);

        return covered != 0;
    }
}
