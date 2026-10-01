using LeanSharp.Runtime;
using static LeanSharp.Runtime.LeanRt;

namespace CadicalCheck;

/// <summary>Tests of the 'lean_cadical_*' externs (mirroring how Lean.Cadical uses them).</summary>
public static class FfiTests
{
    static int failures;

    static void Expect(bool cond, string what)
    {
        if (!cond) { failures++; Console.WriteLine("FAIL: " + what); }
        else Console.WriteLine("ok:   " + what);
    }

    static uint L(int x) => (uint)x;

    static Obj Arr(params int[] lits)
    {
        var objs = lits.Select(l => lean_box((ulong)(uint)l)).ToArray();
        return MkArray(objs);
    }

    static Obj Str(string s) => lean_mk_string(s);

    public static int Run()
    {
        Expect(lean_string_to_net(lean_cadical_signature(lean_box(0))) == "cadical-2.1.2", "signature");

        // Option queries.
        Expect(lean_cadical_solver_is_valid_option(Str("elim")) == 1, "isValidOption elim");
        Expect(lean_cadical_solver_is_valid_option(Str("nope")) == 0, "isValidOption nope");
        Expect(lean_cadical_solver_is_preprocessing_option(Str("elim")) == 1, "isPreprocessingOption elim");
        Expect(lean_cadical_solver_is_preprocessing_option(Str("restart")) == 0, "isPreprocessingOption restart");
        Expect(lean_cadical_solver_is_valid_long_option(Str("--no-elim")) == 1, "isValidLongOption --no-elim");
        Expect(lean_cadical_solver_is_valid_long_option(Str("--elim=1e3")) == 1, "isValidLongOption --elim=1e3");
        Expect(lean_cadical_solver_is_valid_long_option(Str("elim")) == 0, "isValidLongOption elim");
        Expect(lean_cadical_solver_is_valid_configuration(Str("sat")) == 1, "isValidConfiguration sat");
        Expect(lean_cadical_solver_is_valid_configuration(Str("foo")) == 0, "isValidConfiguration foo");

        // Basic SAT.
        var s = lean_cadical_solver_new();
        Expect(lean_cadical_solver_state(s) == 2, "state CONFIGURING");
        Expect(lean_cadical_solver_set(s, Str("seed"), 3) == 1, "set seed");
        Expect(lean_cadical_solver_get(s, Str("seed")) == 3, "get seed");
        Expect(lean_cadical_solver_set_long_option(s, Str("--no-lucky")) == 1, "setLongOption");
        Expect(lean_cadical_solver_get(s, Str("lucky")) == 0, "get lucky");
        Expect(lean_cadical_solver_configure(s, Str("unsat")) == 1, "configure unsat");
        Expect(lean_cadical_solver_get(s, Str("stabilize")) == 0, "configure unsat effect");
        Expect(lean_cadical_solver_is_valid_limit(s, Str("conflicts")) == 1, "isValidLimit");
        lean_cadical_solver_add(s, L(1)); lean_cadical_solver_add(s, L(2)); lean_cadical_solver_add(s, 0);
        Expect(lean_cadical_solver_state(s) == 4, "state STEADY");
        lean_cadical_solver_clause(s, Arr(-1, 2));
        lean_cadical_solver_clause(s, Arr(-2, 3, 4));
        Expect(lean_cadical_solver_vars(s) == 4, "vars");
        Expect(lean_cadical_solver_solve(s) == 0, "solve sat");
        Expect(lean_cadical_solver_status(s) == 0, "status sat");
        Expect(lean_cadical_solver_state(s) == 32, "state SATISFIED");
        int v2 = (int)lean_cadical_solver_val(s, L(2));
        Expect(v2 == 2, "val 2 is true");
        int v1 = (int)lean_cadical_solver_val(s, L(-1));
        Expect(v1 == 1 || v1 == -1, "val -1 returns +-1");
        Expect(lean_cadical_solver_inconsistent(s) == 0, "inconsistent false");
        Expect(lean_cadical_solver_fixed(s, L(2)) is 0 or 1, "fixed 2 in {0,1}");

        // Assumptions.
        lean_cadical_solver_assume(s, L(-2));
        Expect(lean_cadical_solver_solve(s) == 1, "solve unsat under assumption");
        Expect(lean_cadical_solver_failed(s, L(-2)) == 1, "failed -2");
        lean_cadical_solver_assume(s, L(-3));
        lean_cadical_solver_assume(s, L(-4));
        lean_cadical_solver_assume(s, L(5));
        Expect(lean_cadical_solver_solve(s) == 1, "solve unsat under assumptions -3 -4 5");
        Expect(lean_cadical_solver_failed(s, L(-3)) == 1 && lean_cadical_solver_failed(s, L(-4)) == 1, "failed -3 -4");
        Expect(lean_cadical_solver_failed(s, L(5)) == 0, "not failed 5");
        lean_cadical_solver_reset_assumptions(s);
        // Constraint.
        lean_cadical_solver_constrain(s, L(-2)); lean_cadical_solver_constrain(s, 0);
        Expect(lean_cadical_solver_solve(s) == 1, "solve unsat under constraint");
        Expect(lean_cadical_solver_constraint_failed(s) == 1, "constraintFailed");
        lean_cadical_solver_reset_constraint(s);
        Expect(lean_cadical_solver_solve(s) == 0, "solve sat again");
        Expect(lean_cadical_solver_active(s) >= 0, "active");
        Expect(lean_cadical_solver_irredundant(s) >= 1, "irredundant");
        Expect((long)lean_cadical_solver_redundant(s) >= 0, "redundant");

        // flip / flippable on a fresh satisfiable formula
        var f = lean_cadical_solver_new();
        lean_cadical_solver_clause(f, Arr(1, 2));
        lean_cadical_solver_clause(f, Arr(3, 4));
        lean_cadical_solver_freeze(f, L(1));
        Expect(lean_cadical_solver_frozen(f, L(1)) == 1, "frozen");
        lean_cadical_solver_melt(f, L(1));
        Expect(lean_cadical_solver_frozen(f, L(1)) == 0, "melted");
        lean_cadical_solver_phase(f, L(-1));
        lean_cadical_solver_phase(f, L(2));
        Expect(lean_cadical_solver_solve(f) == 0, "solve flip formula");
        int a3 = (int)lean_cadical_solver_val(f, L(3)), a4 = (int)lean_cadical_solver_val(f, L(4));
        if (a3 > 0 && a4 > 0)
        {
            Expect(lean_cadical_solver_flippable(f, L(3)) == 1, "flippable 3");
            Expect(lean_cadical_solver_flip(f, L(3)) == 1, "flip 3");
            Expect((int)lean_cadical_solver_val(f, L(3)) == -3, "3 flipped");
            Expect(lean_cadical_solver_flippable(f, L(4)) == 0, "4 not flippable");
        }
        else Console.WriteLine("note: skipping flip test (model " + a3 + " " + a4 + ")");
        lean_cadical_solver_unphase(f, L(-1));

        // UNSAT pigeonhole PHP(5,4) and limits / terminate.
        var p = lean_cadical_solver_new();
        int h = 4, n = 5;
        int V(int i, int j) => i * h + j + 1;
        for (int i = 0; i < n; i++) lean_cadical_solver_clause(p, Arr(Enumerable.Range(0, h).Select(j => V(i, j)).ToArray()));
        for (int j = 0; j < h; j++)
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++) lean_cadical_solver_clause(p, Arr(-V(a, j), -V(b, j)));
        Expect(lean_cadical_solver_limit(p, Str("conflicts"), 1) == 1, "limit conflicts");
        byte st = lean_cadical_solver_solve(p);
        Expect(st == 2 || st == 1, "limited solve unknown (or unsat)");
        Expect(lean_cadical_solver_solve(p) == 1, "php unsat");
        Expect(lean_cadical_solver_inconsistent(p) == 1, "inconsistent");
        lean_cadical_solver_conclude(p);

        // simplify / lookahead
        var q = lean_cadical_solver_new();
        lean_cadical_solver_clause(q, Arr(1, 2, 3));
        lean_cadical_solver_clause(q, Arr(-1, 2));
        lean_cadical_solver_clause(q, Arr(-2, 3));
        Expect(lean_cadical_solver_simplify(q, 1) != 1, "simplify not unsat");
        int la = (int)lean_cadical_solver_lookahead(q);
        Expect(Math.Abs(la) <= 3, "lookahead literal in range");
        lean_cadical_solver_resize(q, 10);
        Expect(lean_cadical_solver_vars(q) == 10, "resize");
        Expect(lean_cadical_solver_solve(q) == 0, "q sat");
        lean_cadical_solver_terminate(q);

        // printing functions return IO results
        var r = lean_cadical_solver_configurations();
        Expect(lean_io_result_is_ok(r), "configurations");
        r = lean_cadical_solver_statistics(p);
        Expect(lean_io_result_is_ok(r), "statistics");
        r = lean_cadical_solver_resources(p);
        Expect(lean_io_result_is_ok(r), "resources");

        Fuzz();

        Console.WriteLine(failures == 0 ? "FFI ALL OK" : $"FFI {failures} FAILURES");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Incremental fuzzing against brute force: random small formulas, clauses added
    /// between solves, assumptions, with inprocessing triggered as often as possible.</summary>
    static void Fuzz()
    {
        var rng = new Random(12345);
        int bad = 0, solves = 0;
        for (int iter = 0; iter < 300 && bad == 0; iter++)
        {
            int n = rng.Next(4, 13);
            var s = lean_cadical_solver_new();
            foreach (var (o, v) in new[] { ("elimint", 1), ("subsumeint", 1), ("probeint", 1), ("compactint", 1), ("compactmin", 1),
                         ("reduceint", 10), ("rephaseint", 1), ("restartint", 1), ("conditionint", 1) })
                lean_cadical_solver_set(s, Str(o), (uint)v);
            if (iter % 3 == 1) foreach (var o in new[] { "block", "cover", "condition", "instantiate" }) lean_cadical_solver_set(s, Str(o), 1);
            if (iter % 5 == 2) lean_cadical_solver_set(s, Str("ilb"), 1);
            var clauses = new List<int[]>();
            for (int round = 0; round < 6 && bad == 0; round++)
            {
                int m = rng.Next(1, 3 * n);
                for (int k = 0; k < m; k++)
                {
                    int len = rng.Next(1, 5);
                    var c = new int[len];
                    for (int j = 0; j < len; j++) { int v = rng.Next(1, n + 1); c[j] = rng.Next(2) == 0 ? v : -v; }
                    clauses.Add(c);
                    lean_cadical_solver_clause(s, Arr(c));
                }
                var assumptions = new List<int>();
                int na = rng.Next(0, 4);
                for (int k = 0; k < na; k++) { int v = rng.Next(1, n + 1); assumptions.Add(rng.Next(2) == 0 ? v : -v); }
                foreach (var a in assumptions) lean_cadical_solver_assume(s, L(a));
                if (Environment.GetEnvironmentVariable("CADICAL_FUZZ_TRACE") != null) Console.Error.WriteLine($"fuzz {iter}.{round} n={n} clauses={clauses.Count} assume=[{string.Join(",", assumptions)}]");
                if (round == 2 && iter % 4 == 0) lean_cadical_solver_simplify(s, 2);
                else
                {
                    byte st = lean_cadical_solver_solve(s);
                    solves++;
                    // brute force
                    bool sat = false;
                    for (int mask = 0; mask < (1 << n) && !sat; mask++)
                    {
                        bool Val(int l) => ((mask >> (Math.Abs(l) - 1)) & 1) == (l > 0 ? 1 : 0);
                        sat = assumptions.All(Val) && clauses.All(c => c.Any(Val));
                    }
                    if ((st == 0) != sat || st == 2) { bad++; Console.WriteLine($"fuzz iter {iter} round {round}: status {st} expected {(sat ? 0 : 1)}"); break; }
                    if (st == 0)
                    {
                        bool MVal(int l) => (int)lean_cadical_solver_val(s, L(l)) == l;
                        if (!(assumptions.All(MVal) && clauses.All(c => c.Any(MVal)))) { bad++; Console.WriteLine($"fuzz iter {iter} round {round}: bad model"); }
                    }
                    else if (assumptions.Count > 0)
                    {
                        // failed assumptions must form a core
                        var core = assumptions.Where(a => lean_cadical_solver_failed(s, L(a)) == 1).ToList();
                        bool coreSat = false;
                        for (int mask = 0; mask < (1 << n) && !coreSat; mask++)
                        {
                            bool Val(int l) => ((mask >> (Math.Abs(l) - 1)) & 1) == (l > 0 ? 1 : 0);
                            coreSat = core.All(Val) && clauses.All(c => c.Any(Val));
                        }
                        if (coreSat) { bad++; Console.WriteLine($"fuzz iter {iter} round {round}: failed assumptions are not a core"); }
                    }
                }
            }
        }
        Expect(bad == 0, $"incremental fuzz ({solves} solves)");
    }
}
