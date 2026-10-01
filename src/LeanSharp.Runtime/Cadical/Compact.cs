// Port of 'compact.cpp'.

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    public bool compacting()
    {
        if (level != 0) return false;
        if (opts.compact == 0) return false;
        if (stats.conflicts < lim.compact) return false;
        int inactive = max_var - active();
        if (inactive == 0) return false;
        if (inactive < opts.compactmin) return false;
        return inactive >= (1e-3 * opts.compactlim) * max_var;
    }

    sealed class Mapper
    {
        readonly Internal internal_;
        public int new_max_var;
        public readonly int[] table;
        public int first_fixed;
        public int map_first_fixed;
        public sbyte first_fixed_val;
        public int new_vsize;

        public Mapper(Internal i)
        {
            internal_ = i;
            table = new int[i.max_var + 1];
            for (int src = 1; src <= i.max_var; src++)
            {
                ref Flags f = ref i.flags(src);
                if (f.active()) table[src] = ++new_max_var;
                else if (f.@fixed() && first_fixed == 0)
                    table[first_fixed = src] = map_first_fixed = ++new_max_var;
            }
            first_fixed_val = first_fixed != 0 ? i.val(first_fixed) : (sbyte)0;
            new_vsize = new_max_var + 1;
        }

        public int map_idx(int src) => table[src];

        public int map_lit(int src)
        {
            int res = map_idx(Math.Abs(src));
            if (res == 0)
            {
                sbyte tmp = internal_.val(src);
                if (tmp != 0)
                {
                    res = map_first_fixed;
                    if (tmp != first_fixed_val) res = -res;
                }
            }
            else if (src < 0) res = -res;
            return res;
        }

        public void map_vector<T>(ref T[] v)
        {
            for (int src = 1; src <= internal_.max_var; src++)
            {
                int dst = map_idx(src);
                if (dst == 0) continue;
                v[dst] = v[src];
            }
            Array.Resize(ref v, new_vsize);
        }

        public void map_vector<T>(Vec<T> v)
        {
            for (int src = 1; src <= internal_.max_var; src++)
            {
                int dst = map_idx(src);
                if (dst == 0) continue;
                v[dst] = v[src];
            }
            v.resize(new_vsize);
            v.shrink_to_fit();
        }

        public void map2_vector<T>(ref T[] v)
        {
            for (int src = 1; src <= internal_.max_var; src++)
            {
                int dst = map_idx(src);
                if (dst == 0) continue;
                v[2 * dst] = v[2 * src];
                v[2 * dst + 1] = v[2 * src + 1];
            }
            Array.Resize(ref v, 2 * new_vsize);
        }

        public void map_flush_and_shrink_lits(Vec<int> v)
        {
            int j = 0;
            for (int i = 0; i < v.n; i++)
            {
                int src = v[i];
                int dst = map_idx(Math.Abs(src));
                if (dst == 0) continue;
                if (src < 0) dst = -dst;
                v[j++] = dst;
            }
            v.resize(j);
            v.shrink_to_fit();
        }
    }

    public void compact()
    {
        stats.compacts++;
        garbage_collection();
        var mapper = new Mapper(this);

        if (!assumptions.empty()) reset_assumptions();
        bool is_constraint = !constraint.empty();
        if (is_constraint) reset_constraint();

        for (int eidx = 1; eidx <= external.max_var; eidx++)
        {
            int src = external.e2i[eidx];
            if (src == 0) continue;
            if (lrat || frat)
            {
                ulong id1 = external.ext_units[2 * eidx];
                ulong id2 = external.ext_units[2 * eidx + 1];
                if (id1 == 0 && id2 == 0)
                {
                    int asrc = Math.Abs(src);
                    ulong new_id1 = unit_clauses(2 * asrc);
                    ulong new_id2 = unit_clauses(2 * asrc + 1);
                    // 'src' may be negative after earlier compactions mapping to the fixed literal
                    if (src < 0) (new_id1, new_id2) = (new_id2, new_id1);
                    external.ext_units[2 * eidx] = new_id1;
                    external.ext_units[2 * eidx + 1] = new_id2;
                }
            }
            int dst = mapper.map_lit(src);
            external.e2i[eidx] = dst;
        }

        if (lrat || frat)
        {
            for (int src = 1; src <= max_var; src++)
            {
                int dst = mapper.map_idx(src);
                sbyte tmp = val(src);
                if (dst == 0 && tmp == 0)
                {
                    unit_clauses(2 * src) = 0;
                    unit_clauses(2 * src + 1) = 0;
                    continue;
                }
                if (tmp == 0 || src == mapper.first_fixed)
                {
                    if (dst == src) continue;
                    unit_clauses(2 * dst) = unit_clauses(2 * src);
                    unit_clauses(2 * dst + 1) = unit_clauses(2 * src + 1);
                    unit_clauses(2 * src) = 0;
                    unit_clauses(2 * src + 1) = 0;
                    continue;
                }
                unit_clauses(2 * src) = 0;
                unit_clauses(2 * src + 1) = 0;
            }
            Array.Resize(ref unit_clauses_idx, 2 * mapper.new_vsize);
        }

        for (int k = 0; k < clauses.n; k++)
        {
            var c = clauses[k];
            var lits = c.literals;
            for (int i = 0; i < c.size; i++) lits[i] = mapper.map_lit(lits[i]);
        }

        if (wtab.Length != 0)
            for (int idx = 1; idx <= max_var; idx++)
                for (int s = 0; s < 2; s++)
                {
                    var ws = watches(s == 0 ? idx : -idx);
                    for (int k = 0; k < ws.n; k++) ws.a[k].blit = mapper.map_lit(ws.a[k].blit);
                }

        {
            int prev = 0, mapped_prev = 0, next;
            for (int idx = queue.first; idx != 0; idx = next)
            {
                next = links[idx].next;
                if (idx == mapper.first_fixed) continue;
                int dst = mapper.map_idx(idx);
                if (dst == 0) continue;
                if (prev != 0) links[prev].next = dst;
                else queue.first = dst;
                links[idx].prev = mapped_prev;
                mapped_prev = dst;
                prev = idx;
            }
            if (prev != 0) links[prev].next = 0;
            else queue.first = 0;
            queue.unassigned = queue.last = mapped_prev;
        }

        mapper.map_flush_and_shrink_lits(trail);
        propagated = trail.n;
        num_assigned = trail.n;
        if (mapper.first_fixed != 0) @var(mapper.first_fixed).trail = 0;

        if (!probes.empty()) mapper.map_flush_and_shrink_lits(probes);

        mapper.map_vector(ref ftab);
        mapper.map_vector(ref parents);
        mapper.map_vector(ref marks);
        mapper.map_vector(ref phases.saved);
        mapper.map_vector(ref phases.forced);
        mapper.map_vector(ref phases.target);
        mapper.map_vector(ref phases.best);
        mapper.map_vector(ref phases.prev);
        mapper.map_vector(ref phases.min);

        for (int src = 1; src <= max_var; src++)
        {
            int dst = Math.Abs(mapper.map_lit(src));
            if (dst == 0) continue;
            if (src == dst) continue;
            if (src >= frozentab.n) break;
            if (dst >= frozentab.n) break;
            frozentab[dst] += frozentab[src];
            frozentab[src] = 0;
        }
        frozentab.resize(Math.Min(frozentab.n, mapper.new_vsize));
        frozentab.shrink_to_fit();

        {
            for (int src = 1; src <= max_var; src++)
            {
                int dst = Math.Abs(mapper.map_lit(src));
                if (dst == 0) continue;
                if (src == dst) continue;
                if (src >= relevanttab.n) continue;
                relevanttab[dst] += relevanttab[src];
                relevanttab[src] = 0;
            }
            relevanttab.resize(mapper.new_vsize);
            relevanttab.shrink_to_fit();
        }

        if (!external.assumptions.empty())
        {
            for (int k = 0; k < external.assumptions.n; k++)
            {
                int elit = external.assumptions[k];
                int eidx = Math.Abs(elit);
                int ilit = external.e2i[eidx];
                if (elit < 0) ilit = -ilit;
                assume(ilit);
            }
        }

        {
            int nv = mapper.new_vsize;
            var new_array = GC.AllocateArray<sbyte>(2 * nv, pinned: true);
            sbyte* new_vals = (sbyte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref new_array[0]) + nv;
            for (int src = 1; src <= max_var; src++) new_vals[-mapper.map_idx(src)] = vals[-src];
            for (int src = 1; src <= max_var; src++) new_vals[mapper.map_idx(src)] = vals[src];
            new_vals[0] = 0;
            vals_array = new_array;
            vals = new_vals;
            vsize = nv;
        }

        if (is_constraint)
        {
            for (int k = 0; k < external.constraint.n; k++)
            {
                int elit = external.constraint[k];
                int eidx = Math.Abs(elit);
                int ilit = external.e2i[eidx];
                if (elit < 0) ilit = -ilit;
                constrain(ilit);
            }
        }

        mapper.map_vector(i2e);
        mapper.map2_vector(ref ptab);
        mapper.map_vector(ref btab);
        mapper.map_vector(ref gtab);
        mapper.map_vector(ref links);
        mapper.map_vector(ref vtab);
        if (ntab.Length != 0) mapper.map2_vector(ref ntab);
        if (wtab.Length != 0) mapper.map2_vector(ref wtab);
        if (otab.Length != 0) mapper.map2_vector(ref otab);
        if (big.Length != 0) mapper.map2_vector(ref big);

        var saved = new Vec<int>();
        if (!scores.empty())
        {
            while (!scores.empty())
            {
                int src = (int)scores.front();
                scores.pop_front();
                int dst = mapper.map_idx(src);
                if (dst == 0) continue;
                if (src == mapper.first_fixed) continue;
                saved.push_back(dst);
            }
            scores.erase();
        }
        mapper.map_vector(ref stab);
        if (!saved.empty())
        {
            for (int k = 0; k < saved.n; k++) scores.push_back((uint)saved[k]);
            scores.shrink();
        }

        int new_target_assigned = 0, new_best_assigned = 0;
        for (int idx = 1; idx <= mapper.new_max_var; idx++)
        {
            if (phases.target[idx] != 0) new_target_assigned++;
            if (phases.best[idx] != 0) new_best_assigned++;
        }
        target_assigned = new_target_assigned;
        best_assigned = new_best_assigned;
        no_conflict_until = 0;
        notified = 0;

        averages.current.trail_fast = EMA.Init(opts.ematrailfast);
        averages.current.trail_slow = EMA.Init(opts.ematrailslow);

        max_var = mapper.new_max_var;

        stats.unused = 0;
        stats.inactive = stats.now.fixed_ = mapper.first_fixed != 0 ? 1 : 0;
        stats.now.substituted = stats.now.eliminated = stats.now.pure = 0;

        long delta = opts.compactint * (stats.compacts + 1);
        lim.compact = stats.conflicts + delta;
        PHASE("compact", stats.compacts, $"new compact limit {lim.compact} after {delta} conflicts");
        report('/');
    }
}
