// Externs of the IR interpreter (library/ir_interpreter.cpp): `Lean.Environment.evalConstCore`,
// `Lean.runInit`, `Lean.runModInitCore` and `Lean.runMain`.

using LeanSharp.Runtime.Interp;
using static LeanSharp.Runtime.LeanRt;

namespace LeanSharp.Runtime;

public static unsafe partial class LeanRt
{
    /// <summary>`evalConstCore (α) (env : @&amp; Environment) (opts : @&amp; Options) (constName : @&amp; Name) : Except String α`.</summary>
    public static Obj lean_eval_const(Obj env, Obj opts, Obj c)
    {
        try
        {
            Obj v = IrInterpreter.RunBoxed(env, opts, c, 0, null);
            var r = lean_alloc_ctor(1, 1, 0);
            lean_ctor_set(r, 0, v);
            return r;
        }
        catch (InterpreterException ex)
        {
            var r = lean_alloc_ctor(0, 1, 0);
            lean_ctor_set(r, 0, lean_mk_string(ex.Message));
            return r;
        }
    }

    /// <summary>`runInit (env : @&amp; Environment) (opts : @&amp; Options) (decl initDecl : @&amp; Name) : IO Unit`.
    /// Run the initializer `initDecl` for `decl` and store its value for global access.</summary>
    public static Obj lean_run_init(Obj env, Obj opts, Obj decl, Obj initDecl)
    {
        return IrInterpreter.With(env, opts, interp => interp.RunInit(decl, initDecl));
    }

    /// <summary>`runModInitCore (sym : @&amp; String) : IO Bool`: run the initializer of a compiled
    /// module given the C symbol of its initialization function; `false` if not available.</summary>
    public static Obj lean_run_mod_init_core(Obj sym)
    {
        delegate*<byte, Obj> init = LeanCompiledCode.FindModuleInitializer(lean_string_to_net(sym));
        if (init != null)
        {
            byte builtin = 0;
            Obj r = init(builtin);
            if (lean_io_result_is_ok(r))
            {
                lean_dec_ref(r);
                return lean_io_result_mk_ok(lean_box(1));
            }
            return r;
        }
        return lean_io_result_mk_ok(lean_box(0));
    }

    /// <summary>`runMain (env : @&amp; Environment) (opts : @&amp; Options) (args : @&amp; List String) : BaseIO UInt32`.</summary>
    public static uint lean_eval_main(Obj env, Obj opts, Obj args)
    {
        // An executable "linked" by the managed toolchain is run by `lean --run`. Its native
        // counterpart runs the initializers of all its modules, including `builtin_initialize`
        // blocks, before `main`; the launcher asks for the same with this variable.
        var proc = LeanContext.Proc;
        if (InterpretedBuiltinInitHook != null && proc.GetEnv("LEANSHARP_RUN_BUILTIN_INIT") == "1")
        {
            Obj r = RunWithIoInitializing(() => InterpretedBuiltinInitHook(env, opts));
            if (lean_io_result_is_error(r))
            {
                lean_io_result_show_error(r);
                lean_dec_ref(r);
                return 1;
            }
            lean_dec_ref(r);
        }
        return IrInterpreter.With(env, opts, interp => interp.RunMain(args));
    }

    /// <summary>
    /// Set by the host: runs the `[builtin_init]` declarations of the modules of `env` that have
    /// no compiled code and whose builtin initializers have not run in this program yet
    /// (arguments borrowed; returns an `IO Unit` result).
    /// </summary>
    public static Func<Obj, Obj, Obj> InterpretedBuiltinInitHook;

    /// <summary>
    /// Set by the host: "initializes" a stub library written by the managed toolchain when it
    /// is loaded as a plugin, i.e. imports its modules and runs their initializers with the
    /// interpreter (returns an `IO Unit` result).
    /// </summary>
    public static Func<string, Obj> ManagedPluginInitHook;

    /// <summary>Runs `f` with `IO.initializing` true for the current program (as while a native module initializer runs).</summary>
    public static Obj RunWithIoInitializing(Func<Obj> f)
    {
        var proc = LeanContext.Proc;
        bool saved = proc.IoInitializing;
        proc.IoInitializing = true;
        try { return f(); }
        finally { proc.IoInitializing = saved; }
    }

    /// <summary>Runs the `[builtin_init]`/`[init]` declaration `decl` of type `IO Unit` with the interpreter (arguments borrowed).</summary>
    public static Obj lean_run_init_unit(Obj env, Obj opts, Obj decl)
    {
        return IrInterpreter.With(env, opts, interp =>
        {
            try { return interp.CallBoxed(decl, 1, new Obj[] { lean_io_mk_world() }); }
            catch (InterpreterException ex) { return InterpExports.IoResultMkError(ex.Message); }
        });
    }

    /// <summary>Whether the interpreter has already run the initializer of the constant `decl` in this program.</summary>
    public static bool InterpreterHasInitialized(Obj decl) => IrInterpreter.HasInitGlobal(decl);

    /// <summary>The Lean `Name` with the given dot-separated components.</summary>
    public static Obj MkNameFromDotted(string dotted) => IrName.Mk(dotted);

    /// <summary>`Name.toString` without escaping.</summary>
    public static string NameToDotted(Obj name) => IrName.ToString(name);
}

/// <summary>Process-level setup of the interpreter (C++ `initialize_ir_interpreter`).</summary>
public static unsafe class LeanInterpreterInit
{
    /// <summary>Register the `interpreter.prefer_native` option (`register_bool_option`). Must be
    /// called while initializing (after the compiled `Lean` modules are initialized and before
    /// `lean_io_mark_end_initialization`), like the C++ static initializer.</summary>
    public static void RegisterOptions()
    {
        Obj name = IrName.Mk("interpreter", "prefer_native");
        // DataValue.ofBool (ctor 1 with a `Bool` scalar)
        Obj defValue = lean_alloc_ctor(1, 0, 1);
        lean_ctor_set_uint8_s(defValue, 0, IrInterpreter.DefaultPreferNative ? (byte)1 : (byte)0);
        // OptionDecl { name, declName := anonymous, defValue, descr, deprecation? := none }
        Obj decl = lean_alloc_ctor(0, 5, 0);
        lean_inc(name);
        lean_ctor_set(decl, 0, name);
        lean_ctor_set(decl, 1, lean_box(0));
        lean_ctor_set(decl, 2, defValue);
        lean_ctor_set(decl, 3, lean_mk_string("(interpreter) whether to use precompiled code where available"));
        lean_ctor_set(decl, 4, lean_box(0));
        var register = (delegate*<Obj, Obj, Obj>)LeanExports.Get("lean_register_option");
        Obj r = register(name, decl);
        if (lean_io_result_is_error(r))
        {
            Obj err = lean_io_result_get_error(r);
            lean_inc(err);
            string msg = InterpExports.TakeString(InterpExports.IoErrorToString(err));
            lean_dec(r);
            throw new InvalidOperationException("LeanSharp: " + msg);
        }
        lean_dec(r);
    }
}
