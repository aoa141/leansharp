// Port of 'gates.cpp' (gate detection for bounded variable substitution).

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    public int second_literal_in_binary_clause(Eliminator eliminator, Clause c, int first)
    {
        int second = 0;
        var lits = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int lit = lits[i];
            if (lit == first) continue;
            sbyte tmp = val(lit);
            if (tmp < 0) continue;
            if (tmp > 0)
            {
                mark_garbage(c);
                elim_update_removed_clause(eliminator, c);
                return 0;
            }
            if (second != 0)
            {
                second = int.MinValue;
                break;
            }
            second = lit;
        }
        if (second == 0) return 0;
        if (second == int.MinValue) return 0;
        return second;
    }

    public int second_literal_in_binary_clause_lrat(Clause c, int first)
    {
        if (c.garbage) return 0;
        int second = 0;
        var lits = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int lit = lits[i];
            if (lit == first) continue;
            sbyte tmp = val(lit);
            if (tmp == 0)
            {
                if (second != 0)
                {
                    second = int.MinValue;
                    break;
                }
                second = lit;
            }
        }
        if (second == 0) return 0;
        if (second == int.MinValue) return 0;
        return second;
    }

    public Clause find_binary_clause(int first, int second)
    {
        int best = first;
        int other = second;
        if (occs(first).n > occs(second).n)
        {
            best = second;
            other = first;
        }
        var os = occs(best);
        for (int i = 0; i < os.n; i++)
            if (second_literal_in_binary_clause_lrat(os[i], best) == other) return os[i];
        return null;
    }

    // Adds the unit ids of the falsified literals of 'c' (except 'a' and 'b') to 'lrat_chain'.
    void gates_add_unit_ids(Clause c, int a, int b)
    {
        var lits = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int lit = lits[i];
            if (lit == a || lit == b) continue;
            ref Flags f = ref flags(lit);
            if (f.seen) continue;
            analyzed.push_back(lit);
            f.seen = true;
            uint uidx = vlit(-lit);
            ulong id = unit_clauses(uidx);
            lrat_chain.push_back(id);
        }
    }

    public void mark_binary_literals(Eliminator eliminator, int first)
    {
        if (unsat) return;
        if (val(first) != 0) return;
        if (!eliminator.gates.empty()) return;

        var os = occs(first);
        for (int k = 0; k < os.n; k++)
        {
            Clause c = os[k];
            if (c.garbage) continue;
            int second = second_literal_in_binary_clause(eliminator, c, first);
            if (second == 0) continue;
            int tmp = marked(second);
            if (tmp < 0)
            {
                if (lrat)
                {
                    Clause d = find_binary_clause(first, -second);
                    gates_add_unit_ids(d, first, -second);
                    gates_add_unit_ids(c, first, second);
                    lrat_chain.push_back(c.id);
                    lrat_chain.push_back(d.id);
                    clear_analyzed_literals();
                }
                assign_unit(first);
                elim_propagate(eliminator, first);
                return;
            }
            if (tmp > 0)
            {
                elim_update_removed_clause(eliminator, c);
                mark_garbage(c);
                continue;
            }
            eliminator.marked.push_back(second);
            mark(second);
        }
    }

    public void unmark_binary_literals(Eliminator eliminator)
    {
        for (int k = 0; k < eliminator.marked.n; k++) unmark(eliminator.marked[k]);
        eliminator.marked.clear();
    }

    public void find_equivalence(Eliminator eliminator, int pivot)
    {
        if (opts.elimequivs == 0) return;
        if (unsat) return;
        if (val(pivot) != 0) return;
        if (!eliminator.gates.empty()) return;

        mark_binary_literals(eliminator, pivot);
        if (unsat || val(pivot) != 0) goto DONE;

        {
            var ns = occs(-pivot);
            for (int k = 0; k < ns.n; k++)
            {
                Clause c = ns[k];
                if (c.garbage) continue;
                int second = second_literal_in_binary_clause(eliminator, c, -pivot);
                if (second == 0) continue;
                int tmp = marked(second);
                if (tmp > 0)
                {
                    if (lrat)
                    {
                        Clause d = find_binary_clause(pivot, second);
                        gates_add_unit_ids(d, pivot, second);
                        gates_add_unit_ids(c, -pivot, second);
                        lrat_chain.push_back(c.id);
                        lrat_chain.push_back(d.id);
                        clear_analyzed_literals();
                    }
                    assign_unit(second);
                    elim_propagate(eliminator, second);
                    if (val(pivot) != 0) break;
                    if (unsat) break;
                }
                if (tmp >= 0) continue;

                stats.elimequivs++;
                stats.elimgates++;

                c.gate = true;
                eliminator.gates.push_back(c);

                Clause d2 = null;
                var ps = occs(pivot);
                for (int q = 0; q < ps.n; q++)
                {
                    Clause e = ps[q];
                    if (e.garbage) continue;
                    int other = second_literal_in_binary_clause(eliminator, e, pivot);
                    if (other == -second)
                    {
                        d2 = e;
                        break;
                    }
                }
                d2.gate = true;
                eliminator.gates.push_back(d2);
                break;
            }
        }

    DONE:
        unmark_binary_literals(eliminator);
    }

    public void find_and_gate(Eliminator eliminator, int pivot)
    {
        if (opts.elimands == 0) return;
        if (unsat) return;
        if (val(pivot) != 0) return;
        if (!eliminator.gates.empty()) return;

        mark_binary_literals(eliminator, pivot);
        if (unsat || val(pivot) != 0) goto DONE;

        {
            var ns = occs(-pivot);
            for (int k = 0; k < ns.n; k++)
            {
                Clause c = ns[k];
                if (c.garbage) continue;
                if (c.size < 3) continue;

                bool all_literals_marked = true;
                uint arity = 0;
                int satisfied_ = 0;
                var lits = c.literals;
                for (int i = 0; i < c.size; i++)
                {
                    int lit = lits[i];
                    if (lit == -pivot) continue;
                    int tmp = val(lit);
                    if (tmp < 0) continue;
                    if (tmp > 0)
                    {
                        satisfied_ = lit;
                        break;
                    }
                    tmp = marked(lit);
                    if (tmp < 0)
                    {
                        arity++;
                        continue;
                    }
                    all_literals_marked = false;
                    break;
                }

                if (!all_literals_marked) continue;

                if (satisfied_ != 0)
                {
                    mark_garbage(c);
                    continue;
                }

                stats.elimands++;
                stats.elimgates++;

                c.gate = true;
                eliminator.gates.push_back(c);
                for (int i = 0; i < c.size; i++)
                {
                    int lit = lits[i];
                    if (lit == -pivot) continue;
                    sbyte tmp = val(lit);
                    if (tmp < 0) continue;
                    marks[vidx(lit)] *= 2;
                }

                var ps = occs(pivot);
                for (int q = 0; q < ps.n; q++)
                {
                    Clause d = ps[q];
                    if (d.garbage) continue;
                    int other = second_literal_in_binary_clause(eliminator, d, pivot);
                    if (other == 0) continue;
                    int tmp = marked(other);
                    if (tmp != 2) continue;
                    d.gate = true;
                    eliminator.gates.push_back(d);
                }
                break;
            }
        }

    DONE:
        unmark_binary_literals(eliminator);
    }

    public bool get_ternary_clause(Clause d, out int a, out int b, out int c)
    {
        a = b = c = 0;
        if (d.garbage) return false;
        if (d.size < 3) return false;
        int found = 0;
        var lits = d.literals;
        for (int i = 0; i < d.size; i++)
        {
            int lit = lits[i];
            if (val(lit) != 0) continue;
            if (++found == 1) a = lit;
            else if (found == 2) b = lit;
            else if (found == 3) c = lit;
            else return false;
        }
        return found == 3;
    }

    public bool match_ternary_clause(Clause d, int a, int b, int c)
    {
        if (d.garbage) return false;
        int found = 0;
        var lits = d.literals;
        for (int i = 0; i < d.size; i++)
        {
            int lit = lits[i];
            if (val(lit) != 0) continue;
            if (a != lit && b != lit && c != lit) return false;
            found++;
        }
        return found == 3;
    }

    public Clause find_ternary_clause(int a, int b, int c)
    {
        if (occs(b).n > occs(c).n) (b, c) = (c, b);
        if (occs(a).n > occs(b).n) (a, b) = (b, a);
        var os = occs(a);
        for (int i = 0; i < os.n; i++)
            if (match_ternary_clause(os[i], a, b, c)) return os[i];
        return null;
    }

    public void find_if_then_else(Eliminator eliminator, int pivot)
    {
        if (opts.elimites == 0) return;
        if (unsat) return;
        if (val(pivot) != 0) return;
        if (!eliminator.gates.empty()) return;

        var os = occs(pivot);
        int end = os.n;
        for (int i = 0; i != end; i++)
        {
            Clause di = os[i];
            if (!get_ternary_clause(di, out int ai, out int bi, out int ci)) continue;
            if (bi == pivot) (ai, bi) = (bi, ai);
            if (ci == pivot) (ai, ci) = (ci, ai);
            for (int j = i + 1; j != end; j++)
            {
                Clause dj = os[j];
                if (!get_ternary_clause(dj, out int aj, out int bj, out int cj)) continue;
                if (bj == pivot) (aj, bj) = (bj, aj);
                if (cj == pivot) (aj, cj) = (cj, aj);
                if (Math.Abs(bi) == Math.Abs(cj)) (bj, cj) = (cj, bj);
                if (Math.Abs(ci) == Math.Abs(cj)) continue;
                if (bi != -bj) continue;
                Clause d1 = find_ternary_clause(-pivot, bi, -ci);
                if (d1 == null) continue;
                Clause d2 = find_ternary_clause(-pivot, bj, -cj);
                if (d2 == null) continue;
                di.gate = true;
                dj.gate = true;
                d1.gate = true;
                d2.gate = true;
                eliminator.gates.push_back(di);
                eliminator.gates.push_back(dj);
                eliminator.gates.push_back(d1);
                eliminator.gates.push_back(d2);
                stats.elimgates++;
                stats.elimites++;
                return;
            }
        }
    }

    public bool get_clause(Clause c, Vec<int> l)
    {
        if (c.garbage) return false;
        l.clear();
        var lits = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int lit = lits[i];
            if (val(lit) != 0) continue;
            l.push_back(lit);
        }
        return true;
    }

    public bool is_clause(Clause c, Vec<int> lits)
    {
        if (c.garbage) return false;
        int size = lits.n;
        if (c.size < size) return false;
        int found = 0;
        var cl = c.literals;
        for (int i = 0; i < c.size; i++)
        {
            int lit = cl[i];
            if (val(lit) != 0) continue;
            bool contains = false;
            for (int k = 0; k < lits.n; k++) if (lits[k] == lit) { contains = true; break; }
            if (!contains) return false;
            if (++found > size) return false;
        }
        return found == size;
    }

    public Clause find_clause(Vec<int> lits)
    {
        int best = 0;
        int len = 0;
        for (int k = 0; k < lits.n; k++)
        {
            int lit = lits[k];
            int l = occs(lit).n;
            if (best != 0 && l >= len) continue;
            len = l;
            best = lit;
        }
        var os = occs(best);
        for (int i = 0; i < os.n; i++)
            if (is_clause(os[i], lits)) return os[i];
        return null;
    }

    public void find_xor_gate(Eliminator eliminator, int pivot)
    {
        if (opts.elimxors == 0) return;
        if (unsat) return;
        if (val(pivot) != 0) return;
        if (!eliminator.gates.empty()) return;

        var lits = new Vec<int>();
        var os = occs(pivot);
        for (int k = 0; k < os.n; k++)
        {
            Clause d = os[k];
            if (!get_clause(d, lits)) continue;
            int size = lits.n;
            int arity = size - 1;
            if (size < 3) continue;
            if (arity > opts.elimxorlim) continue;

            uint needed = (1u << arity) - 1;
            uint signs = 0;

            do
            {
                uint prev = signs;
                while (CUtil.parity(++signs)) { }
                for (int j = 0; j < size; j++)
                {
                    uint bit = 1u << j;
                    int lit = lits[j];
                    if ((prev & bit) != (signs & bit)) lits[j] = lit = -lit;
                }
                Clause e = find_clause(lits);
                if (e == null) break;
                eliminator.gates.push_back(e);
            } while (--needed != 0);

            if (needed != 0)
            {
                eliminator.gates.clear();
                continue;
            }

            eliminator.gates.push_back(d);

            stats.elimgates++;
            stats.elimxors++;
            var g = eliminator.gates;
            int jj = 0;
            for (int i = 0; i < g.n; i++)
            {
                Clause e = g[i];
                if (e.gate) continue;
                e.gate = true;
                g[jj++] = e;
            }
            g.resize(jj);
            break;
        }
    }

    public void find_gate_clauses(Eliminator eliminator, int pivot)
    {
        if (opts.elimsubst == 0) return;
        if (unsat) return;
        if (val(pivot) != 0) return;
        find_equivalence(eliminator, pivot);
        find_and_gate(eliminator, pivot);
        find_and_gate(eliminator, -pivot);
        find_if_then_else(eliminator, pivot);
        find_xor_gate(eliminator, pivot);
    }

    public void unmark_gate_clauses(Eliminator eliminator)
    {
        var g = eliminator.gates;
        for (int i = 0; i < g.n; i++) g[i].gate = false;
        g.clear();
    }
}
