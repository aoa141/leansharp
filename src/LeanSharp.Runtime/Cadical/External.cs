// Port of 'external.hpp', 'external.cpp', 'extend.cpp' and 'restore.cpp'.

namespace LeanSharp.Runtime.Cadical;

public sealed class External
{
    public readonly Internal internal_;
    public int max_var;
    public int vsize;
    public readonly BoolVec vals = new();
    public readonly Vec<int> e2i = new();
    public readonly Vec<int> assumptions = new();
    public readonly Vec<int> constraint = new();
    public readonly Vec<ulong> ext_units = new();
    public readonly BoolVec ext_flags = new();
    public readonly Vec<int> eclause = new();
    public bool extended;
    public bool concluded;
    public readonly Vec<int> extension = new();
    public readonly BoolVec witness = new();
    public readonly BoolVec tainted = new();
    public readonly Vec<uint> frozentab = new();
    public Func<bool> terminator;
    public readonly BoolVec is_observed = new();
    public sbyte[] solution;
    public readonly Vec<int> original = new();
    public readonly BoolVec moltentab = new();

    public External(Internal i)
    {
        internal_ = i;
        i.external = this;
    }

    public int vidx(int elit) => Math.Abs(elit);

    public void enlarge(int new_max_var)
    {
        long new_vsize = vsize != 0 ? 2L * vsize : 1L + new_max_var;
        while (new_vsize <= new_max_var) new_vsize *= 2;
        vsize = (int)new_vsize;
    }

    public void init(int new_max_var)
    {
        if (new_max_var <= max_var) return;
        int new_vars = new_max_var - max_var;
        int old_internal_max_var = internal_.max_var;
        int new_internal_max_var = old_internal_max_var + new_vars;
        internal_.init_vars(new_internal_max_var);
        if (new_max_var >= vsize) enlarge(new_max_var);
        if (max_var == 0)
        {
            e2i.push_back(0);
            ext_units.push_back(0);
            ext_units.push_back(0);
            ext_flags.push_back(false);
            internal_.i2e.push_back(0);
        }
        uint iidx = (uint)old_internal_max_var + 1, eidx;
        for (eidx = (uint)max_var + 1u; eidx <= (uint)new_max_var; eidx++, iidx++)
        {
            e2i.push_back((int)iidx);
            ext_units.push_back(0);
            ext_units.push_back(0);
            ext_flags.push_back(false);
            internal_.i2e.push_back((int)eidx);
        }
        if (new_max_var >= is_observed.size()) is_observed.resize(1 + new_max_var, false);
        if (internal_.opts.checkfrozen != 0)
            if (new_max_var >= moltentab.size()) moltentab.resize(1 + new_max_var, false);
        max_var = new_max_var;
    }

    public void reset_assumptions()
    {
        assumptions.clear();
        internal_.reset_assumptions();
    }

    public void reset_concluded()
    {
        concluded = false;
        internal_.reset_concluded();
    }

    public void reset_constraint()
    {
        constraint.clear();
        internal_.reset_constraint();
    }

    public void reset_extended()
    {
        if (!extended) return;
        extended = false;
    }

    public void reset_limits() => internal_.reset_limits();

    /*------------------------------------------------------------------------*/

    public uint elit2ulit(int elit)
    {
        int idx = Math.Abs(elit) - 1;
        return 2u * (uint)idx + (elit < 0 ? 1u : 0u);
    }

    public bool marked(BoolVec map, int elit)
    {
        uint ulit = elit2ulit(elit);
        return ulit < (uint)map.size() && map[(int)ulit];
    }

    public void mark(BoolVec map, int elit)
    {
        uint ulit = elit2ulit(elit);
        if (ulit >= (uint)map.size()) map.resize((int)ulit + 1, false);
        map[(int)ulit] = true;
    }

    public void unmark(BoolVec map, int elit)
    {
        uint ulit = elit2ulit(elit);
        if (ulit < (uint)map.size()) map[(int)ulit] = false;
    }

    /*------------------------------------------------------------------------*/

    public int internalize(int elit)
    {
        int ilit;
        if (elit != 0)
        {
            int eidx = Math.Abs(elit);
            if (eidx > max_var) init(eidx);
            ilit = e2i[eidx];
            if (elit < 0) ilit = -ilit;
            if (ilit == 0)
            {
                ilit = internal_.max_var + 1;
                internal_.init_vars(ilit);
                e2i[eidx] = ilit;
                internal_.i2e.push_back(eidx);
                if (elit < 0) ilit = -ilit;
            }
            if (internal_.opts.checkfrozen != 0)
            {
                if (moltentab[eidx]) throw new CadicalException($"can not reuse molten literal {eidx}");
            }
            ref Flags f = ref internal_.flags(ilit);
            if (f.status == Flags.UNUSED) internal_.mark_active(ilit);
            else if (f.status != Flags.ACTIVE && f.status != Flags.FIXED) internal_.reactivate(ilit);
            if (!marked(tainted, elit) && marked(witness, -elit))
            {
                mark(tainted, elit);
            }
        }
        else ilit = 0;
        return ilit;
    }

    public void add(int elit)
    {
        reset_extended();
        if (internal_.opts.check != 0 && (internal_.opts.checkwitness != 0 || internal_.opts.checkfailed != 0))
            original.push_back(elit);
        int ilit = internalize(elit);
        if (elit != 0 && internal_.proof != null)
        {
            eclause.push_back(elit);
            if (internal_.lrat)
            {
                uint eidx = (elit > 0 ? 1u : 0u) + 2u * (uint)Math.Abs(elit);
                ulong id = ext_units[(int)eidx];
                bool added = ext_flags[Math.Abs(elit)];
                if (id != 0 && !added)
                {
                    ext_flags[Math.Abs(elit)] = true;
                    internal_.lrat_chain.push_back(id);
                }
            }
        }
        if (elit == 0 && internal_.proof != null && internal_.lrat)
        {
            for (int k = 0; k < eclause.n; k++) ext_flags[Math.Abs(eclause[k])] = false;
        }
        internal_.add_original_lit(ilit);
        if (elit == 0 && internal_.proof != null) eclause.clear();
    }

    public void assume(int elit)
    {
        reset_extended();
        if (internal_.proof != null) internal_.proof.add_assumption(elit);
        assumptions.push_back(elit);
        int ilit = internalize(elit);
        internal_.assume(ilit);
    }

    public bool flip(int elit)
    {
        int eidx = Math.Abs(elit);
        if (eidx > max_var) return false;
        if (marked(witness, elit)) return false;
        int ilit = e2i[eidx];
        if (ilit == 0) return false;
        bool res = internal_.flip(ilit);
        if (res && extended) reset_extended();
        return res;
    }

    public bool flippable(int elit)
    {
        int eidx = Math.Abs(elit);
        if (eidx > max_var) return false;
        if (marked(witness, elit)) return false;
        int ilit = e2i[eidx];
        if (ilit == 0) return false;
        return internal_.flippable(ilit);
    }

    public bool failed(int elit)
    {
        int eidx = Math.Abs(elit);
        if (eidx > max_var) return false;
        int ilit = e2i[eidx];
        if (ilit == 0) return false;
        if (elit < 0) ilit = -ilit;
        return internal_.failed(ilit);
    }

    public void constrain(int elit)
    {
        if (constraint.n != 0 && constraint.back() == 0) reset_constraint();
        reset_extended();
        int ilit = internalize(elit);
        if (elit == 0 && internal_.proof != null) internal_.proof.add_constraint(constraint);
        constraint.push_back(elit);
        internal_.constrain(ilit);
    }

    public bool failed_constraint() => internal_.failed_constraint();

    public void phase(int elit)
    {
        int ilit = internalize(elit);
        internal_.phase(ilit);
    }

    public void unphase(int elit)
    {
        int eidx = Math.Abs(elit);
        if (eidx > max_var) return;
        int ilit = e2i[eidx];
        if (ilit == 0) return;
        if (elit < 0) ilit = -ilit;
        internal_.unphase(ilit);
    }

    public bool observed(int elit)
    {
        int eidx = Math.Abs(elit);
        if (eidx > max_var) return false;
        if (eidx >= is_observed.size()) return false;
        return is_observed[eidx];
    }

    public bool is_witness(int elit)
    {
        int eidx = Math.Abs(elit);
        if (eidx > max_var) return false;
        return marked(witness, elit) || marked(witness, -elit);
    }

    /*------------------------------------------------------------------------*/

    public void check_satisfiable()
    {
        if (!extended) extend();
        if (internal_.opts.checkwitness != 0) check_assignment();
        if (internal_.opts.checkassumptions != 0 && !assumptions.empty()) check_assumptions_satisfied();
        if (internal_.opts.checkconstraint != 0 && !constraint.empty()) check_constraint_satisfied();
    }

    public void check_unsatisfiable()
    {
        if (internal_.opts.checkfailed == 0) return;
        if (!assumptions.empty() || !constraint.empty()) check_failing();
    }

    public void check_solve_result(int res)
    {
        if (internal_.opts.check == 0) return;
        if (res == 10) check_satisfiable();
        if (res == 20) check_unsatisfiable();
    }

    public void update_molten_literals()
    {
        if (internal_.opts.checkfrozen == 0) return;
        for (int lit = 1; lit <= max_var; lit++)
        {
            if (moltentab[lit]) { }
            else if (frozen(lit)) { }
            else moltentab[lit] = true;
        }
    }

    public int solve(bool preprocess_only)
    {
        reset_extended();
        update_molten_literals();
        int res = internal_.solve(preprocess_only);
        check_solve_result(res);
        reset_limits();
        return res;
    }

    public void terminate() => internal_.terminate();

    public int lookahead()
    {
        reset_extended();
        update_molten_literals();
        int ilit = internal_.lookahead();
        int elit = (ilit != 0 && ilit != int.MinValue) ? internal_.externalize(ilit) : 0;
        return elit;
    }

    public void freeze(int elit)
    {
        reset_extended();
        int ilit = internalize(elit);
        int eidx = vidx(elit);
        if (eidx >= frozentab.n) frozentab.resize(eidx + 1, 0);
        ref uint r = ref frozentab[eidx];
        if (r < uint.MaxValue) r++;
        internal_.freeze(ilit);
    }

    public void melt(int elit)
    {
        reset_extended();
        int ilit = internalize(elit);
        int eidx = vidx(elit);
        ref uint r = ref frozentab[eidx];
        if (r < uint.MaxValue)
        {
            if (--r == 0)
            {
                if (observed(elit)) r++;
            }
        }
        internal_.melt(ilit);
    }

    public bool frozen(int elit)
    {
        int eidx = Math.Abs(elit);
        if (eidx > max_var) return false;
        if (eidx >= frozentab.n) return false;
        return frozentab[eidx] > 0;
    }

    public int fixed_(int elit)
    {
        int eidx = Math.Abs(elit);
        if (eidx > max_var) return 0;
        int ilit = e2i[eidx];
        if (ilit == 0) return 0;
        if (elit < 0) ilit = -ilit;
        return internal_.@fixed(ilit);
    }

    public int ival(int elit)
    {
        int eidx = Math.Abs(elit);
        bool v = false;
        if (eidx <= max_var && eidx < vals.size()) v = vals[eidx];
        if (elit < 0) v = !v;
        return v ? elit : -elit;
    }

    /*------------------------------------------------------------------------*/

    void fatal(string msg) => throw new CadicalException(msg);

    public void check_assignment()
    {
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (ival(idx) == 0) fatal($"unassigned variable: {idx}");
            int value_idx = ival(idx);
            int value_neg_idx = ival(-idx);
            if (value_idx != value_neg_idx) fatal($"inconsistently assigned literals {idx} and {-idx}");
        }
        bool satisfied = false;
        int start = 0;
        for (int i = 0; i < original.n; i++)
        {
            int lit = original[i];
            if (lit == 0)
            {
                if (!satisfied)
                {
                    var sb = new System.Text.StringBuilder("unsatisfied clause:\n");
                    for (int j = start; j != i; j++) sb.Append(original[j]).Append(' ');
                    sb.Append('0');
                    fatal(sb.ToString());
                }
                satisfied = false;
                start = i + 1;
            }
            else if (!satisfied && ival(lit) == lit) satisfied = true;
        }
    }

    void check_assumptions_satisfied()
    {
        for (int k = 0; k < assumptions.n; k++)
        {
            int lit = assumptions[k];
            int tmp = ival(lit);
            if (tmp != lit) fatal($"assumption {lit} falsified");
        }
    }

    void check_constraint_satisfied()
    {
        for (int k = 0; k < constraint.n; k++)
        {
            int lit = constraint[k];
            if (ival(lit) == lit) return;
        }
        fatal("constraint not satisfied");
    }

    void check_failing()
    {
        var checker = new Solver();
        checker.prefix("checker ");
        for (int k = 0; k < assumptions.n; k++)
        {
            int lit = assumptions[k];
            if (!failed(lit)) continue;
            checker.add(lit);
            checker.add(0);
        }
        if (failed_constraint())
        {
            for (int k = 0; k < constraint.n; k++) checker.add(constraint[k]);
        }
        for (int k = 0; k < original.n; k++) checker.add(original[k]);
        int res = checker.solve();
        if (res != 20) fatal("failed assumptions do not form a core");
    }

    /*------------------------------------------------------------------------*/

    public bool traverse_all_frozen_units_as_clauses(Func<Vec<int>, bool> it)
    {
        if (internal_.unsat) return true;
        var clause = new Vec<int>();
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (!frozen(idx)) continue;
            int tmp = fixed_(idx);
            if (tmp == 0) continue;
            int unit = tmp < 0 ? -idx : idx;
            clause.push_back(unit);
            if (!it(clause)) return false;
            clause.clear();
        }
        return true;
    }

    public bool traverse_all_non_frozen_units_as_witnesses(Func<Vec<int>, Vec<int>, ulong, bool> it)
    {
        if (internal_.unsat) return true;
        var clause_and_witness = new Vec<int>();
        for (int idx = 1; idx <= max_var; idx++)
        {
            if (frozen(idx)) continue;
            int tmp = fixed_(idx);
            if (tmp == 0) continue;
            int unit = tmp < 0 ? -idx : idx;
            int ilit = e2i[idx] * (tmp < 0 ? -1 : 1);
            ulong id = internal_.opts.lrat != 0 ? internal_.unit_clauses(internal_.vlit(ilit)) : 1;
            clause_and_witness.push_back(unit);
            if (!it(clause_and_witness, clause_and_witness, id + (ulong)max_var)) return false;
            clause_and_witness.clear();
        }
        return true;
    }

    public void copy_flags(External other)
    {
        var this_ftab = internal_.ftab;
        var other_ftab = other.internal_.ftab;
        int limit = Math.Min(max_var, other.max_var);
        for (int eidx = 1; eidx <= limit; eidx++)
        {
            int this_ilit = e2i[eidx];
            if (this_ilit == 0) continue;
            int other_ilit = other.e2i[eidx];
            if (other_ilit == 0) continue;
            if (!internal_.is_active(this_ilit)) continue;
            if (!other.internal_.is_active(other_ilit)) continue;
            this_ftab[Math.Abs(this_ilit)].copy(ref other_ftab[Math.Abs(other_ilit)]);
        }
    }

    /*------------------------------------------------------------------------*/
    // 'extend.cpp'

    public void push_zero_on_extension_stack() => extension.push_back(0);

    public void push_id_on_extension_stack(ulong id)
    {
        uint higher_bits = (uint)(int)(id << 32); // always zero, as in the C++ code
        uint lower_bits = (uint)(id & ((1ul << 32) - 1));
        extension.push_back((int)higher_bits);
        extension.push_back((int)lower_bits);
    }

    public void push_clause_literal_on_extension_stack(int ilit)
    {
        int elit = internal_.externalize(ilit);
        extension.push_back(elit);
    }

    public void push_witness_literal_on_extension_stack(int ilit)
    {
        int elit = internal_.externalize(ilit);
        extension.push_back(elit);
        if (marked(witness, elit)) return;
        mark(witness, elit);
    }

    public void push_clause_on_extension_stack(Clause c)
    {
        internal_.stats.weakened++;
        internal_.stats.weakenedlen += c.size;
        push_zero_on_extension_stack();
        push_id_on_extension_stack(c.id);
        push_zero_on_extension_stack();
        for (int i = 0; i < c.size; i++) push_clause_literal_on_extension_stack(c.literals[i]);
    }

    public void push_clause_on_extension_stack(Clause c, int pivot)
    {
        push_zero_on_extension_stack();
        push_witness_literal_on_extension_stack(pivot);
        push_clause_on_extension_stack(c);
    }

    public void push_binary_clause_on_extension_stack(ulong id, int pivot, int other)
    {
        internal_.stats.weakened++;
        internal_.stats.weakenedlen += 2;
        push_zero_on_extension_stack();
        push_witness_literal_on_extension_stack(pivot);
        push_zero_on_extension_stack();
        push_id_on_extension_stack(id);
        push_zero_on_extension_stack();
        push_clause_literal_on_extension_stack(pivot);
        push_clause_literal_on_extension_stack(other);
    }

    public void push_external_clause_and_witness_on_extension_stack(Vec<int> c, Vec<int> w, ulong id)
    {
        extension.push_back(0);
        for (int k = 0; k < w.n; k++)
        {
            int elit = w[k];
            init(Math.Abs(elit));
            extension.push_back(elit);
            mark(witness, elit);
        }
        extension.push_back(0);
        uint higher_bits = (uint)(int)(id << 32);
        uint lower_bits = (uint)(id & ((1ul << 32) - 1));
        extension.push_back((int)higher_bits);
        extension.push_back((int)lower_bits);
        extension.push_back(0);
        for (int k = 0; k < c.n; k++)
        {
            int elit = c[k];
            init(Math.Abs(elit));
            extension.push_back(elit);
        }
    }

    public void extend()
    {
        internal_.stats.extensions++;
        for (int i = 1; i <= max_var; i++)
        {
            int ilit = e2i[i];
            if (ilit == 0) continue;
            if (i >= vals.size()) vals.resize(i + 1, false);
            vals[i] = internal_.val(ilit) > 0;
        }
        var ext = extension.a;
        int begin = 0;
        int p = extension.n;
        while (p != begin)
        {
            bool satisfied = false;
            int lit;
            while ((lit = ext[--p]) != 0)
            {
                if (satisfied) continue;
                if (ival(lit) == lit) satisfied = true;
            }
            --p;
            --p;
            --p;
            if (satisfied)
            {
                while (ext[--p] != 0) { }
            }
            else
            {
                while ((lit = ext[--p]) != 0)
                {
                    int tmp = ival(lit);
                    if (tmp != lit)
                    {
                        int idx = Math.Abs(lit);
                        if (idx >= vals.size()) vals.resize(idx + 1, false);
                        vals[idx] = !vals[idx];
                        internal_.stats.extended++;
                    }
                }
            }
        }
        extended = true;
    }

    public bool traverse_witnesses_backward(Func<Vec<int>, Vec<int>, ulong, bool> it)
    {
        if (internal_.unsat) return true;
        var clause = new Vec<int>();
        var witness_ = new Vec<int>();
        var ext = extension.a;
        int begin = 0;
        int i = extension.n;
        while (i != begin)
        {
            int lit;
            while ((lit = ext[--i]) != 0) clause.push_back(lit);
            --i;
            ulong id = ((ulong)(uint)ext[i - 1] << 32) + (ulong)(uint)ext[i];
            i -= 2;
            while ((lit = ext[--i]) != 0) witness_.push_back(lit);
            clause.reverse();
            witness_.reverse();
            if (!it(clause, witness_, id)) return false;
            clause.clear();
            witness_.clear();
        }
        return true;
    }

    public bool traverse_witnesses_forward(Func<Vec<int>, Vec<int>, ulong, bool> it)
    {
        if (internal_.unsat) return true;
        var clause = new Vec<int>();
        var witness_ = new Vec<int>();
        var ext = extension.a;
        int end = extension.n;
        int i = 0;
        if (i != end)
        {
            int lit = ext[i++];
            do
            {
                while ((lit = ext[i++]) != 0) witness_.push_back(lit);
                ulong id = ((ulong)(uint)ext[i] << 32) + (ulong)(uint)ext[i + 1];
                i += 3;
                while (i != end && (lit = ext[i++]) != 0) clause.push_back(lit);
                if (!it(clause, witness_, id)) return false;
                clause.clear();
                witness_.clear();
            } while (i != end);
        }
        return true;
    }

    public void conclude_sat()
    {
        if (internal_.proof == null || concluded) return;
        concluded = true;
        if (!extended) extend();
        var model = new Vec<int>();
        for (int idx = 1; idx <= max_var; idx++) model.push_back(ival(idx));
        internal_.proof.conclude_sat(model);
    }

    /*------------------------------------------------------------------------*/
    // 'restore.cpp'

    void restore_clause(int begin, int end, ulong id)
    {
        var ext = extension.a;
        for (int p = begin; p != end; p++)
        {
            eclause.push_back(ext[p]);
            if (internal_.proof != null && internal_.lrat)
            {
                int elit = ext[p];
                uint eidx = (elit > 0 ? 1u : 0u) + 2u * (uint)Math.Abs(elit);
                ulong uid = ext_units[(int)eidx];
                bool added = ext_flags[Math.Abs(elit)];
                if (uid != 0 && !added)
                {
                    ext_flags[Math.Abs(elit)] = true;
                    internal_.lrat_chain.push_back(uid);
                }
            }
            int ilit = internalize(ext[p]);
            ext = extension.a; // 'internalize' does not touch 'extension' but be safe
            internal_.add_original_lit(ilit);
            internal_.stats.restoredlits++;
        }
        if (internal_.proof != null && internal_.lrat)
        {
            for (int k = 0; k < eclause.n; k++) ext_flags[Math.Abs(eclause[k])] = false;
        }
        internal_.finish_added_clause_with_id(id, true);
        eclause.clear();
        internal_.stats.restored++;
    }

    public void restore_clauses()
    {
        internal_.stats.restorations++;
        long weakened = 0, satisfied_count = 0, restored = 0, removed = 0;
        var ext = extension.a;
        int end_of_extension = extension.n;
        int p = 0, q = 0;
        while (p != end_of_extension)
        {
            weakened++;
            int saved = q;
            ext[q++] = ext[p++];
            int tlit = 0;
            int elit;
            while ((elit = ext[q++] = ext[p++]) != 0)
            {
                if (marked(tainted, -elit)) tlit = elit;
            }
            ulong id = ((ulong)(uint)ext[p] << 32) + (ulong)(uint)ext[p + 1];
            ext[q++] = ext[p++];
            ext[q++] = ext[p++];
            ext[q++] = ext[p++];
            int satisfied = 0;
            int end_of_clause = p;
            while (end_of_clause != end_of_extension && (elit = ext[end_of_clause]) != 0)
            {
                if (satisfied == 0 && fixed_(elit) > 0) satisfied = elit;
                end_of_clause++;
            }
            if (satisfied != 0 && internal_.opts.restoreflush == 0) satisfied = 0;
            if (satisfied != 0 || tlit != 0 || internal_.opts.restoreall != 0)
            {
                if (satisfied != 0) satisfied_count++;
                else
                {
                    restore_clause(p, end_of_clause, id);
                    restored++;
                }
                removed++;
                p = end_of_clause;
                q = saved;
            }
            else
            {
                while (p != end_of_clause) ext[q++] = ext[p++];
            }
        }
        extension.resize(q);
        tainted.clear();
        witness.clear();
        ext = extension.a;
        int begin_of_extension = 0;
        p = extension.n;
        while (p != begin_of_extension)
        {
            while (ext[--p] != 0) { }
            --p;
            --p;
            --p;
            int e;
            while ((e = ext[--p]) != 0) mark(witness, e);
        }
    }
}
