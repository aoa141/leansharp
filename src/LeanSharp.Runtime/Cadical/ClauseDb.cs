// Port of 'clause.cpp', 'collect.cpp', 'reduce.cpp', 'watch.cpp', 'occs.cpp' and 'bins.cpp'.

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    /*------------------------------------------------------------------------*/
    // 'clause.cpp'

    public void mark_removed(Clause c, int except = 0)
    {
        var lits = c.literals;
        for (int i = 0; i < c.size; i++)
            if (lits[i] != except) mark_removed(lits[i]);
    }

    void mark_added(int lit, int size, bool redundant)
    {
        mark_subsume(lit);
        if (size == 3) mark_ternary(lit);
        if (!redundant) mark_block(lit);
    }

    public void mark_added(Clause c)
    {
        var lits = c.literals;
        for (int i = 0; i < c.size; i++) mark_added(lits[i], c.size, c.redundant);
    }

    public Clause new_clause(bool red, int glue = 0)
    {
        int size = clause.n;
        if (glue > size) glue = size;
        bool keep;
        if (!red) keep = true;
        else if (glue <= opts.reducetier1glue) keep = true;
        else keep = false;
        var c = new Clause(size);
        c.id = ++clause_id;
        c.keep = keep;
        c.redundant = red;
        c.glue = glue;
        c.size = size;
        c.pos = 2;
        Array.Copy(clause.a, c.literals, size);
        stats.current.total++;
        stats.added.total++;
        if (red)
        {
            stats.current.redundant++;
            stats.added.redundant++;
        }
        else
        {
            stats.irrlits += size;
            stats.current.irredundant++;
            stats.added.irredundant++;
        }
        clauses.push_back(c);
        if (likely_to_be_kept_clause(c)) mark_added(c);
        return c;
    }

    public void promote_clause(Clause c, int new_glue)
    {
        if (c.keep) return;
        if (c.hyper) return;
        int old_glue = c.glue;
        if (new_glue >= old_glue) return;
        if (!c.keep && new_glue <= opts.reducetier1glue)
        {
            stats.promoted1++;
            c.keep = true;
        }
        else if (old_glue > opts.reducetier2glue && new_glue <= opts.reducetier2glue)
        {
            stats.promoted2++;
            c.used = 2;
        }
        stats.improvedglue++;
        c.glue = new_glue;
    }

    public long shrink_clause(Clause c, int new_size)
    {
        int old_size = c.size;
        if (c.pos >= new_size) c.pos = 2;
        long old_bytes = c.bytes();
        c.size = new_size;
        long new_bytes = c.bytes();
        long res = old_bytes - new_bytes;
        if (c.redundant) promote_clause(c, Math.Min(c.size - 1, c.glue));
        else
        {
            int delta_size = old_size - new_size;
            stats.irrlits -= delta_size;
        }
        if (likely_to_be_kept_clause(c)) mark_added(c);
        return res;
    }

    public void deallocate_clause(Clause c) { }

    public void delete_clause(Clause c)
    {
        long bytes = c.bytes();
        stats.collected += bytes;
        if (c.garbage)
        {
            stats.garbage.bytes -= bytes;
            stats.garbage.clauses--;
            stats.garbage.literals -= c.size;
            if (proof != null && c.size == 2) proof.delete_clause(c);
        }
        deallocate_clause(c);
    }

    public void mark_garbage(Clause c)
    {
        if (proof != null && c.size != 2) proof.delete_clause(c);
        stats.current.total--;
        long bytes = c.bytes();
        if (c.redundant)
        {
            stats.current.redundant--;
        }
        else
        {
            stats.current.irredundant--;
            stats.irrlits -= c.size;
            mark_removed(c);
        }
        stats.garbage.bytes += bytes;
        stats.garbage.clauses++;
        stats.garbage.literals += c.size;
        c.garbage = true;
        c.used = 0;
    }

    public void assign_original_unit(ulong id, int lit)
    {
        int idx = vidx(lit);
        ref Var v = ref @var(idx);
        v.level = 0;
        v.trail = trail.n;
        v.reason = null;
        sbyte tmp = (sbyte)CUtil.sign(lit);
        set_val(idx, tmp);
        trail.push_back(lit);
        num_assigned++;
        uint uidx = vlit(lit);
        if (lrat || frat) unit_clauses(uidx) = id;
        mark_fixed(lit);
        if (level != 0) return;
        if (propagate()) return;
        learn_empty_clause();
    }

    readonly HashSet<int> learned_levels_set = new();

    public void add_new_original_clause(ulong id)
    {
        if (!from_propagator && level != 0 && opts.ilb == 0)
        {
            backtrack();
        }
        else if (tainted_literal != 0)
        {
            int new_level = @var(tainted_literal).level - 1;
            backtrack(new_level);
        }
        bool skip = false;
        var learned_levels = learned_levels_set;
        learned_levels.Clear();
        int unassigned = 0;
        newest_clause = null;
        if (unsat)
        {
            skip = true;
        }
        else
        {
            for (int k = 0; k < original.n; k++)
            {
                int lit = original[k];
                int tmp = marked(lit);
                if (tmp > 0) { }
                else if (tmp < 0)
                {
                    skip = true;
                }
                else
                {
                    mark(lit);
                    tmp = @fixed(lit);
                    if (tmp < 0)
                    {
                        if (lrat)
                        {
                            int elit = externalize(lit);
                            uint eidx = (elit > 0 ? 1u : 0u) + 2u * (uint)Math.Abs(elit);
                            if (external.ext_units[eidx] == 0)
                            {
                                ulong uid = unit_clauses(vlit(-lit));
                                lrat_chain.push_back(uid);
                            }
                        }
                    }
                    else if (tmp > 0)
                    {
                        skip = true;
                    }
                    else
                    {
                        clause.push_back(lit);
                        tmp = val(lit);
                        if (tmp != 0) learned_levels.Add(@var(lit).level);
                        else unassigned++;
                    }
                }
            }
            for (int k = 0; k < original.n; k++) unmark(original[k]);
        }
        if (skip)
        {
            if (proof != null)
                proof.delete_external_original_clause(id, false, external.eclause);
        }
        else
        {
            ulong new_id = id;
            int size = clause.n;
            if (original.n > size)
            {
                new_id = ++clause_id;
                if (proof != null)
                {
                    if (lrat) lrat_chain.push_back(id);
                    proof.add_derived_clause(new_id, false, clause, lrat_chain);
                    proof.delete_external_original_clause(id, false, external.eclause);
                }
            }
            external.eclause.clear();
            lrat_chain.clear();
            if (size == 0)
            {
                if (original.n == 0) VERBOSE(1, "found empty original clause");
                else VERBOSE(1, "found falsified original clause");
                unsat = true;
                conflict_id = new_id;
                marked_failed = true;
                conclusion.push_back(new_id);
            }
            else if (size == 1)
            {
                if (force_no_backtrack)
                {
                    int idx = vidx(clause[0]);
                    ref Var v = ref @var(idx);
                    v.level = 0;
                    v.reason = null;
                    uint uidx = vlit(clause[0]);
                    if (lrat || frat) unit_clauses(uidx) = new_id;
                    mark_fixed(clause[0]);
                }
                else
                {
                    int lit = clause[0];
                    if (val(lit) < 0) backtrack(@var(lit).level - 1);
                    handle_external_clause(null);
                    assign_original_unit(new_id, lit);
                }
            }
            else
            {
                move_literals_to_watch();
                int glue = learned_levels.Count + unassigned;
                bool clause_redundancy = from_propagator && ext_clause_forgettable;
                Clause c = new_clause(clause_redundancy, glue);
                c.id = new_id;
                clause_id--;
                watch_clause(c);
                clause.clear();
                original.clear();
                handle_external_clause(c);
                newest_clause = c;
            }
        }
        clause.clear();
        lrat_chain.clear();
    }

    public Clause new_learned_redundant_clause(int glue)
    {
        Clause res = new_clause(true, glue);
        if (proof != null) proof.add_derived_clause(res, lrat_chain);
        watch_clause(res);
        return res;
    }

    public Clause new_hyper_binary_resolved_clause(bool red, int glue)
    {
        Clause res = new_clause(red, glue);
        if (proof != null) proof.add_derived_clause(res, lrat_chain);
        watch_clause(res);
        return res;
    }

    public Clause new_hyper_ternary_resolved_clause(bool red)
    {
        int size = clause.n;
        Clause res = new_clause(red, size);
        if (proof != null) proof.add_derived_clause(res, lrat_chain);
        return res;
    }

    public Clause new_clause_as(Clause orig)
    {
        int new_glue = orig.glue;
        Clause res = new_clause(orig.redundant, new_glue);
        if (proof != null) proof.add_derived_clause(res, lrat_chain);
        watch_clause(res);
        return res;
    }

    public Clause new_resolved_irredundant_clause()
    {
        if (proof != null) proof.add_derived_clause(clause_id + 1, false, clause, lrat_chain);
        Clause res = new_clause(false);
        return res;
    }

    /*------------------------------------------------------------------------*/
    // 'collect.cpp'

    public int clause_contains_fixed_literal(Clause c)
    {
        int satisfied_ = 0, falsified = 0;
        var lits = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int tmp = @fixed(lits[i]);
            if (tmp > 0) satisfied_++;
            if (tmp < 0) falsified++;
        }
        if (satisfied_ != 0) return 1;
        else if (falsified != 0) return -1;
        else return 0;
    }

    public void remove_falsified_literals(Clause c)
    {
        int end = c.size;
        int num_non_false = 0;
        var lits = c.literals;
        for (int i = 0; num_non_false < 2 && i != end; i++)
            if (@fixed(lits[i]) >= 0) num_non_false++;
        if (num_non_false < 2) return;
        if (proof != null) proof.flush_clause(c);
        int j = 0;
        for (int i = 0; i != end; i++)
        {
            int lit = lits[j++] = lits[i], tmp = @fixed(lit);
            if (tmp >= 0) continue;
            j--;
        }
        stats.collected += shrink_clause(c, j);
    }

    public void mark_satisfied_clauses_as_garbage()
    {
        if (last.collect.fixed_ >= stats.all.fixed_) return;
        last.collect.fixed_ = stats.all.fixed_;
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (c.garbage) continue;
            int tmp = clause_contains_fixed_literal(c);
            if (tmp > 0) mark_garbage(c);
            else if (tmp < 0) remove_falsified_literals(c);
        }
    }

    public void protect_reasons()
    {
        for (int k = 0; k < trail.n; k++)
        {
            int lit = trail[k];
            if (!is_active(lit)) continue;
            ref Var v = ref @var(lit);
            Clause reason = v.reason;
            if (reason == null) continue;
            if (reason == external_reason) continue;
            reason.reason = true;
        }
        protected_reasons = true;
    }

    public void unprotect_reasons()
    {
        for (int k = 0; k < trail.n; k++)
        {
            int lit = trail[k];
            if (!is_active(lit)) continue;
            ref Var v = ref @var(lit);
            Clause reason = v.reason;
            if (reason == null) continue;
            if (reason == external_reason) continue;
            reason.reason = false;
        }
        protected_reasons = false;
    }

    public int flush_occs(int lit)
    {
        var os = occs(lit);
        int end = os.n;
        int j = 0;
        int res = 0;
        var a = os.a;
        for (int i = 0; i != end; i++)
        {
            Clause c = a[i];
            if (c.collect()) continue;
            a[j++] = c;
            res++;
        }
        os.shrink(j);
        os.shrink_to_fit();
        return res;
    }

    void flush_watches(int lit, Vec<Watch> saved)
    {
        var ws = watches(lit);
        int end = ws.n;
        int j = 0;
        var a = ws.a;
        for (int i = 0; i != end; i++)
        {
            Watch w = a[i];
            Clause c = w.clause;
            if (c.collect()) continue;
            w.size = c.size;
            int new_blit_pos = c.literals[0] == lit ? 1 : 0;
            w.blit = c.literals[new_blit_pos];
            if (w.size == 2) a[j++] = w;
            else saved.push_back(w);
        }
        ws.shrink(j);
        for (int k = 0; k < saved.n; k++) ws.push_back(saved[k]);
        saved.clear();
        if (ws.capacity() > 4 * ws.n + 16) ws.shrink_to_fit();
    }

    public void flush_all_occs_and_watches()
    {
        if (occurring())
            for (int idx = 1; idx <= max_var; idx++) { flush_occs(idx); flush_occs(-idx); }
        if (watching())
        {
            var tmp = new Vec<Watch>();
            for (int idx = 1; idx <= max_var; idx++) { flush_watches(idx, tmp); flush_watches(-idx, tmp); }
        }
    }

    public void delete_garbage_clauses()
    {
        flush_all_occs_and_watches();
        int end = clauses.n;
        int j = 0;
        var a = clauses.a;
        for (int i = 0; i != end; i++)
        {
            Clause c = a[j++] = a[i];
            if (!c.collect()) continue;
            delete_clause(c);
            j--;
        }
        clauses.shrink(j);
        if (clauses.n < clauses.capacity() / 4) clauses.shrink_to_fit();
    }

    public void check_clause_stats() { }

    public void remove_garbage_binaries()
    {
        if (unsat) return;
        if (!protected_reasons) protect_reasons();
        int backtrack_level = level + 1;
        for (int v = 1; v <= max_var; v++)
        {
            for (int s = 0; s < 2; s++)
            {
                int lit = s == 0 ? -v : v;
                var ws = watches(lit);
                int end = ws.n;
                int j = 0;
                var a = ws.a;
                for (int i = 0; i != end; i++)
                {
                    Watch w = a[i];
                    a[j++] = w;
                    Clause c = w.clause;
                    if (w.size != 2) continue;
                    if (c.reason && c.garbage)
                    {
                        backtrack_level = Math.Min(backtrack_level, @var(c.literals[0]).level);
                        --j;
                        continue;
                    }
                    if (!c.collect()) continue;
                    --j;
                }
                ws.shrink(j);
                ws.shrink_to_fit();
            }
        }
        delete_garbage_clauses();
        unprotect_reasons();
        if (backtrack_level - 1 < level) backtrack(backtrack_level - 1);
    }

    public bool arenaing() => false;

    public void garbage_collection()
    {
        if (unsat) return;
        report('G', 1);
        stats.collections++;
        mark_satisfied_clauses_as_garbage();
        if (!protected_reasons) protect_reasons();
        delete_garbage_clauses();
        unprotect_reasons();
        report('C', 1);
    }

    /*------------------------------------------------------------------------*/
    // 'reduce.cpp'

    public bool reducing()
    {
        if (opts.reduce == 0) return false;
        if (stats.current.redundant == 0) return false;
        return stats.conflicts >= lim.reduce;
    }

    public bool flushing()
    {
        if (opts.flush == 0) return false;
        return stats.conflicts >= lim.flush;
    }

    public void mark_clauses_to_be_flushed()
    {
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (!c.redundant) continue;
            if (c.garbage) continue;
            if (c.reason) continue;
            uint used = c.used;
            if (used != 0) c.used--;
            if (used != 0) continue;
            mark_garbage(c);
            if (c.hyper) stats.flush.hyper++;
            else stats.flush.learned++;
        }
    }

    static int reduce_less_useful(Clause c, Clause d)
    {
        // returns negative if c is 'less useful' (sorted first)
        if (c.glue > d.glue) return -1;
        if (c.glue < d.glue) return 1;
        if (c.size > d.size) return -1;
        if (c.size < d.size) return 1;
        return 0;
    }

    public void mark_useless_redundant_clauses_as_garbage()
    {
        var stack = new Vec<Clause>((int)Math.Max(0, stats.current.redundant));
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (!c.redundant) continue;
            if (c.garbage) continue;
            if (c.reason) continue;
            uint used = c.used;
            if (used != 0) c.used--;
            if (c.hyper)
            {
                if (used == 0) mark_garbage(c);
                continue;
            }
            if (used != 0) continue;
            if (c.keep) continue;
            stack.push_back(c);
        }
        CUtil.stable_sort(stack.AsSpan(), reduce_less_useful);
        long target = (long)(1e-2 * opts.reducetarget * stack.n);
        if (target > stack.n) target = stack.n;
        PHASE("reduce", stats.reductions,
              $"reducing {target} clauses {CUtil.percent(target, stats.current.redundant):F0}%");
        int i = 0;
        int t = (int)target;
        while (i != t)
        {
            Clause c = stack[i++];
            mark_garbage(c);
            stats.reduced++;
        }
        lim.keptsize = lim.keptglue = 0;
        for (i = t; i != stack.n; i++)
        {
            Clause c = stack[i];
            if (c.size > lim.keptsize) lim.keptsize = c.size;
            if (c.glue > lim.keptglue) lim.keptglue = c.glue;
        }
        PHASE("reduce", stats.reductions, $"maximum kept size {lim.keptsize} glue {lim.keptglue}");
    }

    public bool propagate_out_of_order_units()
    {
        if (level == 0) return true;
        int oou = 0;
        for (int i = control[1].trail; oou == 0 && i < trail.n; i++)
        {
            int lit = trail[i];
            if (@var(lit).level != 0) continue;
            oou = lit;
        }
        if (oou == 0) return true;
        backtrack(0);
        if (propagate()) return true;
        learn_empty_clause();
        return false;
    }

    public void reduce()
    {
        stats.reductions++;
        report('.', 1);
        bool flush = flushing();
        if (flush) stats.flush.count++;
        if (!propagate_out_of_order_units()) goto DONE;
        mark_satisfied_clauses_as_garbage();
        protect_reasons();
        if (flush) mark_clauses_to_be_flushed();
        else mark_useless_redundant_clauses_as_garbage();
        garbage_collection();
        {
            long delta = opts.reduceint * (stats.reductions + 1);
            if (irredundant() > 1e5)
            {
                delta = (long)(delta * (Math.Log(irredundant() / 1e4) / Math.Log(10)));
                if (delta < 1) delta = 1;
            }
            lim.reduce = stats.conflicts + delta;
            PHASE("reduce", stats.reductions, $"new reduce limit {lim.reduce} after {delta} conflicts");
        }
        if (flush)
        {
            inc.flush *= opts.flushfactor;
            lim.flush = stats.conflicts + inc.flush;
        }
        last.reduce.conflicts = stats.conflicts;
    DONE:
        report(flush ? 'f' : '-');
    }

    /*------------------------------------------------------------------------*/
    // 'watch.cpp', 'occs.cpp', 'bins.cpp'

    public void init_watches()
    {
        if (wtab.Length < 2 * vsize)
        {
            int old = wtab.Length;
            Array.Resize(ref wtab, 2 * vsize);
            for (int i = old; i < wtab.Length; i++) wtab[i] = new Vec<Watch>();
        }
    }

    public void clear_watches()
    {
        for (int idx = 1; idx <= max_var; idx++)
        {
            watches(idx).clear();
            watches(-idx).clear();
        }
    }

    public void reset_watches()
    {
        wtab = new Vec<Watch>[0];
    }

    public void connect_watches(bool irredundant_only = false)
    {
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (irredundant_only && c.redundant) continue;
            if (c.garbage || c.size > 2) continue;
            watch_clause(c);
        }
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (irredundant_only && c.redundant) continue;
            if (c.garbage || c.size == 2) continue;
            watch_clause(c);
            if (level == 0)
            {
                int lit0 = c.literals[0];
                int lit1 = c.literals[1];
                sbyte tmp0 = val(lit0);
                sbyte tmp1 = val(lit1);
                if (tmp0 > 0) continue;
                if (tmp1 > 0) continue;
                if (tmp0 < 0)
                {
                    int pos0 = @var(lit0).trail;
                    if (pos0 < propagated) propagated = pos0;
                }
                if (tmp1 < 0)
                {
                    int pos1 = @var(lit1).trail;
                    if (pos1 < propagated) propagated = pos1;
                }
            }
        }
    }

    public void sort_watches()
    {
        var saved = new Vec<Watch>();
        for (int idx = 1; idx <= max_var; idx++)
        {
            for (int s = 0; s < 2; s++)
            {
                int lit = s == 0 ? idx : -idx;
                var ws = watches(lit);
                int end = ws.n;
                int j = 0;
                var a = ws.a;
                for (int i = 0; i != end; i++)
                {
                    Watch w = a[i];
                    if (w.size == 2) a[j++] = w;
                    else saved.push_back(w);
                }
                for (int k = 0; k < saved.n; k++) a[j++] = saved[k];
                saved.clear();
            }
        }
    }

    public void init_occs()
    {
        if (otab.Length < 2 * vsize)
        {
            int old = otab.Length;
            Array.Resize(ref otab, 2 * vsize);
            for (int i = old; i < otab.Length; i++) otab[i] = new Vec<Clause>();
        }
    }

    public void reset_occs() { otab = new Vec<Clause>[0]; }

    public void init_noccs()
    {
        if (ntab.Length < 2 * vsize) Array.Resize(ref ntab, 2 * vsize);
    }

    public void reset_noccs() { ntab = new long[0]; }

    public void init_bins()
    {
        if (big.Length < 2 * vsize)
        {
            int old = big.Length;
            Array.Resize(ref big, 2 * vsize);
            for (int i = old; i < big.Length; i++) big[i] = new Vec<Bin>();
        }
    }

    public void reset_bins() { big = new Vec<Bin>[0]; }
}
