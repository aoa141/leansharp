// Port of 'condition.cpp' (globally blocked clause elimination).

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    public bool conditioning()
    {
        if (opts.condition == 0) return false;
        if (!preprocessing && opts.inprocessing == 0) return false;
        if (lim.condition > stats.conflicts) return false;
        if (level == 0) return false;
        if (level <= averages.current.jump) return false;
        if (stats.current.irredundant == 0) return false;
        double remain = active();
        if (remain == 0) return false;
        double ratio = stats.current.irredundant / remain;
        return ratio <= opts.conditionmaxrat;
    }

    void condition_unassign(int lit) => set_val(lit, 0);
    void condition_assign(int lit) => set_val(lit, 1);

    bool is_conditional_literal(int lit) => val(lit) > 0 && getbit(lit, 0);
    bool is_autarky_literal(int lit) => val(lit) > 0 && !getbit(lit, 0);
    void mark_as_conditional_literal(int lit) => setbit(lit, 0);
    void unmark_as_conditional_literal(int lit) => unsetbit(lit, 0);

    bool is_in_candidate_clause(int lit) => marked67(lit) > 0;
    void mark_in_candidate_clause(int lit) => mark67(lit);
    void unmark_in_candidate_clause(int lit) => unmark67(lit);

    struct CondSizes
    {
        public long assigned, conditional, autarky;
    }

    long condition_round(long delta)
    {
        long limit;
        long props = 0;
        if (long.MaxValue - delta < stats.condprops) limit = long.MaxValue;
        else limit = stats.condprops + delta;

        int initial_trail_level = trail.n;
        int initial_level = level;

        protect_reasons();

        for (int idx = 1; idx <= max_var; idx++)
        {
            sbyte tmp = val(idx);
            ref Var v = ref @var(idx);
            if (tmp != 0)
            {
                if (v.level != 0)
                {
                    int lit = tmp < 0 ? -idx : idx;
                    if (!is_active(idx)) condition_unassign(lit);
                    if (frozen(idx)) condition_unassign(lit);
                }
            }
            else if (frozen(idx)) { }
            else if (!is_active(idx)) { }
            else
            {
                if (initial_level == level) level++;
                int lit = decide_phase(idx, true);
                condition_assign(lit);
                v.level = level;
                trail.push_back(lit);
            }
        }

        CondSizes initial, remain;
        initial.assigned = 0;
        for (int idx = 1; idx <= max_var; idx++)
        {
            sbyte tmp = val(idx);
            if (tmp == 0) continue;
            if (@var(idx).level == 0) continue;
            initial.assigned++;
        }

        PHASE("condition", stats.conditionings, $"initial assignment of size {initial.assigned}");

        var conditional = new Vec<int>();
        var candidates = new Vec<Clause>();
        long watched = 0;

        initial.autarky = initial.assigned;
        initial.conditional = 0;

        int size_max = clauses.n + 1;

        init_occs();

        long conditioned = 0, unconditioned = 0;

        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            if (c.garbage) continue;
            if (c.redundant) continue;
            int positive = 0, negative = 0, watch = 0;
            int minsize = size_max;
            bool satisfied_ = false;
            for (int l = 0; !satisfied_ && l != c.size; l++)
            {
                int lit = c.literals[l];
                sbyte tmp = val(lit);
                if (tmp != 0 && @var(lit).level == 0) satisfied_ = tmp > 0;
                else if (tmp < 0) negative++;
                else if (tmp > 0)
                {
                    int size = occs(lit).n;
                    if (size < minsize)
                    {
                        watch = lit;
                        minsize = size;
                    }
                    positive++;
                }
            }
            if (satisfied_)
            {
                mark_garbage(c);
                continue;
            }
            if (positive > 0)
            {
                candidates.push_back(c);
                if (c.conditioned) conditioned++;
                else unconditioned++;
            }
            if (negative > 0 && positive > 0)
            {
                occs(watch).push_back(c);
                watched++;
            }
            if (negative > 0 && positive == 0)
            {
                long new_conditionals = 0;
                for (int l = 0; l != c.size; l++)
                {
                    int lit = c.literals[l];
                    sbyte tmp = val(lit);
                    if (tmp == 0) continue;
                    if (@var(lit).level == 0) continue;
                    if (is_conditional_literal(-lit)) continue;
                    mark_as_conditional_literal(-lit);
                    conditional.push_back(-lit);
                    new_conditionals++;
                }
                initial.conditional += new_conditionals;
                initial.autarky -= new_conditionals;
            }
        }

        PHASE("condition", stats.conditionings, $"found {candidates.n} candidate clauses");
        PHASE("condition", stats.conditionings, $"watching {watched} literals and clauses");
        PHASE("condition", stats.conditionings,
              $"initially {initial.conditional} conditional literals {CUtil.percent(initial.conditional, initial.assigned):F0}%");
        PHASE("condition", stats.conditionings,
              $"initially {initial.autarky} autarky literals {CUtil.percent(initial.autarky, initial.assigned):F0}%");

        stats.condassinit += initial.assigned;
        stats.condcondinit += initial.conditional;
        stats.condautinit += initial.autarky;
        stats.condassvars += active();

        for (int k = 0; k < trail.n; k++)
        {
            int lit = trail[k];
            if (@fixed(lit) != 0) condition_unassign(lit);
        }

        var unassigned = new Vec<int>();

        if (conditioned != 0 && unconditioned != 0)
        {
            // stable sort with 'less_conditioned' (not conditioned before conditioned)
            CUtil.stable_sort(candidates.AsSpan(), (a, b) =>
                (!a.conditioned && b.conditioned) ? -1 : (!b.conditioned && a.conditioned) ? 1 : 0);
            PHASE("condition", stats.conditionings,
                  $"focusing on {unconditioned} candidates {CUtil.percent(unconditioned, candidates.n):F0}% not tried last time");
        }
        else if (conditioned != 0 && unconditioned == 0)
        {
            for (int k = 0; k < candidates.n; k++) candidates[k].conditioned = false;
            PHASE("condition", stats.conditionings, $"all {conditioned} candidates tried before");
        }
        else
        {
            PHASE("condition", stats.conditionings, $"all {unconditioned} candidates are fresh");
        }

        long blocked = 0;
        long untried = candidates.n;
        for (int ci = 0; ci < candidates.n; ci++)
        {
            Clause c = candidates[ci];
            if (initial.autarky <= 0) break;
            if (c.reason) continue;

            bool terminated_or_limit_hit = true;
            if (terminated_asynchronously()) { }
            else if (stats.condprops >= limit) { }
            else terminated_or_limit_hit = false;

            if (terminated_or_limit_hit)
            {
                PHASE("condition", stats.conditionings,
                      $"{untried} candidates {CUtil.percent(untried, candidates.n):F0}% not tried after {props} propagations");
                break;
            }
            untried--;
            c.conditioned = true;

            int watched_autarky_literal = 0;
            for (int l = 0; l != c.size; l++)
            {
                int lit = c.literals[l];
                mark_in_candidate_clause(lit);
                if (watched_autarky_literal != 0) continue;
                if (!is_autarky_literal(lit)) continue;
                watched_autarky_literal = lit;
            }

            if (watched_autarky_literal == 0)
            {
                for (int l = 0; l != c.size; l++) unmark_in_candidate_clause(c.literals[l]);
                continue;
            }

            stats.condcands++;

            remain = initial;

            int next_conditional = 0, next_unassigned = 0;

            while (watched_autarky_literal != 0 && stats.condprops < limit && next_conditional < conditional.n)
            {
                int conditional_lit = conditional[next_conditional++];
                if (is_in_candidate_clause(-conditional_lit)) continue;

                condition_unassign(conditional_lit);
                unassigned.push_back(conditional_lit);

                remain.conditional--;
                remain.assigned--;

                while (watched_autarky_literal != 0 && stats.condprops < limit && next_unassigned < unassigned.n)
                {
                    int unassigned_lit = unassigned[next_unassigned++];
                    props++;
                    stats.condprops++;

                    var os = occs(unassigned_lit);
                    if (os.empty()) continue;

                    int i = 0, j = 0;
                    for (; watched_autarky_literal != 0 && j != os.n; j++)
                    {
                        Clause d = os.a[i++] = os.a[j];
                        int replacement = 0;
                        int negative = 0;
                        for (int l = 0; l != d.size; l++)
                        {
                            int lit = d.literals[l];
                            sbyte tmp = val(lit);
                            if (tmp > 0) replacement = lit;
                            if (tmp < 0 && is_autarky_literal(-lit)) negative++;
                        }
                        if (replacement != 0)
                        {
                            i--;
                            occs(replacement).push_back(d);
                            continue;
                        }
                        if (negative == 0) continue;
                        for (int l = 0; watched_autarky_literal != 0 && l != d.size; l++)
                        {
                            int lit = d.literals[l];
                            if (!is_autarky_literal(-lit)) continue;
                            mark_as_conditional_literal(-lit);
                            conditional.push_back(-lit);
                            remain.conditional++;
                            remain.autarky--;
                            if (-lit != watched_autarky_literal) continue;
                            replacement = 0;
                            for (int q = 0; replacement == 0 && q != c.size; q++)
                            {
                                int other = c.literals[q];
                                if (is_autarky_literal(other)) replacement = other;
                            }
                            watched_autarky_literal = replacement;
                            if (replacement != 0) watched_autarky_literal = replacement;
                            else watched_autarky_literal = 0;
                        }
                    }
                    if (i < j)
                    {
                        while (j != os.n) os.a[i++] = os.a[j++];
                        os.resize(i);
                    }
                }
            }

            if (watched_autarky_literal != 0 && stats.condprops < limit)
            {
                blocked++;
                stats.conditioned++;
                external.push_zero_on_extension_stack();
                for (int k = 0; k < trail.n; k++)
                {
                    int lit = trail[k];
                    if (is_autarky_literal(lit)) external.push_witness_literal_on_extension_stack(lit);
                }
                if (proof != null) proof.weaken_minus(c);
                external.push_clause_on_extension_stack(c);
                mark_garbage(c);
                stats.condassrem += remain.assigned;
                stats.condcondrem += remain.conditional;
                stats.condautrem += remain.autarky;
                stats.condassirem += initial.assigned;
            }

            if (!unassigned.empty())
            {
                while (!unassigned.empty())
                {
                    int lit = unassigned.back();
                    unassigned.pop_back();
                    condition_assign(lit);
                }
            }

            if (initial.conditional < conditional.n)
            {
                while (initial.conditional < conditional.n)
                {
                    int lit = conditional.back();
                    conditional.pop_back();
                    unmark_as_conditional_literal(lit);
                }
            }

            for (int l = 0; l != c.size; l++) unmark_in_candidate_clause(c.literals[l]);
        }

        PHASE("condition", stats.conditionings,
              $"globally blocked {blocked} clauses {CUtil.percent(blocked, candidates.n):F0}%");

        for (int k = 0; k < conditional.n; k++) unmark_as_conditional_literal(conditional[k]);

        unassigned.erase();
        conditional.erase();
        candidates.erase();

        while (trail.n > initial_trail_level)
        {
            int lit = trail.back();
            trail.pop_back();
            condition_unassign(lit);
        }

        if (level > initial_level) level = initial_level;

        reset_occs();
        delete_garbage_clauses();

        for (int i = 0; i < initial_trail_level; i++)
        {
            int lit = trail[i];
            sbyte tmp = val(lit);
            if (tmp == 0) condition_assign(lit);
        }

        unprotect_reasons();

        return blocked;
    }

    public void condition(bool update_limits = true)
    {
        if (unsat) return;
        if (stats.current.irredundant == 0) return;

        // START_SIMPLIFIER (condition, CONDITION)
        if (!preprocessing && !lookingahead) reset_mode(SEARCH);
        set_mode(SIMPLIFY);
        set_mode(CONDITION);
        stats.conditionings++;

        long limit = stats.propagations.search;
        limit *= opts.conditionreleff;
        limit /= 1000;
        if (limit < opts.conditionmineff) limit = opts.conditionmineff;
        if (limit > opts.conditionmaxeff) limit = opts.conditionmaxeff;
        limit = (long)(limit * (2.0 * active() / (double)stats.current.irredundant));
        limit = Math.Max(limit, 2L * active());

        PHASE("condition", stats.conditionings,
              $"started after {stats.conflicts} conflicts limited by {limit} propagations");

        long blocked = condition_round(limit);

        // STOP_SIMPLIFIER (condition, CONDITION)
        reset_mode(CONDITION);
        reset_mode(SIMPLIFY);
        if (!preprocessing && !lookingahead) set_mode(SEARCH);
        report('g', blocked == 0 ? 1 : 0);

        if (!update_limits) return;

        long delta = opts.conditionint * (stats.conditionings + 1);
        lim.condition = stats.conflicts + delta;

        PHASE("condition", stats.conditionings, $"next limit at {lim.condition} after {delta} conflicts");
    }
}
