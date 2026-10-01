// Port of 'decompose.cpp' (SCC decomposition of the binary implication graph and
// equivalent literal substitution, with LRAT chains).

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    const uint TRAVERSED = uint.MaxValue;

    void decompose_analyze_binary_chain(DFS[] dfs, int from)
    {
        if (!lrat) return;
        // Iterative version of the tail recursive C++ function.
        for (; ; )
        {
            Clause reason = dfs[vlit(from)].parent;
            if (reason == null) return;
            mini_chain.push_back(reason.id);
            int other = reason.literals[0];
            other = other == from ? -reason.literals[1] : -other;
            ref Flags f = ref flags(other);
            if (f.seen) return;
            f.seen = true;
            analyzed.push_back(other);
            from = other;
        }
    }

    List<Clause> decompose_analyze_binary_clauses(DFS[] dfs, int from)
    {
        var result = new List<Clause>();
        Clause reason = dfs[vlit(from)].parent;
        while (reason != null)
        {
            result.Add(reason);
            int other = reason.literals[0];
            other = other == from ? -reason.literals[1] : -other;
            ref Flags f = ref flags(other);
            if (f.seen) break;
            f.seen = true;
            analyzed.push_back(other);
            from = other;
            reason = dfs[vlit(from)].parent;
        }
        return result;
    }

    void decompose_conflicting_scc_lrat(DFS[] dfs, Vec<int> scc)
    {
        if (!lrat) return;
        for (int k = 0; k < scc.n; k++)
        {
            int lit = scc[k];
            ref Flags f = ref flags(lit);
            if (f.seen) return;
            f.seen = true;
            analyzed.push_back(lit);
            decompose_analyze_binary_chain(dfs, lit);
            for (int p = mini_chain.n - 1; p >= 0; p--) lrat_chain.push_back(mini_chain[p]);
            mini_chain.clear();
        }
        clear_analyzed_literals();
    }

    void build_lrat_for_clause(List<Clause>[] dfs_chains, bool invert = false)
    {
        for (int ci = 0; ci < clause.n; ci++)
        {
            int lit = clause[ci];
            int other = lit;
            if (val(other) > 0)
            {
                if (marked_decompose(other)) continue;
                mark_decomposed(other);
                uint uidx = vlit(other);
                ulong id = unit_clauses(uidx);
                lrat_chain.push_back(id);
                continue;
            }
            var chain = dfs_chains[vlit(other)];
            if (chain != null)
            {
                foreach (var p in chain)
                {
                    if (marked_decompose(other)) continue;
                    mark_decomposed(other);
                    int implied = p.literals[0];
                    implied = implied == other ? -p.literals[1] : -implied;
                    other = implied;
                    mini_chain.push_back(p.id);
                    if (val(implied) <= 0) continue;
                    if (marked_decompose(implied)) break;
                    mark_decomposed(implied);
                    uint uidx = vlit(implied);
                    ulong id = unit_clauses(uidx);
                    mini_chain.push_back(id);
                    break;
                }
            }
            if (invert)
                for (int p = mini_chain.n - 1; p >= 0; p--) lrat_chain.push_back(mini_chain[p]);
            else
                for (int p = 0; p < mini_chain.n; p++) lrat_chain.push_back(mini_chain[p]);
            mini_chain.clear();
        }
        clear_decomposed_literals();
    }

    public void clear_decomposed_literals()
    {
        for (int k = 0; k < decomposed.n; k++) unmark_decompose(decomposed[k]);
        decomposed.clear();
    }

    bool decompose_round()
    {
        if (opts.decompose == 0) return false;
        if (unsat) return false;
        if (terminated_asynchronously()) return false;

        START_SIMPLIFIER_G4(DECOMP);

        stats.decompositions++;

        int size_dfs = 2 * (1 + max_var);
        var dfs = new DFS[size_dfs];
        var reprs = new int[size_dfs];
        var dfs_chains = new List<Clause>[size_dfs];

        int substituted = 0;
        int non_trivial_sccs = 0;
        int before = active();
        uint dfs_idx = 0;

        var work = new Vec<int>();
        var scc = new Vec<int>();

        for (int root_idx = 1; root_idx <= max_var; root_idx++)
        {
            if (unsat) break;
            if (!is_active(root_idx)) continue;
            for (int root_sign = -1; !unsat && root_sign <= 1; root_sign += 2)
            {
                int root = root_sign * root_idx;
                if (dfs[vlit(root)].min == TRAVERSED) continue;
                work.push_back(root);
                while (!unsat && !work.empty())
                {
                    int parent = work.back();
                    ref DFS parent_dfs = ref dfs[vlit(parent)];
                    if (parent_dfs.min == TRAVERSED)
                    {
                        work.pop_back();
                    }
                    else
                    {
                        var ws = watches(-parent);
                        if (parent_dfs.idx != 0)
                        {
                            work.pop_back();
                            uint new_min = parent_dfs.min;
                            for (int wi = 0; wi < ws.n; wi++)
                            {
                                Watch w = ws.a[wi];
                                if (w.size != 2) continue;
                                int child = w.blit;
                                if (!is_active(child)) continue;
                                ref DFS child_dfs = ref dfs[vlit(child)];
                                if (new_min > child_dfs.min) new_min = child_dfs.min;
                            }

                            if (parent_dfs.idx == new_min)
                            {
                                if (lrat)
                                {
                                    int o, first = 0;
                                    bool conflicting = false;
                                    int jj = scc.n;
                                    do
                                    {
                                        o = scc[--jj];
                                        if (first == 0 || vlit(o) < vlit(first)) first = o;
                                        ref Flags f = ref flags(o);
                                        if (o == -parent) conflicting = true;
                                        if (f.seen) continue;
                                        f.seen = true;
                                        analyzed.push_back(o);
                                    } while (o != parent);

                                    var todo = new Vec<int>();
                                    if (conflicting) todo.push_back(-parent);
                                    else todo.push_back(first);
                                    while (!todo.empty())
                                    {
                                        int next = todo.back();
                                        todo.pop_back();
                                        var next_ws = watches(-next);
                                        for (int wi = 0; wi < next_ws.n; wi++)
                                        {
                                            Watch w = next_ws.a[wi];
                                            if (w.size != 2) continue;
                                            int child = w.blit;
                                            if (!is_active(child)) continue;
                                            if (!flags(child).seen) continue;
                                            ref DFS child_dfs = ref dfs[vlit(child)];
                                            if (child_dfs.parent != null) continue;
                                            child_dfs.parent = w.clause;
                                            todo.push_back(child);
                                        }
                                    }
                                    clear_analyzed_literals();
                                }

                                int other, repr = parent;
                                int size = 0;
                                int j = scc.n;
                                do
                                {
                                    other = scc[--j];
                                    if (other == -parent)
                                    {
                                        if (lrat)
                                        {
                                            ref Flags f = ref flags(-parent);
                                            f.seen = true;
                                            analyzed.push_back(-parent);
                                            decompose_analyze_binary_chain(dfs, parent);
                                            for (int p = 0; p < mini_chain.n; p++) lrat_chain.push_back(mini_chain[p]);
                                            mini_chain.clear();
                                        }
                                        assign_unit(parent);
                                        if (lrat) propagate();
                                        learn_empty_clause();
                                        lrat_chain.clear();
                                    }
                                    else
                                    {
                                        if (Math.Abs(other) < Math.Abs(repr)) repr = other;
                                        size++;
                                    }
                                } while (!unsat && other != parent);

                                if (unsat) break;

                                do
                                {
                                    other = scc.back();
                                    scc.pop_back();
                                    dfs[vlit(other)].min = TRAVERSED;
                                    if (frozen(other))
                                    {
                                        reprs[vlit(other)] = other;
                                        continue;
                                    }
                                    reprs[vlit(other)] = repr;
                                    if (other == repr) continue;
                                    substituted++;
                                    if (!lrat) continue;
                                    ref Flags f = ref flags(repr);
                                    f.seen = true;
                                    analyzed.push_back(repr);
                                    dfs_chains[vlit(other)] = decompose_analyze_binary_clauses(dfs, other);
                                    clear_analyzed_literals();
                                } while (other != parent);

                                if (size > 1) non_trivial_sccs++;
                            }
                            else
                            {
                                parent_dfs.min = new_min;
                            }
                        }
                        else
                        {
                            dfs_idx++;
                            parent_dfs.idx = parent_dfs.min = dfs_idx;
                            scc.push_back(parent);
                            for (int wi = 0; wi < ws.n; wi++)
                            {
                                Watch w = ws.a[wi];
                                if (w.size != 2) continue;
                                int child = w.blit;
                                if (!is_active(child)) continue;
                                ref DFS child_dfs = ref dfs[vlit(child)];
                                if (child_dfs.idx != 0) continue;
                                work.push_back(child);
                            }
                        }
                    }
                }
            }
        }

        work.erase();
        scc.erase();

        PHASE("decompose", stats.decompositions,
              $"{non_trivial_sccs} non-trivial sccs, {substituted} substituted {CUtil.percent(substituted, before):F2}%");

        bool new_unit = false, new_binary_clause = false;

        int dsize = 2 * (1 + max_var);
        var decompose_ids = new ulong[dsize];

        for (int idx = 1; idx <= max_var; idx++)
        {
            if (unsat) break;
            if (!is_active(idx)) continue;
            int other = reprs[vlit(idx)];
            if (other == idx) continue;

            clause.push_back(other);
            clause.push_back(-idx);
            if (lrat) build_lrat_for_clause(dfs_chains);

            ulong id1 = ++clause_id;
            if (proof != null)
            {
                proof.add_derived_clause(id1, false, clause, lrat_chain);
                proof.weaken_minus(id1, clause);
            }
            external.push_binary_clause_on_extension_stack(id1, -idx, other);

            decompose_ids[vlit(-idx)] = id1;

            lrat_chain.clear();
            clause.clear();

            clause.push_back(idx);
            clause.push_back(-other);
            if (lrat) build_lrat_for_clause(dfs_chains);
            ulong id2 = ++clause_id;
            if (proof != null)
            {
                proof.add_derived_clause(id2, false, clause, lrat_chain);
                proof.weaken_minus(id2, clause);
            }
            external.push_binary_clause_on_extension_stack(id2, idx, -other);
            decompose_ids[vlit(idx)] = id2;

            clause.clear();
            lrat_chain.clear();
        }

        var postponed_garbage = new Vec<Clause>();

        int clauses_size = clauses.n;
        long garbage = 0, replaced = 0;
        for (int i = 0; substituted != 0 && !unsat && i < clauses_size; i++)
        {
            Clause c = clauses[i];
            if (c.garbage) continue;
            int j, size = c.size;
            for (j = 0; j < size; j++)
            {
                int lit = c.literals[j];
                if (reprs[vlit(lit)] != lit) break;
            }
            if (j == size) continue;

            replaced++;

            bool satisfied_ = false;

            for (int k = 0; !satisfied_ && k < size; k++)
            {
                int lit = c.literals[k];
                sbyte tmp = val(lit);
                if (tmp > 0) satisfied_ = true;
                else if (tmp < 0)
                {
                    if (!lrat) continue;
                    ref Flags f = ref flags(lit);
                    if (f.seen) continue;
                    f.seen = true;
                    analyzed.push_back(lit);
                    uint uidx = vlit(-lit);
                    ulong id = unit_clauses(uidx);
                    lrat_chain.push_back(id);
                    continue;
                }
                else
                {
                    int other = reprs[vlit(lit)];
                    tmp = val(other);
                    if (tmp < 0)
                    {
                        if (!lrat) continue;
                        ref Flags f = ref flags(other);
                        if (!f.seen)
                        {
                            f.seen = true;
                            analyzed.push_back(other);
                            uint uidx = vlit(-other);
                            ulong uid = unit_clauses(uidx);
                            lrat_chain.push_back(uid);
                        }
                        if (other == lit) continue;
                        ulong id = decompose_ids[vlit(-lit)];
                        lrat_chain.push_back(id);
                        continue;
                    }
                    else if (tmp > 0) satisfied_ = true;
                    else
                    {
                        tmp = marked(other);
                        if (tmp < 0) satisfied_ = true;
                        else if (tmp == 0)
                        {
                            mark(other);
                            clause.push_back(other);
                        }
                        if (other == lit) continue;
                        if (!lrat) continue;
                        ulong id = decompose_ids[vlit(-lit)];
                        lrat_chain.push_back(id);
                    }
                }
            }
            if (lrat) lrat_chain.push_back(c.id);
            clear_analyzed_literals();
            if (satisfied_)
            {
                postponed_garbage.push_back(c);
                garbage++;
            }
            else if (clause.n == 0)
            {
                learn_empty_clause();
            }
            else if (clause.n == 1)
            {
                assign_unit(clause[0]);
                mark_garbage(c);
                new_unit = true;
                garbage++;
            }
            else if (c.literals[0] != clause[0] || c.literals[1] != clause[1])
            {
                if (clause.n == 2) new_binary_clause = true;
                int d_clause_idx = clauses.n;
                Clause d = new_clause_as(c);
                clauses[d_clause_idx] = c;
                clauses[i] = d;
                mark_garbage(c);
                garbage++;
            }
            else
            {
                if (!c.redundant) mark_removed(c);
                if (proof != null)
                {
                    proof.add_derived_clause(++clause_id, c.redundant, clause, lrat_chain);
                    proof.delete_clause(c);
                    c.id = clause_id;
                }
                int l;
                int[] literals = c.literals;
                for (l = 2; l < clause.n; l++) literals[l] = clause[l];
                int flushed = c.size - l;
                if (flushed != 0)
                {
                    if (l == 2) new_binary_clause = true;
                    shrink_clause(c, l);
                }
                else if (likely_to_be_kept_clause(c)) mark_added(c);
                if (c.size == 2)
                {
                    WatchOps.update_watch_size(watches(c.literals[0]), c.literals[1], c);
                    WatchOps.update_watch_size(watches(c.literals[1]), c.literals[0], c);
                }
            }
            while (!clause.empty())
            {
                int lit = clause.back();
                clause.pop_back();
                unmark(lit);
            }
            lrat_chain.clear();
        }

        if (proof != null)
        {
            for (int idx = 1; idx <= max_var; idx++)
            {
                if (!is_active(idx)) continue;
                ulong id1 = decompose_ids[vlit(-idx)];
                if (id1 == 0) continue;
                int other = reprs[vlit(idx)];

                clause.push_back(other);
                clause.push_back(-idx);
                proof.delete_clause(id1, false, clause);
                clause.clear();

                clause.push_back(idx);
                clause.push_back(-other);
                ulong id2 = decompose_ids[vlit(idx)];
                proof.delete_clause(id2, false, clause);
                clause.clear();
            }
        }

        if (!unsat && !postponed_garbage.empty())
        {
            for (int k = 0; k < postponed_garbage.n; k++) mark_garbage(postponed_garbage[k]);
        }
        postponed_garbage.erase();

        PHASE("decompose", stats.decompositions,
              $"{replaced} clauses replaced {CUtil.percent(replaced, clauses_size):F2}% producing {garbage} garbage clauses {CUtil.percent(garbage, replaced):F2}%");

        if (!unsat && propagated < trail.n && !propagate())
        {
            learn_empty_clause();
        }

        for (int idx = 1; idx <= max_var; idx++)
        {
            if (unsat) break;
            if (!is_active(idx)) continue;
            int other = reprs[vlit(idx)];
            if (other == idx) continue;
            if (!flags(other).@fixed()) mark_substituted(idx);
        }

        flush_all_occs_and_watches();

        bool success = unsat || (substituted > 0 && (new_unit || new_binary_clause));
        report('d', (opts.reportall == 0 && !success) ? 1 : 0);

        STOP_SIMPLIFIER_G4(DECOMP);

        return success;
    }

    public void decompose()
    {
        for (int round = 1; round <= opts.decomposerounds; round++)
            if (!decompose_round()) break;
    }
}
