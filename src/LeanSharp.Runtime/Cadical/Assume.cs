// Port of 'assume.cpp', 'constrain.cpp', 'flip.cpp' and 'lucky.cpp'.

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    /*------------------------------------------------------------------------*/
    // 'assume.cpp'

    public void assume(int lit)
    {
        if (level != 0 && opts.ilbassumptions == 0) backtrack();
        else if (val(lit) < 0) backtrack(Math.Max(0, @var(lit).level - 1));
        ref Flags f = ref flags(lit);
        byte bit = (byte)CUtil.bign(lit);
        if ((f.assumed & bit) != 0) return;
        f.assumed |= bit;
        assumptions.push_back(lit);
        freeze(lit);
    }

    void assume_analyze_literal(int lit)
    {
        ref Flags f = ref flags(lit);
        if (f.seen) return;
        f.seen = true;
        analyzed.push_back(lit);
        ref Var v = ref @var(lit);
        if (v.reason == external_reason) v.reason = wrapped_learn_external_reason_clause(-lit);
        if (v.level == 0)
        {
            uint uidx = vlit(-lit);
            ulong id = unit_clauses(uidx);
            lrat_chain.push_back(id);
            return;
        }
        if (v.reason != null)
        {
            Clause reason = v.reason;
            for (int i = 0; i < reason.size; i++) assume_analyze_literal(reason.literals[i]);
            lrat_chain.push_back(reason.id);
            return;
        }
        clause.push_back(lit);
    }

    void assume_analyze_reason(int lit, Clause reason)
    {
        for (int i = 0; i < reason.size; i++)
        {
            int other = reason.literals[i];
            if (other != lit) assume_analyze_literal(other);
        }
        lrat_chain.push_back(reason.id);
    }

    public void failing()
    {
        if (!unsat_constraint)
        {
            int failed_unit = 0;
            int failed_clashing = 0;
            int first_failed = 0;
            int failed_level = int.MaxValue;
            int efailed = 0;

            for (int k = 0; k < external.assumptions.n; k++)
            {
                int elit = external.assumptions[k];
                int lit = external.e2i[Math.Abs(elit)];
                if (elit < 0) lit = -lit;
                if (val(lit) >= 0) continue;
                ref Var v = ref @var(lit);
                if (v.level == 0)
                {
                    failed_unit = lit;
                    efailed = elit;
                    break;
                }
                if (failed_clashing != 0) continue;
                if (v.reason == null)
                {
                    failed_clashing = lit;
                    efailed = elit;
                }
                else if (first_failed == 0 || v.level < failed_level)
                {
                    first_failed = lit;
                    efailed = elit;
                    failed_level = v.level;
                }
            }

            int failed_;
            if (failed_unit != 0) failed_ = failed_unit;
            else if (failed_clashing != 0) failed_ = failed_clashing;
            else failed_ = first_failed;

            {
                ref Flags f = ref flags(failed_);
                uint bit = CUtil.bign(failed_);
                f.failed |= (byte)bit;
            }

            if (failed_unit != 0)
            {
                if (proof != null)
                {
                    if (lrat)
                    {
                        uint eidx = (efailed > 0 ? 1u : 0u) + 2u * (uint)Math.Abs(efailed);
                        ulong id = external.ext_units[eidx];
                        if (id != 0) lrat_chain.push_back(id);
                        else
                        {
                            uint uidx = vlit(-failed_unit);
                            ulong uid = unit_clauses(uidx);
                            lrat_chain.push_back(uid);
                        }
                    }
                    var cl = new Vec<int>();
                    cl.push_back(-efailed);
                    proof.add_assumption_clause(++clause_id, cl, lrat_chain);
                    conclusion.push_back(clause_id);
                    lrat_chain.clear();
                }
                goto DONE;
            }

            if (failed_clashing != 0)
            {
                ref Flags f = ref flags(-failed_);
                uint bit = CUtil.bign(-failed_);
                f.failed |= (byte)bit;
                if (proof != null)
                {
                    var clash = new Vec<int>();
                    clash.push_back(externalize(failed_));
                    clash.push_back(externalize(-failed_));
                    proof.add_assumption_clause(++clause_id, clash, lrat_chain);
                    conclusion.push_back(clause_id);
                }
                goto DONE;
            }

            {
                ref Flags f = ref flags(first_failed);
                f.seen = true;
                analyzed.push_back(-first_failed);
                clause.push_back(-first_failed);
            }
        }
        else
        {
            for (int k = 0; k < constraint.n; k++)
            {
                int lit = -constraint[k];
                flags(lit).seen = true;
                analyzed.push_back(lit);
            }
        }

        {
            var constraint_chains = new List<List<ulong>>();
            var constraint_clauses = new List<List<int>>();
            var sum_constraints = new Vec<int>();
            var econstraints = new Vec<int>();
            for (int k = 0; k < external.constraint.n; k++)
            {
                int elit = external.constraint[k];
                int lit = external.e2i[Math.Abs(elit)];
                if (elit < 0) lit = -lit;
                if (lit == 0) continue;
                ref Flags f = ref flags(lit);
                if (f.seen) continue;
                bool found = false;
                for (int q = 0; q < econstraints.n; q++) if (econstraints[q] == elit) { found = true; break; }
                if (found) continue;
                econstraints.push_back(elit);
            }

            if (!lrat)
            {
                int next = 0;
                while (next < analyzed.n)
                {
                    int lit = analyzed[next++];
                    ref Var v = ref @var(lit);
                    if (v.level == 0) continue;
                    if (v.reason != null)
                    {
                        Clause reason = v.reason;
                        for (int i = 0; i < reason.size; i++)
                        {
                            int other = reason.literals[i];
                            ref Flags f = ref flags(other);
                            if (f.seen) continue;
                            f.seen = true;
                            analyzed.push_back(-other);
                        }
                    }
                    else
                    {
                        clause.push_back(-lit);
                        ref Flags f = ref flags(lit);
                        uint bit = CUtil.bign(lit);
                        f.failed |= (byte)bit;
                    }
                }
                clear_analyzed_literals();
            }
            else if (!unsat_constraint)
            {
                int lit = clause[0];
                ref Var v = ref @var(lit);
                if (v.reason != null) assume_analyze_reason(lit, v.reason);
                else
                {
                    uint uidx = vlit(lit);
                    ulong id = unit_clauses(uidx);
                    lrat_chain.push_back(id);
                }
                for (int k = 0; k < clause.n; k++)
                {
                    int l = clause[k];
                    ref Flags f = ref flags(l);
                    uint bit = CUtil.bign(-l);
                    if ((f.failed & bit) == 0) f.failed |= (byte)bit;
                }
                clear_analyzed_literals();
            }
            else
            {
                clear_analyzed_literals();
                for (int k = 0; k < constraint.n; k++)
                {
                    int lit = constraint[k];
                    assume_analyze_literal(lit);
                    constraint_chains.Add(new List<ulong>());
                    constraint_clauses.Add(new List<int>());
                    for (int q = 0; q < clause.n; q++)
                    {
                        int ign = clause[q];
                        constraint_clauses[^1].Add(ign);
                        ref Flags f = ref flags(ign);
                        uint bit = CUtil.bign(-ign);
                        if ((f.failed & bit) == 0)
                        {
                            sum_constraints.push_back(ign);
                            f.failed |= (byte)bit;
                        }
                    }
                    clause.clear();
                    clear_analyzed_literals();
                    for (int q = 0; q < lrat_chain.n; q++) constraint_chains[^1].Add(lrat_chain[q]);
                    lrat_chain.clear();
                }
                for (int q = 0; q < sum_constraints.n; q++) clause.push_back(sum_constraints[q]);
            }
            clear_analyzed_literals();

            VERBOSE(1, $"found {clause.n} failed assumptions {CUtil.percent(clause.n, assumptions.n):F0}%");

            if (!unsat_constraint)
            {
                if (proof != null)
                {
                    var eclause = new Vec<int>();
                    for (int k = 0; k < clause.n; k++) eclause.push_back(externalize(clause[k]));
                    proof.add_assumption_clause(++clause_id, eclause, lrat_chain);
                    conclusion.push_back(clause_id);
                }
            }
            else
            {
                for (int p = constraint.n - 1; p >= 0; p--)
                {
                    int lit = constraint[p];
                    if (lrat)
                    {
                        clause.clear();
                        foreach (var ign in constraint_clauses[^1]) clause.push_back(ign);
                        constraint_clauses.RemoveAt(constraint_clauses.Count - 1);
                    }
                    clause.push_back(-lit);
                    if (proof != null)
                    {
                        if (lrat)
                        {
                            foreach (var id in constraint_chains[^1]) lrat_chain.push_back(id);
                            constraint_chains.RemoveAt(constraint_chains.Count - 1);
                        }
                        var eclause = new Vec<int>();
                        for (int k = 0; k < clause.n; k++) eclause.push_back(externalize(clause[k]));
                        proof.add_assumption_clause(++clause_id, eclause, lrat_chain);
                        conclusion.push_back(clause_id);
                        lrat_chain.clear();
                    }
                    clause.pop_back();
                }
                if (proof != null)
                {
                    for (int k = 0; k < econstraints.n; k++)
                    {
                        int elit = econstraints[k];
                        if (lrat)
                        {
                            uint eidx = (elit > 0 ? 1u : 0u) + 2u * (uint)Math.Abs(elit);
                            ulong id = external.ext_units[eidx];
                            if (id != 0) lrat_chain.push_back(id);
                            else
                            {
                                int lit = external.e2i[Math.Abs(elit)];
                                if (elit < 0) lit = -lit;
                                uint uidx = vlit(-lit);
                                ulong uid = unit_clauses(uidx);
                                lrat_chain.push_back(uid);
                            }
                        }
                        var cl = new Vec<int>();
                        cl.push_back(-elit);
                        proof.add_assumption_clause(++clause_id, cl, lrat_chain);
                        conclusion.push_back(clause_id);
                        lrat_chain.clear();
                    }
                }
            }
            lrat_chain.clear();
            clause.clear();
        }
    DONE:;
    }

    public bool failed(int lit)
    {
        if (!marked_failed)
        {
            if (conflict_id == 0) failing();
            marked_failed = true;
        }
        conclude_unsat();
        ref Flags f = ref flags(lit);
        uint bit = CUtil.bign(lit);
        return (f.failed & bit) != 0;
    }

    public void conclude_unsat()
    {
        if (proof == null || concluded) return;
        concluded = true;
        if (!marked_failed)
        {
            if (conflict_id == 0) failing();
            marked_failed = true;
        }
        ConclusionType con;
        if (conflict_id != 0) con = ConclusionType.CONFLICT;
        else if (unsat_constraint) con = ConclusionType.CONSTRAINT;
        else con = ConclusionType.ASSUMPTIONS;
        proof.conclude_unsat(con, conclusion);
    }

    public void reset_concluded()
    {
        if (proof != null) proof.reset_assumptions();
        if (concluded) concluded = false;
        if (conflict_id != 0) return;
        conclusion.clear();
    }

    public void reset_assumptions()
    {
        for (int k = 0; k < assumptions.n; k++)
        {
            int lit = assumptions[k];
            ref Flags f = ref flags(lit);
            byte bit = (byte)CUtil.bign(lit);
            f.assumed &= (byte)~bit;
            f.failed &= (byte)~bit;
            melt(lit);
        }
        assumptions.clear();
        marked_failed = true;
    }

    public void sort_and_reuse_assumptions()
    {
        if (assumptions.empty()) return;
        uint max_level_ = (uint)level + 1u;
        CUtil.msort(opts.radixsortlim, assumptions.AsSpan(), a =>
        {
            int v_ = val(a);
            bool assigned = v_ != 0;
            ref Var v = ref @var(a);
            ulong res = assigned ? (uint)v.level : max_level_;
            res <<= 32;
            res |= assigned ? (uint)v.trail : (uint)Math.Abs(a);
            return res;
        });
        uint max_level = 0;
        for (int k = 0; k < assumptions.n; k++)
        {
            int lit = assumptions[k];
            if (val(lit) != 0) max_level = (uint)@var(lit).level;
            else break;
        }
        uint size = Math.Min((uint)level + 1u, max_level + 1);
        int target = 0;
        for (uint i = 1, j = 0; i < size;)
        {
            ref Level l = ref control[(int)i];
            int lit = l.decision;
            int alit = assumptions[(int)j];
            int lev = (int)i;
            target = lev;
            if (val(alit) != 0 && @var(alit).level < lev)
            {
                ++j;
                continue;
            }
            if (lit == 0)
            {
                target = lev - 1;
                break;
            }
            ++i; ++j;
            if (l.decision == alit) continue;
            target = lev - 1;
            break;
        }
        if (target < level) backtrack(target);
        if (level > assumptions.n) stats.assumptionsreused += assumptions.n;
        else stats.assumptionsreused += level;
    }

    /*------------------------------------------------------------------------*/
    // 'constrain.cpp'

    public void constrain(int lit)
    {
        if (lit != 0) constraint.push_back(lit);
        else
        {
            if (level != 0) backtrack();
            bool satisfied_constraint = false;
            int end = constraint.n;
            int i = 0;
            for (int j = 0; j != end; j++)
            {
                int l = constraint[j];
                int tmp = marked(l);
                if (tmp > 0) { }
                else if (tmp < 0)
                {
                    satisfied_constraint = true;
                    break;
                }
                else
                {
                    tmp = val(l);
                    if (tmp < 0) { }
                    else if (tmp > 0)
                    {
                        satisfied_constraint = true;
                        break;
                    }
                    else
                    {
                        constraint[i++] = l;
                        mark(l);
                    }
                }
            }
            constraint.resize(i);
            for (int k = 0; k < constraint.n; k++) unmark(constraint[k]);
            if (satisfied_constraint) constraint.clear();
            else if (constraint.empty())
            {
                unsat_constraint = true;
                if (conflict_id == 0) marked_failed = false;
            }
            else
                for (int k = 0; k < constraint.n; k++) freeze(constraint[k]);
        }
    }

    public bool failed_constraint() => unsat_constraint;

    public void reset_constraint()
    {
        for (int k = 0; k < constraint.n; k++) melt(constraint[k]);
        constraint.clear();
        unsat_constraint = false;
        marked_failed = true;
    }

    /*------------------------------------------------------------------------*/
    // 'flip.cpp'

    public bool flip(int lit)
    {
        if (!is_active(lit) && !flags(lit).unused()) return false;
        if (propergated < trail.n) propergate();
        int idx = vidx(lit);
        sbyte original_value = vals[idx];
        lit = original_value < 0 ? -idx : idx;
        bool res = true;
        var ws = watches(lit);
        int eow = ws.n;
        var a = ws.a;
        for (int i = 0; i != eow; i++)
        {
            Watch w = a[i];
            if (w.size != 2) continue;
            sbyte b = val(w.blit);
            if (b > 0) continue;
            res = false;
            break;
        }
        if (res)
        {
            int i = 0, j = 0;
            while (i != eow)
            {
                Watch w = a[j++] = a[i++];
                if (w.size == 2) continue;
                if (w.clause.garbage) { j--; continue; }
                int[] lits = w.clause.literals;
                int other = lits[0] ^ lits[1] ^ lit;
                sbyte u = val(other);
                if (u > 0) continue;
                int size = w.clause.size;
                int middle = w.clause.pos;
                int k = middle;
                int r = 0;
                sbyte v = -1;
                while (k != size && (v = val(r = lits[k])) < 0) k++;
                if (v < 0)
                {
                    k = 2;
                    while (k != middle && (v = val(r = lits[k])) < 0) k++;
                }
                if (v < 0)
                {
                    res = false;
                    break;
                }
                w.clause.pos = k;
                lits[0] = other; lits[1] = r; lits[k] = lit;
                watch_literal(r, lit, w.clause);
                j--;
            }
            if (j != i)
            {
                while (i != eow) a[j++] = a[i++];
                ws.shrink(j);
            }
        }
        if (res)
        {
            idx = vidx(lit);
            original_value = vals[idx];
            lit = original_value < 0 ? -idx : idx;
            set_val(idx, (sbyte)-original_value);
            ref Var v = ref @var(idx);
            trail[v.trail] = -lit;
            if (opts.ilb != 0)
            {
                if (tainted_literal == 0) tainted_literal = lit;
                else if (v.level < @var(tainted_literal).level) tainted_literal = lit;
            }
        }
        return res;
    }

    public bool flippable(int lit)
    {
        if (!is_active(lit) && !flags(lit).unused()) return false;
        if (propergated < trail.n) propergate();
        int idx = vidx(lit);
        sbyte original_value = vals[idx];
        lit = original_value < 0 ? -idx : idx;
        bool res = true;
        var ws = watches(lit);
        int eow = ws.n;
        var a = ws.a;
        for (int i = 0; i != eow; i++)
        {
            Watch w = a[i];
            sbyte b = val(w.blit);
            if (b > 0) continue;
            if (w.size == 2)
            {
                res = false;
                break;
            }
            if (w.clause.garbage) continue;
            int[] lits = w.clause.literals;
            int other = lits[0] ^ lits[1] ^ lit;
            sbyte u = val(other);
            if (u > 0)
            {
                a[i].blit = other;
                continue;
            }
            int size = w.clause.size;
            int middle = w.clause.pos;
            int k = middle;
            int r = 0;
            sbyte v = -1;
            while (k != size && (v = val(r = lits[k])) < 0) k++;
            if (v < 0)
            {
                k = 2;
                while (k != middle && (v = val(r = lits[k])) < 0) k++;
            }
            if (v < 0)
            {
                res = false;
                break;
            }
            w.clause.pos = k;
            a[i].blit = r;
        }
        return res;
    }

    /*------------------------------------------------------------------------*/
    // 'lucky.cpp'

    int unlucky(int res)
    {
        if (level > 0) backtrack();
        if (conflict != null) conflict = null;
        return res;
    }

    int trivially_false_satisfiable()
    {
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (terminated_asynchronously(100)) return unlucky(-1);
            if (c.garbage) continue;
            if (c.redundant) continue;
            bool satisfied_ = false, found_negative_literal = false;
            for (int i = 0; i < c.size; i++)
            {
                int lit = c.literals[i];
                sbyte tmp = val(lit);
                if (tmp > 0) { satisfied_ = true; break; }
                if (tmp < 0) continue;
                if (lit > 0) continue;
                found_negative_literal = true;
                break;
            }
            if (satisfied_ || found_negative_literal) continue;
            return unlucky(0);
        }
        VERBOSE(1, "all clauses contain a negative literal");
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (terminated_asynchronously(10)) return unlucky(-1);
            if (val(idx) != 0) continue;
            search_assume_decision(-idx);
            if (propagate()) continue;
            return unlucky(0);
        }
        stats.lucky.constant.zero++;
        return 10;
    }

    int trivially_true_satisfiable()
    {
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (terminated_asynchronously(100)) return unlucky(-1);
            if (c.garbage) continue;
            if (c.redundant) continue;
            bool satisfied_ = false, found_positive_literal = false;
            for (int i = 0; i < c.size; i++)
            {
                int lit = c.literals[i];
                sbyte tmp = val(lit);
                if (tmp > 0) { satisfied_ = true; break; }
                if (tmp < 0) continue;
                if (lit < 0) continue;
                found_positive_literal = true;
                break;
            }
            if (satisfied_ || found_positive_literal) continue;
            return unlucky(0);
        }
        VERBOSE(1, "all clauses contain a positive literal");
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (terminated_asynchronously(10)) return unlucky(-1);
            if (val(idx) != 0) continue;
            search_assume_decision(idx);
            if (propagate()) continue;
            return unlucky(0);
        }
        stats.lucky.constant.one++;
        return 10;
    }

    int forward_false_satisfiable()
    {
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (terminated_asynchronously(100)) return unlucky(-1);
            if (val(idx) != 0) continue;
            search_assume_decision(-idx);
            if (!propagate()) return unlucky(0);
        }
        VERBOSE(1, "forward assuming variables false satisfies formula");
        stats.lucky.forward.zero++;
        return 10;
    }

    int forward_true_satisfiable()
    {
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (terminated_asynchronously(10)) return unlucky(-1);
            if (val(idx) != 0) continue;
            search_assume_decision(idx);
            if (!propagate()) return unlucky(0);
        }
        VERBOSE(1, "forward assuming variables true satisfies formula");
        stats.lucky.forward.one++;
        return 10;
    }

    int backward_false_satisfiable()
    {
        for (int idx = max_var; idx > 0; idx--)
        {
            if (terminated_asynchronously(10)) return unlucky(-1);
            if (val(idx) != 0) continue;
            search_assume_decision(-idx);
            if (!propagate()) return unlucky(0);
        }
        VERBOSE(1, "backward assuming variables false satisfies formula");
        stats.lucky.backward.zero++;
        return 10;
    }

    int backward_true_satisfiable()
    {
        for (int idx = max_var; idx > 0; idx--)
        {
            if (terminated_asynchronously(10)) return unlucky(-1);
            if (val(idx) != 0) continue;
            search_assume_decision(idx);
            if (!propagate()) return unlucky(0);
        }
        VERBOSE(1, "backward assuming variables true satisfies formula");
        stats.lucky.backward.one++;
        return 10;
    }

    int positive_horn_satisfiable()
    {
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (terminated_asynchronously(10)) return unlucky(-1);
            if (c.garbage) continue;
            if (c.redundant) continue;
            int positive_literal = 0;
            bool satisfied_ = false;
            for (int i = 0; i < c.size; i++)
            {
                int lit = c.literals[i];
                sbyte tmp = val(lit);
                if (tmp > 0) { satisfied_ = true; break; }
                if (tmp < 0) continue;
                if (lit < 0) continue;
                positive_literal = lit;
                break;
            }
            if (satisfied_) continue;
            if (positive_literal == 0) return unlucky(0);
            search_assume_decision(positive_literal);
            if (propagate()) continue;
            return unlucky(0);
        }
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (terminated_asynchronously(10)) return unlucky(-1);
            if (val(idx) != 0) continue;
            search_assume_decision(-idx);
            if (propagate()) continue;
            return unlucky(0);
        }
        VERBOSE(1, "clauses are positive horn satisfied");
        stats.lucky.horn.positive++;
        return 10;
    }

    int negative_horn_satisfiable()
    {
        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (terminated_asynchronously(10)) return unlucky(-1);
            if (c.garbage) continue;
            if (c.redundant) continue;
            int negative_literal = 0;
            bool satisfied_ = false;
            for (int i = 0; i < c.size; i++)
            {
                int lit = c.literals[i];
                sbyte tmp = val(lit);
                if (tmp > 0) { satisfied_ = true; break; }
                if (tmp < 0) continue;
                if (lit > 0) continue;
                negative_literal = lit;
                break;
            }
            if (satisfied_) continue;
            if (negative_literal == 0)
            {
                if (level > 0) backtrack();
                return unlucky(0);
            }
            search_assume_decision(negative_literal);
            if (propagate()) continue;
            return unlucky(0);
        }
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (terminated_asynchronously(10)) return unlucky(-1);
            if (val(idx) != 0) continue;
            search_assume_decision(idx);
            if (propagate()) continue;
            return unlucky(0);
        }
        VERBOSE(1, "clauses are negative horn satisfied");
        stats.lucky.horn.negative++;
        return 10;
    }

    public int lucky_phases()
    {
        if (opts.lucky == 0) return 0;
        if (!assumptions.empty() || !constraint.empty() || external_prop) return 0;
        searching_lucky_phases = true;
        stats.lucky.tried++;
        int res = trivially_false_satisfiable();
        if (res == 0) res = trivially_true_satisfiable();
        if (res == 0) res = forward_true_satisfiable();
        if (res == 0) res = forward_false_satisfiable();
        if (res == 0) res = backward_false_satisfiable();
        if (res == 0) res = backward_true_satisfiable();
        if (res == 0) res = positive_horn_satisfiable();
        if (res == 0) res = negative_horn_satisfiable();
        if (res < 0) res = 0;
        if (res == 10) stats.lucky.succeeded++;
        report('l', res == 0 ? 1 : 0);
        searching_lucky_phases = false;
        return res;
    }
}
