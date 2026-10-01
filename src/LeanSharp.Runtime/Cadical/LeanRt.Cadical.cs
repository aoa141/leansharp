// Port of 'src/runtime/cadical.cpp': the Lean FFI bindings ('Lean.Cadical.Internal') backed by the
// managed CaDiCaL port. All solver arguments are borrowed ('@&').

using System.Runtime.CompilerServices;
using System.Text;
using LeanSharp.Runtime.Cadical;

namespace LeanSharp.Runtime;

public static unsafe partial class LeanRt
{
    static readonly ExternalClass g_cadical_solver_external_class = lean_register_external_class(_ => { }, (_, _) => { });

    /// <summary>Writer for CaDiCaL messages printed by the FFI ('stdout' of the Lean process).</summary>
    sealed class CadicalStdoutWriter : TextWriter
    {
        readonly StringBuilder sb = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { lock (sb) sb.Append(value); }
        public override void Write(string value) { lock (sb) sb.Append(value); }
        public override void Flush()
        {
            string s;
            lock (sb) { s = sb.ToString(); sb.Clear(); }
            if (s.Length > 0) LeanIO.StdoutWrite(s);
        }
    }

    sealed class CadicalStderrWriter : TextWriter
    {
        readonly StringBuilder sb = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { lock (sb) sb.Append(value); }
        public override void Write(string value) { lock (sb) sb.Append(value); }
        public override void Flush()
        {
            string s;
            lock (sb) { s = sb.ToString(); sb.Clear(); }
            if (s.Length > 0) LeanIO.StderrWrite(s);
        }
    }

    static Solver cadical_solver_of(Obj o) => (Solver)lean_get_external_data(o);

    static int cadical_lit(uint lit) => (int)lit;

    static byte cadical_status(int r) => r switch { 10 => 0, 20 => 1, _ => 2 };

    static string cadical_cstr(Obj s)
    {
        string str = lean_string_to_net(s);
        int nul = str.IndexOf('\0');
        return nul >= 0 ? str.Substring(0, nul) : str;
    }

    static byte cadical_bool(bool b) => b ? (byte)1 : (byte)0;

    /// <summary>Run a solver operation; contract violations / fatal errors abort like in C++.</summary>
    static T cadical_guard<T>(Func<T> f)
    {
        try { return f(); }
        catch (CadicalException e) { throw lean_internal_panic("CaDiCaL: " + e.Message); }
        catch (CadicalExit e) { throw lean_internal_panic("CaDiCaL: exit " + e.code); }
    }

    static Obj cadical_unit() => lean_box(0);

    /* Lean.Cadical.Internal.getSignature (u : Unit) : String */
    public static Obj lean_cadical_signature(Obj u) => lean_mk_string(Solver.signature());

    /* Solver.new : BaseIO Solver */
    public static Obj lean_cadical_solver_new()
    {
        var solver = new Solver(0, new CadicalStdoutWriter(), new CadicalStderrWriter());
        return lean_alloc_external(g_cadical_solver_external_class, solver);
    }

    public static Obj lean_cadical_solver_add(Obj s, uint lit)
    {
        cadical_guard(() => { cadical_solver_of(s).add(cadical_lit(lit)); return 0; });
        return cadical_unit();
    }

    public static Obj lean_cadical_solver_clause(Obj s, Obj lits)
    {
        ulong n = lean_array_size(lits);
        var buf = new int[n];
        for (ulong i = 0; i < n; i++) buf[i] = cadical_lit((uint)lean_unbox(lean_array_get_core(lits, i)));
        cadical_guard(() => { cadical_solver_of(s).clause(buf); return 0; });
        return cadical_unit();
    }

    public static byte lean_cadical_solver_inconsistent(Obj s) => cadical_bool(cadical_solver_of(s).inconsistent());

    public static Obj lean_cadical_solver_assume(Obj s, uint lit)
    {
        cadical_guard(() => { cadical_solver_of(s).assume(cadical_lit(lit)); return 0; });
        return cadical_unit();
    }

    public static byte lean_cadical_solver_solve(Obj s) => cadical_status(cadical_guard(() => cadical_solver_of(s).solve()));

    public static uint lean_cadical_solver_val(Obj s, uint lit) => (uint)cadical_guard(() => cadical_solver_of(s).val(cadical_lit(lit)));

    public static byte lean_cadical_solver_flip(Obj s, uint lit) => cadical_bool(cadical_guard(() => cadical_solver_of(s).flip(cadical_lit(lit))));

    public static byte lean_cadical_solver_flippable(Obj s, uint lit) => cadical_bool(cadical_guard(() => cadical_solver_of(s).flippable(cadical_lit(lit))));

    public static byte lean_cadical_solver_failed(Obj s, uint lit) => cadical_bool(cadical_guard(() => cadical_solver_of(s).failed(cadical_lit(lit))));

    public static Obj lean_cadical_solver_constrain(Obj s, uint lit)
    {
        cadical_guard(() => { cadical_solver_of(s).constrain(cadical_lit(lit)); return 0; });
        return cadical_unit();
    }

    public static byte lean_cadical_solver_constraint_failed(Obj s) => cadical_bool(cadical_guard(() => cadical_solver_of(s).constraint_failed()));

    public static uint lean_cadical_solver_lookahead(Obj s) => (uint)cadical_guard(() => cadical_solver_of(s).lookahead());

    public static Obj lean_cadical_solver_reset_assumptions(Obj s)
    {
        cadical_guard(() => { cadical_solver_of(s).reset_assumptions(); return 0; });
        return cadical_unit();
    }

    public static Obj lean_cadical_solver_reset_constraint(Obj s)
    {
        cadical_guard(() => { cadical_solver_of(s).reset_constraint(); return 0; });
        return cadical_unit();
    }

    public static ushort lean_cadical_solver_state(Obj s) => (ushort)cadical_solver_of(s).state();

    public static byte lean_cadical_solver_status(Obj s)
    {
        // 'Solver::status' maps the state to 10/20/0.
        int st = cadical_solver_of(s).state();
        int r = st == Solver.SATISFIED ? 10 : st == Solver.UNSATISFIED ? 20 : 0;
        return cadical_status(r);
    }

    public static uint lean_cadical_solver_vars(Obj s) => (uint)cadical_guard(() => cadical_solver_of(s).vars());

    public static Obj lean_cadical_solver_resize(Obj s, uint min_max_var)
    {
        cadical_guard(() => { cadical_solver_of(s).reserve(cadical_lit(min_max_var)); return 0; });
        return cadical_unit();
    }

    public static byte lean_cadical_solver_is_valid_option(Obj opt) => cadical_bool(Solver.is_valid_option(cadical_cstr(opt)));

    public static byte lean_cadical_solver_is_preprocessing_option(Obj opt) => cadical_bool(Solver.is_preprocessing_option(cadical_cstr(opt)));

    public static byte lean_cadical_solver_is_valid_long_option(Obj opt) => cadical_bool(Solver.is_valid_long_option(cadical_cstr(opt)));

    public static uint lean_cadical_solver_get(Obj s, Obj opt) => (uint)cadical_guard(() => cadical_solver_of(s).get(cadical_cstr(opt)));

    public static byte lean_cadical_solver_set(Obj s, Obj opt, uint val) =>
        cadical_bool(cadical_guard(() => cadical_solver_of(s).set(cadical_cstr(opt), cadical_lit(val))));

    public static byte lean_cadical_solver_set_long_option(Obj s, Obj opt) =>
        cadical_bool(cadical_guard(() => cadical_solver_of(s).set_long_option(cadical_cstr(opt))));

    public static byte lean_cadical_solver_is_valid_configuration(Obj opt) => cadical_bool(Solver.is_valid_configuration(cadical_cstr(opt)));

    public static byte lean_cadical_solver_configure(Obj s, Obj opt) =>
        cadical_bool(cadical_guard(() => cadical_solver_of(s).configure(cadical_cstr(opt))));

    public static Obj lean_cadical_solver_optimize(Obj s, uint val)
    {
        cadical_guard(() => { cadical_solver_of(s).optimize(cadical_lit(val)); return 0; });
        return cadical_unit();
    }

    public static byte lean_cadical_solver_limit(Obj s, Obj limit, uint val) =>
        cadical_bool(cadical_guard(() => cadical_solver_of(s).limit(cadical_cstr(limit), cadical_lit(val))));

    public static byte lean_cadical_solver_is_valid_limit(Obj s, Obj limit) => cadical_bool(Solver.is_valid_limit(cadical_cstr(limit)));

    public static uint lean_cadical_solver_active(Obj s) => (uint)cadical_guard(() => cadical_solver_of(s).active());

    public static ulong lean_cadical_solver_redundant(Obj s) => (ulong)cadical_guard(() => cadical_solver_of(s).redundant());

    public static ulong lean_cadical_solver_irredundant(Obj s) => (ulong)cadical_guard(() => cadical_solver_of(s).irredundant());

    public static byte lean_cadical_solver_simplify(Obj s, uint rounds) => cadical_status(cadical_guard(() => cadical_solver_of(s).simplify(cadical_lit(rounds))));

    public static Obj lean_cadical_solver_terminate(Obj s)
    {
        cadical_guard(() => { cadical_solver_of(s).terminate(); return 0; });
        return cadical_unit();
    }

    public static byte lean_cadical_solver_frozen(Obj s, uint lit) => cadical_bool(cadical_guard(() => cadical_solver_of(s).frozen(cadical_lit(lit))));

    public static Obj lean_cadical_solver_freeze(Obj s, uint lit)
    {
        cadical_guard(() => { cadical_solver_of(s).freeze(cadical_lit(lit)); return 0; });
        return cadical_unit();
    }

    public static Obj lean_cadical_solver_melt(Obj s, uint lit)
    {
        cadical_guard(() => { cadical_solver_of(s).melt(cadical_lit(lit)); return 0; });
        return cadical_unit();
    }

    public static uint lean_cadical_solver_fixed(Obj s, uint lit) => (uint)cadical_guard(() => cadical_solver_of(s).@fixed(cadical_lit(lit)));

    public static Obj lean_cadical_solver_phase(Obj s, uint lit)
    {
        cadical_guard(() => { cadical_solver_of(s).phase(cadical_lit(lit)); return 0; });
        return cadical_unit();
    }

    public static Obj lean_cadical_solver_unphase(Obj s, uint lit)
    {
        cadical_guard(() => { cadical_solver_of(s).unphase(cadical_lit(lit)); return 0; });
        return cadical_unit();
    }

    public static Obj lean_cadical_solver_conclude(Obj s)
    {
        cadical_guard(() => { cadical_solver_of(s).conclude(); return 0; });
        return cadical_unit();
    }

    /* Solver.usage : IO Unit */
    public static Obj lean_cadical_solver_usage()
    {
        var w = new CadicalStdoutWriter();
        Solver.usage(w);
        w.Flush();
        return lean_io_result_mk_ok(lean_box(0));
    }

    /* Solver.configurations : IO Unit */
    public static Obj lean_cadical_solver_configurations()
    {
        var w = new CadicalStdoutWriter();
        Solver.configurations(w);
        w.Flush();
        return lean_io_result_mk_ok(lean_box(0));
    }

    /* Solver.statistics (s : @& Solver) : IO Unit */
    public static Obj lean_cadical_solver_statistics(Obj s)
    {
        cadical_guard(() => { cadical_solver_of(s).statistics(); return 0; });
        cadical_solver_of(s).internal_.output.Flush();
        return lean_io_result_mk_ok(lean_box(0));
    }

    /* Solver.resources (s : @& Solver) : IO Unit */
    public static Obj lean_cadical_solver_resources(Obj s)
    {
        cadical_guard(() => { cadical_solver_of(s).resources(); return 0; });
        cadical_solver_of(s).internal_.output.Flush();
        return lean_io_result_mk_ok(lean_box(0));
    }
}
