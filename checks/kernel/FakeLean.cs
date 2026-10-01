// C# implementations of the Lean-exported functions used by the kernel, for testing without the
// generated code. Names, levels and expressions are built exactly like the Lean definitions
// (including the cached hash/data fields; verified against the real Lean in Program.cs).
// Environments, local contexts and metavariable contexts are simple fakes (their
// representation is opaque to the kernel).

using System.Numerics;
using LeanSharp.Runtime;
using static LeanSharp.Runtime.LeanRt;

namespace KernelCheck;

public static unsafe class FakeLean
{
    static ulong Mix(ulong a, ulong b) => LeanHash.Mix(a, b);

    // ---------------------------------------------------------------- names
    public static ulong NameHash(Obj n) => lean_is_scalar(n) ? 1723UL : lean_ctor_get_uint64_s(n, 0);
    static ulong StrHash(Obj s) => LeanHash.HashStr(lean_string_span(s), 11);

    static Obj NameMkString(Obj p, Obj s)
    {
        var r = lean_alloc_ctor(1, 2, 8);
        lean_ctor_set(r, 0, p); lean_ctor_set(r, 1, s);
        lean_ctor_set_uint64_s(r, 0, Mix(NameHash(p), StrHash(s)));
        return r;
    }

    static Obj NameMkNumeral(Obj p, Obj n)
    {
        BigInteger v = lean_nat_to_big(n);
        ulong h = v < (BigInteger.One << 64) ? (ulong)v : 17UL;
        var r = lean_alloc_ctor(2, 2, 8);
        lean_ctor_set(r, 0, p); lean_ctor_set(r, 1, n);
        lean_ctor_set_uint64_s(r, 0, Mix(NameHash(p), h));
        return r;
    }

    static Obj NameAppendAfter(Obj n, Obj s)
    {
        if (!lean_is_scalar(n) && n.m_tag == 1)
            return NameMkString(lean_ctor_get(n, 0), lean_mk_string(lean_string_to_net(lean_ctor_get(n, 1)) + lean_string_to_net(s)));
        return NameMkString(n, s);
    }

    static Obj NameAppendIndexAfter(Obj n, Obj i)
    {
        string suffix = "_" + lean_nat_to_big(i).ToString();
        if (!lean_is_scalar(n) && n.m_tag == 1)
            return NameMkString(lean_ctor_get(n, 0), lean_mk_string(lean_string_to_net(lean_ctor_get(n, 1)) + suffix));
        return NameMkString(n, lean_mk_string(suffix));
    }

    // ---------------------------------------------------------------- levels
    static ulong LData(Obj l) => lean_is_scalar(l) ? 2221UL : lean_ctor_get_uint64_s(l, 0);
    static ulong LHash(Obj l) => (uint)LData(l);
    static ulong LDepth(Obj l) => LData(l) >> 40;
    static bool LMVar(Obj l) => ((LData(l) >> 32) & 1) == 1;
    static bool LParam(Obj l) => ((LData(l) >> 33) & 1) == 1;
    static byte B(bool b) => b ? (byte)1 : (byte)0;

    static Obj MkLevel(uint tag, ulong data, params Obj[] fields)
    {
        var r = lean_alloc_ctor(tag, (uint)fields.Length, 8);
        for (int i = 0; i < fields.Length; i++) lean_ctor_set(r, (uint)i, fields[i]);
        lean_ctor_set_uint64_s(r, 0, data);
        return r;
    }

    static Obj LevelMkZero(Obj unit) => lean_box(0);
    static Obj LevelMkSucc(Obj u) =>
        MkLevel(1, lean_level_mk_data(Mix(2243, LHash(u)), lean_box(LDepth(u) + 1), B(LMVar(u)), B(LParam(u))), u);
    static Obj LevelMkMax(Obj u, Obj v) =>
        MkLevel(2, lean_level_mk_data(Mix(2251, Mix(LHash(u), LHash(v))), lean_box(Math.Max(LDepth(u), LDepth(v)) + 1), B(LMVar(u) || LMVar(v)), B(LParam(u) || LParam(v))), u, v);
    static Obj LevelMkIMax(Obj u, Obj v) =>
        MkLevel(3, lean_level_mk_data(Mix(2267, Mix(LHash(u), LHash(v))), lean_box(Math.Max(LDepth(u), LDepth(v)) + 1), B(LMVar(u) || LMVar(v)), B(LParam(u) || LParam(v))), u, v);
    static Obj LevelMkParam(Obj n) => MkLevel(4, lean_level_mk_data(Mix(2239, NameHash(n)), lean_box(0), 0, 1), n);
    public static Obj LevelMkMVar(Obj n) => MkLevel(5, lean_level_mk_data(Mix(2237, Mix(0, NameHash(n))), lean_box(0), 1, 0), n);

    // ---------------------------------------------------------------- exprs
    static ulong EData(Obj e) => lean_ctor_get_uint64_s(e, 0);
    static ulong EHash(Obj e) => (uint)EData(e);
    static uint EDepth(Obj e) => (uint)((EData(e) >> 32) & 255);
    static ulong ERange(Obj e) => EData(e) >> 44;
    static byte Flag(Obj e, int bit) => (byte)((EData(e) >> bit) & 1);
    static ulong NatHash(Obj n) => (ulong)(lean_nat_to_big(n) & ulong.MaxValue);

    static Obj MkExpr(uint tag, uint scalarSz, ulong data, params Obj[] fields)
    {
        var r = lean_alloc_ctor(tag, (uint)fields.Length, scalarSz);
        for (int i = 0; i < fields.Length; i++) lean_ctor_set(r, (uint)i, fields[i]);
        lean_ctor_set_uint64_s(r, 0, data);
        return r;
    }

    static Obj ExprMkBVar(Obj idx) =>
        MkExpr(0, 8, lean_expr_mk_data(Mix(7, NatHash(idx)), lean_big_to_nat(lean_nat_to_big(idx) + 1), 0, 0, 0, 0, 0), idx);
    static Obj ExprMkFVar(Obj n) => MkExpr(1, 8, lean_expr_mk_data(Mix(13, Mix(0, NameHash(n))), lean_box(0), 0, 1, 0, 0, 0), n);
    static Obj ExprMkMVar(Obj n) => MkExpr(2, 8, lean_expr_mk_data(Mix(17, Mix(0, NameHash(n))), lean_box(0), 0, 0, 1, 0, 0), n);
    static Obj ExprMkSort(Obj l) => MkExpr(3, 8, lean_expr_mk_data(Mix(11, LHash(l)), lean_box(0), 0, 0, 0, B(LMVar(l)), B(LParam(l))), l);

    static Obj ExprMkConst(Obj n, Obj ls)
    {
        ulong h = 7; bool mv = false, lp = false;
        for (Obj it = ls; !lean_is_scalar(it); it = lean_ctor_get(it, 1))
        {
            Obj l = lean_ctor_get(it, 0);
            h = Mix(h, LHash(l));
            mv |= LMVar(l); lp |= LParam(l);
        }
        return MkExpr(4, 8, lean_expr_mk_data(Mix(5, Mix(NameHash(n), h)), lean_box(0), 0, 0, 0, B(mv), B(lp)), n, ls);
    }

    static Obj ExprMkApp(Obj f, Obj a) => MkExpr(5, 8, lean_expr_mk_app_data(EData(f), EData(a)), f, a);

    static Obj MkBinder(uint tag, Obj n, Obj t, Obj b, byte bi)
    {
        uint d = Math.Max(EDepth(t), EDepth(b)) + 1;
        ulong rb = ERange(b);
        ulong range = Math.Max(ERange(t), rb == 0 ? 0 : rb - 1);
        ulong data = lean_expr_mk_data(Mix(d, Mix(EHash(t), EHash(b))), lean_box(range), d,
            (byte)(Flag(t, 40) | Flag(b, 40)), (byte)(Flag(t, 41) | Flag(b, 41)), (byte)(Flag(t, 42) | Flag(b, 42)), (byte)(Flag(t, 43) | Flag(b, 43)));
        var r = MkExpr(tag, 9, data, n, t, b);
        lean_ctor_set_uint8_s(r, 8, bi);
        return r;
    }

    static Obj ExprMkLambda(Obj n, Obj t, Obj b, byte bi) => MkBinder(6, n, t, b, bi);
    static Obj ExprMkForall(Obj n, Obj t, Obj b, byte bi) => MkBinder(7, n, t, b, bi);

    static Obj ExprMkLet(Obj n, Obj t, Obj v, Obj b, byte nondep)
    {
        uint d = Math.Max(Math.Max(EDepth(t), EDepth(v)), EDepth(b)) + 1;
        ulong rb = ERange(b);
        ulong range = Math.Max(Math.Max(ERange(t), ERange(v)), rb == 0 ? 0 : rb - 1);
        ulong data = lean_expr_mk_data(Mix(d, Mix(EHash(t), Mix(EHash(v), EHash(b)))), lean_box(range), d,
            (byte)(Flag(t, 40) | Flag(v, 40) | Flag(b, 40)), (byte)(Flag(t, 41) | Flag(v, 41) | Flag(b, 41)),
            (byte)(Flag(t, 42) | Flag(v, 42) | Flag(b, 42)), (byte)(Flag(t, 43) | Flag(v, 43) | Flag(b, 43)));
        var r = MkExpr(8, 9, data, n, t, v, b);
        lean_ctor_set_uint8_s(r, 8, nondep);
        return r;
    }

    static Obj ExprMkLit(Obj l)
    {
        Obj v = lean_ctor_get(l, 0);
        ulong lh = l.m_tag == 0 ? NatHash(v) : StrHash(v);
        return MkExpr(9, 8, lean_expr_mk_data(Mix(3, lh), lean_box(0), 0, 0, 0, 0, 0), l);
    }

    static Obj ExprMkMData(Obj m, Obj e)
    {
        uint d = EDepth(e) + 1;
        ulong data = lean_expr_mk_data(Mix(d, EHash(e)), lean_box(ERange(e)), d, Flag(e, 40), Flag(e, 41), Flag(e, 42), Flag(e, 43));
        return MkExpr(10, 8, data, m, e);
    }

    static Obj ExprMkProj(Obj s, Obj i, Obj e)
    {
        uint d = EDepth(e) + 1;
        ulong data = lean_expr_mk_data(Mix(d, Mix(NameHash(s), Mix(NatHash(i), EHash(e)))), lean_box(ERange(e)), d, Flag(e, 40), Flag(e, 41), Flag(e, 42), Flag(e, 43));
        return MkExpr(11, 8, data, s, i, e);
    }

    static Obj MkName(params string[] parts)
    {
        Obj r = lean_box(0);
        foreach (var p in parts) r = NameMkString(r, lean_mk_string(p));
        return r;
    }

    static Obj LitType(Obj l) => ExprMkConst(l.m_tag == 0 ? MkName("Nat") : MkName("String"), lean_box(0));
    static Obj ExprConsumeTypeAnnotations(Obj e) => e;

    // ---------------------------------------------------------------- local contexts
    // LocalContext fake: ctor 0 [decls : List LocalDecl (newest first), numIndices : Nat]
    static Obj MkEmptyLocalCtx(Obj unit) => RCx.Ctor(0, lean_box(0), lean_box(0));
    static Obj LocalCtxNumIndices(Obj lctx) { var n = lean_ctor_get(lctx, 1); lean_inc(n); return n; }

    static Obj LocalCtxPush(Obj lctx, Obj decl)
    {
        Obj decls = lean_ctor_get(lctx, 0); lean_inc(decls);
        Obj n = lean_ctor_get(lctx, 1);
        return RCx.Ctor(0, lean_mk_list_cons(decl, decls), lean_box(lean_unbox(n) + 1));
    }

    static Obj LocalCtxMkLocalDecl(Obj lctx, Obj fvarId, Obj userName, Obj type, byte bi)
    {
        Obj idx = lean_ctor_get(lctx, 1);
        var d = lean_alloc_ctor(0, 4, 2);
        lean_ctor_set(d, 0, idx); lean_ctor_set(d, 1, fvarId); lean_ctor_set(d, 2, userName); lean_ctor_set(d, 3, type);
        lean_ctor_set_uint8_s(d, 0, bi); lean_ctor_set_uint8_s(d, 1, 0);
        return LocalCtxPush(lctx, d);
    }

    static Obj LocalCtxMkLetDecl(Obj lctx, Obj fvarId, Obj userName, Obj type, Obj value, byte nondep)
    {
        Obj idx = lean_ctor_get(lctx, 1);
        var d = lean_alloc_ctor(1, 5, 2);
        lean_ctor_set(d, 0, idx); lean_ctor_set(d, 1, fvarId); lean_ctor_set(d, 2, userName); lean_ctor_set(d, 3, type); lean_ctor_set(d, 4, value);
        lean_ctor_set_uint8_s(d, 0, nondep); lean_ctor_set_uint8_s(d, 1, 0);
        return LocalCtxPush(lctx, d);
    }

    static Obj LocalCtxFind(Obj lctx, Obj fvarId)
    {
        for (Obj it = lean_ctor_get(lctx, 0); !lean_is_scalar(it); it = lean_ctor_get(it, 1))
        {
            Obj d = lean_ctor_get(it, 0);
            if (lean_name_eq_k(lean_ctor_get(d, 1), fvarId)) { lean_inc(d); return lean_mk_option_some(d); }
        }
        return lean_box(0);
    }

    static bool lean_name_eq_k(Obj a, Obj b) => LeanSharp.Kernel.Name.Eq(a, b);

    // ---------------------------------------------------------------- environment
    public sealed class FakeEnv
    {
        public Dictionary<string, Obj> Consts = new();
        public bool QuotInit;
        public Obj Diag;
        public FakeEnv Clone() => new FakeEnv { Consts = new Dictionary<string, Obj>(Consts), QuotInit = QuotInit, Diag = Diag };
    }

    static readonly ExternalClass s_envClass = new ExternalClass(_ => { }, null);
    public static Obj MkEnv(FakeEnv e) => new ExternalObj { m_tag = LeanExternal, m_class = s_envClass, m_data = e };
    public static FakeEnv GetEnv(Obj o) => (FakeEnv)((ExternalObj)o).m_data;
    static string Key(Obj n) => new LeanSharp.Kernel.Name(n).ToString();

    /// <summary>Kernel.Diagnostics fake: ctor 0 [unfoldCounter] [enabled : UInt8].</summary>
    public static Obj MkDiag(bool enabled)
    {
        var d = lean_alloc_ctor(0, 1, 1);
        lean_ctor_set(d, 0, lean_box(0));
        lean_ctor_set_uint8_s(d, 0, B(enabled));
        return d;
    }

    public static Obj EmptyEnv() => MkEnv(new FakeEnv { Diag = MkDiag(false) });

    static Obj EnvironmentFind(Obj env, Obj n)
    {
        if (GetEnv(env).Consts.TryGetValue(Key(n), out var c)) { lean_inc(c); return lean_mk_option_some(c); }
        return lean_box(0);
    }

    public static int AddCount;
    static Obj EnvironmentAdd(Obj env, Obj cinfo)
    {
        AddCount++;
        var e = GetEnv(env).Clone();
        Obj cval = lean_ctor_get(lean_ctor_get(cinfo, 0), 0);
        e.Consts[Key(lean_ctor_get(cval, 0))] = cinfo;
        return MkEnv(e);
    }

    static Obj EnvironmentMarkQuotInit(Obj env) { var e = GetEnv(env).Clone(); e.QuotInit = true; return MkEnv(e); }
    static byte EnvironmentQuotInit(Obj env) => B(GetEnv(env).QuotInit);
    static Obj KernelGetDiag(Obj env) { var d = GetEnv(env).Diag; lean_inc(d); return d; }
    static Obj KernelSetDiag(Obj env, Obj d) { var e = GetEnv(env).Clone(); e.Diag = d; return MkEnv(e); }
    static byte KernelDiagIsEnabled(Obj d) => lean_ctor_get_uint8_s(d, 0);
    public static List<string> Unfolds = new();
    static Obj KernelRecordUnfold(Obj d, Obj n) { Unfolds.Add(Key(n)); return d; }
    static Obj ElabEnvironmentToKernelEnv(Obj env) { lean_inc(env); return env; }
    static Obj ElabEnvironmentUpdateBaseAfterKernelAdd(Obj env, Obj kenv, Obj decl) => kenv;

    // ---------------------------------------------------------------- declarations
    public static Obj MkConstantVal(Obj n, Obj lparams, Obj type) => RCx.Ctor(0, n, lparams, type);

    static Obj MkDefinitionVal(Obj n, Obj lparams, Obj type, Obj value, Obj hints, byte safety, Obj all)
    {
        var r = lean_alloc_ctor(0, 4, 1);
        lean_ctor_set(r, 0, MkConstantVal(n, lparams, type)); lean_ctor_set(r, 1, value); lean_ctor_set(r, 2, hints); lean_ctor_set(r, 3, all);
        lean_ctor_set_uint8_s(r, 0, safety);
        return r;
    }

    static Obj MkQuotVal(Obj n, Obj lparams, Obj type, byte k)
    {
        var r = lean_alloc_ctor(0, 1, 1);
        lean_ctor_set(r, 0, MkConstantVal(n, lparams, type));
        lean_ctor_set_uint8_s(r, 0, k);
        return r;
    }

    static Obj MkInductiveVal(Obj n, Obj lparams, Obj type, Obj nparams, Obj nindices, Obj all, Obj ctors, Obj nnested, byte rec, byte isUnsafe, byte refl)
    {
        var r = lean_alloc_ctor(0, 6, 3);
        lean_ctor_set(r, 0, MkConstantVal(n, lparams, type)); lean_ctor_set(r, 1, nparams); lean_ctor_set(r, 2, nindices);
        lean_ctor_set(r, 3, all); lean_ctor_set(r, 4, ctors); lean_ctor_set(r, 5, nnested);
        lean_ctor_set_uint8_s(r, 0, rec); lean_ctor_set_uint8_s(r, 1, isUnsafe); lean_ctor_set_uint8_s(r, 2, refl);
        return r;
    }

    static Obj MkConstructorVal(Obj n, Obj lparams, Obj type, Obj induct, Obj cidx, Obj nparams, Obj nfields, byte isUnsafe)
    {
        var r = lean_alloc_ctor(0, 5, 1);
        lean_ctor_set(r, 0, MkConstantVal(n, lparams, type)); lean_ctor_set(r, 1, induct); lean_ctor_set(r, 2, cidx);
        lean_ctor_set(r, 3, nparams); lean_ctor_set(r, 4, nfields);
        lean_ctor_set_uint8_s(r, 0, isUnsafe);
        return r;
    }

    static Obj MkRecursorVal(Obj n, Obj lparams, Obj type, Obj all, Obj nparams, Obj nindices, Obj nmotives, Obj nminors, Obj rules, byte k, byte isUnsafe)
    {
        var r = lean_alloc_ctor(0, 7, 2);
        lean_ctor_set(r, 0, MkConstantVal(n, lparams, type)); lean_ctor_set(r, 1, all); lean_ctor_set(r, 2, nparams);
        lean_ctor_set(r, 3, nindices); lean_ctor_set(r, 4, nmotives); lean_ctor_set(r, 5, nminors); lean_ctor_set(r, 6, rules);
        lean_ctor_set_uint8_s(r, 0, k); lean_ctor_set_uint8_s(r, 1, isUnsafe);
        return r;
    }

    public static Obj MkInductiveDecl(Obj lparams, Obj nparams, Obj types, byte isUnsafe)
    {
        var r = lean_alloc_ctor(6, 3, 1);
        lean_ctor_set(r, 0, lparams); lean_ctor_set(r, 1, nparams); lean_ctor_set(r, 2, types);
        lean_ctor_set_uint8_s(r, 0, isUnsafe);
        return r;
    }

    // ---------------------------------------------------------------- metavariable contexts
    public sealed class FakeMCtx
    {
        public Dictionary<string, Obj> EAssign = new(), LAssign = new(), DAssign = new();
        public FakeMCtx Clone() => new FakeMCtx { EAssign = new(EAssign), LAssign = new(LAssign), DAssign = new(DAssign) };
    }

    static readonly ExternalClass s_mctxClass = new ExternalClass(_ => { }, null);
    public static Obj MkMCtx(FakeMCtx m) => new ExternalObj { m_tag = LeanExternal, m_class = s_mctxClass, m_data = m };
    public static FakeMCtx GetMCtx(Obj o) => (FakeMCtx)((ExternalObj)o).m_data;

    static Obj Lookup(Dictionary<string, Obj> d, Obj n)
    {
        if (d.TryGetValue(Key(n), out var v)) { lean_inc(v); return lean_mk_option_some(v); }
        return lean_box(0);
    }

    public static int MCtxAssignCount;
    static Obj GetLMVarAssignment(Obj m, Obj mid) => Lookup(GetMCtx(m).LAssign, mid);
    static Obj AssignLMVar(Obj m, Obj mid, Obj val) { MCtxAssignCount++; var c = GetMCtx(m).Clone(); c.LAssign[Key(mid)] = val; return MkMCtx(c); }
    static Obj GetMVarAssignment(Obj m, Obj mid) => Lookup(GetMCtx(m).EAssign, mid);
    static Obj AssignMVar(Obj m, Obj mid, Obj val) { MCtxAssignCount++; var c = GetMCtx(m).Clone(); c.EAssign[Key(mid)] = val; return MkMCtx(c); }
    static Obj GetDelayedMVarAssignment(Obj m, Obj mid) => Lookup(GetMCtx(m).DAssign, mid);
    static Obj DelayedFVars(Obj d) { var r = lean_ctor_get(d, 0); lean_inc(r); return r; }
    static Obj DelayedPending(Obj d) { var r = lean_ctor_get(d, 1); lean_inc(r); return r; }

    // ---------------------------------------------------------------- data values
    static byte DataValueBeq(Obj a, Obj b)
    {
        if (lean_obj_tag(a) != lean_obj_tag(b)) return 0;
        switch (lean_obj_tag(a))
        {
            case 0: return B(LeanSharp.Kernel.Name.StringEq(lean_ctor_get(a, 0), lean_ctor_get(b, 0)));
            case 1: return B(lean_ctor_get_uint8_s(a, 0) == lean_ctor_get_uint8_s(b, 0));
            case 2: return B(LeanSharp.Kernel.Name.Eq(lean_ctor_get(a, 0), lean_ctor_get(b, 0)));
            case 3: return B(lean_nat_to_big(lean_ctor_get(a, 0)) == lean_nat_to_big(lean_ctor_get(b, 0)));
        }
        return 0;
    }

    static byte DataValueBool(Obj v) => lean_ctor_get_uint8_s(v, 0);

    // ---------------------------------------------------------------- registration
    static void R(string name, void* f) => LeanExports.Register(name, (nint)f);

    public static void Register()
    {
        R("lean_name_mk_string", (delegate*<Obj, Obj, Obj>)&NameMkString);
        R("lean_name_mk_numeral", (delegate*<Obj, Obj, Obj>)&NameMkNumeral);
        R("lean_name_append_after", (delegate*<Obj, Obj, Obj>)&NameAppendAfter);
        R("lean_name_append_index_after", (delegate*<Obj, Obj, Obj>)&NameAppendIndexAfter);
        R("lean_level_mk_zero", (delegate*<Obj, Obj>)&LevelMkZero);
        R("lean_level_mk_succ", (delegate*<Obj, Obj>)&LevelMkSucc);
        R("lean_level_mk_max", (delegate*<Obj, Obj, Obj>)&LevelMkMax);
        R("lean_level_mk_imax", (delegate*<Obj, Obj, Obj>)&LevelMkIMax);
        R("lean_level_mk_param", (delegate*<Obj, Obj>)&LevelMkParam);
        R("lean_expr_mk_bvar", (delegate*<Obj, Obj>)&ExprMkBVar);
        R("lean_expr_mk_fvar", (delegate*<Obj, Obj>)&ExprMkFVar);
        R("lean_expr_mk_sort", (delegate*<Obj, Obj>)&ExprMkSort);
        R("lean_expr_mk_const", (delegate*<Obj, Obj, Obj>)&ExprMkConst);
        R("lean_expr_mk_app", (delegate*<Obj, Obj, Obj>)&ExprMkApp);
        R("lean_expr_mk_lambda", (delegate*<Obj, Obj, Obj, byte, Obj>)&ExprMkLambda);
        R("lean_expr_mk_forall", (delegate*<Obj, Obj, Obj, byte, Obj>)&ExprMkForall);
        R("lean_expr_mk_let", (delegate*<Obj, Obj, Obj, Obj, byte, Obj>)&ExprMkLet);
        R("lean_expr_mk_lit", (delegate*<Obj, Obj>)&ExprMkLit);
        R("lean_expr_mk_mdata", (delegate*<Obj, Obj, Obj>)&ExprMkMData);
        R("lean_expr_mk_proj", (delegate*<Obj, Obj, Obj, Obj>)&ExprMkProj);
        R("lean_lit_type", (delegate*<Obj, Obj>)&LitType);
        R("lean_expr_consume_type_annotations", (delegate*<Obj, Obj>)&ExprConsumeTypeAnnotations);
        R("lean_mk_empty_local_ctx", (delegate*<Obj, Obj>)&MkEmptyLocalCtx);
        R("lean_local_ctx_num_indices", (delegate*<Obj, Obj>)&LocalCtxNumIndices);
        R("lean_local_ctx_mk_local_decl", (delegate*<Obj, Obj, Obj, Obj, byte, Obj>)&LocalCtxMkLocalDecl);
        R("lean_local_ctx_mk_let_decl", (delegate*<Obj, Obj, Obj, Obj, Obj, byte, Obj>)&LocalCtxMkLetDecl);
        R("lean_local_ctx_find", (delegate*<Obj, Obj, Obj>)&LocalCtxFind);
        R("lean_environment_add", (delegate*<Obj, Obj, Obj>)&EnvironmentAdd);
        R("lean_environment_find", (delegate*<Obj, Obj, Obj>)&EnvironmentFind);
        R("lean_environment_mark_quot_init", (delegate*<Obj, Obj>)&EnvironmentMarkQuotInit);
        R("lean_environment_quot_init", (delegate*<Obj, byte>)&EnvironmentQuotInit);
        R("lean_kernel_record_unfold", (delegate*<Obj, Obj, Obj>)&KernelRecordUnfold);
        R("lean_kernel_get_diag", (delegate*<Obj, Obj>)&KernelGetDiag);
        R("lean_kernel_set_diag", (delegate*<Obj, Obj, Obj>)&KernelSetDiag);
        R("lean_kernel_diag_is_enabled", (delegate*<Obj, byte>)&KernelDiagIsEnabled);
        R("lean_elab_environment_to_kernel_env", (delegate*<Obj, Obj>)&ElabEnvironmentToKernelEnv);
        R("lean_elab_environment_update_base_after_kernel_add", (delegate*<Obj, Obj, Obj, Obj>)&ElabEnvironmentUpdateBaseAfterKernelAdd);
        R("lean_mk_definition_val", (delegate*<Obj, Obj, Obj, Obj, Obj, byte, Obj, Obj>)&MkDefinitionVal);
        R("lean_mk_quot_val", (delegate*<Obj, Obj, Obj, byte, Obj>)&MkQuotVal);
        R("lean_mk_inductive_val", (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, byte, byte, byte, Obj>)&MkInductiveVal);
        R("lean_mk_constructor_val", (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, byte, Obj>)&MkConstructorVal);
        R("lean_mk_recursor_val", (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, byte, byte, Obj>)&MkRecursorVal);
        R("lean_mk_inductive_decl", (delegate*<Obj, Obj, Obj, byte, Obj>)&MkInductiveDecl);
        R("lean_get_lmvar_assignment", (delegate*<Obj, Obj, Obj>)&GetLMVarAssignment);
        R("lean_assign_lmvar", (delegate*<Obj, Obj, Obj, Obj>)&AssignLMVar);
        R("lean_get_mvar_assignment", (delegate*<Obj, Obj, Obj>)&GetMVarAssignment);
        R("lean_get_delayed_mvar_assignment", (delegate*<Obj, Obj, Obj>)&GetDelayedMVarAssignment);
        R("lean_delayed_mvar_assignment_fvars", (delegate*<Obj, Obj>)&DelayedFVars);
        R("lean_delayed_mvar_assignment_mvar_id_pending", (delegate*<Obj, Obj>)&DelayedPending);
        R("lean_assign_mvar", (delegate*<Obj, Obj, Obj, Obj>)&AssignMVar);
        R("lean_data_value_beq", (delegate*<Obj, Obj, byte>)&DataValueBeq);
        R("lean_data_value_bool", (delegate*<Obj, byte>)&DataValueBool);
        // used only by the tests
        R("test_expr_mk_mvar", (delegate*<Obj, Obj>)&ExprMkMVar);
    }
}

internal static class RCx
{
    public static Obj Ctor(uint tag, params Obj[] fields)
    {
        var r = lean_alloc_ctor(tag, (uint)fields.Length, 0);
        for (int i = 0; i < fields.Length; i++) lean_ctor_set(r, (uint)i, fields[i]);
        return r;
    }
}
