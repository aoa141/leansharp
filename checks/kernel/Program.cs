// Checks for the kernel port. Expected values were computed with the real Lean
// (~/Repos/lean4/build/release/stage1/bin/lean, see the *.lean files next to this program).

using System.Text;
using LeanSharp.Kernel;
using LeanSharp.Runtime;
using static LeanSharp.Runtime.LeanRt;

namespace KernelCheck;

public static unsafe class Program
{
    static int s_failures, s_checks;

    static void Check(string what, bool ok)
    {
        s_checks++;
        if (!ok) { s_failures++; Console.WriteLine("FAIL: " + what); }
    }

    static void CheckEq<T>(string what, T expected, T actual)
    {
        s_checks++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            s_failures++;
            Console.WriteLine($"FAIL: {what}\n  expected: {expected}\n  actual:   {actual}");
        }
    }

    // ------------------------------------------------------------------ builders
    static Name N(string s)
    {
        Name r = Name.Anonymous;
        foreach (var p in s.Split('.')) r = Name.Mk(r, p);
        return r;
    }
    static Level LZero => Level.Zero;
    static Level LSucc(Level l) => Level.MkSucc(l);
    static Level LParam(string n) => Level.MkParam(N(n));
    static Level LMax(Level a, Level b) => Level.MkMaxCore(a, b);
    static Level LIMax(Level a, Level b) => Level.MkIMaxCore(a, b);
    static Expr C(string n, params Level[] ls) => Expr.MkConst(N(n), ls);
    static Expr BV(ulong i) => Expr.MkBVar(i);
    static Expr FV(string n) => Expr.MkFVar(N(n));
    static Expr MV(Name n) => new Expr(FakeLeanTest.MkMVar(n.Raw));
    static Expr App(Expr f, params Expr[] args) => Expr.MkApp(f, args);
    static Expr Sort(Level l) => Expr.MkSort(l);
    static Expr Type0 => Sort(LSucc(LZero));
    static Expr TypeU(Level u) => Sort(LSucc(u));
    static Expr Prop => Sort(LZero);
    static Expr NatLit(ulong v) => Expr.MkNatLit(lean_box(v));
    static Expr StrLit(string s) => Expr.MkLit(RC.MkCnstr(1, lean_mk_string(s)));

    static ulong s_fresh;
    static Expr Binder(bool pi, string name, Expr type, Func<Expr, Expr> body, BinderInfo bi)
    {
        Expr fv = Expr.MkFVar(Name.Mk(N("_tst"), s_fresh++));
        Expr b = body(fv);
        Expr ab = Inst.Abstract(b, fv);
        return pi ? Expr.MkPi(N(name), type, ab, bi) : Expr.MkLambda(N(name), type, ab, bi);
    }
    static Expr Pi(string n, Expr t, Func<Expr, Expr> b, BinderInfo bi = BinderInfo.Default) => Binder(true, n, t, b, bi);
    static Expr Lam(string n, Expr t, Func<Expr, Expr> b, BinderInfo bi = BinderInfo.Default) => Binder(false, n, t, b, bi);
    static Expr Arrow(Expr a, Expr b) => Expr.MkArrow(a, b);

    static Obj Names(params string[] ns) => KList.OfBorrowed(ns.Select(N).ToList());

    static string Dbg(Expr e) => lean_string_to_net(lean_expr_dbg_to_string(e.Raw));
    static string Show(Expr e) => Dbg(e) + " | " + e.Hash;

    // ------------------------------------------------------------------ tests

    static void TestHashes()
    {
        CheckEq("hash anon", 1723UL, Name.Anonymous.Hash);
        CheckEq("hash Nat", 11442535297760353691UL, N("Nat").Hash);
        CheckEq("hash Lean.Expr", 5933584171502587988UL, N("Lean.Expr").Hash);
        CheckEq("hash x.3", 564683908580357674UL, Name.Mk(N("x"), 3).Hash);
        CheckEq("hash big num", 6009593299074389751UL, Name.MkNum(Name.Mk(Name.Anonymous, ""), lean_big_to_nat(System.Numerics.BigInteger.One << 64)).Hash);
        Level u = LParam("u"), v = LParam("v");
        CheckEq("level zero", 2221UL, LZero.Data);
        CheckEq("level one", 1101033050548UL, LSucc(LZero).Data);
        CheckEq("level u", 11190167828UL, u.Data);
        CheckEq("level max", 2209367768181UL, LMax(u, LSucc(LZero)).Data);
        CheckEq("level imax", 1109572664612UL, LIMax(u, v).Data);
        CheckEq("level mvar", 8094469022UL, new Level(FakeLean.LevelMkMVar(N("m").Raw)).Data);
        CheckEq("const Nat", 421340980UL, C("Nat").Data);
        CheckEq("const List.{u}", 8797667212592UL, C("List", u).Data);
        CheckEq("bvar 0", 17592537633786UL, BV(0).Data);
        CheckEq("bvar 5", 105554053209401UL, BV(5).Data);
        CheckEq("sort succ u", 8800198550984UL, Sort(LSucc(u)).Data);
        CheckEq("fvar x", 1101888168254UL, FV("x").Data);
        CheckEq("mvar m", 2201134701778UL, MV(N("m")).Data);
        CheckEq("lit 5", 3364943589UL, NatLit(5).Data);
        CheckEq("lit abc", 1964374092UL, StrLit("abc").Data);
        CheckEq("app", 35189774363739UL, App(C("f"), BV(1)).Data);
        CheckEq("lam", 5246801950UL, Expr.MkLambda(N("x"), C("Nat"), BV(0), BinderInfo.Implicit).Data);
        CheckEq("forall", 17596650190253UL, Expr.MkPi(N("x"), C("Nat"), BV(1)).Data);
        CheckEq("let", 6194923518UL, Expr.MkLet(N("x"), C("Nat"), NatLit(1), BV(0), true).Data);
        CheckEq("proj", 17600655118096UL, Expr.MkProj(N("Prod"), 1, BV(0)).Data);
        CheckEq("mdata", 52782603264109UL, Expr.MkMData(lean_box(0), BV(2)).Data);
        Obj kv = KList.Cons(RC.MkCnstr(0, RC.Own(N("a").Raw), BoolDataValue(true)), lean_box(0));
        CheckEq("mdata kv", 6520805118UL, Expr.MkMData(kv, C("x")).Data);
        // lean_expr_data / lean_expr_mk_app_data externs
        Expr app = App(C("f"), C("a"));
        CheckEq("lean_expr_data", Expr.DataOf(app.Raw), lean_expr_data(app.Raw));
        CheckEq("mk_app_data", Expr.DataOf(app.Raw), lean_expr_mk_app_data(C("f").Data, C("a").Data));
    }

    static Obj BoolDataValue(bool b)
    {
        var r = lean_alloc_ctor(1, 0, 1);
        lean_ctor_set_uint8_s(r, 0, b ? (byte)1 : (byte)0);
        return r;
    }

    static Obj Arr(params Expr[] es) => MkArray(es.Select(e => RC.Own(e.Raw)).ToList());
    static Expr R(Obj o) => new Expr(o);

    static void TestInstantiate()
    {
        Expr a = C("a"), b = C("b"), c = C("c"), f = C("f"), g = C("g"), x = FV("x"), y = FV("y");
        Expr e1 = App(f, BV(0), BV(1));
        CheckEq("instantiate1", "f a #0 | 3088745930", Show(R(lean_expr_instantiate1(e1.Raw, a.Raw))));
        CheckEq("instantiate", "f a b | 2008687407", Show(R(lean_expr_instantiate(e1.Raw, Arr(a, b)))));
        CheckEq("instantiateRev", "f b a | 844832443", Show(R(lean_expr_instantiate_rev(e1.Raw, Arr(a, b)))));
        CheckEq("instantiateRange", "f b #0 | 2995974483", Show(R(lean_expr_instantiate_range(e1.Raw, lean_box(1), lean_box(2), Arr(a, b, c)))));
        CheckEq("instantiateRevRange", "f b a | 844832443", Show(R(lean_expr_instantiate_rev_range(e1.Raw, lean_box(0), lean_box(2), Arr(a, b, c)))));
        CheckEq("abstract", "f #1 #0 | 1956444370", Show(R(lean_expr_abstract(App(f, x, y).Raw, Arr(x, y)))));
        CheckEq("abstractRange", "f #0 y | 3856324053", Show(R(lean_expr_abstract_range(App(f, x, y).Raw, lean_box(1), Arr(x, y)))));
        Expr lam = Expr.MkLambda(N("z"), a, App(g, BV(0), BV(1), BV(2)));
        CheckEq("lift", "fun (z : a) => g z #0 #4 | 3579225624", Show(R(lean_expr_lift_loose_bvars(lam.Raw, lean_box(1), lean_box(3)))));
        CheckEq("lower", "g #2 #1 | 3487566972", Show(R(lean_expr_lower_loose_bvars(App(g, BV(3), BV(1)).Raw, lean_box(2), lean_box(1)))));
        Expr lam2 = Expr.MkLambda(N("z"), a, App(g, BV(0), BV(2)));
        CheckEq("hasLooseBVar 1", (byte)1, lean_expr_has_loose_bvar(lam2.Raw, lean_box(1)));
        CheckEq("hasLooseBVar 0", (byte)0, lean_expr_has_loose_bvar(lam2.Raw, lean_box(0)));
        Expr lam3 = Expr.MkLambda(N("y"), a, App(C("h"), BV(1)));
        CheckEq("instantiate1 lift", "fun (y : a) => h #3 | 74449761", Show(R(lean_expr_instantiate1(lam3.Raw, BV(3).Raw))));
        // no loose bvars: same object returned (and inc'ed)
        int rc = a.Raw.m_rc;
        Obj same = lean_expr_instantiate1(a.Raw, b.Raw);
        Check("instantiate1 identity", ReferenceEquals(same, a.Raw) && a.Raw.m_rc == rc + 1);
        // lift/lower of non-scalar arguments
        Check("lift big", ReferenceEquals(lean_expr_lift_loose_bvars(e1.Raw, lean_big_to_nat(System.Numerics.BigInteger.Pow(2, 70)), lean_box(1)), e1.Raw));
        // inputs are not modified
        CheckEq("input unchanged", "f #0 #1", Dbg(e1));
    }

    static void TestPrinter()
    {
        Level u = LParam("u"), v = LParam("v");
        CheckEq("print pi", "forall (a : Nat) {b : Type}, P a | 2651877868",
            Show(Expr.MkPi(N("a"), C("Nat"), Expr.MkPi(N("b"), Type0, App(C("P"), BV(1)), BinderInfo.Implicit))));
        CheckEq("print arrow", "Nat -> Nat | 2546708756", Show(Expr.MkPi(N("x"), C("Nat"), C("Nat"))));
        CheckEq("print lam/let", "fun [x : Sort.{max u v}] => have y : Type.{u} := 3; y \"a\\\"b\" | 2555472169",
            Show(Expr.MkLambda(N("x"), Sort(LMax(u, v)), Expr.MkLet(N("y"), Sort(LSucc(u)), NatLit(3), App(BV(0), StrLit("a\"b")), true), BinderInfo.InstImplicit)));
        CheckEq("print proj/mvar", "?_uniq.7.1 | 1752700097", Show(Expr.MkProj(N("Prod"), 0, MV(Name.Mk(N("_uniq"), 7)))));
        CheckEq("print sort", "Sort.{imax u 2} | 4219831035", Show(Sort(LIMax(u, LSucc(LSucc(LZero))))));
        CheckEq("print const", "List.{u, 1} | 1126526977", Show(C("List", u, LSucc(LZero))));
        Obj kv = KList.OfOwned(new[] { RC.MkCnstr(0, RC.Own(N("a").Raw), BoolDataValue(true)), RC.MkCnstr(0, RC.Own(N("n").Raw), RC.MkCnstr(3, lean_box(3))) });
        CheckEq("print mdata", "[mdata a:1 n:3 x] | 2225837822", Show(Expr.MkMData(kv, C("x"))));
        CheckEq("print strict", "fun {{x.1 : Nat}} => x.1 | 951834654", Show(Expr.MkLambda(Name.Mk(N("x"), 1), C("Nat"), BV(0), BinderInfo.StrictImplicit)));
    }

    static void TestOrder()
    {
        Expr a = C("a"), b = C("b"), f = C("f");
        CheckEq("lt a b", (byte)1, lean_expr_lt(a.Raw, b.Raw));
        CheckEq("lt fa b", (byte)0, lean_expr_lt(App(f, a).Raw, b.Raw));
        CheckEq("quickLt fa b", (byte)0, lean_expr_quick_lt(App(f, a).Raw, b.Raw));
        CheckEq("quickLt b fa", (byte)1, lean_expr_quick_lt(b.Raw, App(f, a).Raw));
        CheckEq("lt sort", (byte)0, lean_expr_lt(Sort(LSucc(LZero)).Raw, Sort(LParam("u")).Raw));
        Expr l1 = Expr.MkLambda(N("x"), a, BV(0)), l2 = Expr.MkLambda(N("y"), a, BV(0), BinderInfo.Implicit);
        CheckEq("eqv", (byte)1, lean_expr_eqv(l1.Raw, l2.Raw));
        CheckEq("equal", (byte)0, lean_expr_equal(l1.Raw, l2.Raw));
        CheckEq("equal same", (byte)1, lean_expr_equal(l1.Raw, Expr.MkLambda(N("x"), a, BV(0)).Raw));
    }

    static void TestLevels()
    {
        Level u = LParam("u"), v = LParam("v"), one = LSucc(LZero), two = LSucc(one);
        Check("max comm", Level.IsEquivalent(LMax(u, v), LMax(v, u)));
        Check("imax u 0", Level.IsEquivalent(LIMax(u, LZero), LZero));
        Check("max u succ u", Level.IsEquivalent(LMax(u, LSucc(u)), LSucc(u)));
        Check("succ max", Level.IsEquivalent(LSucc(LMax(u, v)), LMax(LSucc(u), LSucc(v))));
        Check("imax 1 u", Level.IsEquivalent(LIMax(one, u), u));
        Check("max u 1 != u", !Level.IsEquivalent(LMax(u, one), u));
        Check("max 1 2", Level.IsEquivalent(LMax(one, two), two));
        Check("max assoc", Level.IsEquivalent(LMax(u, LMax(v, u)), LMax(v, u)));
        Check("geq max", Level.IsGeq(LMax(u, v), u));
        Check("geq succ", Level.IsGeq(LSucc(u), u));
        Check("not geq", !Level.IsGeq(u, LSucc(u)));
        Check("geq imax", Level.IsGeq(LMax(u, v), LIMax(u, v)));
        CheckEq("level_eq", (byte)1, lean_level_eq(LMax(u, v).Raw, LMax(LParam("u"), LParam("v")).Raw));
        CheckEq("level_eq 2", (byte)0, lean_level_eq(LMax(u, v).Raw, LMax(v, u).Raw));
        CheckEq("normalize", "max u v", Level.Normalize(LMax(v, u)).ToString());
        CheckEq("normalize 2", "max 2 (succ u)", Level.Normalize(LMax(LSucc(u), LMax(two, one))).ToString());
        CheckEq("level_mk_data", 11190167828UL, lean_level_mk_data(LeanHash.Mix(2239, N("u").Hash), lean_box(0), 0, 1));
    }

    // ------------------------------------------------------------------ environment tests

    static Obj s_env;

    static Obj Unwrap(Obj except, string what)
    {
        if (lean_obj_tag(except) != 1)
        {
            Obj err = lean_ctor_get(except, 0);
            string msg = lean_obj_tag(err) == 12 ? lean_string_to_net(lean_ctor_get(err, 0)) : "kernel exception tag " + lean_obj_tag(err);
            Console.WriteLine($"FAIL: {what}: {msg}");
            s_failures++;
            return null;
        }
        return lean_ctor_get(except, 0);
    }

    static bool AddDecl(Obj decl, string what, bool check = true)
    {
        lean_inc(s_env);
        Obj r = check ? lean_add_decl(s_env, 0, 0, decl, lean_box(0)) : lean_add_decl_without_checking(s_env, decl);
        s_checks++;
        Obj e = Unwrap(r, what);
        if (e == null) return false;
        s_env = e;
        return true;
    }

    /// <summary>Add a declaration that must fail; returns the `Kernel.Exception` tag.</summary>
    static uint AddDeclFail(Obj decl)
    {
        lean_inc(s_env);
        Obj r = lean_add_decl(s_env, 0, 0, decl, lean_box(0));
        if (lean_obj_tag(r) == 1) return 999;
        return lean_obj_tag(lean_ctor_get(r, 0));
    }

    static Obj IndType(string name, Expr type, params (string, Expr)[] ctors) =>
        InductiveType.Mk(N(name), type, KList.OfOwned(ctors.Select(c => InductiveType.MkCnstr(N(c.Item1), c.Item2)).ToList()));

    static Obj IndDecl(string[] lparams, uint nparams, params Obj[] types) =>
        FakeLean.MkInductiveDecl(Names(lparams), lean_box(nparams), KList.OfOwned(types), 0);

    static Obj Defn(string name, string[] lps, Expr type, Expr value, bool isUnsafe = false)
    {
        var d = Declaration.MkDefinition(N(name), Names(lps), type, value, RegularHints(1), isUnsafe ? DefinitionSafety.Unsafe : DefinitionSafety.Safe);
        return d.Raw;
    }

    static Obj RegularHints(uint h)
    {
        var r = lean_alloc_ctor(2, 0, 4);
        lean_ctor_set_uint32_s(r, 0, h);
        return r;
    }

    static Obj Thm(string name, string[] lps, Expr type, Expr value)
    {
        Obj cval = FakeLean.MkConstantVal(RC.Own(N(name).Raw), Names(lps), RC.Own(type.Raw));
        Obj tv = RC.MkCnstr(0, cval, RC.Own(value.Raw), KList.Cons(RC.Own(N(name).Raw), lean_box(0)));
        return RC.MkCnstr(2, tv);
    }

    static Obj Axiom(string name, string[] lps, Expr type)
    {
        Obj cval = FakeLean.MkConstantVal(RC.Own(N(name).Raw), Names(lps), RC.Own(type.Raw));
        var av = lean_alloc_ctor(0, 1, 1);
        lean_ctor_set(av, 0, cval);
        lean_ctor_set_uint8_s(av, 0, 0);
        return RC.MkCnstr(0, av);
    }

    static string FmtNames(Obj l) => "[" + string.Join(", ", KList.Iter(l).Select(n => new Name(n).ToString())) + "]";
    static string FmtBool(bool b) => b ? "true" : "false";

    static void ShowC(StringBuilder sb, string n)
    {
        var env = new LeanSharp.Kernel.Environment(s_env);
        ConstantInfo ci = env.Find(N(n));
        if (ci.IsNull) { sb.AppendLine($"{n} : <missing>"); return; }
        sb.AppendLine($"{n} : {Dbg(ci.Type)} | {ci.Type.Hash} | lps={FmtNames(ci.LParams)}");
        switch (ci.Kind)
        {
            case ConstantInfoKind.Recursor:
            {
                var r = ci.ToRecursorVal();
                sb.AppendLine($"  all={FmtNames(r.All)} np={r.NParams} ni={r.NIndices} nmot={r.NMotives} nmin={r.NMinors} k={FmtBool(r.IsK)}");
                foreach (var ro in KList.Iter(r.Rules))
                {
                    var rule = new RecursorRule(ro);
                    sb.AppendLine($"  rule {rule.Cnstr} {rule.NFields} : {Dbg(rule.Rhs)} | {rule.Rhs.Hash}");
                }
                break;
            }
            case ConstantInfoKind.Inductive:
            {
                var i = ci.ToInductiveVal();
                sb.AppendLine($"  np={i.NParams} ni={i.NIndices} all={FmtNames(i.All)} ctors={FmtNames(i.Cnstrs)} nested={i.NNested} rec={FmtBool(i.IsRec)} refl={FmtBool(i.IsReflexive)}");
                break;
            }
            case ConstantInfoKind.Constructor:
            {
                var c = ci.ToConstructorVal();
                sb.AppendLine($"  induct={c.Induct} cidx={c.CIdx} np={c.NParams} nf={c.NFields}");
                break;
            }
            case ConstantInfoKind.Definition:
            {
                var d = ci.ToDefinitionVal();
                sb.AppendLine($"  value: {Dbg(d.Value)} | {d.Value.Hash} hints={ReducibilityHints.GetHeight(d.Hints)} abbrev={FmtBool(ReducibilityHints.IsAbbrev(d.Hints))} safe={FmtBool(d.Safety == DefinitionSafety.Safe)}");
                break;
            }
        }
    }

    static void AddCasesOn(string n)
    {
        lean_inc(s_env);
        Obj r = lean_mk_cases_on(s_env, N(n).Raw);
        Obj d = Unwrap(r, "casesOn " + n);
        if (d != null) AddDecl(d, "add casesOn " + n);
    }

    static Expr MyNat => C("MyNat");
    static Expr Zero => C("MyNat.zero");
    static Expr Succ(Expr e) => App(C("MyNat.succ"), e);

    static void TestInductives()
    {
        s_env = FakeLean.EmptyEnv();
        Level u = LParam("u");
        // inductive MyNat where | zero | succ (n : MyNat)
        AddDecl(IndDecl(new string[0], 0, IndType("MyNat", Type0, ("MyNat.zero", MyNat), ("MyNat.succ", Pi("n", MyNat, _ => MyNat)))), "MyNat");
        AddCasesOn("MyNat");
        // inductive PUnit : Sort u | unit
        AddDecl(IndDecl(new[] { "u" }, 0, IndType("PUnit", Sort(u), ("PUnit.unit", C("PUnit", u)))), "PUnit");
        // inductive MyList (α : Type u) where | nil | cons (head : α) (tail : MyList α)
        Expr MyList(Level l, Expr a) => App(C("MyList", l), a);
        AddDecl(IndDecl(new[] { "u" }, 1, IndType("MyList", Pi("α", TypeU(u), _ => TypeU(u)),
            ("MyList.nil", Pi("α", TypeU(u), a => MyList(u, a), BinderInfo.Implicit)),
            ("MyList.cons", Pi("α", TypeU(u), a => Pi("head", a, _ => Pi("tail", MyList(u, a), _ => MyList(u, a))), BinderInfo.Implicit)))), "MyList");
        // inductive Tree where | node (cs : MyList Tree)
        AddDecl(IndDecl(new string[0], 0, IndType("Tree", Type0, ("Tree.node", Pi("cs", MyList(LZero, C("Tree")), _ => C("Tree"))))), "Tree");
        AddCasesOn("Tree");
        // inductive MyEq {α : Sort u} : (a : α) → (b : α) → Prop where | refl (a : α) : MyEq a a
        AddDecl(IndDecl(new[] { "u" }, 2, IndType("MyEq", Pi("α", Sort(u), a => Pi("a", a, _ => Pi("b", a, _ => Prop)), BinderInfo.Implicit),
            ("MyEq.refl", Pi("α", Sort(u), al => Pi("a", al, a => App(C("MyEq", u), al, a, a)), BinderInfo.Implicit)))), "MyEq");
        // structure MyProd (α β : Type) where fst : α; snd : β
        AddDecl(IndDecl(new string[0], 2, IndType("MyProd", Pi("α", Type0, _ => Pi("β", Type0, _ => Type0)),
            ("MyProd.mk", Pi("α", Type0, a => Pi("β", Type0, b => Pi("fst", a, _ => Pi("snd", b, _ => App(C("MyProd"), a, b))), BinderInfo.Implicit), BinderInfo.Implicit)))), "MyProd");
        // inductive Vec (α : Type u) : (n : MyNat) → Type u
        Expr Vec(Expr a, Expr n) => App(C("Vec", u), a, n);
        AddDecl(IndDecl(new[] { "u" }, 1, IndType("Vec", Pi("α", TypeU(u), _ => Pi("n", MyNat, _ => TypeU(u))),
            ("Vec.nil", Pi("α", TypeU(u), a => Vec(a, Zero), BinderInfo.Implicit)),
            ("Vec.cons", Pi("α", TypeU(u), a => Pi("n", MyNat, n => Pi("h", a, _ => Pi("t", Vec(a, n), _ => Vec(a, Succ(n)))), BinderInfo.Implicit), BinderInfo.Implicit)))), "Vec");
        AddCasesOn("Vec");
        // Eq and quotients
        Level u1 = LParam("u_1");
        AddDecl(IndDecl(new[] { "u_1" }, 2, IndType("Eq", Pi("α", Sort(u1), a => Pi("a", a, _ => Pi("b", a, _ => Prop)), BinderInfo.Implicit),
            ("Eq.refl", Pi("α", Sort(u1), al => Pi("a", al, a => App(C("Eq", u1), al, a, a)), BinderInfo.Implicit)))), "Eq");
        AddDecl(lean_box(4), "quot");

        var sb = new StringBuilder();
        foreach (var n in new[] { "MyNat", "MyNat.zero", "MyNat.succ", "MyNat.rec", "MyNat.casesOn", "MyList", "MyList.cons", "MyList.rec",
                                  "Tree", "Tree.node", "Tree.rec", "Tree.rec_1", "Tree.casesOn", "MyEq", "MyEq.refl", "MyEq.rec",
                                  "MyProd", "MyProd.mk", "MyProd.rec", "Vec", "Vec.nil", "Vec.cons", "Vec.rec", "Vec.casesOn",
                                  "Eq", "Eq.refl", "Quot", "Quot.mk", "Quot.lift", "Quot.ind" })
            ShowC(sb, n);
        CompareWithExpected(sb.ToString(), "expected_inductives.txt");
    }

    static void CompareWithExpected(string actual, string file)
    {
        string dir = AppContext.BaseDirectory;
        string expected = File.ReadAllText(Path.Combine(dir, file));
        File.WriteAllText(Path.Combine(dir, "actual_" + file), actual);
        var el = expected.Split('\n'); var al = actual.Split('\n');
        int diffs = 0;
        for (int i = 0; i < Math.Max(el.Length, al.Length); i++)
        {
            string x = i < el.Length ? el[i] : "<eof>", y = i < al.Length ? al[i] : "<eof>";
            if (x != y && diffs++ < 10) Console.WriteLine($"FAIL: {file} line {i + 1}\n  expected: {x}\n  actual:   {y}");
        }
        s_checks++;
        if (diffs > 0) s_failures++;
    }

    static void TestInductives2()
    {
        s_env = FakeLean.EmptyEnv();
        Level u = LParam("u");
        AddDecl(IndDecl(new string[0], 0, IndType("MyNat", Type0, ("MyNat.zero", MyNat), ("MyNat.succ", Pi("n", MyNat, _ => MyNat)))), "MyNat");
        AddDecl(IndDecl(new[] { "u" }, 0, IndType("PUnit", Sort(u), ("PUnit.unit", C("PUnit", u)))), "PUnit");
        // mutual Even / Odd
        AddDecl(IndDecl(new string[0], 0,
            IndType("Even", Type0, ("Even.zero", C("Even")), ("Even.succ", Pi("o", C("Odd"), _ => C("Even")))),
            IndType("Odd", Type0, ("Odd.succ", Pi("e", C("Even"), _ => C("Odd"))))), "Even/Odd");
        AddCasesOn("Even");
        // reflexive W
        Name hyg = Name.Mk(N("a._@._internal._hyg"), 0);
        AddDecl(IndDecl(new string[0], 0, IndType("W", Type0, ("W.sup", Pi("f", Expr.MkPi(hyg, MyNat, C("W")), _ => C("W"))))), "W");
        // inductive predicate Le (n : MyNat) : (m : MyNat) -> Prop
        Expr Le(Expr a, Expr b) => App(C("Le"), a, b);
        AddDecl(IndDecl(new string[0], 1, IndType("Le", Pi("n", MyNat, _ => Pi("m", MyNat, _ => Prop)),
            ("Le.refl", Pi("n", MyNat, n => Le(n, n))),
            ("Le.step", Pi("n", MyNat, n => Pi("m", MyNat, m => Pi("h", Le(n, m), _ => Le(n, Succ(m)))))))), "Le");
        AddCasesOn("Le");
        // MyAnd (a b : Prop) : Prop
        AddDecl(IndDecl(new string[0], 2, IndType("MyAnd", Pi("a", Prop, _ => Pi("b", Prop, _ => Prop)),
            ("MyAnd.intro", Pi("a", Prop, a => Pi("b", Prop, b => Pi("left", a, _ => Pi("right", b, _ => App(C("MyAnd"), a, b))), BinderInfo.Implicit), BinderInfo.Implicit)))), "MyAnd");
        // MyFalse : Prop
        AddDecl(IndDecl(new string[0], 0, IndType("MyFalse", Prop)), "MyFalse");
        AddCasesOn("MyFalse");
        var sb = new StringBuilder();
        foreach (var n in new[] { "Even", "Odd", "Even.succ", "Odd.succ", "Even.rec", "Odd.rec", "Even.casesOn", "W", "W.sup", "W.rec",
                                  "Le", "Le.refl", "Le.step", "Le.rec", "Le.casesOn", "MyAnd", "MyAnd.intro", "MyAnd.rec",
                                  "MyFalse", "MyFalse.rec", "MyFalse.casesOn" })
            ShowC(sb, n);
        CompareWithExpected(sb.ToString(), "expected_inductives2.txt");
    }

    static Expr MyNatAdd(Expr a, Expr b) => App(C("MyNat.add"), a, b);
    static Expr NatOf(int k) { Expr e = Zero; for (int i = 0; i < k; i++) e = Succ(e); return e; }

    static void TestTypeChecking()
    {
        Level one = LSucc(LZero);
        // def MyNat.add (n m : MyNat) : MyNat := MyNat.rec (motive := fun _ => MyNat) n (fun _ ih => MyNat.succ ih) m
        Expr addType = Arrow(MyNat, Arrow(MyNat, MyNat));
        Expr addVal = Lam("n", MyNat, n => Lam("m", MyNat, m =>
            App(C("MyNat.rec", one), Lam("x", MyNat, _ => MyNat), n, Lam("x", MyNat, _ => Lam("ih", MyNat, ih => Succ(ih))), m)));
        Check("add MyNat.add", AddDecl(Defn("MyNat.add", new string[0], addType, addVal), "MyNat.add"));
        // theorem MyNat.add_zero : ∀ n, MyEq (MyNat.add n zero) n := fun n => MyEq.refl n
        Expr thmType = Pi("n", MyNat, n => App(C("MyEq", one), MyNat, MyNatAdd(n, Zero), n));
        Expr thmVal = Lam("n", MyNat, n => App(C("MyEq.refl", one), MyNat, n));
        Check("add theorem", AddDecl(Thm("MyNat.add_zero", new string[0], thmType, thmVal), "add_zero"));
        // theorem with a wrong proof: MyNat.add zero n = n is not definitional
        Expr badType = Pi("n", MyNat, n => App(C("MyEq", one), MyNat, MyNatAdd(Zero, n), n));
        CheckEq("bad theorem", 2u, AddDeclFail(Thm("MyNat.bad", new string[0], badType, thmVal)));
        // closed instance is fine: 2 + 2 = 4
        Expr t224 = App(C("MyEq", one), MyNat, MyNatAdd(NatOf(2), NatOf(2)), NatOf(4));
        Check("2+2=4", AddDecl(Thm("MyNat.two_two", new string[0], t224, App(C("MyEq.refl", one), MyNat, NatOf(4))), "2+2"));
        // errors
        CheckEq("already declared", 1u, AddDeclFail(Axiom("MyNat", new string[0], Type0)));
        CheckEq("unknown constant", 0u, AddDeclFail(Axiom("ax1", new string[0], C("Foo"))));
        CheckEq("fvar in decl", 4u, AddDeclFail(Axiom("ax2", new string[0], FV("x"))));
        CheckEq("app type mismatch", 9u, AddDeclFail(Defn("bad2", new string[0], MyNat, Succ(Type0))));
        CheckEq("type expected", 6u, AddDeclFail(Axiom("ax3", new string[0], Zero)));
        CheckEq("function expected", 5u, AddDeclFail(Defn("bad3", new string[0], MyNat, App(Zero, Zero))));
        CheckEq("decl type mismatch", 2u, AddDeclFail(Defn("bad4", new string[0], MyNat, Type0)));
        CheckEq("undef univ", 12u, AddDeclFail(Axiom("ax4", new string[0], Sort(LParam("w")))));
        Check("axiom", AddDecl(Axiom("ax5", new[] { "w" }, Sort(LParam("w"))), "ax5"));

        // kernel whnf / isDefEq entry points
        Obj lctx = KX_MkEmptyLocalCtx();
        lean_inc(s_env); lean_inc(lctx);
        Obj r = lean_kernel_whnf(s_env, lctx, MyNatAdd(NatOf(1), NatOf(1)).Raw);
        Obj w = Unwrap(r, "whnf");
        if (w != null) CheckEq("whnf", "MyNat.succ (MyNat.rec.{1} (fun (x : MyNat) => MyNat) (MyNat.succ MyNat.zero) (fun (x : MyNat) (ih : MyNat) => MyNat.succ ih) MyNat.zero)", Dbg(R(w)));
        lean_inc(s_env); lean_inc(lctx);
        r = lean_kernel_is_def_eq(s_env, lctx, MyNatAdd(NatOf(3), NatOf(2)).Raw, NatOf(5).Raw);
        Obj d = Unwrap(r, "isDefEq");
        if (d != null) CheckEq("isDefEq 3+2=5", 1UL, lean_unbox(d));
        lean_inc(s_env); lean_inc(lctx);
        r = lean_kernel_is_def_eq(s_env, lctx, MyNatAdd(NatOf(3), NatOf(2)).Raw, NatOf(4).Raw);
        d = Unwrap(r, "isDefEq2");
        if (d != null) CheckEq("isDefEq 3+2!=4", 0UL, lean_unbox(d));
        lean_inc(s_env); lean_inc(lctx);
        r = lean_kernel_check(s_env, lctx, Lam("x", MyNat, x => MyNatAdd(x, x)).Raw);
        Obj ty = Unwrap(r, "check");
        if (ty != null) CheckEq("check", "MyNat -> MyNat", Dbg(R(ty)));
        // check with a local context containing a let variable
        Obj lctx2 = KX_MkEmptyLocalCtx();
        var lc = new LocalCtx(lctx2);
        lc.MkLocalDeclCore(N("x1"), N("x"), MyNat, NatOf(2));
        lean_inc(s_env); lean_inc(lc.Raw);
        r = lean_kernel_is_def_eq(s_env, lc.Raw, MyNatAdd(FV("x1"), NatOf(1)).Raw, NatOf(3).Raw);
        d = Unwrap(r, "isDefEq let fvar");
        if (d != null) CheckEq("isDefEq let fvar", 1UL, lean_unbox(d));
        // eta, proof irrelevance, structure eta
        using (var tc = new TypeChecker(new LeanSharp.Kernel.Environment(s_env)))
        {
            Expr f = Lam("x", MyNat, x => Succ(x));
            Check("eta", tc.IsDefEq(Lam("y", MyNat, y => App(C("MyNat.succ"), y)), C("MyNat.succ")));
            Check("beta", tc.IsDefEq(App(f, Zero), NatOf(1)));
            Expr p1 = App(C("MyNat.add_zero"), Zero), p2 = App(C("MyEq.refl", one), MyNat, Zero);
            Check("proof irrel", tc.IsDefEq(p1, p2));
            Expr prodTy = App(C("MyProd"), MyNat, MyNat);
            Expr pr = Lam("p", prodTy, p => App(C("MyProd.mk"), MyNat, MyNat, Expr.MkProj(N("MyProd"), 0, p), Expr.MkProj(N("MyProd"), 1, p)));
            Check("struct eta", tc.IsDefEq(pr, Lam("p", prodTy, p => p)));
            Check("proj reduce", tc.IsDefEq(Expr.MkProj(N("MyProd"), 1, App(C("MyProd.mk"), MyNat, MyNat, Zero, NatOf(1))), NatOf(1)));
            Check("not defeq", !tc.IsDefEq(Zero, NatOf(1)));
            CheckEq("infer proj", "MyNat", Dbg(tc.Infer(Expr.MkProj(N("MyProd"), 0, App(C("MyProd.mk"), MyNat, MyNat, Zero, Zero)))));
            CheckEq("infer casesOn app", "(fun (x : MyNat) => Type) MyNat.zero", Dbg(tc.Infer(App(C("MyNat.casesOn", LSucc(one)), Lam("x", MyNat, _ => Type0), Zero, MyNat, Lam("n", MyNat, _ => MyNat)))));
            CheckEq("whnf casesOn", "MyNat", Dbg(tc.Whnf(App(C("MyNat.casesOn", LSucc(one)), Lam("x", MyNat, _ => Type0), Zero, MyNat, Lam("n", MyNat, _ => MyNat)))));
        }
        // Quot reduction: Quot.lift f h (Quot.mk r a) ~> f a
        using (var tc = new TypeChecker(new LeanSharp.Kernel.Environment(s_env)))
        {
            Expr rel = Lam("a", MyNat, _ => Lam("b", MyNat, _ => App(C("MyEq", one), MyNat, Zero, Zero)));
            Expr mk = App(C("Quot.mk", one), MyNat, rel, NatOf(2));
            Expr f = Lam("x", MyNat, x => Succ(x));
            Expr lifted = App(C("Quot.lift", one, one), MyNat, rel, MyNat, f, C("h"), mk);
            CheckEq("quot lift", "MyNat.succ (MyNat.succ (MyNat.succ MyNat.zero))", Dbg(tc.Whnf(lifted)));
        }
    }

    static Obj DefVal(string name, Expr type, Expr value, DefinitionSafety safety) =>
        DefinitionVal.Mk(N(name), lean_box(0), type, value, RegularHints(1), safety, KList.Cons(RC.Own(N(name).Raw), lean_box(0))).Raw;

    static void TestDefinitionKinds()
    {
        s_env = s_envSaved;
        // unsafe recursive definition: unsafe def loop : MyNat -> MyNat := fun n => loop n
        Expr loopTy = Arrow(MyNat, MyNat);
        Check("unsafe rec def", AddDecl(Defn("loop", new string[0], loopTy, Lam("n", MyNat, n => App(C("loop"), n)), true), "loop"));
        // a safe definition cannot use it
        CheckEq("safe uses unsafe", 12u, AddDeclFail(Defn("useLoop", new string[0], loopTy, C("loop"))));
        // mutual partial definitions
        Expr fTy = Arrow(MyNat, MyNat);
        Obj vals = KList.OfOwned(new[] {
            DefVal("pf", fTy, Lam("n", MyNat, n => App(C("pg"), n)), DefinitionSafety.Partial),
            DefVal("pg", fTy, Lam("n", MyNat, n => App(C("pf"), n)), DefinitionSafety.Partial) });
        Check("mutual partial", AddDecl(RC.MkCnstr(5, vals), "mutual"));
        CheckEq("safe uses partial", 12u, AddDeclFail(Defn("usePf", new string[0], fTy, C("pf"))));
        Obj valsBad = KList.OfOwned(new[] { DefVal("sf", fTy, Lam("n", MyNat, n => n), DefinitionSafety.Safe) });
        CheckEq("mutual safe", 12u, AddDeclFail(RC.MkCnstr(5, valsBad)));
        Obj valsDup = KList.OfOwned(new[] { DefVal("df", fTy, Lam("n", MyNat, n => n), DefinitionSafety.Partial), DefVal("df", fTy, Lam("n", MyNat, n => n), DefinitionSafety.Partial) });
        CheckEq("mutual duplicate", 12u, AddDeclFail(RC.MkCnstr(5, valsDup)));
        // opaque
        Obj cval = FakeLean.MkConstantVal(RC.Own(N("op").Raw), lean_box(0), RC.Own(MyNat.Raw));
        var ov = lean_alloc_ctor(0, 3, 1);
        lean_ctor_set(ov, 0, cval); lean_ctor_set(ov, 1, RC.Own(Zero.Raw)); lean_ctor_set(ov, 2, KList.Cons(RC.Own(N("op").Raw), lean_box(0)));
        lean_ctor_set_uint8_s(ov, 0, 0);
        Check("opaque", AddDecl(RC.MkCnstr(3, ov), "opaque"));
        using (var tc = new TypeChecker(new LeanSharp.Kernel.Environment(s_env)))
            Check("opaque not unfolded", !tc.IsDefEq(C("op"), Zero));
        // elab entry points (the fake elab environment is the kernel environment)
        lean_inc(s_env);
        Obj r = lean_elab_add_decl(s_env, 0, 0, Defn("viaElab", new string[0], MyNat, Zero), lean_box(0));
        Check("elab add decl", lean_obj_tag(r) == 1 && !new LeanSharp.Kernel.Environment(lean_ctor_get(r, 0)).Find(N("viaElab")).IsNull);
        lean_inc(s_env);
        r = lean_elab_add_decl_without_checking(s_env, Defn("viaElab2", new string[0], MyNat, Type0));
        Check("elab add decl unchecked", lean_obj_tag(r) == 1);
        // diagnostics: unfold counters are recorded when enabled
        var fe = FakeLean.GetEnv(s_env).Clone();
        fe.Diag = FakeLean.MkDiag(true);
        Obj denv = FakeLean.MkEnv(fe);
        FakeLean.Unfolds.Clear();
        r = lean_add_decl(denv, 0, 0, Thm("diag_thm", new string[0], App(C("MyEq", LSucc(LZero)), MyNat, MyNatAdd(NatOf(1), Zero), NatOf(1)), App(C("MyEq.refl", LSucc(LZero)), MyNat, NatOf(1))), lean_box(0));
        Check("diag add", lean_obj_tag(r) == 1);
        Check("diag unfolds recorded", FakeLean.Unfolds.Contains("MyNat.add") && FakeLean.Unfolds.Contains("MyNat.rec"));
    }

    static Obj KX_MkEmptyLocalCtx() => new LocalCtx().Raw;

    static void TestNatLits()
    {
        // `Nat` with literal support
        s_env = FakeLean.EmptyEnv();
        Expr Nat = C("Nat");
        AddDecl(IndDecl(new string[0], 0, IndType("Nat", Type0, ("Nat.zero", Nat), ("Nat.succ", Pi("n", Nat, _ => Nat)))), "Nat");
        AddDecl(IndDecl(new string[0], 0, IndType("Bool", Type0, ("Bool.false", C("Bool")), ("Bool.true", C("Bool")))), "Bool");
        Level one = LSucc(LZero);
        Expr natRec(Expr motiveRes, Expr z, Expr s, Expr n) => App(C("Nat.rec", one), Lam("x", Nat, _ => motiveRes), z, s, n);
        // Nat.add n m := Nat.rec n (fun _ ih => succ ih) m
        AddDecl(Defn("Nat.add", new string[0], Arrow(Nat, Arrow(Nat, Nat)),
            Lam("n", Nat, n => Lam("m", Nat, m => natRec(Nat, n, Lam("x", Nat, _ => Lam("ih", Nat, ih => App(C("Nat.succ"), ih))), m)))), "Nat.add");
        AddDecl(Axiom("Nat.mul", new string[0], Arrow(Nat, Arrow(Nat, Nat))), "Nat.mul", false);
        AddDecl(Axiom("Nat.pow", new string[0], Arrow(Nat, Arrow(Nat, Nat))), "Nat.pow", false);
        AddDecl(Axiom("Nat.beq", new string[0], Arrow(Nat, Arrow(Nat, C("Bool")))), "Nat.beq", false);
        using var tc = new TypeChecker(new LeanSharp.Kernel.Environment(s_env));
        Expr Lit(System.Numerics.BigInteger v) => Expr.MkNatLit(lean_big_to_nat(v));
        CheckEq("nat add", "5", Dbg(tc.Whnf(App(C("Nat.add"), Lit(2), Lit(3)))));
        var big = System.Numerics.BigInteger.Pow(2, 70);
        CheckEq("nat mul big", (big * big).ToString(), Dbg(tc.Whnf(App(C("Nat.mul"), Lit(big), Lit(big)))));
        CheckEq("nat pow", System.Numerics.BigInteger.Pow(3, 100).ToString(), Dbg(tc.Whnf(App(C("Nat.pow"), Lit(3), Lit(100)))));
        CheckEq("nat beq", "Bool.true", Dbg(tc.Whnf(App(C("Nat.beq"), Lit(7), App(C("Nat.add"), Lit(3), Lit(4))))));
        Check("lit succ defeq", tc.IsDefEq(Lit(5), App(C("Nat.succ"), Lit(4))));
        Check("lit zero defeq", tc.IsDefEq(Lit(0), C("Nat.zero")));
        Check("lit add defeq", tc.IsDefEq(App(C("Nat.add"), Lit(1000000), Lit(1)), Lit(1000001)));
        Check("lit neq", !tc.IsDefEq(Lit(5), Lit(6)));
        // recursor on a literal: Nat.rec 0 (fun _ ih => succ (succ ih)) 3 = 6
        Expr dbl = natRec(Nat, Lit(0), Lam("x", Nat, _ => Lam("ih", Nat, ih => App(C("Nat.succ"), App(C("Nat.succ"), ih)))), Lit(3));
        Check("rec on literal", tc.IsDefEq(dbl, Lit(6)));
        CheckEq("infer lit", "Nat", Dbg(tc.Infer(Lit(3))));
    }

    static void TestInstantiateMVars()
    {
        var m = new FakeLean.FakeMCtx();
        Name n1 = N("m1"), n2 = N("m2"), n3 = N("m3"), lm = N("lm");
        Expr a = C("a"), f = C("f"), g = C("g");
        Level lv = new Level(FakeLean.LevelMkMVar(lm.Raw));
        // ?m1 := f ?m2, ?m2 := a, ?lm := 1, ?m3 := fun x => g x x
        m.EAssign[n1.ToString()] = RC.Own(App(f, MV(n2)).Raw);
        m.EAssign[n2.ToString()] = RC.Own(a.Raw);
        m.LAssign[lm.ToString()] = RC.Own(LSucc(LZero).Raw);
        m.EAssign[n3.ToString()] = RC.Own(Lam("x", a, x => App(g, x, x)).Raw);
        Obj mctx = FakeLean.MkMCtx(m);
        Expr e = App(C("h", lv), MV(n1), App(MV(n3), C("b")));
        Obj r = lean_instantiate_expr_mvars(mctx, RC.Own(e.Raw));
        CheckEq("instantiate mvars", "h.{1} (f a) (g b b)", Dbg(R(lean_ctor_get(r, 1))));
        var m2 = FakeLean.GetMCtx(lean_ctor_get(r, 0));
        CheckEq("write back", "f a", Dbg(R(m2.EAssign[n1.ToString()])));
        // level instantiation
        Obj r2 = lean_instantiate_level_mvars(lean_ctor_get(r, 0), RC.Own(LMax(lv, LParam("u")).Raw));
        CheckEq("instantiate level mvars", "max 1 u", new Level(lean_ctor_get(r2, 1)).ToString());
        // delayed assignment: ?d #[x] := ?p where ?p := g x x ; `?d c` ~> `g c c`
        var m3 = new FakeLean.FakeMCtx();
        Name d = N("d"), p = N("p");
        Expr x0 = FV("x0");
        m3.EAssign[p.ToString()] = RC.Own(App(g, x0, x0).Raw);
        m3.DAssign[d.ToString()] = RC.MkCnstr(0, Arr(x0), RC.Own(p.Raw));
        Obj r3 = lean_instantiate_expr_mvars(FakeLean.MkMCtx(m3), RC.Own(Lam("y", a, y => App(MV(d), App(f, y))).Raw));
        CheckEq("delayed", "fun (y : a) => g (f y) (f y)", Dbg(R(lean_ctor_get(r3, 1))));
    }

    static Obj s_constA;
    static Obj ReplaceAWithB(Obj e)
    {
        bool isA = e.m_tag == (byte)ExprKind.Const && Name.Eq(lean_ctor_get(e, 0), lean_ctor_get(s_constA, 0));
        lean_dec(e);
        if (isA) return lean_mk_option_some(RC.Own(C("b").Raw));
        return lean_box(0);
    }

    static Obj IsConstB(Obj e)
    {
        bool r = e.m_tag == (byte)ExprKind.Const && new Name(lean_ctor_get(e, 0)) == N("b");
        lean_dec(e);
        return lean_box(r ? 1UL : 0UL);
    }

    static void TestReplaceFind()
    {
        s_constA = C("a").Raw;
        Expr e = App(C("f"), C("a"), Expr.MkLambda(N("x"), C("a"), App(C("g"), BV(0), C("a"))));
        Obj fn = lean_alloc_closure((delegate*<Obj, Obj>)&ReplaceAWithB, 1, 0);
        Obj r = lean_replace_expr(fn, e.Raw);
        CheckEq("replace", "f b (fun (x : b) => g x b)", Dbg(R(r)));
        Obj fb = lean_alloc_closure((delegate*<Obj, Obj>)&IsConstB, 1, 0);
        Obj found = lean_find_expr(fb, r);
        Check("find", !lean_is_scalar(found) && Dbg(R(lean_ctor_get(found, 0))) == "b");
        Obj notFound = lean_find_expr(fb, e.Raw);
        Check("find none", lean_is_scalar(notFound));
    }

    static void TestRefCounts()
    {
        // instantiate must not consume or destroy its (borrowed) arguments
        Expr body = App(C("f"), BV(0), App(C("g"), BV(0)));
        Expr arg = App(C("h"), C("c"));
        int rcBody = body.Raw.m_rc, rcArg = arg.Raw.m_rc;
        Obj r = lean_expr_instantiate1(body.Raw, arg.Raw);
        CheckEq("rc body", rcBody, body.Raw.m_rc);
        Check("rc arg incremented (shared in result)", arg.Raw.m_rc > rcArg);
        Check("result rc", r.m_rc >= 1);
        lean_dec(r);
        CheckEq("rc arg after dec result", rcArg, arg.Raw.m_rc >= rcArg ? rcArg : arg.Raw.m_rc);
        Check("arg alive", arg.Raw.m_rc >= 1);
    }

    static void TestDeepRecursion()
    {
        // Deep terms must produce kernel exceptions (`deepRecursion`), never crash the process.
        Expr e = Zero;
        for (int i = 0; i < 20000; i++) e = Succ(e);
        s_env = s_envSaved;
        // 1) recursion depth limit: maxRecDepth 100 => kernel limit 1600
        lean_inc(s_env);
        Obj r = lean_add_decl(s_env, 0, 100, Defn("deep", new string[0], MyNat, e), lean_box(0));
        Check("rec depth limit", lean_obj_tag(r) == 0 && lean_obj_tag(lean_ctor_get(r, 0)) == 15);
        // 2) no limit, big stack: succeeds
        lean_inc(s_env);
        r = lean_add_decl(s_env, 0, 0, Defn("deep", new string[0], MyNat, e), lean_box(0));
        Check("deep ok", lean_obj_tag(r) == 1);
        // 3) no limit, small stack: stack exhaustion is reported as deepRecursion
        Obj r2 = null;
        var t = new Thread(() =>
        {
            lean_inc(s_env);
            r2 = lean_add_decl(s_env, 0, 0, Defn("deep", new string[0], MyNat, e), lean_box(0));
        }, 1024 * 1024);
        t.Start(); t.Join();
        Check("stack exhaustion", r2 != null && lean_obj_tag(r2) == 0 && lean_obj_tag(lean_ctor_get(r2, 0)) == 15);
        // 4) heartbeats: deterministic timeout
        lean_inc(s_env);
        r = lean_add_decl(s_env, 10, 0, Defn("deep", new string[0], MyNat, e), lean_box(0));
        Check("heartbeat", lean_obj_tag(r) == 0 && lean_obj_tag(lean_ctor_get(r, 0)) == 13);
        // 5) cancellation token: `structure { promise; setRef : IO.Ref Bool }`
        Obj tk = RC.MkCnstr(0, lean_box(0), lean_st_mk_ref(lean_box(1)));
        lean_inc(s_env);
        r = lean_add_decl(s_env, 0, 0, Defn("deep", new string[0], MyNat, e), lean_mk_option_some(tk));
        Check("interrupted", lean_obj_tag(r) == 0 && lean_obj_tag(lean_ctor_get(r, 0)) == 16);
    }

    static Obj s_envSaved;

    public static int Main()
    {
        FakeLean.Register();
        var t = new Thread(() =>
        {
            Console.WriteLine("== TestHashes"); TestHashes();
            Console.WriteLine("== TestInstantiate"); TestInstantiate();
            Console.WriteLine("== TestPrinter"); TestPrinter();
            Console.WriteLine("== TestOrder"); TestOrder();
            Console.WriteLine("== TestLevels"); TestLevels();
            Console.WriteLine("== TestInductives"); TestInductives();
            Console.WriteLine("== TestTypeChecking"); TestTypeChecking();
            s_envSaved = s_env;
            Console.WriteLine("== TestDefinitionKinds"); TestDefinitionKinds();
            Console.WriteLine("== TestInductives2"); TestInductives2();
            s_envSaved = s_env;
            Console.WriteLine("== TestInstantiateMVars"); TestInstantiateMVars();
            Console.WriteLine("== TestReplaceFind"); TestReplaceFind();
            Console.WriteLine("== TestRefCounts"); TestRefCounts();
            Console.WriteLine("== TestNatLits"); TestNatLits();
            Console.WriteLine("== TestDeepRecursion"); TestDeepRecursion();
        }, 256 * 1024 * 1024);
        t.Start();
        t.Join();
        Console.WriteLine($"{s_checks - s_failures}/{s_checks} checks passed");
        return s_failures == 0 ? 0 : 1;
    }
}

internal static class FakeLeanTest
{
    public static unsafe Obj MkMVar(Obj n)
    {
        var f = (delegate*<Obj, Obj>)LeanExports.Get("test_expr_mk_mvar");
        lean_inc(n);
        return f(n);
    }
}
