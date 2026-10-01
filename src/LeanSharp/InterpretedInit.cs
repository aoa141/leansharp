// Initializers of Lean modules that have no compiled code.
//
// A native Lean module is initialized by its `initialize_<Module>` function, which runs the
// module's `builtin_initialize` and `initialize` declarations. User modules have no compiled code
// in LeanSharp. Lean itself runs their `initialize` declarations with the interpreter when they
// are imported, but nothing runs their `builtin_initialize` declarations, and nothing runs
// anything when such modules are loaded as a plugin (`lean --plugin`, `Lean.loadPlugin`). This
// file provides both:
//
//  * `RunBuiltinInits`: runs the `[builtin_init]` declarations of the interpreted modules of an
//    environment, once per program (used before `main` of an interpreter-backed executable and
//    after a plugin was imported);
//  * `InitializePlugin`: imports the modules of a stub library and runs their initializers.

using LeanSharp.Compiled;
using LeanSharp.Runtime;
using static LeanSharp.Runtime.LeanRt;

namespace LeanSharp;

static unsafe class InterpretedInit
{
    public static void Install()
    {
        LeanRt.InterpretedBuiltinInitHook = RunBuiltinInits;
        LeanRt.ManagedPluginInitHook = InitializePlugin;
    }

    static Obj Ok() => lean_io_result_mk_ok(lean_box(0));

    static Obj Error(string msg)
    {
        // IO.Error.userError
        var e = lean_alloc_ctor(18, 1, 0);
        lean_ctor_set(e, 0, lean_mk_string(msg));
        return lean_io_result_mk_error(e);
    }

    /// <summary>
    /// Runs the `[builtin_init]` declarations of the modules of `env` that are not compiled into
    /// LeanSharp, in import order; each module at most once per program. `env` and `opts` are
    /// borrowed. Returns an `IO Unit` result.
    /// </summary>
    public static Obj RunBuiltinInits(Obj env, Obj opts)
    {
        lean_inc(env);
        Obj names = M_Lean_Environment.l_Lean_Environment_allImportedModuleNames(env);
        Obj attr = M_Lean_Compiler_InitAttr.l_Lean_builtinInitAttr;
        Obj ext = lean_ctor_get(attr, 1);
        Obj inhabited = M_Lean_Compiler_InitAttr.l___private_Lean_Compiler_InitAttr_0__Lean_runInitAttrForMod___closed__0;
        ulong n = lean_array_get_size_u(names);
        for (ulong idx = 0; idx < n; idx++)
        {
            Obj modName = lean_array_get_core(names, idx);
            string mod = NameToDotted(modName);
            if (LeanModules.GetModuleClass(mod) != null) continue; // compiled in: initialized at startup
            if (!LeanProgramState.TryMarkOnce("builtin-init:" + mod)) continue;

            // entries from the `.olean` and from the `.ir` (as `runInitAttrForMod` does for `[init]`)
            lean_inc(inhabited); lean_inc(ext); lean_inc(env);
            Obj entries = M_Lean_Environment.l_Lean_PersistentEnvExtension_getModuleEntries___redArg(inhabited, ext, env, lean_box(idx), 0);
            lean_inc(inhabited); lean_inc(ext); lean_inc(env);
            Obj irEntries = M_Lean_Environment.l_Lean_PersistentEnvExtension_getModuleIREntries___redArg(inhabited, ext, env, lean_box(idx));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Obj arr in new[] { entries, irEntries })
            {
                ulong m = lean_array_get_size_u(arr);
                for (ulong i = 0; i < m; i++)
                {
                    Obj entry = lean_array_get_core(arr, i);
                    Obj decl = lean_ctor_get(entry, 0);
                    Obj initDecl = lean_ctor_get(entry, 1);
                    if (!seen.Add(NameToDotted(decl))) continue;
                    Obj r;
                    if (lean_is_scalar(initDecl))
                        r = lean_run_init_unit(env, opts, decl);          // `builtin_initialize <action>`
                    else if (InterpreterHasInitialized(decl))
                        continue;                                           // already run on first use
                    else
                        r = lean_run_init(env, opts, decl, initDecl);       // `builtin_initialize x : T ← ...`
                    if (lean_io_result_is_error(r)) return r;
                    lean_dec_ref(r);
                }
            }
        }
        return Ok();
    }

    static ulong lean_array_get_size_u(Obj a) => lean_unbox(lean_array_get_size(a));

    /// <summary>
    /// "Initializes" the stub library `libPath` as a plugin: imports its modules (which runs
    /// their `initialize` declarations) and runs their `builtin_initialize` declarations.
    /// </summary>
    public static Obj InitializePlugin(string libPath)
    {
        if (!ManagedToolchain.TryReadLibrary(libPath, out var modules, out var libDirs))
            return Error($"error loading plugin, not a library built by LeanSharp: {libPath}");
        if (modules.Count == 0) return Ok();
        if (!LeanProgramState.TryMarkOnce("plugin:" + Path.GetFullPath(libPath))) return Ok();

        // make the plugin's modules importable
        Obj spRef = M_Lean_Util_Path.l_Lean_searchPathRef;
        Obj sp = lean_st_ref_get(spRef);
        for (int i = libDirs.Count - 1; i >= 0; i--)
        {
            var cons = lean_alloc_ctor(1, 2, 0);
            lean_ctor_set(cons, 0, lean_mk_string(libDirs[i]));
            lean_ctor_set(cons, 1, sp);
            sp = cons;
        }
        lean_dec(lean_st_ref_set(spRef, sp));

        var imports = new List<Obj>();
        foreach (var m in modules)
        {
            // structure Import { module : Name, importAll := false, isExported := true, isMeta := true }
            // (`isMeta`: load the IR of everything reachable, for the interpreter)
            var imp = lean_alloc_ctor(0, 1, 3);
            lean_ctor_set(imp, 0, MkNameFromDotted(m));
            lean_ctor_set_uint8(imp, 8 + 0, 0);
            lean_ctor_set_uint8(imp, 8 + 1, 1);
            lean_ctor_set_uint8(imp, 8 + 2, 1);
            imports.Add(imp);
        }

        // `importModules` leaves the "importing" and "run initializers" flags cleared
        // (`withImporting`); the caller's values must survive (the `lean` front end has enabled
        // initializer execution for its own import, a user may be inside `withImporting`).
        Obj importingRef = M_Lean_ImportingFlag.l___private_Lean_ImportingFlag_0__Lean_importingRef;
        Obj runInitRef = M_Lean_ImportingFlag.l___private_Lean_ImportingFlag_0__Lean_runInitializersRef;
        Obj savedImporting = lean_st_ref_get(importingRef);
        Obj savedRunInit = lean_st_ref_get(runInitRef);
        lean_dec(lean_st_ref_set(runInitRef, lean_box(1)));
        Obj opts = M_Lean_Data_Options.l_Lean_Options_empty;
        lean_inc(opts);
        Obj res;
        try
        {
            // importModules imports opts (trustLevel := 0) (plugins := #[]) (leakEnv := true)
            //   (loadExts := true) (level := .private) (arts := {})
            res = M_Lean_Environment.l_Lean_importModules(MkArray(imports), opts, 0, lean_mk_empty_array(), 1, 1, 2, lean_box(1)); // `{}`: `Std.DTreeMap.Internal.Impl.leaf`
        }
        finally
        {
            lean_dec(lean_st_ref_set(importingRef, savedImporting));
            lean_dec(lean_st_ref_set(runInitRef, savedRunInit));
        }
        if (lean_io_result_is_error(res)) return res;
        Obj env = lean_io_result_get_value(res);
        // keep `env` alive for good: initializers may have stored closures over it
        lean_inc(env);
        return RunBuiltinInits(env, opts);
    }
}
