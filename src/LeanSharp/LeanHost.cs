// Process-wide initialization of LeanSharp: the managed equivalent of `lean_initialize()`
// (src/initialize/init.cpp) plus wiring of the runtime hooks to the compiled Lean code.

using LeanSharp.Compiled;
using LeanSharp.Runtime;

namespace LeanSharp;

public static unsafe class LeanHost
{
    static readonly object s_lock = new();
    static bool s_initialized;

    /// <summary>Default stack size of threads running Lean code.</summary>
    public static int MainThreadStackSize = 1024 * 1024 * 1024;

    /// <summary>
    /// Initializes the Lean runtime and all compiled Lean modules (`Init`, `Std`, `Lean`, `Lake`).
    /// Idempotent. Must run on a thread with a large stack (see <see cref="RunWithLargeStack"/>).
    /// </summary>
    public static void Initialize()
    {
        lock (s_lock)
        {
            if (s_initialized) return;
            s_initialized = true;
            // LEANSHARP_TRACE_STARTUP=1: time of each initialization step on stderr
            bool trace = Environment.GetEnvironmentVariable("LEANSHARP_TRACE_STARTUP") == "1";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            void Step(string what)
            {
                if (trace) Console.Error.WriteLine($"[startup] {what}: {sw.Elapsed.TotalMilliseconds:F0} ms");
                sw.Restart();
            }
            LeanModules.RegisterExports();
            Step("register exports");
            HostHooks.Install();
            Step("install hooks");
            const byte builtin = 1;
            Check(M_Init.initialize(builtin), "Init");
            Step("initialize Init");
            Check(M_Std.initialize(builtin), "Std");
            Step("initialize Std");
            Check(M_Lean.initialize(builtin), "Lean");
            Step("initialize Lean");
            Check(M_Lake.initialize(builtin), "Lake");
            Step("initialize Lake");
            Check(M_LakeMain.initialize(builtin), "LakeMain");
            HostHooks.AfterModuleInitialization();
            InitTimeEnvironment.CaptureBase();
            Step("LakeMain, hooks");
            // from here on every program gets its own copy of the libraries' global state
            LeanProgramState.FreezeInitialState();
            Step("freeze global state");
        }
    }

    /// <summary>
    /// Marks the start of an in-process program run (`lean`, `lake`) on the current thread; dispose
    /// the result when the program has ended. A native program starts in a new OS process and
    /// finds all runtime and library state freshly initialized; here the compiled Lean code is
    /// initialized once per OS process, so this
    ///  * resets the runtime state a run may have changed: panic behavior (`--exitOnPanic`), a
    ///    pending `IO.Process.exit` from a task, memory/heartbeat/stack limits set by command-line
    ///    options (one run must not influence the next one executed by the same host process:
    ///    a test worker, Lake running `lean` children, ...);
    ///  * re-evaluates the library state that is computed from the environment at initialization
    ///    time (see <see cref="InitTimeEnvironment"/>) for the environment of this program.
    /// The program also gets a fresh logical process: working directory, environment, pending
    /// exit, cumulative profiling times and the global state of the Lean libraries (the values
    /// of the `IO.Ref`s created by `builtin_initialize`).
    /// </summary>
    internal static IDisposable EnterProgram()
    {
        LeanProgramState.BeginProgram();
        LeanRt.lean_set_exit_on_panic(false);
        LeanRt.lean_set_panic_messages(true);
        LeanTaskManager.ClearPendingExit();
        LeanRuntimeSettings.MaxMemory = 0;
        LeanRuntimeSettings.MaxHeartbeat = 0;
        LeanSharp.Kernel.KernelLimits.MaxHeartbeat = 0;
        LeanSharp.Kernel.KernelLimits.ResetHeartbeat();
        LeanRuntimeSettings.ThreadStackSize = 0;
        LeanHeartbeats.Set(0);
        return new ProgramScope(InitTimeEnvironment.Enter());
    }

    sealed class ProgramScope(IDisposable inner) : IDisposable
    {
        public void Dispose()
        {
            inner.Dispose();
            LeanProgramState.EndProgram();
        }
    }

    static void Check(Obj r, string what)
    {
        if (LeanRt.lean_io_result_is_error(r))
        {
            LeanRt.lean_io_result_show_error(r);
            throw new InvalidOperationException($"LeanSharp: initialization of '{what}' failed");
        }
        LeanRt.lean_dec_ref(r);
    }

    /// <summary>Run `f` on a fresh thread with a large stack (Lean code is deeply recursive) and wait for it.</summary>
    public static T RunWithLargeStack<T>(Func<T> f, int? stackSize = null)
    {
        T result = default;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo error = null;
        var th = new Thread(() =>
        {
            try
            {
                LeanSharp.Runtime.LeanRt.lean_declare_thread_stack(stackSize ?? MainThreadStackSize);
                result = f();
            }
            catch (Exception e) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
        }, stackSize ?? MainThreadStackSize);
        th.Start();
        th.Join();
        error?.Throw();
        return result;
    }
}

/// <summary>
/// Library state that Lean computes from environment variables while *initializing* its modules
/// (natively: at the start of every process). LeanSharp initializes the compiled modules once per
/// OS process, so an in-process child started with a different environment would see the values
/// of the host. The only such state in `Init`/`Std`/`Lean`/`Lake` is `Lean.manualRoot`
/// (`builtin_initialize` in `Lean.DocString.Links`, from `LEAN_MANUAL_ROOT`).
///
/// The constant is a static field of the generated code and therefore shared by all programs
/// running in this OS process: while programs with *different* values of the variable overlap,
/// all of them see the value of the one that started last (and the previous value is restored
/// when it ends). Children that inherit the variable unchanged (e.g. everything Lake spawns)
/// never change it.
/// </summary>
static unsafe class InitTimeEnvironment
{
    const string Var = "LEAN_MANUAL_ROOT";
    static readonly object s_lock = new();
    // value of the variable (null: unset) -> `Lean.manualRoot` computed for it
    static readonly Dictionary<string, Obj> s_roots = new(StringComparer.Ordinal);
    const string Unset = "\0";
    static string s_baseKey;
    static readonly List<Scope> s_active = new();
    static System.Reflection.MethodInfo s_initFn;

    sealed class Scope : IDisposable
    {
        public string Key;
        public void Dispose()
        {
            lock (s_lock)
            {
                if (!s_active.Remove(this)) return;
                Apply(s_active.Count > 0 ? s_active[^1].Key : s_baseKey);
            }
        }
    }

    /// <summary>Called once after module initialization: remembers the value computed from the OS environment.</summary>
    public static void CaptureBase()
    {
        lock (s_lock)
        {
            s_baseKey = CurrentKey();
            s_roots[s_baseKey] = M_Lean_DocString_Links.l_Lean_manualRoot;
            // the `builtin_initialize` function: `private def initFn✝ : IO String`
            foreach (var m in typeof(M_Lean_DocString_Links).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
            {
                if (m.Name.StartsWith("l___private_Lean_DocString_Links_0__Lean_initFn_00_", StringComparison.Ordinal)
                    && m.GetParameters().Length == 0 && m.ReturnType == typeof(Obj))
                {
                    s_initFn = m;
                    break;
                }
            }
        }
    }

    /// <summary>The value of the variable as `IO.getEnv` sees it (the environment of the current logical process).</summary>
    static string CurrentKey()
    {
        Obj name = LeanRt.lean_mk_string(Var);
        Obj opt = LeanRt.lean_io_getenv(name);
        LeanRt.lean_dec(name);
        string key = LeanRt.lean_is_scalar(opt) ? Unset : LeanRt.lean_string_to_net(LeanRt.lean_ctor_get(opt, 0));
        LeanRt.lean_dec(opt);
        return key;
    }

    // must hold s_lock
    static void Apply(string key)
    {
        if (s_roots.TryGetValue(key, out var v) && !ReferenceEquals(M_Lean_DocString_Links.l_Lean_manualRoot, v))
            M_Lean_DocString_Links.l_Lean_manualRoot = v;
    }

    /// <summary>Makes the initialization-time state match the environment of the current logical process.</summary>
    public static IDisposable Enter()
    {
        string key = CurrentKey();
        var scope = new Scope { Key = key };
        lock (s_lock)
        {
            if (s_baseKey == null) return scope; // not captured (initialization failed)
            if (!s_roots.ContainsKey(key) && s_initFn != null)
            {
                // run the initializer again, in the environment of this program
                Obj r = (Obj)s_initFn.Invoke(null, null);
                if (LeanRt.lean_io_result_is_ok(r))
                {
                    Obj v = LeanRt.lean_io_result_get_value(r);
                    LeanRt.lean_mark_persistent(v);
                    s_roots[key] = v;
                }
                LeanRt.lean_dec_ref(r);
            }
            if (!s_roots.ContainsKey(key)) return scope;
            s_active.Add(scope);
            Apply(key);
        }
        return scope;
    }
}
