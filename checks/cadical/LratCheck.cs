// Independent LRAT checker used to validate proofs produced by the managed CaDiCaL port.
//
// It implements the semantics of Lean's checker (Std.Tactic.BVDecide.LRAT): every hint clause must
// have at most one non-falsified literal (unit or conflict), the first conflict ends the check.
// Additionally it simulates Lean's trimming (Lean.Meta.Tactic.BVDecide.LRAT.trim) followed by the
// position based checker 'Std.Tactic.BVDecide.LRAT.Internal.check', which requires the derived
// ids to continue right after the CNF clauses.

using System.Text;

namespace CadicalCheck;

public static class LratCheck
{
    public abstract record Action;
    public sealed record Add(long Id, int[] Clause, long[] Hints) : Action;
    public sealed record Del(long[] Ids) : Action;

    public static List<int[]> ReadCnf(string path, out int maxVar)
    {
        var clauses = new List<int[]>();
        var cur = new List<int>();
        maxVar = 0;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == 'c') continue;
            if (line[0] == 'p')
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                maxVar = int.Parse(parts[2]);
                continue;
            }
            foreach (var tok in line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                int lit = int.Parse(tok);
                if (lit == 0) { clauses.Add(cur.ToArray()); cur.Clear(); }
                else cur.Add(lit);
            }
        }
        return clauses;
    }

    public static List<Action> ReadLrat(string path, bool binary)
    {
        var bytes = File.ReadAllBytes(path);
        var res = new List<Action>();
        if (binary)
        {
            int p = 0;
            ulong ReadNum()
            {
                ulong x = 0; int shift = 0;
                while (true)
                {
                    byte b = bytes[p++];
                    x |= (ulong)(b & 0x7f) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }
                return x;
            }
            long ReadId() { ulong u = ReadNum(); return (u & 1) != 0 ? -(long)(u >> 1) : (long)(u >> 1); }
            int ReadLit() { ulong u = ReadNum(); return (u & 1) != 0 ? -(int)(u >> 1) : (int)(u >> 1); }
            while (p < bytes.Length)
            {
                byte kind = bytes[p++];
                if (kind == 'a')
                {
                    long id = ReadId();
                    var lits = new List<int>();
                    int l;
                    while ((l = ReadLit()) != 0) lits.Add(l);
                    var hints = new List<long>();
                    long h;
                    while ((h = ReadId()) != 0) hints.Add(h);
                    res.Add(new Add(id, lits.ToArray(), hints.ToArray()));
                }
                else if (kind == 'd')
                {
                    var ids = new List<long>();
                    long h;
                    while ((h = ReadId()) != 0) ids.Add(h);
                    res.Add(new Del(ids.ToArray()));
                }
                else throw new Exception($"bad binary LRAT byte {kind} at {p - 1}");
            }
        }
        else
        {
            var text = Encoding.ASCII.GetString(bytes);
            foreach (var raw in text.Split('\n'))
            {
                var toks = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (toks.Length == 0) continue;
                long id = long.Parse(toks[0]);
                if (toks.Length > 1 && toks[1] == "d")
                {
                    var ids = new List<long>();
                    for (int i = 2; i < toks.Length; i++) { long x = long.Parse(toks[i]); if (x == 0) break; ids.Add(x); }
                    res.Add(new Del(ids.ToArray()));
                }
                else
                {
                    int i = 1;
                    var lits = new List<int>();
                    for (; ; i++) { int l = int.Parse(toks[i]); if (l == 0) { i++; break; } lits.Add(l); }
                    var hints = new List<long>();
                    for (; i < toks.Length; i++) { long h = long.Parse(toks[i]); if (h == 0) break; hints.Add(h); }
                    res.Add(new Add(id, lits.ToArray(), hints.ToArray()));
                }
            }
        }
        return res;
    }

    enum PropRes { Conflict, Extended, Error }

    // Lean's 'propagateHints' semantics; 'assign' maps variable -> +1/-1 (value of the variable).
    static PropRes Propagate(Func<long, int[]> get, Dictionary<int, int> assign, long[] hints)
    {
        foreach (var hint in hints)
        {
            var c = get(hint);
            if (c == null) return PropRes.Error;
            int unit = 0; // variable of the non-falsified literal
            int unitLit = 0;
            foreach (var lit in c)
            {
                int v = Math.Abs(lit);
                int pol = lit > 0 ? 1 : -1;
                if (assign.TryGetValue(v, out int val))
                {
                    if (val == pol)
                    {
                        if (unit != 0) { if (unit == v) continue; return PropRes.Error; }
                        unit = v; unitLit = lit;
                    }
                    else
                    {
                        if (unit == v) return PropRes.Error;
                        continue;
                    }
                }
                else
                {
                    if (unit == 0)
                    {
                        unit = v; unitLit = lit;
                        assign[v] = pol;
                    }
                    else return PropRes.Error;
                }
            }
            if (unit == 0) return PropRes.Conflict;
        }
        return PropRes.Extended;
    }

    static bool CheckRup(Func<long, int[]> get, int[] clause, long[] hints)
    {
        var assign = new Dictionary<int, int>();
        foreach (var lit in clause)
        {
            int v = Math.Abs(lit);
            int neg = lit > 0 ? -1 : 1;
            if (assign.TryGetValue(v, out int old) && old != neg) return true; // tautology
            assign[v] = neg;
        }
        return Propagate(get, assign, hints) == PropRes.Conflict;
    }

    /// <summary>Check an id based LRAT proof. Returns null on success or an error message.</summary>
    public static string CheckDirect(List<int[]> cnf, List<Action> proof, bool requireContiguous, out int adds)
    {
        var db = new Dictionary<long, int[]>();
        for (int i = 0; i < cnf.Count; i++) db[i + 1] = cnf[i];
        long last = cnf.Count;
        bool empty = false;
        adds = 0;
        int[] Get(long id) => db.TryGetValue(id, out var c) ? c : null;
        foreach (var a in proof)
        {
            if (a is Add add)
            {
                adds++;
                if (add.Id <= last) return $"non increasing id {add.Id} after {last}";
                if (requireContiguous && add.Id != last + 1) return $"id gap: {add.Id} after {last}";
                last = add.Id;
                foreach (var h in add.Hints) if (h < 0) return $"RAT hint in step {add.Id} not supported";
                if (!CheckRup(Get, add.Clause, add.Hints))
                    return $"RUP check failed for step {add.Id}: [{string.Join(" ", add.Clause)}] hints [{string.Join(" ", add.Hints)}]";
                db[add.Id] = add.Clause;
                if (add.Clause.Length == 0) { empty = true; break; }
            }
            else if (a is Del del)
            {
                foreach (var id in del.Ids)
                    if (!db.Remove(id)) return $"deleting unknown clause {id}";
            }
        }
        return empty ? null : "no empty clause derived";
    }

    /// <summary>Simulate Lean's trim + positional checker.</summary>
    public static string CheckLeanStyle(List<int[]> cnf, List<Action> proof)
    {
        var adds = proof.OfType<Add>().ToList();
        if (adds.Count == 0) return "LRAT proof doesn't contain a proper first proof step.";
        long initialId = adds[0].Id;
        var emptyStep = adds.LastOrDefault(a => a.Clause.Length == 0);
        if (emptyStep == null) return "LRAT proof doesn't contain the empty clause.";
        long emptyId = emptyStep.Id;
        var steps = new Dictionary<long, Add>();
        foreach (var a in adds) steps[a.Id] = a;
        int psize = steps.Count;
        var used = new bool[psize];
        var mapped = new long[psize];
        var lastUse = new long[initialId + psize];
        Array.Fill(lastUse, -1);
        bool idxOk(long id) => id - initialId >= 0 && id - initialId < psize;
        // use analysis
        var work = new List<long> { emptyId };
        while (work.Count > 0)
        {
            long id = work[^1]; work.RemoveAt(work.Count - 1);
            if (id >= initialId)
            {
                if (!idxOk(id)) return $"trim: id {id} out of range (ids not contiguous)";
                if (used[id - initialId]) continue;
                used[id - initialId] = true;
            }
            else continue; // CNF clause: markUsed does nothing; 'isUsed' reads index < 0 which would panic in Lean
            if (steps.TryGetValue(id, out var st))
            {
                foreach (var h in st.Hints)
                {
                    if (h >= lastUse.Length) return $"trim: hint {h} out of lastUse range";
                    lastUse[h] = Math.Max(lastUse[h], id);
                }
                work.AddRange(st.Hints);
            }
        }
        // mapping
        long nextMapped = initialId;
        var newProof = new List<Action>();
        for (long id = initialId; id <= emptyId; id++)
        {
            if (!idxOk(id)) return $"trim: id {id} out of range";
            if (!used[id - initialId]) continue;
            mapped[id - initialId] = nextMapped;
            var st = steps[id];
            var deletions = new List<long>();
            if (id != emptyId)
                foreach (var h in st.Hints) if (lastUse[h] == id) deletions.Add(h);
            long Map(long x) => x < initialId ? x : mapped[x - initialId];
            newProof.Add(new Add(Map(st.Id), st.Clause, st.Hints.Select(Map).ToArray()));
            if (deletions.Count > 0) newProof.Add(new Del(deletions.Select(Map).ToArray()));
            nextMapped++;
        }
        // positional checker: formula array, ids = position + 1
        var formula = new List<int[]>(cnf);
        int[] Get(long idx) => idx - 1 >= 0 && idx - 1 < formula.Count ? formula[(int)(idx - 1)] : null;
        foreach (var a in newProof)
        {
            if (a is Add add)
            {
                if (add.Clause.Length == 0)
                {
                    var assign = new Dictionary<int, int>();
                    return Propagate(Get, assign, add.Hints) == PropRes.Conflict ? null : "empty clause check failed";
                }
                if (!CheckRup(Get, add.Clause, add.Hints)) return $"lean-style RUP check failed at mapped step {add.Id}";
                formula.Add(add.Clause);
                if (formula.Count != add.Id) return $"lean-style: id {add.Id} does not match position {formula.Count}";
            }
            else if (a is Del d)
            {
                foreach (var id in d.Ids)
                    if (id - 1 >= 0 && id - 1 < formula.Count) formula[(int)(id - 1)] = null;
            }
        }
        return "lean-style: proof ended without empty clause";
    }
}
