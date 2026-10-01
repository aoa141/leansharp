// Port of 'solver.cpp' (the public 'CaDiCaL::Solver' API).

namespace LeanSharp.Runtime.Cadical;

public sealed class Solver
{
    public const int INITIALIZING = 1, CONFIGURING = 2, STEADY = 4, ADDING = 8, SOLVING = 16,
        SATISFIED = 32, UNSATISFIED = 64, DELETING = 128,
        READY = CONFIGURING | STEADY | SATISFIED | UNSATISFIED,
        VALID = READY | ADDING,
        INVALID = INITIALIZING | DELETING;

    public const string VERSION = "2.1.2";

    public readonly Internal internal_;
    public readonly External external;
    int _state;
    bool adding_clause, adding_constraint;

    public int state() => _state;

    /// <summary>Create a solver. 'reportdefault' is 1 for the stand alone app and 0 for library
    /// usage. 'output'/'errout' replace 'stdout'/'stderr'.</summary>
    public Solver(int reportdefault = 0, TextWriter output = null, TextWriter errout = null)
    {
        _state = INITIALIZING;
        internal_ = new Internal(reportdefault, output);
        if (errout != null) internal_.errout = errout;
        external = new External(internal_);
        _state = CONFIGURING;
    }

    /*------------------------------------------------------------------------*/

    static void CONTRACT_VIOLATED(string msg) =>
        throw new CadicalException("invalid API usage: " + msg);

    void REQUIRE(bool cond, string msg)
    {
        if (!cond) CONTRACT_VIOLATED(msg);
    }

    void REQUIRE_VALID_STATE() => REQUIRE((_state & VALID) != 0, "solver in invalid state");
    void REQUIRE_READY_STATE()
    {
        REQUIRE_VALID_STATE();
        REQUIRE(_state != ADDING, "clause incomplete (terminating zero not added)");
    }
    void REQUIRE_VALID_OR_SOLVING_STATE() =>
        REQUIRE((_state & (VALID | SOLVING)) != 0, "solver neither in valid nor solving state");
    void REQUIRE_VALID_LIT(int lit) => REQUIRE(lit != 0 && lit != int.MinValue, $"invalid literal '{lit}'");

    void STATE(int s) { _state = s; }

    void transition_to_steady_state()
    {
        if (_state == CONFIGURING)
        {
            if (internal_.opts.check != 0 && internal_.opts.checkproof != 0) internal_.check();
        }
        else if (_state == SATISFIED || _state == UNSATISFIED)
        {
            external.reset_assumptions();
            external.reset_concluded();
            external.reset_constraint();
        }
        if (_state != STEADY) STATE(STEADY);
    }

    /*------------------------------------------------------------------------*/

    public int vars()
    {
        REQUIRE_VALID_OR_SOLVING_STATE();
        return external.max_var;
    }

    public void reserve(int min_max_var)
    {
        REQUIRE_VALID_STATE();
        transition_to_steady_state();
        external.reset_extended();
        external.init(min_max_var);
    }

    public static bool is_valid_option(string name) => Options.has(name) != null;
    public static bool is_preprocessing_option(string name) => Options.is_preprocessing_option(name);
    public static bool is_valid_long_option(string arg) => Options.parse_long_option(arg, out _, out _);

    public int get(string arg)
    {
        REQUIRE_VALID_OR_SOLVING_STATE();
        return internal_.opts.get(arg);
    }

    public bool set(string arg, int val)
    {
        REQUIRE_VALID_STATE();
        if (arg != "log" && arg != "quiet" && arg != "report" && arg != "verbose")
            REQUIRE(_state == CONFIGURING, $"can only set option 'set (\"{arg}\", {val})' right after initialization");
        return internal_.opts.set(arg, val);
    }

    public bool set_long_option(string arg)
    {
        REQUIRE_VALID_STATE();
        REQUIRE(_state == CONFIGURING, $"can only set option '{arg}' right after initialization");
        bool res;
        if (arg.Length < 2 || arg[0] != '-' || arg[1] != '-') res = false;
        else
        {
            res = Options.parse_long_option(arg, out string name, out int val);
            if (res) set(name, val);
        }
        return res;
    }

    public void optimize(int arg)
    {
        REQUIRE_VALID_STATE();
        internal_.opts.optimize(arg);
    }

    public bool limit(string arg, int val)
    {
        REQUIRE_VALID_STATE();
        return internal_.limit(arg, val);
    }

    public static bool is_valid_limit(string arg) => Internal.is_valid_limit(arg);

    public void prefix(string str)
    {
        REQUIRE_VALID_OR_SOLVING_STATE();
        internal_.prefix = str;
    }

    public static bool is_valid_configuration(string name) => Config.has(name);

    public bool configure(string name)
    {
        REQUIRE_VALID_STATE();
        REQUIRE(_state == CONFIGURING, $"can only set configuration '{name}' right after initialization");
        return Config.set(internal_.opts, name);
    }

    /*------------------------------------------------------------------------*/

    public void add(int lit)
    {
        REQUIRE_VALID_STATE();
        if (lit != 0) REQUIRE_VALID_LIT(lit);
        transition_to_steady_state();
        external.add(lit);
        adding_clause = lit != 0;
        if (adding_clause) STATE(ADDING);
        else if (!adding_constraint) STATE(STEADY);
    }

    public void clause(ReadOnlySpan<int> lits)
    {
        foreach (int lit in lits)
        {
            REQUIRE_VALID_LIT(lit);
            add(lit);
        }
        add(0);
    }

    public bool inconsistent() => internal_.unsat;

    public void constrain(int lit)
    {
        REQUIRE_VALID_STATE();
        if (lit != 0) REQUIRE_VALID_LIT(lit);
        transition_to_steady_state();
        external.constrain(lit);
        adding_constraint = lit != 0;
        if (adding_constraint) STATE(ADDING);
        else if (!adding_clause) STATE(STEADY);
    }

    public void assume(int lit)
    {
        REQUIRE_VALID_STATE();
        REQUIRE_VALID_LIT(lit);
        transition_to_steady_state();
        external.assume(lit);
    }

    public int lookahead()
    {
        REQUIRE_VALID_OR_SOLVING_STATE();
        return external.lookahead();
    }

    public void reset_assumptions()
    {
        REQUIRE_VALID_STATE();
        transition_to_steady_state();
        external.reset_assumptions();
        external.reset_concluded();
    }

    public void reset_constraint()
    {
        REQUIRE_VALID_STATE();
        transition_to_steady_state();
        external.reset_constraint();
        external.reset_concluded();
    }

    int call_external_solve_and_check_results(bool preprocess_only)
    {
        transition_to_steady_state();
        STATE(SOLVING);
        int res;
        try
        {
            res = external.solve(preprocess_only);
        }
        catch
        {
            STATE(STEADY);
            throw;
        }
        if (res == 10) STATE(SATISFIED);
        else if (res == 20) STATE(UNSATISFIED);
        else STATE(STEADY);
        if (res == 0)
        {
            external.reset_assumptions();
            external.reset_constraint();
            external.reset_concluded();
        }
        return res;
    }

    public int solve()
    {
        REQUIRE_READY_STATE();
        return call_external_solve_and_check_results(false);
    }

    public int simplify(int rounds = 3)
    {
        REQUIRE_READY_STATE();
        REQUIRE(rounds >= 0, $"negative number of simplification rounds '{rounds}'");
        internal_.limit("preprocessing", rounds);
        return call_external_solve_and_check_results(true);
    }

    public int val(int lit)
    {
        REQUIRE_VALID_STATE();
        REQUIRE_VALID_LIT(lit);
        REQUIRE(_state == SATISFIED, "can only get value in satisfied state");
        if (!external.extended) external.extend();
        external.conclude_sat();
        return external.ival(lit);
    }

    public bool flip(int lit)
    {
        REQUIRE_VALID_STATE();
        REQUIRE_VALID_LIT(lit);
        REQUIRE(_state == SATISFIED, "can only flip value in satisfied state");
        return external.flip(lit);
    }

    public bool flippable(int lit)
    {
        REQUIRE_VALID_STATE();
        REQUIRE_VALID_LIT(lit);
        REQUIRE(_state == SATISFIED, "can only flip value in satisfied state");
        return external.flippable(lit);
    }

    public bool failed(int lit)
    {
        REQUIRE_VALID_STATE();
        REQUIRE_VALID_LIT(lit);
        REQUIRE(_state == UNSATISFIED, "can only get failed assumptions in unsatisfied state");
        return external.failed(lit);
    }

    public bool constraint_failed()
    {
        REQUIRE_VALID_STATE();
        REQUIRE(_state == UNSATISFIED, "can only determine if constraint failed in unsatisfied state");
        return external.failed_constraint();
    }

    public int @fixed(int lit)
    {
        REQUIRE_VALID_STATE();
        REQUIRE_VALID_LIT(lit);
        return external.fixed_(lit);
    }

    public void phase(int lit)
    {
        REQUIRE_VALID_OR_SOLVING_STATE();
        REQUIRE_VALID_LIT(lit);
        external.phase(lit);
    }

    public void unphase(int lit)
    {
        REQUIRE_VALID_OR_SOLVING_STATE();
        REQUIRE_VALID_LIT(lit);
        external.unphase(lit);
    }

    /// <summary>Asynchronous termination; may be called from another thread.</summary>
    public void terminate()
    {
        REQUIRE_VALID_OR_SOLVING_STATE();
        external.terminate();
    }

    public void connect_terminator(Func<bool> terminator)
    {
        REQUIRE_VALID_STATE();
        REQUIRE(terminator != null, "can not connect zero terminator");
        external.terminator = terminator;
    }

    public void disconnect_terminator()
    {
        REQUIRE_VALID_STATE();
        external.terminator = null;
    }

    public int active()
    {
        REQUIRE_VALID_STATE();
        return internal_.active();
    }

    public long redundant()
    {
        REQUIRE_VALID_STATE();
        return internal_.redundant();
    }

    public long irredundant()
    {
        REQUIRE_VALID_STATE();
        return internal_.irredundant();
    }

    public void freeze(int lit)
    {
        REQUIRE_VALID_STATE();
        REQUIRE_VALID_LIT(lit);
        external.freeze(lit);
    }

    public void melt(int lit)
    {
        REQUIRE_VALID_STATE();
        REQUIRE_VALID_LIT(lit);
        REQUIRE(external.frozen(lit), $"can not melt completely melted literal '{lit}'");
        external.melt(lit);
    }

    public bool frozen(int lit)
    {
        REQUIRE_VALID_STATE();
        REQUIRE_VALID_LIT(lit);
        return external.frozen(lit);
    }

    /*------------------------------------------------------------------------*/

    public bool trace_proof(Stream stream, string name)
    {
        REQUIRE_VALID_STATE();
        REQUIRE(_state == CONFIGURING, $"can only start proof tracing to '{name}' right after initialization");
        var file = new CFile(stream, name, false);
        internal_.trace(file);
        return true;
    }

    public bool trace_proof(string path)
    {
        REQUIRE_VALID_STATE();
        REQUIRE(_state == CONFIGURING, $"can only start proof tracing to '{path}' right after initialization");
        var file = CFile.write(path);
        bool res = file != null;
        if (res) internal_.trace(file);
        return res;
    }

    public void connect_proof_tracer(Tracer tracer, bool antecedents, bool finalize_clauses = false)
    {
        REQUIRE_VALID_STATE();
        REQUIRE(_state == CONFIGURING, "can only start proof tracing to right after initialization");
        REQUIRE(tracer != null, "can not connect zero tracer");
        internal_.connect_proof_tracer(tracer, antecedents, finalize_clauses);
    }

    public bool disconnect_proof_tracer(Tracer tracer)
    {
        REQUIRE_VALID_STATE();
        return internal_.disconnect_proof_tracer(tracer);
    }

    public void flush_proof_trace(bool print = false)
    {
        REQUIRE_VALID_STATE();
        REQUIRE(internal_.file_tracers.Count != 0, "proof is not traced");
        REQUIRE(!internal_.file_tracers[^1].closed(), "proof trace already closed");
        internal_.flush_trace(print);
    }

    public void close_proof_trace(bool print = false)
    {
        REQUIRE_VALID_STATE();
        REQUIRE(internal_.file_tracers.Count != 0, "proof is not traced");
        REQUIRE(!internal_.file_tracers[^1].closed(), "proof trace already closed");
        internal_.close_trace(print);
    }

    public void conclude()
    {
        REQUIRE_VALID_STATE();
        REQUIRE(_state == UNSATISFIED || _state == SATISFIED, "can only conclude in satisfied or unsatisfied state");
        if (_state == UNSATISFIED) internal_.conclude_unsat();
        else if (_state == SATISFIED) external.conclude_sat();
    }

    /*------------------------------------------------------------------------*/

    public static string version() => VERSION;
    public static string signature() => "cadical-" + VERSION;

    public static void build(TextWriter w, string prefix)
    {
        w.Write(prefix + "Version " + VERSION + "\n");
        w.Write(prefix + "managed C# port (LeanSharp)\n");
        w.Flush();
    }

    public void options()
    {
        REQUIRE_VALID_STATE();
        internal_.opts.print();
    }

    public static void usage(TextWriter w) => Options.usage(w);
    public static void configurations(TextWriter w) => Config.usage(w);

    public void statistics()
    {
        if (_state == DELETING) return;
        REQUIRE_VALID_OR_SOLVING_STATE();
        internal_.print_statistics();
    }

    public void resources()
    {
        if (_state == DELETING) return;
        REQUIRE_VALID_OR_SOLVING_STATE();
        internal_.print_resource_usage();
    }

    /*------------------------------------------------------------------------*/

    public string read_dimacs(Stream input, string name, out int vars, int strict, out bool incremental, Vec<int> cubes)
    {
        REQUIRE_VALID_STATE();
        REQUIRE(_state == CONFIGURING, "can only read DIMACS file right after initialization");
        var parser = new Parser(this, new CInputFile(input, name), cubes);
        string err = parser.parse_dimacs(out vars, strict, out incremental);
        return err;
    }

    public string read_dimacs(string path, out int vars, int strict, out bool incremental, Vec<int> cubes)
    {
        REQUIRE_VALID_STATE();
        REQUIRE(_state == CONFIGURING, "can only read DIMACS file right after initialization");
        vars = 0; incremental = false;
        Stream s;
        try { s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16); }
        catch { return $"failed to read DIMACS file '{path}'"; }
        using (s)
        {
            return read_dimacs(s, path, out vars, strict, out incremental, cubes);
        }
    }

    public string read_solution(string path)
    {
        REQUIRE_VALID_STATE();
        Stream s;
        try { s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch { return $"failed to read solution file '{path}'"; }
        using (s)
        {
            var parser = new Parser(this, new CInputFile(s, path), null);
            return parser.parse_solution();
        }
    }

    public bool traverse_clauses(Func<Vec<int>, bool> it)
    {
        REQUIRE_VALID_STATE();
        return external.traverse_all_frozen_units_as_clauses(it) && internal_.traverse_clauses(it)
               && internal_.traverse_constraint(it);
    }

    public bool traverse_witnesses_backward(Func<Vec<int>, Vec<int>, ulong, bool> it)
    {
        REQUIRE_VALID_STATE();
        return external.traverse_all_non_frozen_units_as_witnesses(it) && external.traverse_witnesses_backward(it);
    }

    public bool traverse_witnesses_forward(Func<Vec<int>, Vec<int>, ulong, bool> it)
    {
        REQUIRE_VALID_STATE();
        return external.traverse_witnesses_forward(it) && external.traverse_all_non_frozen_units_as_witnesses(it);
    }

    public string write_dimacs(string path, int min_max_var = 0)
    {
        REQUIRE_VALID_STATE();
        double start = internal_.time();
        internal_.restore_clauses();
        int cvars = 0; long cclauses = 0;
        traverse_clauses(c =>
        {
            for (int i = 0; i < c.n; i++) { int idx = Math.Abs(c[i]); if (idx > cvars) cvars = idx; }
            cclauses++;
            return true;
        });
        var file = CFile.write(path);
        string res = null;
        if (file != null)
        {
            int actual_max_vars = Math.Max(min_max_var, cvars);
            internal_.MSG($"writing 'p cnf {actual_max_vars} {cclauses}' header");
            file.put("p cnf "); file.put(actual_max_vars); file.put(' '); file.put(cclauses); file.put('\n');
            if (!traverse_clauses(c =>
            {
                for (int i = 0; i < c.n; i++) { file.put(c[i]); file.put(' '); }
                return file.put("0\n");
            }))
                res = $"writing to DIMACS file '{path}' failed";
            file.close();
        }
        else res = $"failed to open DIMACS file '{path}' for writing";
        if (res == null)
            internal_.MSG($"wrote {cclauses} clauses in {internal_.time() - start:F2} seconds {(internal_.opts.realtime != 0 ? "real" : "process")} time");
        return res;
    }

    public string write_extension(string path)
    {
        REQUIRE_VALID_STATE();
        string res = null;
        var file = CFile.write(path);
        long witnesses = 0;
        if (file != null)
        {
            bool write(Vec<int> a)
            {
                for (int i = 0; i < a.n; i++) { file.put(a[i]); file.put(' '); }
                return file.put('0');
            }
            if (!traverse_witnesses_backward((c, w, id) =>
            {
                write(c); file.put(' '); write(w); file.put('\n');
                witnesses++;
                return true;
            }))
                res = $"writing to DIMACS file '{path}' failed";
            file.close();
        }
        else res = $"failed to open extension file '{path}' for writing";
        if (res == null) internal_.MSG($"wrote {witnesses} witnesses");
        return res;
    }

    public void copy(Solver other)
    {
        REQUIRE_READY_STATE();
        REQUIRE((other._state & CONFIGURING) != 0, "target solver already modified");
        internal_.opts.copy(other.internal_.opts);
        traverse_clauses(c =>
        {
            for (int i = 0; i < c.n; i++) other.add(c[i]);
            other.add(0);
            return true;
        });
        traverse_witnesses_forward((c, w, id) =>
        {
            other.external.push_external_clause_and_witness_on_extension_stack(c, w, id);
            return true;
        });
        external.copy_flags(other.external);
    }

    public void dump_cnf() => internal_.dump();

    public void section(string title)
    {
        if (_state == DELETING) return;
        internal_.section(title);
    }

    public void message(string msg)
    {
        if (_state == DELETING) return;
        internal_.MSG(msg);
    }

    public void message()
    {
        if (_state == DELETING) return;
        internal_.MSG();
    }

    public void verbose(int level, string msg)
    {
        if (_state == DELETING) return;
        internal_.VERBOSE(level, msg);
    }

    public void error(string msg)
    {
        if (_state == DELETING) return;
        internal_.error(msg);
    }
}
