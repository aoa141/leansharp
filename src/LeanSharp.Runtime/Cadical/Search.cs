// Port of 'propagate.cpp', 'analyze.cpp', 'minimize.cpp', 'shrink.cpp', 'backtrack.cpp',
// 'decide.cpp', 'restart.cpp' and 'rephase.cpp'.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    /*------------------------------------------------------------------------*/
    // 'propagate.cpp'

    int assignment_level(int lit, Clause reason)
    {
        if (reason == null || reason == external_reason) return level;
        int res = 0;
        var lits = reason.literals;
        for (int i = 0; i < reason.size; i++)
        {
            int other = lits[i];
            if (other == lit) continue;
            int tmp = @var(other).level;
            if (tmp > res) res = tmp;
        }
        return res;
    }

    public void build_chain_for_units(int lit, Clause reason, bool forced)
    {
        if (!lrat) return;
        if (opts.chrono != 0 && assignment_level(lit, reason) != 0 && !forced) return;
        else if (opts.chrono == 0 && level != 0 && !forced) return;
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

    public void build_chain_for_empty()
    {
        if (!lrat || !lrat_chain.empty()) return;
        var lits = conflict.literals;
        for (int i = 0; i < conflict.size; i++)
        {
            int lit = lits[i];
            uint uidx = vlit(-lit);
            ulong id = unit_clauses(uidx);
            lrat_chain.push_back(id);
        }
        lrat_chain.push_back(conflict.id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void search_assign(int lit, Clause reason)
    {
        int idx = vidx(lit);
        bool from_external = reason == external_reason;
        ref Var v = ref vtab[idx];
        int lit_level;
        if (reason == null) lit_level = 0;
        else if (reason == decision_reason) { lit_level = level; reason = null; }
        else if (opts.chrono != 0) lit_level = assignment_level(lit, reason);
        else lit_level = level;
        if (lit_level == 0) reason = null;
        v.level = lit_level;
        v.trail = trail.n;
        v.reason = reason;
        num_assigned++;
        if (lit_level == 0 && !from_external) learn_unit_clause(lit);
        sbyte tmp = (sbyte)(lit > 0 ? 1 : -1);
        vals[idx] = tmp;
        vals[-idx] = (sbyte)-tmp;
        if (!searching_lucky_phases) phases.saved[idx] = tmp;
        trail.push_back(lit);
        if (lrat_chain.n != 0) lrat_chain.clear();
    }

    public void assign_unit(int lit) => search_assign(lit, null);

    public void search_assume_decision(int lit)
    {
        new_trail_level(lit);
        notify_decision();
        search_assign(lit, decision_reason);
    }

    public void search_assign_driving(int lit, Clause c)
    {
        search_assign(lit, c);
        notify_assignments();
    }

    public void search_assign_external(int lit)
    {
        search_assign(lit, external_reason);
        notify_assignments();
    }

    public bool propagate()
    {
        long before = propagated;
        sbyte* vals = this.vals;
        var wtab = this.wtab;
        var trail = this.trail;

        while (conflict == null && propagated != trail.n)
        {
            int lit = -trail.a[propagated++];
            var ws = wtab[(lit < 0 ? 1u : 0u) + 2u * (uint)(lit < 0 ? -lit : lit)];
            Watch[] wa = ws.a;
            int eow = ws.n;
            int i = 0, j = 0;

            while (i != eow)
            {
                // 'Watch w = *j++ = *i++' but avoid the (GC write barrier) copy while 'i == j'.
                Watch w = wa[i];
                if (i != j) wa[j] = w;
                i++; j++;
                sbyte b = vals[w.blit];
                if (b > 0) continue;

                if (w.size == 2)
                {
                    if (b < 0) conflict = w.clause;
                    else
                    {
                        if (lrat) build_chain_for_units(w.blit, w.clause, false);
                        search_assign(w.blit, w.clause);
                    }
                }
                else
                {
                    if (conflict != null) break;
                    Clause c = w.clause;
                    if (c.garbage) { j--; continue; }
                    int[] lits = c.literals;
                    int other = lits[0] ^ lits[1] ^ lit;
                    sbyte u = vals[other];
                    if (u > 0) wa[j - 1].blit = other;
                    else
                    {
                        int size = c.size;
                        int middle = c.pos;
                        int k = middle;
                        int r = 0;
                        sbyte v = -1;
                        while (k != size && (v = vals[r = lits[k]]) < 0) k++;
                        if (v < 0)
                        {
                            k = 2;
                            while (k != middle && (v = vals[r = lits[k]]) < 0) k++;
                        }
                        c.pos = k;
                        if (v > 0)
                        {
                            wa[j - 1].blit = r;
                        }
                        else if (v == 0)
                        {
                            lits[0] = other;
                            lits[1] = r;
                            lits[k] = lit;
                            watch_literal(r, lit, c);
                            j--;
                        }
                        else if (u == 0)
                        {
                            if (lrat) build_chain_for_units(other, c, false);
                            search_assign(other, c);
                            if (opts.chrono > 1)
                            {
                                int other_level = @var(other).level;
                                if (other_level > @var(lit).level)
                                {
                                    int pos, s = 0;
                                    for (pos = 2; pos < size; pos++)
                                        if (@var(s = lits[pos]).level == other_level) break;
                                    lits[pos] = lit;
                                    lits[0] = other;
                                    lits[1] = s;
                                    watch_literal(s, other, c);
                                    j--;
                                }
                            }
                        }
                        else
                        {
                            conflict = c;
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

        if (searching_lucky_phases) { }
        else
        {
            stats.propagations.search += propagated - before;
            if (conflict == null) no_conflict_until = propagated;
            else
            {
                if (stable) stats.stabconflicts++;
                stats.conflicts++;
                no_conflict_until = control[level].trail;
            }
        }
        return conflict == null;
    }

    public void propergate()
    {
        while (propergated != trail.n)
        {
            int lit = -trail[propergated++];
            var ws = watches(lit);
            Watch[] wa = ws.a;
            int eow = ws.n;
            int i = 0, j = 0;
            while (i != eow)
            {
                Watch w = wa[j++] = wa[i++];
                if (w.size == 2) continue;
                Clause c = w.clause;
                if (c.garbage) { j--; continue; }
                int[] lits = c.literals;
                int other = lits[0] ^ lits[1] ^ lit;
                sbyte u = val(other);
                if (u > 0) continue;
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
                lits[0] = other;
                lits[1] = r;
                lits[k] = lit;
                watch_literal(r, lit, c);
                j--;
            }
            if (j != i)
            {
                while (i != eow) wa[j++] = wa[i++];
                ws.shrink(j);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    // 'analyze.cpp'

    public void learn_empty_clause()
    {
        build_chain_for_empty();
        ulong id = ++clause_id;
        if (proof != null) proof.add_derived_empty_clause(id, lrat_chain);
        unsat = true;
        conflict_id = id;
        marked_failed = true;
        conclusion.push_back(id);
        lrat_chain.clear();
    }

    public void learn_unit_clause(int lit)
    {
        ulong id = ++clause_id;
        if (lrat || frat)
        {
            uint uidx = vlit(lit);
            unit_clauses(uidx) = id;
        }
        if (proof != null) proof.add_derived_unit_clause(id, lit, lrat_chain);
        mark_fixed(lit);
    }

    public void bump_queue(int lit)
    {
        int idx = vidx(lit);
        if (links[idx].next == 0) return;
        queue.dequeue(links, idx);
        queue.enqueue(links, idx);
        btab[idx] = ++stats.bumped;
        if (vals[idx] == 0) update_queue_unassigned(idx);
    }

    static bool evsids_limit_hit(double score) => score > 1e150;

    public void rescale_variable_scores()
    {
        stats.rescored++;
        double divider = score_inc;
        for (int idx = 1; idx <= max_var; idx++)
        {
            double tmp = stab[idx];
            if (tmp > divider) divider = tmp;
        }
        double factor = 1.0 / divider;
        for (int idx = 1; idx <= max_var; idx++) stab[idx] *= factor;
        score_inc *= factor;
    }

    public void bump_variable_score(int lit)
    {
        int idx = vidx(lit);
        double old_score = score(idx);
        double new_score = old_score + score_inc;
        if (evsids_limit_hit(new_score))
        {
            rescale_variable_scores();
            old_score = score(idx);
            new_score = old_score + score_inc;
        }
        score(idx) = new_score;
        if (scores.contains((uint)idx)) scores.update((uint)idx);
    }

    public void bump_variable(int lit)
    {
        if (use_scores()) bump_variable_score(lit);
        else bump_queue(lit);
    }

    public void bump_variable_score_inc()
    {
        double f = 1e3 / opts.scorefactor;
        double new_score_inc = score_inc * f;
        if (evsids_limit_hit(new_score_inc))
        {
            rescale_variable_scores();
            new_score_inc = score_inc * f;
        }
        score_inc = new_score_inc;
    }

    public void bump_variables()
    {
        if (opts.bumpreason != 0) bump_also_all_reason_literals();
        if (!use_scores())
        {
            var bt = btab;
            CUtil.msort(opts.radixsortlim, analyzed.AsSpan(), a => (ulong)bt[a < 0 ? -a : a]);
        }
        for (int k = 0; k < analyzed.n; k++) bump_variable(analyzed[k]);
        if (use_scores()) bump_variable_score_inc();
    }

    public int recompute_glue(Clause c)
    {
        int res = 0;
        long stamp = ++stats.recomputed;
        var lits = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int lev = @var(lits[i]).level;
            if (gtab[lev] == stamp) continue;
            gtab[lev] = stamp;
            res++;
        }
        return res;
    }

    void bump_clause(Clause c)
    {
        uint used = c.used;
        c.used = 1;
        if (c.keep) return;
        if (c.hyper) return;
        if (!c.redundant) return;
        int new_glue = recompute_glue(c);
        if (new_glue < c.glue) promote_clause(c, new_glue);
        else if (used != 0 && c.glue <= opts.reducetier2glue) c.used = 2;
    }

    void analyze_literal(int lit, ref int open, ref int resolvent_size, ref int antecedent_size)
    {
        ref Var v = ref @var(lit);
        ref Flags f = ref flags(lit);
        if (v.level == 0)
        {
            if (f.seen || !lrat) return;
            f.seen = true;
            unit_analyzed.push_back(lit);
            uint uidx = vlit(-lit);
            ulong id = unit_clauses(uidx);
            unit_chain.push_back(id);
            return;
        }
        ++antecedent_size;
        if (f.seen) return;
        if (v.reason == external_reason)
        {
            v.reason = learn_external_reason_clause(-lit, 0, true);
        }
        f.seen = true;
        analyzed.push_back(lit);
        if (v.level < level) clause.push_back(lit);
        ref Level l = ref control[v.level];
        if (l.seen_count++ == 0) levels.push_back(v.level);
        if (v.trail < l.seen_trail) l.seen_trail = v.trail;
        ++resolvent_size;
        if (v.level == level) open++;
    }

    void analyze_reason(int lit, Clause reason, ref int open, ref int resolvent_size, ref int antecedent_size)
    {
        bump_clause(reason);
        if (lrat) lrat_chain.push_back(reason.id);
        var lits = reason.literals;
        for (int i = 0; i < reason.size; i++)
        {
            int other = lits[i];
            if (other != lit) analyze_literal(other, ref open, ref resolvent_size, ref antecedent_size);
        }
    }

    bool bump_also_reason_literal(int lit)
    {
        ref Flags f = ref flags(lit);
        if (f.seen) return false;
        ref Var v = ref @var(lit);
        if (v.level == 0) return false;
        f.seen = true;
        analyzed.push_back(lit);
        return true;
    }

    void bump_also_reason_literals(int lit, int limit)
    {
        ref Var v = ref @var(lit);
        if (v.level == 0) return;
        Clause reason = v.reason;
        if (reason == null || reason == external_reason) return;
        var lits = reason.literals;
        for (int i = 0; i < reason.size; i++)
        {
            int other = lits[i];
            if (other == lit) continue;
            if (!bump_also_reason_literal(other)) continue;
            if (limit < 2) continue;
            bump_also_reason_literals(-other, limit - 1);
        }
    }

    void bump_also_all_reason_literals()
    {
        for (int k = 0; k < clause.n; k++)
            bump_also_reason_literals(-clause[k], opts.bumpreasondepth + (stable ? 1 : 0));
    }

    public void clear_unit_analyzed_literals()
    {
        for (int k = 0; k < unit_analyzed.n; k++)
        {
            flags(unit_analyzed[k]).seen = false;
        }
        unit_analyzed.clear();
    }

    public void clear_analyzed_literals()
    {
        for (int k = 0; k < analyzed.n; k++)
        {
            flags(analyzed[k]).seen = false;
        }
        analyzed.clear();
    }

    public void clear_analyzed_levels()
    {
        for (int k = 0; k < levels.n; k++)
        {
            int l = levels[k];
            if (l < control.n) control[l].reset();
        }
        levels.clear();
    }

    ulong analyze_trail_negative_rank(int a)
    {
        ref Var v = ref @var(a);
        ulong res = (ulong)(uint)v.level;
        res <<= 32;
        res |= (uint)v.trail;
        return ~res;
    }

    Clause new_driving_clause(int glue, out int jump)
    {
        int size = clause.n;
        Clause res;
        if (size == 0)
        {
            jump = 0;
            res = null;
        }
        else if (size == 1)
        {
            iterating = true;
            jump = 0;
            res = null;
        }
        else
        {
            CUtil.msort(opts.radixsortlim, clause.AsSpan(), analyze_trail_negative_rank);
            jump = @var(clause[1]).level;
            res = new_learned_redundant_clause(glue);
            res.used = (byte)(1 + (glue <= opts.reducetier2glue ? 1 : 0));
        }
        return res;
    }

    int otfs_find_backtrack_level(ref int forced)
    {
        int res = 0;
        var lits = conflict.literals;
        for (int i = 0; i < conflict.size; i++)
        {
            int lit = lits[i];
            int tmp = @var(lit).level;
            if (tmp == level) forced = lit;
            else if (tmp > res) res = tmp;
        }
        return res;
    }

    int find_conflict_level(out int forced)
    {
        int res = 0, count = 0;
        forced = 0;
        var lits = conflict.literals;
        for (int i = 0; i < conflict.size; i++)
        {
            int lit = lits[i];
            int tmp = @var(lit).level;
            if (tmp > res)
            {
                res = tmp;
                forced = lit;
                count = 1;
            }
            else if (tmp == res)
            {
                count++;
                if (res == level && count > 1) break;
            }
        }
        int size = conflict.size;
        for (int i = 0; i < 2; i++)
        {
            int lit = lits[i];
            int highest_position = i;
            int highest_literal = lit;
            int highest_level = @var(highest_literal).level;
            for (int j = i + 1; j < size; j++)
            {
                int other = lits[j];
                int tmp = @var(other).level;
                if (highest_level >= tmp) continue;
                highest_literal = other;
                highest_position = j;
                highest_level = tmp;
                if (highest_level == res) break;
            }
            if (highest_position == i) continue;
            if (highest_position > 1)
                WatchOps.remove_watch(watches(lit), conflict);
            lits[highest_position] = lit;
            lits[i] = highest_literal;
            if (highest_position > 1)
                watch_literal(highest_literal, lits[i == 0 ? 1 : 0], conflict);
        }
        if (count != 1) forced = 0;
        return res;
    }

    int determine_actual_backtrack_level(int jump)
    {
        int res;
        if (opts.chrono == 0) res = jump;
        else if (opts.chronoalways != 0)
        {
            stats.chrono++;
            res = level - 1;
        }
        else if (jump >= level - 1) res = jump;
        else if (jump < assumptions.n) res = jump;
        else if (level - jump > opts.chronolevelim)
        {
            stats.chrono++;
            res = level - 1;
        }
        else if (opts.chronoreusetrail != 0)
        {
            int best_idx = 0, best_pos = 0;
            if (use_scores())
            {
                for (int i = control[jump + 1].trail; i < trail.n; i++)
                {
                    int idx = Math.Abs(trail[i]);
                    if (best_idx != 0 && !score_smaller((uint)best_idx, (uint)idx)) continue;
                    best_idx = idx;
                    best_pos = i;
                }
            }
            else
            {
                for (int i = control[jump + 1].trail; i < trail.n; i++)
                {
                    int idx = Math.Abs(trail[i]);
                    if (best_idx != 0 && bumped(best_idx) >= bumped(idx)) continue;
                    best_idx = idx;
                    best_pos = i;
                }
            }
            res = jump;
            while (res < level - 1 && control[res + 1].trail <= best_pos) res++;
            if (res != jump) stats.chrono++;
        }
        else res = jump;
        return res;
    }

    public void eagerly_subsume_recently_learned_clauses(Clause c)
    {
        mark(c);
        long lim_ = stats.eagertried + opts.eagersubsumelim;
        int it = clauses.n;
        while (it != 0 && stats.eagertried++ <= lim_)
        {
            Clause d = clauses[--it];
            if (c == d) continue;
            if (d.garbage) continue;
            if (!d.redundant) continue;
            int needed = c.size;
            var dl = d.literals;
            for (int i = 0; i < d.size; i++)
            {
                if (marked(dl[i]) <= 0) continue;
                if (--needed == 0) break;
            }
            if (needed != 0) continue;
            stats.eagersub++;
            stats.subsumed++;
            mark_garbage(d);
        }
        unmark(c);
    }

    readonly Vec<int> otfs_sorted = new();

    Clause on_the_fly_strengthen(Clause new_conflict, int uip)
    {
        var sorted = otfs_sorted;
        sorted.clear();
        ++stats.otfs.strengthened;
        int[] lits = new_conflict.literals;
        int other_init = lits[0] ^ lits[1] ^ uip;
        int old_size = new_conflict.size;
        int new_size = 0;
        for (int i = 0; i < old_size; ++i)
        {
            int o = lits[i];
            sorted.push_back(o);
            if (@var(o).level != 0) lits[new_size++] = o;
        }
        int other = lits[0] ^ lits[1] ^ uip;
        lits[0] = other;
        lits[1] = lits[--new_size];
        if (other_init != other) WatchOps.remove_watch(watches(other_init), new_conflict);
        WatchOps.remove_watch(watches(uip), new_conflict);
        if (lrat)
        {
            for (int k = 0; k < unit_chain.n; k++) mini_chain.push_back(unit_chain[k]);
            for (int k = lrat_chain.n - 1; k >= 0; k--) mini_chain.push_back(lrat_chain[k]);
            lrat_chain.clear();
            clear_unit_analyzed_literals();
            unit_chain.clear();
        }
        {
            int highest_pos = 0;
            int highest_level = 0;
            for (int i = 1; i < new_size; i++)
            {
                int o = lits[i];
                int lev = @var(o).level;
                if (lev <= highest_level) continue;
                highest_pos = i;
                highest_level = lev;
            }
            if (highest_pos != 1) (lits[1], lits[highest_pos]) = (lits[highest_pos], lits[1]);
            if (new_size == 1)
            {
                return null;
            }
            else
            {
                otfs_strengthen_clause(new_conflict, uip, new_size, sorted);
            }
        }
        if (other_init != other) watch_literal(other, lits[1], new_conflict);
        else WatchOps.update_watch_size(watches(other), lits[1], new_conflict);
        watch_literal(lits[1], other, new_conflict);
        sorted.clear();
        return new_conflict;
    }

    void otfs_subsume_clause(Clause subsuming, Clause subsumed)
    {
        stats.subsumed++;
        if (subsumed.redundant) stats.subred++;
        else stats.subirr++;
        if (subsumed.redundant || !subsuming.redundant)
        {
            mark_garbage(subsumed);
            return;
        }
        subsuming.redundant = false;
        if (proof != null) proof.strengthen(subsuming.id);
        mark_garbage(subsumed);
        stats.current.irredundant++;
        stats.added.irredundant++;
        stats.irrlits += subsuming.size;
        stats.current.redundant--;
        stats.added.redundant--;
    }

    void otfs_strengthen_clause(Clause c, int lit, int new_size, Vec<int> old)
    {
        stats.strengthened++;
        shrink_clause(c, new_size);
        if (proof != null) proof.otfs_strengthen_clause(c, old, mini_chain);
        if (!c.redundant) mark_removed(lit);
        mini_chain.clear();
        c.used = 1;
    }

    public void analyze()
    {
        averages.current.trail_fast.update(num_assigned);
        averages.current.trail_slow.update(num_assigned);

        if (opts.chrono != 0 || external_prop)
        {
            int conflict_level = find_conflict_level(out int forced);
            if (forced != 0)
            {
                backtrack(conflict_level - 1);
                build_chain_for_units(forced, conflict, false);
                search_assign_driving(forced, conflict);
                conflict = null;
                return;
            }
            backtrack(conflict_level);
        }

        if (level == 0)
        {
            learn_empty_clause();
            return;
        }

        Clause reason = conflict;
        var t = trail;
        int i = t.n;
        int open = 0;
        int uip = 0;
        int resolvent_size = 0;
        int antecedent_size = 1;
        int conflict_size = 0;
        int resolved = 0;
        bool otfs = opts.otfs != 0;

        for (; ; )
        {
            antecedent_size = 1;
            analyze_reason(uip, reason, ref open, ref resolvent_size, ref antecedent_size);
            if (resolved == 0) conflict_size = antecedent_size - 1;

            if (otfs && resolved > 0 && antecedent_size > 2 && resolvent_size < antecedent_size)
            {
                int other = reason.literals[0] ^ reason.literals[1] ^ uip;
                reason = on_the_fly_strengthen(reason, uip);
                if (opts.bump != 0) bump_variables();
                if (reason == null)
                {
                    uip = -other;
                    mini_chain.reverse();
                    for (int k = 0; k < mini_chain.n; k++) lrat_chain.push_back(mini_chain[k]);
                    mini_chain.clear();
                    clear_analyzed_levels();
                    clause.clear();
                    break;
                }
                if (resolved == 1 && resolvent_size < conflict_size)
                {
                    otfs_subsume_clause(reason, conflict);
                    --conflict_size;
                    ++stats.otfs.subsumed;
                    ++stats.subsumed;
                    ++stats.conflicts;
                }
                conflict = reason;
                if (open == 1)
                {
                    int forced = 0;
                    int conflict_level = otfs_find_backtrack_level(ref forced);
                    int new_level_ = determine_actual_backtrack_level(conflict_level);
                    averages.current.level.update(new_level_);
                    backtrack(new_level_);
                    search_assign_driving(forced, conflict);
                    conflict = null;
                    clear_analyzed_literals();
                    clear_analyzed_levels();
                    clause.clear();
                    return;
                }
                resolved = 0;
                clear_analyzed_literals();
                clause.clear();
                resolvent_size = 0;
                antecedent_size = 1;
                open = 0;
                analyze_reason(0, reason, ref open, ref resolvent_size, ref antecedent_size);
                conflict_size = antecedent_size - 1;
            }

            ++resolved;

            uip = 0;
            while (uip == 0)
            {
                int lit = t[--i];
                if (!flags(lit).seen) continue;
                if (@var(lit).level == level) uip = lit;
            }
            if (--open == 0) break;
            reason = @var(uip).reason;
            if (reason == external_reason)
            {
                reason = learn_external_reason_clause(-uip, 0, true);
                @var(uip).reason = reason;
            }
            --resolvent_size;
        }
        clause.push_back(-uip);

        int size = clause.n;
        int glue = levels.n - 1;
        averages.current.glue_fast.update(glue);
        averages.current.glue_slow.update(glue);
        stats.learned.literals += size;
        stats.learned.clauses++;

        if (size > 1)
        {
            if (opts.shrink != 0) shrink_and_minimize_clause();
            else if (opts.minimize != 0) minimize_clause();
            size = clause.n;
            if (opts.bump != 0) bump_variables();
        }

        stats.units += size == 1 ? 1 : 0;
        stats.binaries += size == 2 ? 1 : 0;
        averages.current.size.update(size);

        if (lrat)
        {
            for (int k = 0; k < unit_chain.n; k++) lrat_chain.push_back(unit_chain[k]);
            unit_chain.clear();
            lrat_chain.reverse();
        }

        Clause driving_clause = new_driving_clause(glue, out int jump);
        averages.current.jump.update(jump);

        int new_level = determine_actual_backtrack_level(jump);
        averages.current.level.update(new_level);
        backtrack(new_level);

        if (uip != 0) search_assign_driving(-uip, driving_clause);
        else learn_empty_clause();

        if (stable) reluctant.tick();

        clear_analyzed_literals();
        clear_unit_analyzed_literals();
        clear_analyzed_levels();
        clause.clear();
        conflict = null;
        lrat_chain.clear();

        if (driving_clause != null && opts.eagersubsume != 0)
            eagerly_subsume_recently_learned_clauses(driving_clause);
    }

    public void iterate()
    {
        iterating = false;
        report('i');
    }

    /*------------------------------------------------------------------------*/
    // 'minimize.cpp'

    public bool minimize_literal(int lit, int depth = 0)
    {
        ref Flags f = ref flags(lit);
        ref Var v = ref @var(lit);
        if (v.level == 0 || f.removable || f.keep) return true;
        if (v.reason == null || f.poison || v.level == level) return false;
        ref Level l = ref control[v.level];
        if (depth == 0 && l.seen_count < 2) return false;
        if (v.trail <= l.seen_trail) return false;
        if (depth > opts.minimizedepth) return false;
        bool res = true;
        if (v.reason == external_reason)
        {
            v.reason = learn_external_reason_clause(lit, 0, true);
            if (v.reason == null) return true;
        }
        Clause reason = v.reason;
        var lits = reason.literals;
        int end = reason.size;
        for (int i = 0; res && i != end; i++)
        {
            int other = lits[i];
            if (other == lit) continue;
            res = minimize_literal(-other, depth + 1);
        }
        // 'f' may have been invalidated? No: ftab is not resized during minimization.
        ref Flags f2 = ref flags(lit);
        if (res) f2.removable = true;
        else f2.poison = true;
        minimized.push_back(lit);
        return res;
    }

    readonly Vec<int> minimize_stack = new();

    public void minimize_clause()
    {
        minimize_sort_clause();
        int end = clause.n;
        int j = 0, i = 0;
        var stack = minimize_stack;
        for (; i != end; i++)
        {
            if (minimize_literal(-clause[i]))
            {
                if (lrat)
                {
                    calculate_minimize_chain(-clause[i], stack);
                    for (int k = 0; k < mini_chain.n; k++) minimize_chain.push_back(mini_chain[k]);
                    mini_chain.clear();
                }
                stats.minimized++;
            }
            else flags(clause[j++] = clause[i]).keep = true;
        }
        if (j != end) clause.resize(j);
        clear_minimized_literals();
        for (int k = minimize_chain.n - 1; k >= 0; k--) lrat_chain.push_back(minimize_chain[k]);
        minimize_chain.clear();
    }

    public void calculate_minimize_chain(int lit, Vec<int> stack)
    {
        stack.push_back(vidx(lit));
        while (!stack.empty())
        {
            int idx = stack.back();
            stack.pop_back();
            if (idx < 0)
            {
                ref Var vv = ref @var(idx);
                mini_chain.push_back(vv.reason.id);
                continue;
            }
            ref Flags f = ref flags(idx);
            ref Var v = ref @var(idx);
            if (f.keep || f.added || f.poison) continue;
            if (v.level == 0)
            {
                if (f.seen) continue;
                f.seen = true;
                unit_analyzed.push_back(idx);
                int ulit = val(idx) > 0 ? idx : -idx;
                uint uidx = vlit(ulit);
                ulong id = unit_clauses(uidx);
                unit_chain.push_back(id);
                continue;
            }
            f.added = true;
            stack.push_back(-idx);
            var reason = v.reason;
            var lits = reason.literals;
            for (int i = 0; i < reason.size; i++)
            {
                int other = lits[i];
                if (other == idx) continue;
                stack.push_back(vidx(other));
            }
        }
    }

    public void minimize_sort_clause()
    {
        var vt = vtab;
        CUtil.msort(opts.radixsortlim, clause.AsSpan(), a => (ulong)(uint)vt[a < 0 ? -a : a].trail);
    }

    public void clear_minimized_literals()
    {
        for (int k = 0; k < minimized.n; k++)
        {
            ref Flags f = ref flags(minimized[k]);
            f.poison = f.removable = f.shrinkable = f.added = false;
        }
        for (int k = 0; k < clause.n; k++)
        {
            ref Flags f = ref flags(clause[k]);
            f.keep = f.shrinkable = f.added = false;
        }
        minimized.clear();
    }

    /*------------------------------------------------------------------------*/
    // 'shrink.cpp'. Reverse iterators over 'clause' are represented by indices into
    // 'clause' (moving towards smaller indices).

    void reset_shrinkable()
    {
        for (int k = 0; k < shrinkable.n; k++) flags(shrinkable[k]).shrinkable = false;
    }

    void mark_shrinkable_as_removable(int blevel, int minimized_start)
    {
        for (int k = 0; k < shrinkable.n; k++)
        {
            int lit = shrinkable[k];
            ref Flags f = ref flags(lit);
            f.shrinkable = false;
            if (f.removable) continue;
            f.removable = true;
            minimized.push_back(lit);
        }
    }

    int shrink_literal(int lit, int blevel, uint max_trail)
    {
        ref Flags f = ref flags(lit);
        ref Var v = ref @var(lit);
        if (v.level == 0) return 0;
        if (v.reason == external_reason)
        {
            v.reason = learn_external_reason_clause(-lit, 0, true);
            if (v.reason == null) return 0;
        }
        if (f.shrinkable) return 0;
        if (v.level < blevel)
        {
            if (f.removable) return 0;
            bool always_minimize_on_lower_blevel = opts.shrink > 2;
            if (always_minimize_on_lower_blevel && minimize_literal(-lit, 1)) return 0;
            return -1;
        }
        f.shrinkable = true;
        f.poison = false;
        shrinkable.push_back(lit);
        if (opts.shrinkreap != 0)
        {
            uint dist = max_trail - (uint)v.trail;
            reap.push(dist);
        }
        return 1;
    }

    uint shrunken_block_uip(int uip, int blevel, int rbegin_block, int rend_block, int minimized_start, int uip0)
    {
        uint block_shrunken = 0;
        clause[rbegin_block] = -uip;
        ref Var v = ref @var(-uip);
        ref Level l = ref control[v.level];
        l.seen_trail = v.trail;
        l.seen_count = 1;
        ref Flags f = ref flags(-uip);
        if (!f.seen)
        {
            analyzed.push_back(-uip);
            f.seen = true;
        }
        flags(-uip).keep = true;
        for (int p = rbegin_block - 1; p != rend_block; --p)
        {
            int lit = clause[p];
            if (lit == -uip0) continue;
            clause[p] = uip0;
            ++block_shrunken;
        }
        mark_shrinkable_as_removable(blevel, minimized_start);
        return block_shrunken;
    }

    void shrunken_block_no_uip(int rbegin_block, int rend_block, ref uint block_minimized, int uip0)
    {
        for (int p = rbegin_block; p != rend_block; --p)
        {
            int lit = clause[p];
            if (opts.minimize != 0 && minimize_literal(-lit))
            {
                ++block_minimized;
                clause[p] = uip0;
            }
            else
            {
                flags(lit).keep = true;
            }
        }
    }

    void push_literals_of_block(int rbegin_block, int rend_block, int blevel, uint max_trail)
    {
        for (int p = rbegin_block; p != rend_block; --p)
        {
            int lit = clause[p];
            shrink_literal(lit, blevel, max_trail);
        }
    }

    int shrink_next(int blevel, ref uint open, ref uint max_trail)
    {
        var t = trail;
        if (opts.shrinkreap != 0)
        {
            uint dist = reap.pop();
            --open;
            uint pos = max_trail - dist;
            int uip = t[(int)pos];
            return uip;
        }
        else
        {
            int uip;
            do
            {
                uip = t[(int)max_trail--];
            } while (!flags(uip).shrinkable);
            --open;
            return uip;
        }
    }

    uint shrink_along_reason(int uip, int blevel, bool resolve_large_clauses, ref bool failed_ptr, uint max_trail)
    {
        uint open = 0;
        ref Var v = ref @var(uip);
        if (resolve_large_clauses || v.reason.size == 2)
        {
            Clause c = v.reason;
            var lits = c.literals;
            for (int i = 0; i < c.size; i++)
            {
                int lit = lits[i];
                if (lit == uip) continue;
                int tmp = shrink_literal(lit, blevel, max_trail);
                if (tmp < 0)
                {
                    failed_ptr = true;
                    break;
                }
                if (tmp > 0) ++open;
            }
        }
        else failed_ptr = true;
        return open;
    }

    uint shrink_block(int rbegin_lits, int rend_block, int blevel, ref uint open, ref uint block_minimized, int uip0, uint max_trail)
    {
        bool resolve_large_clauses = opts.shrink > 1;
        bool failed = false;
        uint block_shrunken = 0;
        int minimized_start = minimized.n;
        int uip = uip0;
        uint max_trail2 = max_trail;

        push_literals_of_block(rbegin_lits, rend_block, blevel, max_trail);
        while (!failed)
        {
            uip = shrink_next(blevel, ref open, ref max_trail);
            if (open == 0) break;
            open += shrink_along_reason(uip, blevel, resolve_large_clauses, ref failed, max_trail2);
        }

        if (failed)
        {
            reset_shrinkable();
            shrunken_block_no_uip(rbegin_lits, rend_block, ref block_minimized, uip0);
        }
        else
            block_shrunken = shrunken_block_uip(uip, blevel, rbegin_lits, rend_block, minimized_start, uip0);

        if (opts.shrinkreap != 0) reap.clear();
        shrinkable.clear();
        return block_shrunken;
    }

    int minimize_and_shrink_block(int rbegin_block, ref uint total_shrunken, ref uint total_minimized, int uip0)
    {
        int blevel;
        uint open = 0;
        uint max_trail;
        int rend_block;
        {
            int lit = clause[rbegin_block];
            int idx = vidx(lit);
            blevel = vtab[idx].level;
            max_trail = (uint)vtab[idx].trail;
            rend_block = rbegin_block;
            bool finished;
            do
            {
                int lit2 = clause[--rend_block];
                int idx2 = vidx(lit2);
                finished = blevel != vtab[idx2].level;
                if (!finished && (uint)vtab[idx2].trail > max_trail) max_trail = (uint)vtab[idx2].trail;
                ++open;
            } while (!finished);
        }
        uint block_shrunken = 0, block_minimized = 0;
        if (open < 2)
        {
            flags(clause[rbegin_block]).keep = true;
            minimized.push_back(clause[rbegin_block]);
        }
        else
            block_shrunken = shrink_block(rbegin_block, rend_block, blevel, ref open, ref block_minimized, uip0, max_trail);
        total_shrunken += block_shrunken;
        total_minimized += block_minimized;
        return rend_block;
    }

    readonly Vec<int> old_clause_lrat = new();

    public void shrink_and_minimize_clause()
    {
        CUtil.msort(opts.radixsortlim, clause.AsSpan(), analyze_trail_negative_rank);
        uint total_shrunken = 0;
        uint total_minimized = 0;
        int rend_lits = 0; // 'clause.rend () - 1'
        int rend_block = clause.n - 1; // 'clause.rbegin ()'
        int uip0 = clause[0];
        old_clause_lrat.clear();
        if (lrat)
            for (int k = 0; k < clause.n; k++) old_clause_lrat.push_back(clause[k]);
        while (rend_block != rend_lits)
            rend_block = minimize_and_shrink_block(rend_block, ref total_shrunken, ref total_minimized, uip0);
        var stack = minimize_stack;
        {
            int i = 1;
            for (int j = 1; j < clause.n; ++j)
            {
                clause[i] = clause[j];
                if (lrat)
                {
                    if (clause[j] != old_clause_lrat[j])
                    {
                        calculate_minimize_chain(-old_clause_lrat[j], stack);
                        for (int k = 0; k < mini_chain.n; k++) minimize_chain.push_back(mini_chain[k]);
                        mini_chain.clear();
                    }
                }
                if (clause[j] == uip0) continue;
                ++i;
            }
            clause.resize(i);
        }
        stats.shrunken += total_shrunken;
        stats.minishrunken += total_minimized;
        clear_minimized_literals();
        for (int k = minimize_chain.n - 1; k >= 0; k--) lrat_chain.push_back(minimize_chain[k]);
        minimize_chain.clear();
    }

    /*------------------------------------------------------------------------*/
    // 'backtrack.cpp'

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void unassign(int lit)
    {
        vals[lit] = 0;
        vals[-lit] = 0;
        int idx = vidx(lit);
        num_assigned--;
        if (!scores.contains((uint)idx)) scores.push_back((uint)idx);
        if (queue.bumped < btab[idx]) update_queue_unassigned(idx);
    }

    public void update_target_and_best()
    {
        bool reset = rephased != 0 && stats.conflicts > last.rephase.conflicts;
        if (reset)
        {
            target_assigned = 0;
            if (rephased == 'B') best_assigned = 0;
        }
        if (no_conflict_until > target_assigned)
        {
            copy_phases(phases.target);
            target_assigned = no_conflict_until;
        }
        if (no_conflict_until > best_assigned)
        {
            copy_phases(phases.best);
            best_assigned = no_conflict_until;
        }
        if (reset)
        {
            report(rephased);
            rephased = (char)0;
        }
    }

    public void backtrack(int new_level = 0)
    {
        if (new_level == level) return;
        stats.backtracks++;
        update_target_and_best();
        int assigned = control[new_level + 1].trail;
        int end_of_trail = trail.n;
        int i = assigned, j = i;
        int reassigned = 0;
        notify_backtrack(new_level);
        var ta = trail.a;
        while (i < end_of_trail)
        {
            int lit = ta[i++];
            ref Var v = ref @var(lit);
            if (v.level > new_level)
            {
                unassign(lit);
            }
            else
            {
                ta[j] = lit;
                v.trail = j++;
                reassigned++;
            }
        }
        trail.shrink(j);
        if (propagated > assigned) propagated = assigned;
        if (propagated2 > assigned) propagated2 = assigned;
        if (no_conflict_until > assigned) no_conflict_until = assigned;
        propergated = 0;
        if (reassigned != 0) notify_assignments();
        control.shrink(new_level + 1);
        level = new_level;
        if (tainted_literal != 0)
        {
            if (val(tainted_literal) == 0) tainted_literal = 0;
        }
    }

    /*------------------------------------------------------------------------*/
    // 'decide.cpp'

    int next_decision_variable_on_queue()
    {
        long searched = 0;
        int res = queue.unassigned;
        while (vals[res] != 0)
        {
            res = links[res].prev;
            searched++;
        }
        if (searched != 0)
        {
            stats.searched += searched;
            update_queue_unassigned(res);
        }
        return res;
    }

    int next_decision_variable_with_best_score()
    {
        int res;
        for (; ; )
        {
            res = (int)scores.front();
            if (vals[res] == 0) break;
            scores.pop_front();
        }
        return res;
    }

    public int next_decision_variable()
    {
        if (use_scores()) return next_decision_variable_with_best_score();
        else return next_decision_variable_on_queue();
    }

    public int decide_phase(int idx, bool target)
    {
        int initial_phase = opts.phase != 0 ? 1 : -1;
        int phase_ = 0;
        if (force_saved_phase) phase_ = phases.saved[idx];
        if (phase_ == 0) phase_ = phases.forced[idx];
        if (phase_ == 0 && opts.forcephase != 0) phase_ = initial_phase;
        if (phase_ == 0 && target) phase_ = phases.target[idx];
        if (phase_ == 0) phase_ = phases.saved[idx];
        if (phase_ == 0) phase_ = initial_phase;
        return phase_ * idx;
    }

    public int likely_phase(int idx) => decide_phase(idx, false);

    public void new_trail_level(int lit)
    {
        level++;
        control.push_back(new Level(lit, trail.n));
    }

    public bool satisfied()
    {
        if (level < assumptions.n + (constraint.n != 0 ? 1 : 0)) return false;
        if (num_assigned < max_var) return false;
        if (propagated < trail.n) return false;
        return num_assigned == max_var;
    }

    bool better_decision(int lit, int other)
    {
        int lit_idx = Math.Abs(lit);
        int other_idx = Math.Abs(other);
        if (stable) return stab[lit_idx] > stab[other_idx];
        else return btab[lit_idx] > btab[other_idx];
    }

    public int decide()
    {
        int res = 0;
        if (level < assumptions.n)
        {
            int lit = assumptions[level];
            sbyte tmp = val(lit);
            if (tmp < 0) res = 20;
            else if (tmp > 0)
            {
                new_trail_level(0);
                notify_decision();
            }
            else search_assume_decision(lit);
        }
        else if (level == assumptions.n && constraint.n != 0)
        {
            int satisfied_lit = 0;
            int unassigned_lit = 0;
            int previous_lit = 0;
            int size_constraint = constraint.n;
            for (int i = 0; i != size_constraint; i++)
            {
                int lit = constraint[i];
                constraint[i] = previous_lit;
                previous_lit = lit;
                sbyte tmp = val(lit);
                if (tmp < 0) continue;
                if (tmp > 0)
                {
                    satisfied_lit = lit;
                    break;
                }
                if (unassigned_lit == 0 || better_decision(lit, unassigned_lit)) unassigned_lit = lit;
            }
            if (satisfied_lit != 0)
            {
                constraint[0] = satisfied_lit;
                new_trail_level(0);
                notify_decision();
            }
            else
            {
                if (size_constraint != 0)
                {
                    for (int i = 0; i + 1 != size_constraint; i++) constraint[i] = constraint[i + 1];
                    constraint[size_constraint - 1] = previous_lit;
                }
                if (unassigned_lit != 0) search_assume_decision(unassigned_lit);
                else
                {
                    unsat_constraint = true;
                    res = 20;
                }
            }
        }
        else
        {
            int decision = ask_decision();
            if (level < assumptions.n || (level == assumptions.n && constraint.n != 0))
            {
                res = decide();
            }
            else
            {
                stats.decisions++;
                if (decision == 0)
                {
                    int idx = next_decision_variable();
                    bool target = opts.target > 1 || (stable && opts.target != 0);
                    decision = decide_phase(idx, target);
                }
                search_assume_decision(decision);
            }
        }
        if (res != 0) marked_failed = false;
        return res;
    }

    /*------------------------------------------------------------------------*/
    // 'restart.cpp'

    public bool stabilizing()
    {
        if (opts.stabilize == 0) return false;
        if (stable && opts.stabilizeonly != 0) return true;
        if (stats.conflicts >= lim.stabilize)
        {
            report(stable ? ']' : '}');
            stable = !stable;
            if (stable) stats.stabphases++;
            PHASE("stabilizing", stats.stabphases,
                  $"reached stabilization limit {lim.stabilize} after {stats.conflicts} conflicts");
            inc.stabilize = (long)(inc.stabilize * (opts.stabilizefactor * 1e-2));
            if (inc.stabilize > opts.stabilizemaxint) inc.stabilize = opts.stabilizemaxint;
            lim.stabilize = stats.conflicts + inc.stabilize;
            if (lim.stabilize <= stats.conflicts) lim.stabilize = stats.conflicts + 1;
            swap_averages();
            PHASE("stabilizing", stats.stabphases,
                  $"new stabilization limit {lim.stabilize} at conflicts interval {inc.stabilize}");
            report(stable ? '[' : '{');
        }
        return stable;
    }

    public bool restarting()
    {
        if (opts.restart == 0) return false;
        if (level < assumptions.n + 2) return false;
        if (stabilizing()) return reluctant.Triggered();
        if (stats.conflicts <= lim.restart) return false;
        double f = averages.current.glue_fast;
        double margin = (100.0 + opts.restartmargin) / 100.0;
        double s = averages.current.glue_slow, l = margin * s;
        return l <= f;
    }

    public int reuse_trail()
    {
        int trivial_decisions = assumptions.n + (control[assumptions.n + 1].decision == 0 ? 1 : 0);
        if (opts.restartreusetrail == 0) return trivial_decisions;
        int next_decision = next_decision_variable();
        int res = trivial_decisions;
        if (use_scores())
        {
            while (res < level)
            {
                int decision = control[res + 1].decision;
                if (decision != 0 && score_smaller((uint)Math.Abs(decision), (uint)next_decision)) break;
                res++;
            }
        }
        else
        {
            long limit = bumped(next_decision);
            while (res < level)
            {
                int decision = control[res + 1].decision;
                if (decision != 0 && bumped(decision) < limit) break;
                res++;
            }
        }
        int reused = res - trivial_decisions;
        if (reused > 0)
        {
            stats.reused++;
            stats.reusedlevels += reused;
            if (stable) stats.reusedstable++;
        }
        return res;
    }

    public void restart()
    {
        stats.restarts++;
        stats.restartlevels += level;
        if (stable) stats.restartstable++;
        backtrack(reuse_trail());
        lim.restart = stats.conflicts + opts.restartint;
        report('R', 2);
    }

    /*------------------------------------------------------------------------*/
    // 'rephase.cpp'

    public bool rephasing()
    {
        if (opts.rephase == 0) return false;
        if (opts.forcephase != 0) return false;
        return stats.conflicts > lim.rephase;
    }

    char rephase_original()
    {
        stats.rephased.original++;
        sbyte v = (sbyte)(opts.phase != 0 ? 1 : -1);
        for (int idx = 1; idx <= max_var; idx++) phases.saved[idx] = v;
        return 'O';
    }

    char rephase_inverted()
    {
        stats.rephased.inverted++;
        sbyte v = (sbyte)(opts.phase != 0 ? -1 : 1);
        for (int idx = 1; idx <= max_var; idx++) phases.saved[idx] = v;
        return 'I';
    }

    char rephase_flipping()
    {
        stats.rephased.flipped++;
        for (int idx = 1; idx <= max_var; idx++) phases.saved[idx] = (sbyte)-phases.saved[idx];
        return 'F';
    }

    char rephase_random()
    {
        stats.rephased.random++;
        var random = new CRandom((ulong)opts.seed);
        random.add((ulong)stats.rephased.random);
        for (int idx = 1; idx <= max_var; idx++) phases.saved[idx] = (sbyte)(random.generate_bool() ? -1 : 1);
        return '#';
    }

    char rephase_best()
    {
        stats.rephased.best++;
        sbyte v;
        for (int idx = 1; idx <= max_var; idx++)
            if ((v = phases.best[idx]) != 0) phases.saved[idx] = v;
        return 'B';
    }

    char rephase_walk()
    {
        stats.rephased.walk++;
        walk();
        return 'W';
    }

    public void rephase()
    {
        stats.rephased.total++;
        report('~', 1);
        backtrack();
        clear_phases(phases.target);
        target_assigned = 0;
        long count = lim.rephased[stable ? 1 : 0]++;
        bool single;
        char type;
        if (opts.stabilize != 0 && opts.stabilizeonly != 0) single = true;
        else single = opts.stabilize == 0;
        bool walk_ = opts.walk != 0;
        if (single && !walk_)
        {
            switch (count % 8)
            {
                case 0: type = rephase_inverted(); break;
                case 1: type = rephase_best(); break;
                case 2: type = rephase_flipping(); break;
                case 3: type = rephase_best(); break;
                case 4: type = rephase_random(); break;
                case 5: type = rephase_best(); break;
                case 6: type = rephase_original(); break;
                default: type = rephase_best(); break;
            }
        }
        else if (single && walk_)
        {
            switch (count % 12)
            {
                case 0: type = rephase_inverted(); break;
                case 1: type = rephase_best(); break;
                case 2: type = rephase_walk(); break;
                case 3: type = rephase_flipping(); break;
                case 4: type = rephase_best(); break;
                case 5: type = rephase_walk(); break;
                case 6: type = rephase_random(); break;
                case 7: type = rephase_best(); break;
                case 8: type = rephase_walk(); break;
                case 9: type = rephase_original(); break;
                case 10: type = rephase_best(); break;
                default: type = rephase_walk(); break;
            }
        }
        else if (stable && !walk_)
        {
            if (count == 0) type = rephase_original();
            else if (count == 1) type = rephase_inverted();
            else
                switch ((count - 2) % 4)
                {
                    case 0: type = rephase_best(); break;
                    case 1: type = rephase_original(); break;
                    case 2: type = rephase_best(); break;
                    default: type = rephase_inverted(); break;
                }
        }
        else if (stable && walk_)
        {
            if (count == 0) type = rephase_original();
            else if (count == 1) type = rephase_inverted();
            else
                switch ((count - 2) % 6)
                {
                    case 0: type = rephase_best(); break;
                    case 1: type = rephase_walk(); break;
                    case 2: type = rephase_original(); break;
                    case 3: type = rephase_best(); break;
                    case 4: type = rephase_walk(); break;
                    default: type = rephase_inverted(); break;
                }
        }
        else if (!stable && (!walk_ || opts.walknonstable == 0))
        {
            if (count == 0) type = rephase_flipping();
            else
                switch ((count - 1) % 4)
                {
                    case 0: type = rephase_random(); break;
                    case 1: type = rephase_best(); break;
                    case 2: type = rephase_flipping(); break;
                    default: type = rephase_best(); break;
                }
        }
        else
        {
            if (count == 0) type = rephase_flipping();
            else
                switch ((count - 1) % 6)
                {
                    case 0: type = rephase_random(); break;
                    case 1: type = rephase_best(); break;
                    case 2: type = rephase_walk(); break;
                    case 3: type = rephase_flipping(); break;
                    case 4: type = rephase_best(); break;
                    default: type = rephase_walk(); break;
                }
        }
        long delta = opts.rephaseint * (stats.rephased.total + 1);
        lim.rephase = stats.conflicts + delta;
        last.rephase.conflicts = stats.conflicts;
        rephased = type;
        if (stable) shuffle_scores();
        else shuffle_queue();
    }
}
