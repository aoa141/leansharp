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
    public static string LeantarExe => Path.Combine(BinDir, OperatingSystem.IsWindows() ? "leantar.exe" : "leantar");

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
        EnsureLauncher(Path.Combine(BinDir, OperatingSystem.IsWindows() ? "leanc.exe" : "leanc"), "leanc");
        EnsureLauncher(LeantarExe, "leantar");
    }

    /// <summary>
    /// The managed program the `lean`/`lake` launcher scripts in `<sysroot>/bin` start: an
    /// assembly that accepts `lean <args>` and `lake <args>` (default: `LeanSharp.Cli.dll` next
    /// to the LeanSharp assemblies, if present).
    /// </summary>
    public static string LauncherAssembly { get; set; }

    internal static string LauncherAssemblyPath => LauncherAssembly ?? Path.Combine(AppContext.BaseDirectory, "LeanSharp.Cli.dll");

    /// <summary>
    /// Windows: if this process was started through a launcher of a sysroot that is an
    /// application host (`<sysroot>\bin\lean.exe`, see `AppHost`), returns the command line as
    /// the launcher assembly expects it (`lean <args>`, `lake <args>`, ...) and makes that
    /// sysroot the current one, as native Lean finds its sysroot from its executable. Otherwise
    /// returns `args`. Programs that serve as <see cref="LauncherAssembly"/> call this first.
    /// </summary>
    public static string[] LauncherArgs(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (!OperatingSystem.IsWindows() || exe == null) return args;
        string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe), ".."));
        if (name is "lean" or "lake" or "leanc" or "leantar")
        {
            Environment.SetEnvironmentVariable("LEANSHARP_SYSROOT", root);
            return args.Prepend(name).ToArray();
        }
        // the sysroot's tools that are Lean programs (`leanchecker.exe`, ...)
        if (name != "dotnet" && ManagedToolchain.AppHostLauncherArgs(exe, args) is string[] leanArgs)
        {
            Environment.SetEnvironmentVariable("LEANSHARP_SYSROOT", root);
            return leanArgs.Prepend("lean").ToArray();
        }
        return args;
    }

    static void EnsureLauncher(string path, string tool)
    {
        try
        {
            var cli = LauncherAssemblyPath;
            bool windows = OperatingSystem.IsWindows();
            if (!File.Exists(cli))
            {
                if (!File.Exists(path)) File.WriteAllText(path, "#!/bin/sh\necho \"LeanSharp: no command-line host for this sysroot\" >&2\nexit 1\n");
                return;
            }
            // the `dotnet` host running this process (it need not be on `PATH`)
            var host = Environment.ProcessPath;
            if (host == null || Path.GetFileNameWithoutExtension(host) != "dotnet") host = "dotnet";
            if (windows)
            {
                // a copy of the .NET application host of the launcher assembly (see `AppHost`)
                var exe = AppHost.Create(cli, path);
                var cmdPath = Path.ChangeExtension(path, ".cmd");
                if (exe != null)
                {
                    AppHost.WriteIfChanged(path, exe);
                    if (File.Exists(cmdPath)) File.Delete(cmdPath);
                    return;
                }
                // Without one (e.g. the sysroot is on another drive), `lean.exe` is a script for
                // MSYS shells, and cmd.exe and PowerShell use the `.cmd` launcher next to it.
                var cmd = "@echo off\r\nrem Generated by LeanSharp: runs the managed `" + tool + "` with this sysroot.\r\n"
                    + "set \"LEANSHARP_SYSROOT=%~dp0..\"\r\n\"" + host + "\" \"" + cli + "\" " + tool + " %*\r\n";
                if (!File.Exists(cmdPath) || File.ReadAllText(cmdPath) != cmd) File.WriteAllText(cmdPath, cmd);
            }
            // Without an application host, the same script is written to `lean.exe` on Windows:
            // Lake only checks that the file exists (the process itself runs in-process), and an
            // MSYS shell (Git Bash) executes a `#!` script whatever its extension. `pwd -W` is the
            // Windows form of the directory.
            static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
            var text = "#!/bin/sh\n# Generated by LeanSharp: runs the managed `" + tool + "` with this sysroot.\n"
                + "LEANSHARP_SYSROOT=\"$(cd \"$(dirname \"$0\")/..\" && " + (windows ? "pwd -W" : "pwd") + ")\" exec " + Q(host) + " " + Q(cli) + " " + tool + " \"$@\"\n";
            if (File.Exists(path) && File.ReadAllText(path) == text) return;
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, text);
            if (!windows)
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            try { File.Move(tmp, path, overwrite: true); }
            finally
            {
                // Windows: the move fails while another process has the launcher open
                if (File.Exists(tmp)) try { File.Delete(tmp); } catch (IOException) { }
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
        // as in C, every task starts with a fresh count of the kernel's heartbeats
        // (`reset_heartbeat`; the allocation counter of `IO.getNumHeartbeats` is not reset)
        LeanTaskManager.ResetHeartbeat = LeanSharp.Kernel.KernelLimits.ResetHeartbeat;
        // IR interpreter: access to the compiled Lean code
        LeanCompiledCode.ModuleNames = () => LeanSharp.Compiled.LeanModules.ModuleNames;
        LeanCompiledCode.ModuleClassResolver = LeanSharp.Compiled.LeanModules.GetModuleClass;
        LeanCompiledCode.InitializerResolver = m => (nint)LeanSharp.Compiled.LeanModules.GetInitializer(m);
        InterpretedInit.Install();
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
        // Windows: file names are case-insensitive and the `.exe` extension is optional
        bool windows = OperatingSystem.IsWindows();
        var cmp = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (windows && !cmd.EndsWith(".exe", cmp)) cmd += ".exe";
        if (string.Equals(cmd, windows ? name + ".exe" : name, cmp)) return true;
        try
        {
            var full = Path.GetFullPath(cmd, req.EffectiveCwd);
            if (string.Equals(full, sysrootExe, cmp)) return true;
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
        // `leantar` (Lake's artifact cache): the managed port
        if (IsCommand(req, "leantar", LeanSysroot.LeantarExe))
            return new InProcessChild(req, ctx =>
            {
                var utf8 = new System.Text.UTF8Encoding(false);
                using var stdout = new StreamWriter(ctx.Stdout, utf8, 4096, leaveOpen: true);
                using var stderr = new StreamWriter(ctx.Stderr, utf8, 4096, leaveOpen: true);
                try { return LeanSharp.Leantar.LeantarCli.Main(req.Args, new StreamReader(ctx.Stdin, utf8), stdout, stderr, ctx.Cwd); }
                finally { stdout.Flush(); stderr.Flush(); }
            }, 16 * 1024 * 1024);
        // executables "linked" by the managed toolchain run in the interpreter
        if (ManagedToolchain.TryGetLauncher(req, out var launcherArgs, out var leanPath))
        {
            var run = new SpawnRequest
            {
                Cmd = req.Cmd, Args = req.Args, Cwd = req.Cwd, InheritEnv = req.InheritEnv, SetSid = req.SetSid,
                Stdin = req.Stdin, Stdout = req.Stdout, Stderr = req.Stderr,
                Env = req.Env.Append(new KeyValuePair<string, string>("LEAN_PATH", leanPath))
                    .Append(new KeyValuePair<string, string>("LEANSHARP_RUN_BUILTIN_INIT", "1")).ToList(),
            };
            return new InProcessChild(run, ctx => LeanShell.RunOnCurrentThread(launcherArgs), LeanHost.MainThreadStackSize);
        }
        // C compiler / archiver / linker steps on Lean-generated code
        var tool = ManagedToolchain.TryHandle(req);
        if (tool != null)
            return new InProcessChild(req, ctx =>
            {
                using var stderr = new StreamWriter(ctx.Stderr, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true);
                try { return tool(stderr); }
                catch (Exception e) { stderr.WriteLine("leansharp: " + e.Message); return 1; }
                finally { stderr.Flush(); }
            }, 16 * 1024 * 1024);
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
