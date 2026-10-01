// High-level API: run Lean and Lake on a project directory, in-process, without any native
// Lean installation.

using System.Text;
using LeanSharp.Runtime;

namespace LeanSharp;

/// <summary>Outcome of a `lean`/`lake` invocation.</summary>
public sealed record LeanResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
    /// <summary>Standard output followed by standard error.</summary>
    public string Output => Stderr.Length == 0 ? Stdout : Stdout + Stderr;
    public override string ToString() => $"exit code {ExitCode}\n{Output}";
}

/// <summary>
/// A Lean project directory (a Lake package, or just a folder with `.lean` files).
/// All commands run inside the current process; the working directory and environment of the
/// host process are not modified.
/// </summary>
/// <example>
/// <code>
/// LeanSysroot.Root = "/path/to/lean-sysroot";     // lib/lean/*.olean
/// var project = new LeanProject("/src/MyProject");
/// var build = project.Build();                     // lake build
/// var check = project.RunFile("Scratch.lean");     // lean Scratch.lean
/// </code>
/// </example>
public sealed class LeanProject
{
    /// <summary>Absolute path of the project directory.</summary>
    public string Directory { get; }

    /// <summary>Extra environment variables for commands (e.g. `LEAN_PATH`).</summary>
    public Dictionary<string, string> Environment { get; } = new();

    public LeanProject(string directory)
    {
        Directory = Path.GetFullPath(directory);
        if (!System.IO.Directory.Exists(Directory))
            throw new DirectoryNotFoundException(Directory);
    }

    /// <summary>`lake build [targets]`.</summary>
    public LeanResult Build(params string[] targets) => Lake(new[] { "build" }.Concat(targets).ToArray());

    /// <summary>`lake test`.</summary>
    public LeanResult Test(params string[] args) => Lake(new[] { "test" }.Concat(args).ToArray());

    /// <summary>`lake clean`.</summary>
    public LeanResult Clean() => Lake("clean");

    /// <summary>`lake env lean [args] file`: elaborate a file of the project with the project's dependencies on the search path.</summary>
    public LeanResult CheckFile(string file, params string[] leanArgs) =>
        Lake(new[] { "env", "lean" }.Concat(leanArgs).Append(file).ToArray());

    /// <summary>`lean [args] file` (no Lake: only the standard library is on the search path unless `LEAN_PATH` is set).</summary>
    public LeanResult RunFile(string file, params string[] leanArgs) =>
        Lean(leanArgs.Append(file).ToArray());

    /// <summary>`lean --run file [programArgs]`: runs the `main` function of a file with the interpreter.</summary>
    public LeanResult RunMain(string file, params string[] programArgs) =>
        Lean(new[] { "--run", file }.Concat(programArgs).ToArray());

    /// <summary>Runs `lake` with arbitrary arguments in the project directory.</summary>
    public LeanResult Lake(params string[] args) => Run("lake", args, null);

    /// <summary>Runs `lean` with arbitrary arguments in the project directory.</summary>
    public LeanResult Lean(params string[] args) => Run("lean", args, null);

    /// <summary>Runs `lean` with arbitrary arguments, feeding <paramref name="stdin"/> to its standard input (e.g. with `--stdin`).</summary>
    public LeanResult LeanWithInput(string stdin, params string[] args) => Run("lean", args, stdin);

    LeanResult Run(string tool, string[] args, string stdin)
    {
        LeanHost.RunWithLargeStack(() => { LeanHost.Initialize(); return 0; });
        var req = new SpawnRequest
        {
            Cmd = tool == "lake" ? LeanSysroot.LakeExe : LeanSysroot.LeanExe,
            Args = args,
            Cwd = Directory,
            Env = Environment.ToList(),
            Stdin = stdin == null ? LeanStdioMode.Null : LeanStdioMode.Piped,
            Stdout = LeanStdioMode.Piped,
            Stderr = LeanStdioMode.Piped,
        };
        var child = LeanProcess.Spawn(req);
        string so = "", se = "";
        var t1 = new Thread(() => so = ReadAll(child.StdoutPipe));
        var t2 = new Thread(() => se = ReadAll(child.StderrPipe));
        t1.Start(); t2.Start();
        if (stdin != null)
        {
            using var w = child.StdinPipe;
            var bytes = Encoding.UTF8.GetBytes(stdin);
            w.Write(bytes, 0, bytes.Length);
        }
        int code = child.WaitForExit();
        t1.Join(); t2.Join();
        return new LeanResult(code, so, se);
    }

    static string ReadAll(Stream s)
    {
        if (s == null) return "";
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
