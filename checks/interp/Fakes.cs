// Fake Lean exports and a fake compiled module class for the interpreter checks.

using LeanSharp.Runtime;
using LeanSharp.Runtime.Interp;
using static LeanSharp.Runtime.LeanRt;

/// <summary>Stands in for a generated module class (`LeanSharp.Compiled.M_Test_Mod`).</summary>
public static unsafe class M_Test_Mod
{
    public static int TripleCalls;
    public static int InitCalls;

    static Obj NatAdd(Obj a, Obj b) => lean_box(lean_unbox(a) + lean_unbox(b));

    // boxed wrappers of externs with borrowed parameters: they `dec` their arguments
    public static Obj l_Nat_add___boxed(Obj x_1, Obj x_2) { var r = NatAdd(x_1, x_2); lean_dec(x_2); lean_dec(x_1); return r; }
    public static Obj l_Nat_sub___boxed(Obj x_1, Obj x_2)
    {
        ulong a = lean_unbox(x_1), b = lean_unbox(x_2);
        var r = lean_box(a >= b ? a - b : 0); lean_dec(x_2); lean_dec(x_1); return r;
    }
    public static Obj l_Nat_mul___boxed(Obj x_1, Obj x_2) { var r = lean_box(lean_unbox(x_1) * lean_unbox(x_2)); lean_dec(x_2); lean_dec(x_1); return r; }
    public static byte l_Nat_decEq(Obj x_1, Obj x_2) => lean_unbox(x_1) == lean_unbox(x_2) ? (byte)1 : (byte)0;
    public static Obj l_Nat_decEq___boxed(Obj x_1, Obj x_2) { byte r = l_Nat_decEq(x_1, x_2); lean_dec(x_2); lean_dec(x_1); return lean_box(r); }

    // `Test.len (s : @& String) : Nat` (extern)
    public static Obj l_Test_len___boxed(Obj x_1) { var r = lean_box(lean_string_len(x_1)); lean_dec(x_1); return r; }

    // compiled function with only owned object parameters (no boxed version)
    public static Obj l_Test_triple(Obj x_1) { TripleCalls++; return lean_box(3 * lean_unbox(x_1)); }

    // compiled function with an unboxed parameter/result and its boxed version
    public static byte l_Test_isZero(byte x_1) => x_1 == 0 ? (byte)1 : (byte)0;
    public static Obj l_Test_isZero___boxed(Obj x_1) => lean_box(l_Test_isZero((byte)lean_unbox(x_1)));

    // compiled code calling a closure
    public static Obj l_Test_callWith5(Obj x_1) => lean_apply_1(x_1, lean_box(5));

    // > 16 parameters: boxed version takes an array
    public static Obj l_Test_sum17___boxed(Obj[] _args)
    {
        ulong s = 0;
        foreach (var a in _args) s += lean_unbox(a);
        return lean_box(s);
    }

    // constants
    static Obj l_Test_compiledConst_cell;
    public static Obj l_Test_compiledConst => l_Test_compiledConst_cell ?? lean_obj_once(ref l_Test_compiledConst_cell, &_init_l_Test_compiledConst);
    public static Obj _init_l_Test_compiledConst() => lean_mk_string("compiled constant");
    public static Obj l_Test_compiledField;
    public static byte l_Test_byteConst => 77;
    public static Obj l_Test_initField; // `[init]` decl stored in a field

    // module initializer
    static bool _G_initialized;
    public static Obj initialize(byte builtin)
    {
        InitCalls++;
        if (_G_initialized) return lean_io_result_mk_ok(lean_box(0));
        _G_initialized = true;
        l_Test_compiledField = lean_mk_string("field value");
        lean_mark_persistent(l_Test_compiledField);
        return lean_io_result_mk_ok(lean_box(0));
    }
}

/// <summary>Stands in for the compiled `Lean.Environment` module: the interpreter calls these
/// to find the defining module of a declaration.</summary>
public static unsafe class M_Lean_Environment
{
    public static Obj l_Lean_Environment_header___boxed(Obj env)
    {
        // fake environments are ctors with one field: the header
        Obj h = lean_ctor_get(env, 0); lean_inc(h); lean_dec(env); return h;
    }
    public static Obj l_Lean_Environment_allImportedModuleNames___boxed(Obj env)
    {
        lean_dec(env);
        return MkArray(new[] { IrName.Mk("Test.Mod"), IrName.Mk("Other.Mod") });
    }
    public static Obj l_Lean_Environment_getModuleIdxFor_x3f___boxed(Obj env, Obj n)
    {
        string s = IrName.ToString(n);
        Obj header = lean_ctor_get(env, 0);
        lean_dec(env); lean_dec(n);
        if (FakeEnv.LocalIn.TryGetValue(s, out var h) && ReferenceEquals(h, header)) return lean_box(0);
        if (FakeEnv.ModuleOf.TryGetValue(s, out int i)) return lean_mk_option_some(lean_box((ulong)i));
        return lean_box(0);
    }
}

static unsafe class FakeEnv
{
    /// <summary>Imported declarations: name -> module index (0 = Test.Mod (compiled), 1 = Other.Mod).</summary>
    public static readonly Dictionary<string, int> ModuleOf = new();
    /// <summary>Declarations that are local (not imported) in the environment with the given header.</summary>
    public static readonly Dictionary<string, Obj> LocalIn = new();
    public static Obj MkEnv() { var e = lean_alloc_ctor(0, 1, 0); lean_ctor_set(e, 0, lean_alloc_ctor(0, 0, 1)); return e; }

    public static readonly Dictionary<string, Obj> Decls = new();
    public static readonly Dictionary<string, string> InitAttr = new();        // decl -> init fn ("" = IO Unit)
    public static readonly Dictionary<string, string> SorryDeps = new();
    public static Obj MainType;

    static string S(Obj n) => IrName.ToString(n);

    static Obj Find(Obj env, Obj n)
    {
        string s = S(n);
        lean_dec(env); lean_dec(n);
        if (Decls.TryGetValue(s, out var d)) { lean_inc(d); return lean_mk_option_some(d); }
        return lean_box(0);
    }

    static Obj FindBoxed(Obj env, Obj n)
    {
        string s = S(n) + "._boxed";
        lean_dec(env); lean_dec(n);
        if (Decls.TryGetValue(s, out var d)) { lean_inc(d); return lean_mk_option_some(d); }
        return lean_box(0);
    }

    static Obj SymbolStem(Obj env, Obj n)
    {
        var r = lean_mk_string(LeanNameMangling.Mangle(n));
        lean_dec(env); lean_dec(n);
        return r;
    }

    static Obj MangledBoxed(Obj s)
    {
        var r = lean_mk_string(LeanNameMangling.MkMangledBoxedName(lean_string_to_net(s)));
        lean_dec(s);
        return r;
    }

    static Obj InitFnFor(Obj env, Obj n, bool regularOnly)
    {
        string s = S(n);
        lean_dec(env); lean_dec(n);
        if (InitAttr.TryGetValue(s, out var f) && f != "") return lean_mk_option_some(IrName.Mk(f));
        return lean_box(0);
    }
    static Obj GetInitFnNameFor(Obj env, Obj n) => InitFnFor(env, n, false);
    static Obj GetRegularInitFnNameFor(Obj env, Obj n) => InitFnFor(env, n, true);

    static Obj ExportNameFor(Obj env, Obj n) { lean_dec(env); lean_dec(n); return lean_box(0); }

    static Obj SorryDep(Obj env, Obj n)
    {
        string s = S(n);
        lean_dec(env); lean_dec(n);
        if (SorryDeps.TryGetValue(s, out var dep)) return lean_mk_option_some(IrName.Mk(dep));
        return lean_box(0);
    }

    /// <summary>Fake `Options`: a ctor with one scalar byte = value of `interpreter.prefer_native`.</summary>
    public static Obj MkOpts(bool preferNative)
    {
        var o = lean_alloc_ctor(0, 0, 1);
        lean_ctor_set_uint8_s(o, 0, preferNative ? (byte)1 : (byte)0);
        return o;
    }

    static byte OptionsGetBool(Obj opts, Obj n, byte def)
    {
        byte r = S(n) == "interpreter.prefer_native" ? lean_ctor_get_uint8_s(opts, 0) : def;
        lean_dec(opts); lean_dec(n);
        return r;
    }

    // IO.Error.userError: fake representation ctor 7 [msg]
    static Obj MkIoUserError(Obj s) { var r = lean_alloc_ctor(7, 1, 0); lean_ctor_set(r, 0, s); return r; }
    static Obj IoErrorToString(Obj e) { var s = lean_ctor_get(e, 0); lean_inc(s); lean_dec(e); return s; }
    static Obj ToKernelEnv(Obj env) => env;

    static Obj EnvironmentFind(Obj kenv, Obj n)
    {
        string s = S(n);
        lean_dec(kenv); lean_dec(n);
        if (s != "main" || MainType == null) return lean_box(0);
        var cval = lean_alloc_ctor(0, 3, 0);
        lean_ctor_set(cval, 0, IrName.Mk("main"));
        lean_ctor_set(cval, 1, lean_box(0));
        lean_inc(MainType);
        lean_ctor_set(cval, 2, MainType);
        var dval = lean_alloc_ctor(0, 1, 0);
        lean_ctor_set(dval, 0, cval);
        var ci = lean_alloc_ctor(1, 1, 0);
        lean_ctor_set(ci, 0, dval);
        return lean_mk_option_some(ci);
    }

    public static Obj RegisteredOption, RegisteredOptionDecl;
    static Obj RegisterOption(Obj n, Obj d) { RegisteredOption = n; RegisteredOptionDecl = d; return lean_io_result_mk_ok(lean_box(0)); }

    public static void Register()
    {
        LeanExports.Register("lean_register_option", (nint)(delegate*<Obj, Obj, Obj>)&RegisterOption);
        LeanExports.Register("lean_ir_find_env_decl", (nint)(delegate*<Obj, Obj, Obj>)&Find);
        LeanExports.Register("lean_ir_find_env_decl_boxed", (nint)(delegate*<Obj, Obj, Obj>)&FindBoxed);
        LeanExports.Register("lean_get_symbol_stem", (nint)(delegate*<Obj, Obj, Obj>)&SymbolStem);
        LeanExports.Register("lean_mk_mangled_boxed_name", (nint)(delegate*<Obj, Obj>)&MangledBoxed);
        LeanExports.Register("lean_get_init_fn_name_for", (nint)(delegate*<Obj, Obj, Obj>)&GetInitFnNameFor);
        LeanExports.Register("lean_get_regular_init_fn_name_for", (nint)(delegate*<Obj, Obj, Obj>)&GetRegularInitFnNameFor);
        LeanExports.Register("lean_get_export_name_for", (nint)(delegate*<Obj, Obj, Obj>)&ExportNameFor);
        LeanExports.Register("lean_decl_get_sorry_dep", (nint)(delegate*<Obj, Obj, Obj>)&SorryDep);
        LeanExports.Register("lean_options_get_bool", (nint)(delegate*<Obj, Obj, byte, byte>)&OptionsGetBool);
        LeanExports.Register("lean_mk_io_user_error", (nint)(delegate*<Obj, Obj>)&MkIoUserError);
        LeanExports.Register("lean_io_error_to_string", (nint)(delegate*<Obj, Obj>)&IoErrorToString);
        LeanExports.Register("lean_elab_environment_to_kernel_env", (nint)(delegate*<Obj, Obj>)&ToKernelEnv);
        LeanExports.Register("lean_environment_find", (nint)(delegate*<Obj, Obj, Obj>)&EnvironmentFind);
    }
}

/// <summary>Builders for `Lean.IR` objects (constructor layouts of Lean/Compiler/IR/Basic.lean).</summary>
static class B
{
    public static Obj Ty(IrType t) => lean_box((ulong)t);
    public const IrType O = IrType.Object, U8 = IrType.UInt8, F64 = IrType.Float, U64 = IrType.UInt64, USz = IrType.USize, VoidT = IrType.Void;

    static Obj C(uint tag, params Obj[] fs)
    {
        var o = lean_alloc_ctor(tag, (uint)fs.Length, 0);
        for (int i = 0; i < fs.Length; i++) lean_ctor_set(o, (uint)i, fs[i]);
        return o;
    }
    static Obj CS(uint tag, int scalarSz, params Obj[] fs)
    {
        var o = lean_alloc_ctor(tag, (uint)fs.Length, (uint)scalarSz);
        for (int i = 0; i < fs.Length; i++) lean_ctor_set(o, (uint)i, fs[i]);
        return o;
    }
    static Obj N(int i) => lean_box((ulong)i);
    static Obj Arr(Obj[] xs) => MkArray(xs);

    // Arg
    public static Obj A(int x) => C(0, N(x));
    public static readonly Obj Erased = lean_box(1);
    static Obj Args(object[] xs) => Arr(xs.Select(x => x is int i ? A(i) : (Obj)x).ToArray());

    public static Obj CI(string name, int cidx, int size = 0, int usize = 0, int ssize = 0) =>
        C(0, IrName.Mk(name), N(cidx), N(size), N(usize), N(ssize));

    // Expr
    public static Obj Ctor(Obj info, params object[] ys) => C(0, info, Args(ys));
    public static Obj Reset(int n, int x) => C(1, N(n), N(x));
    public static Obj Reuse(int x, Obj info, bool updt, params object[] ys)
    {
        var o = CS(2, 1, N(x), info, Args(ys));
        lean_ctor_set_uint8_s(o, 0, updt ? (byte)1 : (byte)0);
        return o;
    }
    public static Obj Proj(int i, int x) => C(3, N(i), N(x));
    public static Obj UProj(int i, int x) => C(4, N(i), N(x));
    public static Obj SProj(int n, int off, int x) => C(5, N(n), N(off), N(x));
    public static Obj FAp(string f, params object[] ys) => C(6, IrName.Mk(f), Args(ys));
    public static Obj PAp(string f, params object[] ys) => C(7, IrName.Mk(f), Args(ys));
    public static Obj Ap(int x, params object[] ys) => C(8, N(x), Args(ys));
    public static Obj Box(IrType t, int x) => C(9, Ty(t), N(x));
    public static Obj Unbox(int x) => C(10, N(x));
    public static Obj Lit(ulong n) => C(11, C(0, lean_usize_to_nat(n)));
    public static Obj LitStr(string s) => C(11, C(1, lean_mk_string(s)));
    public static Obj IsShared(int x) => C(12, N(x));

    // Param
    public static Obj P(int x, IrType t, bool borrow = false)
    {
        var o = CS(0, 1, N(x), Ty(t));
        lean_ctor_set_uint8_s(o, 0, borrow ? (byte)1 : (byte)0);
        return o;
    }

    // Alt
    public static Obj Alt(Obj info, Obj b) => C(0, info, b);
    public static Obj Default(Obj b) => C(1, b);
    public static readonly Obj False = CI("Bool.false", 0), True = CI("Bool.true", 1);

    // FnBody
    public static Obj VDecl(int x, IrType t, Obj e, Obj b) => C(0, N(x), Ty(t), e, b);
    public static Obj JDecl(int j, Obj[] ps, Obj v, Obj b) => C(1, N(j), Arr(ps), v, b);
    public static Obj Set(int x, int i, object y, Obj b) => C(2, N(x), N(i), y is int k ? A(k) : (Obj)y, b);
    public static Obj SetTag(int x, int cidx, Obj b) => C(3, N(x), N(cidx), b);
    public static Obj USet(int x, int i, int y, Obj b) => C(4, N(x), N(i), N(y), b);
    public static Obj SSet(int x, int i, int off, int y, IrType t, Obj b) => C(5, N(x), N(i), N(off), N(y), Ty(t), b);
    public static Obj Inc(int x, int n, Obj b) { var o = CS(6, 2, N(x), N(n), b); lean_ctor_set_uint8_s(o, 0, 1); return o; }
    public static Obj Dec(int x, Obj b) { var o = CS(7, 2, N(x), N(1), b); lean_ctor_set_uint8_s(o, 0, 1); return o; }
    public static Obj Del(int x, Obj b) => C(8, N(x), b);
    public static Obj Case(int x, IrType t, params Obj[] alts) => C(9, IrName.Mk("Test"), N(x), Ty(t), Arr(alts));
    public static Obj Ret(object x) => C(10, x is int k ? A(k) : (Obj)x);
    public static Obj Jmp(int j, params object[] ys) => C(11, N(j), Args(ys));
    public static readonly Obj Unreachable = lean_box(12);

    // Decl
    public static Obj Fun(string f, Obj[] ps, IrType t, Obj body)
    {
        var d = C(0, IrName.Mk(f), Arr(ps), Ty(t), body, lean_box(0));
        FakeEnv.Decls[f] = d;
        return d;
    }
    public static Obj Extern(string f, Obj[] ps, IrType t)
    {
        var d = C(1, IrName.Mk(f), Arr(ps), Ty(t), lean_box(0));
        FakeEnv.Decls[f] = d;
        return d;
    }
}
