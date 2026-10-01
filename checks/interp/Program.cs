// Check program for the IR interpreter (Interp area). IR declarations are built by hand as Lean
// objects; the Lean exports the interpreter needs are faked (Fakes.cs), and `M_Test_Mod` plays the
// role of a generated module class reached through the `LeanCompiledCode` hooks.

using System.Reflection;
using LeanSharp.Runtime;
using LeanSharp.Runtime.Interp;
using static LeanSharp.Runtime.LeanRt;
using static B;

static unsafe class Program
{
    static int s_pass, s_fail;
    static Obj s_env, s_opts, s_optsInterp;

    static void Check(bool cond, string what)
    {
        if (cond) s_pass++;
        else { s_fail++; Console.WriteLine("FAIL: " + what); }
    }

    static void Eq<T>(T actual, T expected, string what)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        if (ok) s_pass++;
        else { s_fail++; Console.WriteLine($"FAIL: {what}: expected {expected}, got {actual}"); }
    }

    static Obj Run(Obj opts, string f, params Obj[] args) =>
        IrInterpreter.RunBoxed(s_env, opts, IrName.Mk(f), args.Length, args);
    static Obj Run(string f, params Obj[] args) => Run(s_opts, f, args);
    static ulong RunNat(string f, params ulong[] args) => lean_unbox(Run(f, args.Select(a => lean_box(a)).ToArray()));

    static string ExceptError(Obj r) => r.m_tag == 0 ? lean_string_to_net(lean_ctor_get(r, 0)) : null;

    static void Main()
    {
        FakeEnv.Register();
        LeanCompiledCode.ModuleNames = () => new[] { "Test.Mod", "Lean.Environment" };
        LeanCompiledCode.ModuleClassResolver = m => m == "Test.Mod" ? typeof(M_Test_Mod) : m == "Lean.Environment" ? typeof(M_Lean_Environment) : null;
        LeanCompiledCode.InitializerResolver = m => m == "Test.Mod" ? (nint)(delegate*<byte, Obj>)&M_Test_Mod.initialize : 0;
        M_Test_Mod.initialize(1);

        s_env = FakeEnv.MkEnv();
        foreach (var n in new[] { "Nat.add", "Nat.sub", "Nat.mul", "Nat.decEq", "Test.len", "Test.triple", "Test.isZero",
                     "Test.callWith5", "Test.sum17", "Test.compiledConst", "Test.compiledField", "Test.byteConst", "Test.initField" })
            FakeEnv.ModuleOf[n] = 0;
        s_opts = FakeEnv.MkOpts(true);
        s_optsInterp = FakeEnv.MkOpts(false);

        DefineDecls();

        LeanInterpreterInit.RegisterOptions();
        Eq(IrName.ToString(FakeEnv.RegisteredOption), "interpreter.prefer_native", "option registered");
        Eq(lean_ctor_get_uint8_s(lean_ctor_get(FakeEnv.RegisteredOptionDecl, 2), 0), (byte)1, "option default true");

        TestMangling();
        TestArith();
        TestJoinPoints();
        TestRecursion();
        TestClosures();
        TestCompiledLookup();
        TestScalars();
        TestConstants();
        TestRC();
        TestExterns();
        TestEntryPoints();
        TestErrors();
        TestNoLeaks();
        MeasureIndex();

        Console.WriteLine($"interp checks: {s_pass} passed, {s_fail} failed");
        if (s_fail > 0) Environment.Exit(1);
    }

    // ------------------------------------------------------------------
    // IR declarations

    static void DefineDecls()
    {
        // externs (all with borrowed params, like `Nat.add`), implemented by `*___boxed` in M_Test_Mod
        Extern("Nat.add", new[] { P(1, O, true), P(2, O, true) }, O);
        Extern("Nat.sub", new[] { P(1, O, true), P(2, O, true) }, O);
        Extern("Nat.mul", new[] { P(1, O, true), P(2, O, true) }, O);
        Extern("Nat.decEq", new[] { P(1, O, true), P(2, O, true) }, U8);
        Extern("Test.len", new[] { P(1, O, true) }, O);
        Extern("Test.missingExtern", new[] { P(1, O) }, O);

        // add3 x y z = (x + y) + z
        Fun("Test.add3", new[] { P(1, O), P(2, O), P(3, O) }, O,
            VDecl(4, O, FAp("Nat.add", 1, 2),
            VDecl(5, O, FAp("Nat.add", 4, 3),
            Dec(4, Dec(1, Dec(2, Dec(3, Ret(5))))))));

        // sumTo n = Σ_{i<n} i, with a join point loop
        Fun("Test.sumTo", new[] { P(1, O) }, O,
            JDecl(2, new[] { P(3, O), P(4, O) },
                VDecl(5, U8, FAp("Nat.decEq", 4, 1),
                Case(5, U8,
                    Alt(False,
                        VDecl(6, O, Lit(1),
                        VDecl(7, O, FAp("Nat.add", 4, 6),
                        VDecl(8, O, FAp("Nat.add", 3, 4),
                        Jmp(2, 8, 7))))),
                    Default(Ret(3)))),
            VDecl(9, O, Lit(0), Jmp(2, 9, 9))));

        // loop n acc = if n == 0 then acc else loop (n-1) (acc+1)   (tail recursive)
        Fun("Test.loop", new[] { P(1, O), P(2, O) }, O,
            VDecl(3, O, Lit(0),
            VDecl(4, U8, FAp("Nat.decEq", 1, 3),
            Case(4, U8,
                Alt(False,
                    VDecl(5, O, Lit(1),
                    VDecl(6, O, FAp("Nat.sub", 1, 5),
                    VDecl(7, O, FAp("Nat.add", 2, 5),
                    VDecl(8, O, FAp("Test.loop", 6, 7),
                    Ret(8)))))),
                Alt(True, Ret(2))))));

        // deep n = if n == 0 then 0 else deep (n-1) + 1   (not tail recursive)
        Fun("Test.deep", new[] { P(1, O) }, O,
            VDecl(2, O, Lit(0),
            VDecl(3, U8, FAp("Nat.decEq", 1, 2),
            Case(3, U8,
                Alt(False,
                    VDecl(4, O, Lit(1),
                    VDecl(5, O, FAp("Nat.sub", 1, 4),
                    VDecl(6, O, FAp("Test.deep", 5),
                    VDecl(7, O, FAp("Nat.add", 6, 4),
                    Ret(7)))))),
                Default(Ret(2))))));

        // fact n = if n == 0 then 1 else n * fact (n - 1)
        Fun("Test.fact", new[] { P(1, O) }, O,
            VDecl(2, O, Lit(0),
            VDecl(3, U8, FAp("Nat.decEq", 1, 2),
            Case(3, U8,
                Alt(True, VDecl(4, O, Lit(1), Ret(4))),
                Alt(False,
                    VDecl(5, O, Lit(1),
                    VDecl(6, O, FAp("Nat.sub", 1, 5),
                    VDecl(7, O, FAp("Test.fact", 6),
                    VDecl(8, O, FAp("Nat.mul", 1, 7),
                    Ret(8))))))))));

        // closures
        Fun("Test.addN", new[] { P(1, O), P(2, O) }, O,
            VDecl(3, O, FAp("Nat.add", 1, 2), Dec(1, Dec(2, Ret(3)))));
        Fun("Test.mkAdder", new[] { P(1, O) }, O,
            VDecl(2, O, PAp("Test.addN", 1), Ret(2)));
        Fun("Test.applyTwice", new[] { P(1, O), P(2, O) }, O,
            Inc(1, 1, VDecl(3, O, Ap(1, 2), VDecl(4, O, Ap(1, 3), Ret(4)))));
        Fun("Test.useAdder", new[] { P(1, O) }, O,
            VDecl(2, O, FAp("Test.mkAdder", 1),
            VDecl(3, O, Lit(10),
            VDecl(4, O, FAp("Test.applyTwice", 2, 3), Ret(4)))));
        Fun("Test.tripleTwice", new[] { P(1, O) }, O,
            VDecl(2, O, PAp("Test.triple"),
            VDecl(3, O, FAp("Test.applyTwice", 2, 1), Ret(3))));
        Fun("Test.viaCompiled", new[] { P(1, O) }, O,
            VDecl(2, O, PAp("Test.addN", 1),
            VDecl(3, O, FAp("Test.callWith5", 2), Ret(3))));
        // over-application inside IR: `ap` with 2 args on a closure of arity 1 that returns a closure
        Fun("Test.overApply", new[] { P(1, O), P(2, O) }, O,
            VDecl(3, O, PAp("Test.mkAdder"),
            VDecl(4, O, Ap(3, 1, 2), Ret(4))));
        // under-application inside IR: pap with 1 of 3 args, then ap with the other 2
        Fun("Test.underApply", new[] { P(1, O) }, O,
            VDecl(2, O, PAp("Test.add3", 1),
            VDecl(3, O, Lit(20),
            VDecl(4, O, Lit(300),
            VDecl(5, O, Ap(2, 3, 4), Ret(5))))));
        // > 16 params
        var ps17 = Enumerable.Range(1, 17).Select(i => P(i, O)).ToArray();
        Fun("Test.sum17", ps17, O, Unreachable);
        Fun("Test.sum17._boxed", ps17.Select((_, i) => P(i + 1, O)).ToArray(), O, Unreachable);
        Fun("Test.callSum17", new[] { P(1, O) }, O,
            VDecl(2, O, FAp("Test.sum17", Enumerable.Repeat((object)1, 17).ToArray()), Ret(2)));
        // interpreted function with 17 params (stub closure through `Obj[]` stub)
        Fun("Test.first17", Enumerable.Range(1, 17).Select(i => P(i, O)).ToArray(), O, Ret(17));

        // compiled functions (IR bodies used only when not preferring native code)
        Fun("Test.triple", new[] { P(1, O) }, O,
            VDecl(2, O, FAp("Nat.add", 1, 1), VDecl(3, O, FAp("Nat.add", 2, 1), Ret(3))));
        Fun("Test.isZero", new[] { P(1, U8) }, U8, VDecl(2, U8, Lit(42), Ret(2)));
        Fun("Test.callWith5", new[] { P(1, O) }, O, Unreachable);
        Fun("Test.callIsZero", new[] { P(1, O) }, O,
            VDecl(2, U8, Unbox(1),
            VDecl(3, U8, FAp("Test.isZero", 2),
            VDecl(4, O, Box(U8, 3), Ret(4)))));

        // scalar fields, usize fields, floats and uint64
        var sInfo = CI("S.mk", 0, 1, 1, 9);
        Fun("Test.scalars", new[] { P(1, O), P(2, O), P(3, O) }, O,
            VDecl(4, F64, Unbox(1),
            VDecl(5, U64, Unbox(2),
            VDecl(6, O, Ctor(sInfo, 3),
            USet(6, 1, 5,
            SSet(6, 2, 0, 4, F64,
            VDecl(7, U8, Lit(200),
            SSet(6, 2, 8, 7, U8,
            VDecl(8, F64, SProj(2, 0, 6),
            VDecl(9, U8, SProj(2, 8, 6),
            VDecl(10, USz, UProj(1, 6),
            VDecl(11, O, Box(F64, 8),
            VDecl(12, O, Box(U8, 9),
            VDecl(13, O, Box(USz, 10),
            VDecl(14, F64, Lit(3),
            VDecl(15, O, Box(F64, 14),
            VDecl(16, U64, Lit(ulong.MaxValue),
            VDecl(17, O, Box(U64, 16),
            VDecl(18, O, Ctor(CI("T.mk", 0, 6), 11, 12, 13, 6, 15, 17),
            Dec(1, Dec(2, Ret(18))))))))))))))))))))));

        // constants
        Fun("Test.ten", new Obj[0], O, VDecl(1, O, Lit(10), Ret(1)));
        Fun("Test.strConst", new Obj[0], O, VDecl(1, O, LitStr("hello"), Ret(1)));
        Fun("Test.byteC", new Obj[0], U8, VDecl(1, U8, Lit(9), Ret(1)));
        Fun("Test.compiledConst", new Obj[0], O, Unreachable);
        Fun("Test.compiledField", new Obj[0], O, Unreachable);
        Fun("Test.byteConst", new Obj[0], U8, Unreachable);
        Fun("Test.useConsts", new Obj[0], O,
            VDecl(1, O, FAp("Test.compiledConst"),
            VDecl(2, O, FAp("Test.compiledField"),
            VDecl(3, U8, FAp("Test.byteConst"),
            VDecl(4, O, Box(U8, 3),
            VDecl(5, U8, FAp("Test.byteC"),
            VDecl(6, O, Box(U8, 5),
            VDecl(7, O, FAp("Test.strConst"),
            Inc(1, 1, Inc(2, 1, Inc(7, 1,
            VDecl(8, O, Ctor(CI("C.mk", 0, 5), 1, 2, 4, 6, 7), Ret(8)))))))))))));

        // reset/reuse: swap (a, b) = (b, a)
        var pair = CI("Prod.mk", 0, 2);
        Fun("Test.swap", new[] { P(1, O) }, O,
            VDecl(2, O, Proj(0, 1),
            VDecl(3, O, Proj(1, 1),
            Inc(2, 1, Inc(3, 1,
            VDecl(4, O, Reset(2, 1),
            VDecl(5, O, Reuse(4, pair, false, 3, 2), Ret(5))))))));
        Fun("Test.isShared", new[] { P(1, O, true) }, O,
            VDecl(2, U8, IsShared(1), VDecl(3, O, Box(U8, 2), Ret(3))));
        // set / setTag on a fresh object
        Fun("Test.setTag", new[] { P(1, O) }, O,
            VDecl(2, O, Ctor(CI("X.a", 0, 2), 1, 1),
            Inc(1, 1,
            VDecl(3, O, Lit(7),
            Set(2, 1, 3,
            SetTag(2, 3, Ret(2)))))));
        // borrowed param passed to boxed extern
        Fun("Test.callLen", new[] { P(1, O, true) }, O,
            VDecl(2, O, FAp("Test.len", 1), Ret(2)));
        Fun("Test.callMissing", new[] { P(1, O) }, O,
            VDecl(2, O, FAp("Test.missingExtern", 1), Ret(2)));
        Fun("Test.unknownCall", new[] { P(1, O) }, O,
            VDecl(2, O, FAp("Test.doesNotExist", 1), Ret(2)));
        Fun("Test.boom", new[] { P(1, O) }, O, Unreachable);
        Fun("Test.badConst", new Obj[0], O, Unreachable);
        Fun("Test.withSorry", new Obj[0], O, VDecl(1, O, Lit(1), Ret(1)));
        FakeEnv.SorryDeps["Test.withSorry"] = "Test.sorryDecl";

        // [init] declarations
        Fun("Test.mkRef", new[] { P(1, VoidT) }, O,
            VDecl(2, O, LitStr("initialized"),
            VDecl(3, O, Ctor(CI("EStateM.Result.ok", 0, 1), 2), Ret(3))));
        Fun("Test.myRef", new Obj[0], O, Unreachable);
        FakeEnv.InitAttr["Test.myRef"] = "Test.mkRef";
        Fun("Test.useRef", new Obj[0], O, VDecl(1, O, FAp("Test.myRef"), Inc(1, 1, Ret(1))));
        Fun("Test.initField", new Obj[0], O, Unreachable);
        FakeEnv.InitAttr["Test.initField"] = "Test.mkRef";
        Fun("Test.failingInit", new[] { P(1, VoidT) }, O,
            VDecl(2, O, FAp("Test.boom", 1), Ret(2)));
        Fun("Test.myRef2", new Obj[0], O, Unreachable);
        Fun("Test.useMyRef2", new Obj[0], O, VDecl(1, O, FAp("Test.myRef2"), Ret(1)));
        FakeEnv.InitAttr["Test.myRef2"] = "Test.failingInit";

        // main (args : List String) : IO UInt32 := return 42
        Fun("main", new[] { P(1, O), P(2, VoidT) }, O,
            VDecl(3, O, Lit(42),
            VDecl(4, O, Ctor(CI("EStateM.Result.ok", 0, 1), 3),
            Dec(1, Ret(4)))));
        // List String → IO UInt32
        Obj Const(string n)
        {
            var c = lean_alloc_ctor(4, 2, 8);
            lean_ctor_set(c, 0, IrName.Mk(n)); lean_ctor_set(c, 1, lean_box(0));
            return c;
        }
        var app = lean_alloc_ctor(5, 2, 8);
        lean_ctor_set(app, 0, Const("IO")); lean_ctor_set(app, 1, Const("UInt32"));
        var pi = lean_alloc_ctor(7, 3, 9);
        lean_ctor_set(pi, 0, IrName.Mk("args")); lean_ctor_set(pi, 1, Const("List")); lean_ctor_set(pi, 2, app);
        FakeEnv.MainType = pi;
    }

    // ------------------------------------------------------------------

    static void TestMangling()
    {
        Eq(LeanNameMangling.Mangle(IrName.Mk("Lean.Elab.Term.elabTerm")), "l_Lean_Elab_Term_elabTerm", "mangle simple");
        // `_private.Lean.Compiler.InitAttr.0.Lean.initFn._@.Lean.Compiler.InitAttr.3590725331._hygCtx._hyg.2`
        Obj n = IrName.Mk("_private.Lean.Compiler.InitAttr");
        n = IrName.MkNum(n, 0);
        n = IrName.MkStr(n, lean_mk_string("Lean")); n = IrName.MkStr(n, lean_mk_string("initFn"));
        n = IrName.MkStr(n, lean_mk_string("_@")); n = IrName.MkStr(n, lean_mk_string("Lean"));
        n = IrName.MkStr(n, lean_mk_string("Compiler")); n = IrName.MkStr(n, lean_mk_string("InitAttr"));
        n = IrName.MkNum(n, 3590725331); n = IrName.MkStr(n, lean_mk_string("_hygCtx"));
        n = IrName.MkStr(n, lean_mk_string("_hyg")); n = IrName.MkNum(n, 2);
        Eq(LeanNameMangling.Mangle(n),
            "l___private_Lean_Compiler_InitAttr_0__Lean_initFn_00___x40_Lean_Compiler_InitAttr_3590725331____hygCtx___hyg_2_",
            "mangle private/hygienic");
        Eq(LeanNameMangling.MkMangledBoxedName("l_Nat_add"), "l_Nat_add___boxed", "boxed name");
        Eq(LeanNameMangling.MkMangledBoxedName("l_foo_1__"), "l_foo_1___00__boxed", "boxed name after num");
        Eq(LeanNameMangling.ModuleInitStem("Init.Data.Option.Basic"), "Init_Data_Option_Basic", "module stem");
        Eq(LeanNameMangling.ModuleInitStem("Lean.Elab.Tactic.BVDecide.Frontend.BVDecide"), "Lean_Elab_Tactic_BVDecide_Frontend_BVDecide", "module stem 2");
        Eq(LeanNameMangling.Mangle(IrName.Mk("Nat.add._boxed")), "l_Nat_add___boxed", "boxed via name");
        Eq(LeanNameMangling.Mangle(IrName.Mk("String.utf8ByteSize")), "l_String_utf8ByteSize", "digits");
        Eq(LeanNameMangling.Mangle(IrName.Mk(new[] { "Std", "Format", "term_++_" })), "l_Std_Format_term___x2b_x2b__", "special chars");
        string longName = new string('a', 500);
        string sn = LeanNameMangling.ShortName(longName);
        Check(sn.Length < 430 && sn.StartsWith(new string('a', 400) + "_H"), "short name");
        Check(IrName.Eq(IrName.Mk("a.b"), IrName.Mk("a.b")) && !IrName.Eq(IrName.Mk("a.b"), IrName.Mk("a.c")), "name eq");
    }

    static void TestArith()
    {
        Eq(RunNat("Test.add3", 1, 20, 300), 321UL, "add3 via native Nat.add");
        Eq(RunNat("Test.fact", 10), 3628800UL, "fact 10");
    }

    static void TestJoinPoints()
    {
        Eq(RunNat("Test.sumTo", 100), 4950UL, "sumTo 100 (jdecl/jmp/case)");
        Eq(RunNat("Test.sumTo", 0), 0UL, "sumTo 0");
    }

    static void TestRecursion()
    {
        // tail recursion: must not grow the C# stack
        Eq(RunNat("Test.loop", 1_000_000, 0), 1_000_000UL, "tail-recursive loop 1e6");
        Eq(RunNat("Test.deep", 1000), 1000UL, "non-tail recursion 1000");
        // deep non-tail recursion on a small stack: stack check instead of a crash
        Exception caught = null;
        var th = new Thread(() =>
        {
            try { RunNat("Test.deep", 100_000_000); }
            catch (Exception e) { caught = e; }
        }, 1 << 20);
        th.Start(); th.Join();
        Check(caught is InterpreterStackOverflowException, "deep recursion -> InterpreterStackOverflowException, got " + caught?.GetType().Name);
        Check(caught?.Message.Contains("deep recursion was detected at 'interpreter'") == true && caught.Message.Contains("#1 Test.deep"), "stack overflow message");
    }

    static void TestClosures()
    {
        Eq(RunNat("Test.useAdder", 5), 20UL, "pap of interpreted fn + ap twice");
        Eq(RunNat("Test.tripleTwice", 2), 18UL, "pap of compiled fn + ap");
        Eq(RunNat("Test.viaCompiled", 2), 7UL, "interpreted closure called from compiled code");
        Eq(RunNat("Test.overApply", 3, 4), 7UL, "IR over-application");
        Eq(RunNat("Test.underApply", 1), 321UL, "IR under-application");

        // closures returned to "compiled" code
        Obj cls = Run("Test.mkAdder"); // zero-arg call_boxed: closure over a stub
        Check(lean_is_closure(cls), "call_boxed with 0 args returns a closure");
        Eq(lean_closure_arity(cls), 4u, "stub closure arity = 3 + params");
        Eq(lean_closure_num_fixed(cls), 3u, "stub closure fixed = env, opts, decl");
        lean_inc(cls);
        Eq(lean_unbox(lean_apply_2(cls, lean_box(3), lean_box(4))), 7UL, "over-application from compiled code");
        Obj c1 = lean_apply_1(cls, lean_box(10));
        Check(lean_is_closure(c1), "mkAdder 10 is a closure");
        Eq(lean_unbox(lean_apply_1(c1, lean_box(5))), 15UL, "apply interpreted closure");

        Obj add3 = Run("Test.add3");
        Obj p1 = lean_apply_1(add3, lean_box(1));
        Eq(lean_closure_num_fixed(p1), 4u, "under-application: fixed args");
        Eq(lean_unbox(lean_apply_2(p1, lean_box(20), lean_box(300))), 321UL, "under-application completed");

        // run_boxed with arguments (apply_n on the stub)
        Eq(lean_unbox(Run("Test.addN", lean_box(2), lean_box(3))), 5UL, "run_boxed with args");
        // over-application through run_boxed
        Eq(lean_unbox(Run("Test.mkAdder", lean_box(2), lean_box(3))), 5UL, "run_boxed over-application");

        // > 16 parameters
        Eq(RunNat("Test.callSum17", 2), 34UL, "compiled `_boxed` with Obj[] (17 params)");
        Obj f17 = Run("Test.first17");
        Eq(lean_closure_arity(f17), 20u, "stub closure with 20 params uses Obj[] stub");
        var args17 = Enumerable.Range(1, 17).Select(i => lean_box((ulong)i)).ToArray();
        Eq(lean_unbox(lean_apply_m(f17, 17, args17)), 17UL, "apply 17-arg interpreted closure");

        // stub closure called on another thread uses a fresh interpreter
        Obj c2 = Run("Test.mkAdder", lean_box(100));
        ulong r = 0;
        var th = new Thread(() => { r = lean_unbox(lean_apply_1(c2, lean_box(1))); });
        th.Start(); th.Join();
        Eq(r, 101UL, "stub closure on another thread");
    }

    static void TestCompiledLookup()
    {
        M_Test_Mod.TripleCalls = 0;
        Eq(RunNat("Test.triple", 7), 21UL, "compiled Test.triple");
        Eq(M_Test_Mod.TripleCalls, 1, "prefer_native=true calls compiled code");
        // like C++, the native symbol cache is global (keyed by declaration name only): a function
        // found as compiled code stays compiled; drop the cache to observe `prefer_native := false`
        LeanCompiledCode.Reset();
        Eq(lean_unbox(Run(s_optsInterp, "Test.triple", lean_box(7))), 21UL, "interpreted Test.triple");
        Eq(M_Test_Mod.TripleCalls, 1, "prefer_native=false interprets the IR");
        // externs are always native, even without prefer_native
        Eq(lean_unbox(Run(s_optsInterp, "Test.add3", lean_box(1), lean_box(2), lean_box(3))), 6UL, "externs native w/o prefer_native");

        // a declaration of the current module never uses compiled code, even if a compiled module
        // (not imported as such) has a symbol with the same name (e.g. the user's `main` vs Lake's)
        Obj env2 = FakeEnv.MkEnv();
        FakeEnv.LocalIn["Test.triple"] = lean_ctor_get(env2, 0);
        int calls = M_Test_Mod.TripleCalls;
        Eq(lean_unbox(IrInterpreter.RunBoxed(env2, s_opts, IrName.Mk("Test.triple"), 1, new[] { lean_box(7) })), 21UL, "local Test.triple");
        Eq(M_Test_Mod.TripleCalls, calls, "local declaration is interpreted despite matching compiled symbol");
        Eq(RunNat("Test.triple", 7), 21UL, "imported Test.triple");
        Eq(M_Test_Mod.TripleCalls, calls + 1, "same name imported from the compiled module in another env is native");
        Eq(lean_unbox(IrInterpreter.RunBoxed(env2, s_opts, IrName.Mk("Test.triple"), 1, new[] { lean_box(7) })), 21UL, "local Test.triple again");
        Eq(M_Test_Mod.TripleCalls, calls + 1, "global cache does not leak into the env where the name is local");
        // imported from a module that is not compiled: interpreted
        FakeEnv.ModuleOf["Test.triple"] = 1;
        Obj env3 = FakeEnv.MkEnv();
        Eq(lean_unbox(IrInterpreter.RunBoxed(env3, s_opts, IrName.Mk("Test.triple"), 1, new[] { lean_box(7) })), 21UL, "Test.triple from uncompiled module");
        Eq(M_Test_Mod.TripleCalls, calls + 1, "declaration of a non-compiled module is interpreted");
        FakeEnv.ModuleOf["Test.triple"] = 0;

        var m = LeanCompiledCode.Find("l_Test_triple");
        Check(m != null && m.Kind == CompiledMemberKind.Method, "index finds method");
        Check(LeanCompiledCode.Find("l_Test_compiledConst")?.Kind == CompiledMemberKind.Property, "index finds property");
        Check(LeanCompiledCode.Find("l_Test_compiledField")?.Kind == CompiledMemberKind.Field, "index finds field");
        Check(LeanCompiledCode.Find("l_Test_nope") == null, "index miss");
        Check(LeanCompiledCode.Find("TripleCalls")?.Kind == CompiledMemberKind.Field, "index public static field");
    }

    static void TestScalars()
    {
        Eq(lean_unbox(Run("Test.callIsZero", lean_box(0))), 1UL, "unboxed compiled fn via boxed version (0)");
        Eq(lean_unbox(Run("Test.callIsZero", lean_box(5))), 0UL, "unboxed compiled fn via boxed version (5)");
        LeanCompiledCode.Reset();
        Eq(lean_unbox(Run(s_optsInterp, "Test.callIsZero", lean_box(5))), 42UL, "interpreted unboxed fn");
        LeanCompiledCode.Reset();

        Obj f = InterpNum.BoxFloat(1.5);
        Obj u = InterpNum.BoxUInt64(1234567890123UL);
        Obj s = lean_mk_string("obj");
        Obj r = Run("Test.scalars", f, u, s);
        Eq(InterpNum.UnboxFloat(lean_ctor_get(r, 0)), 1.5, "sset/sproj float");
        Eq(lean_unbox(lean_ctor_get(r, 1)), 200UL, "sset/sproj u8");
        Eq(InterpNum.UnboxUInt64(lean_ctor_get(r, 2)), 1234567890123UL, "uset/uproj usize");
        Obj st = lean_ctor_get(r, 3);
        Check(ReferenceEquals(lean_ctor_get(st, 0), s), "struct object field");
        Eq(st.m_cs_sz, (ushort)17, "struct scalar size = 8*usize + ssize");
        Eq(lean_ctor_get_usize(st, 1), 1234567890123UL, "usize slot");
        Eq(lean_ctor_get_float_s(st, 8), 1.5, "float at scalar offset 8");
        Eq(lean_ctor_get_uint8_s(st, 16), (byte)200, "u8 at scalar offset 16");
        Eq(InterpNum.UnboxFloat(lean_ctor_get(r, 4)), 3.0, "float literal");
        Eq(InterpNum.UnboxUInt64(lean_ctor_get(r, 5)), ulong.MaxValue, "uint64 literal");
        lean_dec(r);
        Eq(s.m_rc, 0, "string freed with struct (rc reaches 0)");
    }

    static void TestConstants()
    {
        Obj r = lean_eval_const(s_env, s_opts, IrName.Mk("Test.ten"));
        Check(r.m_tag == 1 && lean_unbox(lean_ctor_get(r, 0)) == 10, "eval_const interpreted constant");

        Obj c = Run("Test.useConsts");
        Check(ReferenceEquals(lean_ctor_get(c, 0), M_Test_Mod.l_Test_compiledConst), "compiled property constant");
        Check(ReferenceEquals(lean_ctor_get(c, 1), M_Test_Mod.l_Test_compiledField), "compiled field constant");
        Eq(lean_unbox(lean_ctor_get(c, 2)), 77UL, "compiled byte property constant");
        Eq(lean_unbox(lean_ctor_get(c, 3)), 9UL, "interpreted scalar constant");
        Eq(lean_string_to_net(lean_ctor_get(c, 4)), "hello", "interpreted string constant");

        // RC of an interpreted constant: literal object owned by the IR, +1 for the result
        Obj lit = lean_ctor_get(lean_ctor_get(lean_ctor_get(FakeEnv.Decls["Test.strConst"], 3), 2), 0);
        lit = lean_ctor_get(lit, 0);
        int before = lit.m_rc;
        Obj v = Run("Test.strConst");
        Check(ReferenceEquals(v, lit), "constant value is the literal");
        Eq(lit.m_rc, before + 1, "constant cache released after interpretation; result owned");
        lean_dec(v);
        Eq(lit.m_rc, before, "constant rc restored");
        lean_dec(c);
    }

    static void TestRC()
    {
        // exclusive pair: reset/reuse updates in place
        Obj a = lean_mk_string("a"), b = lean_mk_string("b");
        Obj p = lean_mk_pair(a, b);
        Obj q = Run("Test.swap", p);
        Check(ReferenceEquals(p, q), "reuse of exclusive object");
        Check(ReferenceEquals(lean_ctor_get(q, 0), b) && ReferenceEquals(lean_ctor_get(q, 1), a), "swapped");
        Eq(a.m_rc, 1, "rc a after swap"); Eq(b.m_rc, 1, "rc b after swap");
        // shared pair: fresh allocation, original decremented
        lean_inc(q);
        Obj q2 = Run("Test.swap", q);
        Check(!ReferenceEquals(q, q2), "no reuse of shared object");
        Eq(q.m_rc, 1, "shared input decremented by reset");
        Eq(a.m_rc, 2, "fields shared by both pairs");
        lean_dec(q2);
        Eq(a.m_rc, 1, "rc a after dec");
        lean_dec(q);
        Eq(a.m_rc, 0, "rc a freed");

        Obj x = lean_mk_string("x");
        Eq(lean_unbox(Run("Test.isShared", x)), 0UL, "isShared exclusive");
        lean_inc(x);
        Eq(lean_unbox(Run("Test.isShared", x)), 1UL, "isShared shared");
        lean_dec(x); lean_dec(x);

        // set / setTag
        Obj y = lean_mk_string("y");
        Obj z = Run("Test.setTag", y);
        Eq(z.m_tag, (byte)3, "setTag");
        Check(ReferenceEquals(lean_ctor_get(z, 0), y) && lean_unbox(lean_ctor_get(z, 1)) == 7, "set");
        Eq(y.m_rc, 2, "rc after set (`set` does not release the overwritten field)");
        lean_dec(z);
        Eq(y.m_rc, 1, "rc after freeing the object");

        // borrowed parameter passed to a boxed extern wrapper: interpreter adds an inc
        Obj str = lean_mk_string("héllo");
        Obj len = Run("Test.callLen", str);
        Eq(lean_unbox(len), 5UL, "String length via extern");
        Eq(str.m_rc, 1, "borrowed param rc preserved across boxed call (stub owns it)");
        // via an interpreted caller with a borrowed param inside IR
        Fun("Test.callLen2", new[] { P(1, O) }, O,
            VDecl(2, O, FAp("Test.callLen", 1), Dec(1, Ret(2))));
        Obj str2 = lean_mk_string("abc");
        lean_inc(str2);
        Eq(lean_unbox(Run("Test.callLen2", str2)), 3UL, "nested borrowed call");
        Eq(str2.m_rc, 1, "rc after nested borrowed call");

        // closures keep env/opts alive and release them
        int envRc = s_env.m_rc, optsRc = s_opts.m_rc;
        Obj cls = Run("Test.mkAdder", lean_box(1));
        Eq(s_env.m_rc, envRc + 1, "stub closure holds env");
        Eq(lean_unbox(lean_apply_1(cls, lean_box(1))), 2UL, "consumed closure");
        Eq(s_env.m_rc, envRc, "env released by stub");
        Eq(s_opts.m_rc, optsRc, "opts released by stub");
    }

    static void TestExterns()
    {
        Obj r = lean_eval_const(s_env, s_opts, IrName.Mk("Test.ten"));
        lean_dec(r);
        // extern without compiled code
        Exception ex = null;
        try { Run("Test.callMissing", lean_box(1)); } catch (Exception e) { ex = e; }
        Check(ex is InterpreterException && ex.Message.Contains("Could not find native implementation of external declaration 'Test.missingExtern'")
              && ex.Message.Contains("l_Test_missingExtern___boxed"), "missing extern message: " + ex?.Message);
    }

    static void TestEntryPoints()
    {
        // run_mod_init_core
        int calls = M_Test_Mod.InitCalls;
        Obj r = lean_run_mod_init_core(lean_mk_string("initialize_Test_Mod"));
        Check(lean_io_result_is_ok(r) && lean_unbox(lean_io_result_get_value(r)) == 1, "run_mod_init_core found");
        Eq(M_Test_Mod.InitCalls, calls + 1, "initializer called");
        r = lean_run_mod_init_core(lean_mk_string("meta_initialize_Test_Mod"));
        Check(lean_io_result_is_ok(r) && lean_unbox(lean_io_result_get_value(r)) == 1, "run_mod_init_core meta_ prefix");
        r = lean_run_mod_init_core(lean_mk_string("initialize_Other_Mod"));
        Check(lean_io_result_is_ok(r) && lean_unbox(lean_io_result_get_value(r)) == 0, "run_mod_init_core missing");

        // run_init: value stored in the interpreter's globals
        r = lean_run_init(s_env, s_opts, IrName.Mk("Test.myRef"), IrName.Mk("Test.mkRef"));
        Check(lean_io_result_is_ok(r), "run_init ok");
        Obj v = Run("Test.useRef");
        Eq(lean_string_to_net(v), "initialized", "[init] value");
        Eq(v.m_rc, 0, "[init] value is persistent");
        Obj v2 = Run("Test.useRef");
        Check(ReferenceEquals(v, v2), "[init] value shared");
        // run_init for a decl with a compiled field: stored in the field
        r = lean_run_init(s_env, s_opts, IrName.Mk("Test.initField"), IrName.Mk("Test.mkRef"));
        Check(lean_io_result_is_ok(r) && M_Test_Mod.l_Test_initField != null &&
              lean_string_to_net(M_Test_Mod.l_Test_initField) == "initialized", "run_init stores into compiled field");
        // failing initializer -> IO error
        r = lean_run_init(s_env, s_opts, IrName.Mk("Test.myRef2"), IrName.Mk("Test.failingInit"));
        Check(lean_io_result_is_error(r) && lean_string_to_net(lean_ctor_get(lean_io_result_get_error(r), 0)) == "unreachable code", "run_init error");
        // [init] decl without value in the same module
        Obj e = lean_eval_const(s_env, s_opts, IrName.Mk("Test.useMyRef2"));
        Eq(ExceptError(e), "cannot evaluate `[init]` declaration 'Test.myRef2' in the same module", "eval [init] same module");

        // eval_main
        Obj args = MkList(new[] { lean_mk_string("a"), lean_mk_string("b") });
        uint ret = lean_eval_main(s_env, s_opts, args);
        Eq(ret, 42u, "eval_main IO UInt32");
        Eq(args.m_rc, 1, "eval_main args borrowed");
    }

    static void TestErrors()
    {
        Eq(ExceptError(lean_eval_const(s_env, s_opts, IrName.Mk("Test.nope"))), "(interpreter) unknown declaration 'Test.nope'", "unknown decl");
        Eq(ExceptError(lean_eval_const(s_env, s_opts, IrName.Mk("Test.withSorry"))),
            "cannot evaluate code because 'Test.sorryDecl' uses 'sorry' and/or contains errors", "sorry dep");
        string err = ExceptError(lean_eval_const(s_env, s_opts, IrName.Mk("Test.badConst")));
        Check(err != null && err.Contains("unreachable"), "unreachable constant: " + err);
        Exception ex = null;
        try { Run("Test.unknownCall", lean_box(1)); } catch (Exception e) { ex = e; }
        Eq(ex?.Message, "(interpreter) unknown declaration 'Test.doesNotExist'", "unknown callee");
        Eq(ExceptError(lean_eval_const(s_env, s_opts, IrName.Mk("Nat.add"))) != null ? "err" : "closure", "closure", "eval_const of extern fn returns closure");
        // the interpreter of the thread is consistent after an exception in nested code
        Eq(RunNat("Test.sumTo", 10), 45UL, "interpreter usable after errors");
    }

    static void TestNoLeaks()
    {
        // all interpreters disposed, all closures consumed: decls are back to their initial RC
        foreach (var kv in FakeEnv.Decls)
            if (kv.Value.m_rc != 1) { Check(false, $"decl {kv.Key} rc = {kv.Value.m_rc}"); return; }
        Check(true, "no decl leaks");
    }

    static void MeasureIndex()
    {
        // index all public static members of System.Private.CoreLib to estimate the cost of the
        // metadata-based index (the generated assembly has ~2,500 module classes)
        var oldAll = LeanCompiledCode.AllModuleClasses;
        var types = typeof(object).Assembly.GetTypes();
        LeanCompiledCode.AllModuleClasses = () => types;
        LeanCompiledCode.Reset();
        LeanCompiledCode.WarmUp();
        Console.WriteLine($"index: {LeanCompiledCode.IndexedMembers} members of {types.Length} CoreLib types in {LeanCompiledCode.IndexBuildTime.TotalMilliseconds:F1} ms");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var m = LeanCompiledCode.Find("IsNullOrEmpty");
        Console.WriteLine($"lookup + resolve: {sw.Elapsed.TotalMilliseconds:F3} ms ({m})");
        Check(m != null, "CoreLib lookup");
        LeanCompiledCode.AllModuleClasses = oldAll;
        LeanCompiledCode.Reset();
        Check(LeanCompiledCode.Find("l_Test_triple") != null, "index rebuilt after reset");
    }
}
