// Wiring between the runtime (which cannot reference the generated code) and the host:
// sysroot location, in-process execution of `lean`/`lake` subprocesses, etc.

using LeanSharp.Runtime;

namespace LeanSharp;

/// <summary>Configuration of where LeanSharp finds the Lean library files (`.olean`).</summary>
public static class LeanSysroot
{
    static string s_root;

    /// <summary>
    /// The Lean "sysroot": a directory with `lib/lean/` containing the `.olean` files of
    /// `Init`, `Std`, `Lean` and `Lake` (and a `bin/` directory). Defaults to the environment
    /// variable `LEANSHARP_SYSROOT`, or `lean-sysroot` next to the LeanSharp assemblies.
    /// </summary>
    public static string Root
    {
        get
        {
            if (s_root != null) return s_root;
            var env = Environment.GetEnvironmentVariable("LEANSHARP_SYSROOT");
            if (!string.IsNullOrEmpty(env)) return s_root = Path.GetFullPath(env);
            return s_root = Path.Combine(AppContext.BaseDirectory, "lean-sysroot");
        }
        set
        {
            s_root = value == null ? null : Path.GetFullPath(value);
            Apply();
        }
    }

    public static string BinDir => Path.Combine(Root, "bin");
    public static string LibDir => Path.Combine(Root, "lib", "lean");
    public static string LeanExe => Path.Combine(BinDir, OperatingSystem.IsWindows() ? "lean.exe" : "lean");
    public static string LakeExe => Path.Combine(BinDir, OperatingSystem.IsWindows() ? "lake.exe" : "lake");

    internal static void Apply()
    {
        // `IO.appPath`: Lean derives its sysroot from the directory of the running executable.
        Directory.CreateDirectory(BinDir);
        LeanPaths.AppPath = LeanExe;
        // Lake detects a Lean installation by the presence of `lean` next to `lake`; the
        // processes themselves are run in-process (see `HostHooks`), but the launchers also make
        // the sysroot usable from a shell.
        EnsureLauncher(LeanExe, "lean");
        EnsureLauncher(LakeExe, "lake");
    }

    static void EnsureLauncher(string path, string tool)
    {
        try
        {
            if (File.Exists(path)) return;
            var cli = Path.Combine(AppContext.BaseDirectory, "LeanSharp.Cli.dll");
            if (OperatingSystem.IsWindows())
                File.WriteAllText(path, "");
            else
            {
                File.WriteAllText(path, $"#!/bin/sh\nexec dotnet \"{cli}\" {tool} \"$@\"\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

static unsafe class HostHooks
{
    public static void Install()
    {
        LeanSysroot.Apply();
        LeanProcess.ProcessSpawnHook = SpawnHook;
        // kernel heartbeats use the runtime's per-thread allocation counter
        LeanSharp.Kernel.KernelLimits.GetHeartbeatHook = LeanHeartbeats.Get;
        LeanSharp.Kernel.KernelLimits.AddHeartbeatsHook = LeanHeartbeats.Add;
        // as in C, every task starts with a fresh heartbeat count
        LeanTaskManager.ResetHeartbeat = () => LeanHeartbeats.Set(0);
        // IR interpreter: access to the compiled Lean code
        LeanCompiledCode.ModuleNames = () => LeanSharp.Compiled.LeanModules.ModuleNames;
        LeanCompiledCode.ModuleClassResolver = LeanSharp.Compiled.LeanModules.GetModuleClass;
        LeanCompiledCode.InitializerResolver = m => (nint)LeanSharp.Compiled.LeanModules.GetInitializer(m);
    }

    public static void AfterModuleInitialization()
    {
        LeanInterpreterInit.RegisterOptions();
        // build the interpreter's symbol index in the background
        ThreadPool.QueueUserWorkItem(_ => { try { LeanCompiledCode.WarmUp(); } catch { } });
    }

    static bool IsCommand(SpawnRequest req, string name, string sysrootExe)
    {
        var cmd = req.Cmd;
        if (string.Equals(cmd, name, StringComparison.Ordinal)) return true;
        try
        {
            var full = Path.GetFullPath(cmd, req.EffectiveCwd);
            if (string.Equals(full, sysrootExe, StringComparison.Ordinal)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Runs `lean` and `lake` subprocesses in-process.</summary>
    static LeanChildProcess SpawnHook(SpawnRequest req)
    {
        if (IsCommand(req, "lean", LeanSysroot.LeanExe))
            return new InProcessChild(req, ctx => LeanShell.RunOnCurrentThread(req.Args.ToArray()), LeanHost.MainThreadStackSize);
        if (IsCommand(req, "lake", LeanSysroot.LakeExe))
            return new InProcessChild(req, ctx => LakeShell.RunOnCurrentThread(req.Args.ToArray()), LeanHost.MainThreadStackSize);
        if (req.CommandName == "cadical")
            return new InProcessChild(req, ctx =>
            {
                var utf8 = new System.Text.UTF8Encoding(false);
                using var stdout = new StreamWriter(ctx.Stdout, utf8, 4096, leaveOpen: true);
                using var stderr = new StreamWriter(ctx.Stderr, utf8, 4096, leaveOpen: true);
                try
                {
                    return LeanSharp.Runtime.Cadical.CadicalCli.Main(req.Args.ToArray(), new StreamReader(ctx.Stdin), stdout, stderr, ctx.Cwd, ctx.KillRequested);
                }
                finally { stdout.Flush(); stderr.Flush(); }
            }, 256 * 1024 * 1024);
        return null;
    }
}
