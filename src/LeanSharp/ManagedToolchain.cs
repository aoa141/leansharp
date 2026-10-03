// "Native" build steps without a native toolchain.
//
// Lake builds executables by compiling the C file Lean emits for every module (`cc -c`),
// archiving (`ar rcs`) and linking (`cc -o`). LeanSharp has no C compiler and no native runtime
// to link against, but it has the IR interpreter and the `.olean`/`.ir` files Lake produces
// anyway. So these build steps are emulated when (and only when) they operate on Lean-generated
// code:
//
//  * compiling a C file that starts with `// Lean compiler output` writes a small text file (a
//    *stub object*) recording the module name and whether the module defines `main`;
//  * archiving or linking stub objects into a library writes a *stub library* (the list of stubs);
//  * linking stub objects into an executable writes a *launcher*: a shell script that runs
//    `lean --run` on a one-line Lean file importing the main module, with the build's library
//    directories on `LEAN_PATH`. The program is then executed by the IR interpreter.
//
// Starting a launcher from an in-process program (e.g. `lake exe foo`) runs it in-process as well.
// Commands that involve anything else (hand-written C files, real object files) are not touched
// and go to the real toolchain, if there is one.

using System.Text;
using LeanSharp.Runtime;

namespace LeanSharp;

/// <summary>Emulation of `cc`/`ar` for Lean-generated code (see the file comment).</summary>
public static class ManagedToolchain
{
    const string ObjMagic = "!<leansharp-obj>";
    const string LibMagic = "!<leansharp-lib>";
    const string ExeMagic = "# !<leansharp-exe>";
    const string LeanCHeader = "// Lean compiler output";

    /// <summary>Set to false to pass all compiler/linker commands to the real toolchain.</summary>
    public static bool Enabled { get; set; } = true;

    /// <param name="LibDir">Directory with the module's `.olean` files, if it is not the one of Lake's layout.</param>
    sealed record Stub(string Module, string CFile, bool HasMain, string LibDir = null);

    // ------------------------------------------------------------------
    // Command recognition

    /// <summary>
    /// Returns the emulation of the command `req`, or null if it is not a compile/archive/link
    /// step on Lean-generated code. The returned function receives the output writer for
    /// diagnostics and returns the exit code.
    /// </summary>
    internal static Func<TextWriter, int> TryHandle(SpawnRequest req)
    {
        if (!Enabled) return null;
        try { return TryHandle(req.CommandName, req.Args, req.EffectiveCwd, compileSources: false); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// `leanc`: the compiler driver of a Lean installation, for use from a shell or script
    /// (`leanc -o prog prog.c`, `leanc -shared -o plugin.so Plugin.c`). Unlike the steps Lake
    /// runs, the C file usually comes from `lean --c=file.c file.lean`, which writes no
    /// `.olean`: the module is compiled from the source file next to the C file.
    /// </summary>
    public static int Leanc(string[] args, TextWriter stderr)
    {
        var handler = TryHandle("leanc", args, Directory.GetCurrentDirectory(), compileSources: true);
        if (handler == null)
        {
            stderr.WriteLine("leanc: LeanSharp has no C compiler; only Lean-generated C files can be \"compiled\" (they are run by the interpreter)");
            return 1;
        }
        return handler(stderr);
    }

    static Func<TextWriter, int> TryHandle(string commandName, IReadOnlyList<string> rawArgs, string cwd, bool compileSources)
    {
        var args = ExpandResponseFiles(rawArgs, cwd);

        if (commandName == "ar")
        {
            // ar rcs [--thin] lib objs...
            if (args.Count < 2 || !args[0].TrimStart('-').Contains('c')) return null;
            var files = args.Skip(1).Where(a => !a.StartsWith('-')).ToList();
            if (files.Count < 1) return null;
            string lib = Full(files[0], cwd);
            var inputs = files.Skip(1).Select(f => Full(f, cwd)).ToList();
            if (!TryReadStubs(inputs, out var stubs)) return null;
            return _ => { WriteLib(lib, stubs); return 0; };
        }

        int o = args.IndexOf("-o");
        if (o < 0 || o + 1 >= args.Count) return null;
        string output = Full(args[o + 1], cwd);
        var positional = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (i == o) { i++; continue; }
            if (args[i].StartsWith('-')) continue;
            positional.Add(Full(args[i], cwd));
        }

        if (args.Contains("-c"))
        {
            // cc -c -o out.o in.c ...
            var sources = positional.Where(p => p.EndsWith(".c", StringComparison.Ordinal)).ToList();
            if (sources.Count != 1 || !IsLeanC(sources[0], out var stub)) return null;
            return _ => { WriteAtomic(output, ObjMagic + "\n" + Line(stub)); return 0; };
        }

        // cc [-shared] -o out objs... libs...   (inputs may also be Lean-generated C files)
        var inputs2 = positional.Where(File.Exists).ToList();
        var cStubs = new List<Stub>();
        var objs = new List<string>();
        foreach (var f in inputs2)
        {
            if (f.EndsWith(".c", StringComparison.Ordinal))
            {
                if (!IsLeanC(f, out var cs)) return null;
                cStubs.Add(cs);
            }
            else objs.Add(f);
        }
        var linked = new List<Stub>();
        if (objs.Count > 0 && !TryReadStubs(objs, out linked)) return null;
        if (objs.Count == 0 && cStubs.Count == 0) return null;
        bool shared = args.Contains("-shared");
        return err =>
        {
            foreach (var cs in cStubs)
            {
                var s = cs;
                if (compileSources && LibDirOf(s) == null)
                {
                    s = CompileSourceOf(s, output, err);
                    if (s == null) return 1;
                }
                if (!linked.Any(l => l.Module == s.Module)) linked.Add(s);
            }
            if (shared) { WriteLib(output, linked); return 0; }
            return LinkExe(output, linked, err);
        };
    }

    static string Full(string path, string cwd) => Path.GetFullPath(path, cwd);

    /// <summary>GNU-style response files (`@file`): whitespace-separated arguments with quotes and backslash escapes.</summary>
    static List<string> ExpandResponseFiles(IReadOnlyList<string> args, string cwd)
    {
        var r = new List<string>();
        foreach (var a in args)
        {
            if (a.Length > 1 && a[0] == '@' && File.Exists(Full(a.Substring(1), cwd)))
                r.AddRange(SplitResponse(File.ReadAllText(Full(a.Substring(1), cwd))));
            else
                r.Add(a);
        }
        return r;
    }

    static IEnumerable<string> SplitResponse(string text)
    {
        var sb = new StringBuilder();
        bool inArg = false;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length) { sb.Append(text[++i]); inArg = true; continue; }
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else sb.Append(c);
                continue;
            }
            if (c == '"' || c == '\'') { quote = c; inArg = true; continue; }
            if (char.IsWhiteSpace(c))
            {
                if (inArg) { yield return sb.ToString(); sb.Clear(); inArg = false; }
                continue;
            }
            sb.Append(c); inArg = true;
        }
        if (inArg) yield return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Stubs

    static string Line(Stub s) => $"obj\t{s.Module}\t{(s.HasMain ? 1 : 0)}\t{s.CFile}" + (s.LibDir != null ? "\t" + s.LibDir : "") + "\n";

    /// <summary>
    /// The module of `s` has no `.olean` (the C file was written by `lean --c=`): compiles the
    /// Lean source next to the C file (`X.lean` for `X.c` or `X.lean.c`) into a directory under the
    /// temp directory that is derived from the output path.
    /// </summary>
    static Stub CompileSourceOf(Stub s, string output, TextWriter err)
    {
        string src = s.CFile.EndsWith(".lean.c", StringComparison.Ordinal)
            ? s.CFile.Substring(0, s.CFile.Length - 2)
            : Path.ChangeExtension(s.CFile, ".lean");
        if (!File.Exists(src))
        {
            err.WriteLine($"leanc: cannot find the Lean source of '{s.CFile}' (looked for '{src}')");
            return null;
        }
        // kept outside the source tree (the output is often written next to checked-in files)
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(output))).Substring(0, 16);
        string dir = Path.Combine(Path.GetTempPath(), "leansharp-leanc", key);
        string stem = Path.Combine(new[] { dir }.Concat(s.Module.Split('.')).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(stem));
        int rc = LeanShell.Main(new[] { "-q", src, "-o", stem + ".olean", "-i", stem + ".ilean" });
        if (rc != 0 || !File.Exists(stem + ".olean"))
        {
            err.WriteLine($"leanc: compiling '{src}' failed");
            return null;
        }
        return s with { LibDir = dir };
    }

    /// <summary>Whether `path` is a C file emitted by Lean; reads the module name and looks for `main`.</summary>
    static bool IsLeanC(string path, out Stub stub)
    {
        stub = null;
        try
        {
            if (!File.Exists(path)) return false;
            using var r = new StreamReader(path);
            if (r.ReadLine() != LeanCHeader) return false;
            string l2 = r.ReadLine() ?? "";
            const string p = "// Module: ";
            if (!l2.StartsWith(p, StringComparison.Ordinal)) return false;
            string module = l2.Substring(p.Length).Trim();
            bool hasMain = false;
            string line;
            while ((line = r.ReadLine()) != null)
                if (line.StartsWith("int main(", StringComparison.Ordinal)) { hasMain = true; break; }
            stub = new Stub(module, path, hasMain);
            return true;
        }
        catch (IOException) { return false; }
    }

    static string ReadHead(string path, int max)
    {
        try
        {
            using var f = File.OpenRead(path);
            var buf = new byte[max];
            int n = f.Read(buf, 0, max);
            return Encoding.UTF8.GetString(buf, 0, n);
        }
        catch (Exception) { return ""; }
    }

    /// <summary>Reads the stubs of object/library files; false if one of the files is not a stub.</summary>
    static bool TryReadStubs(IEnumerable<string> files, out List<Stub> stubs)
    {
        stubs = new List<Stub>();
        bool any = false;
        foreach (var f in files)
        {
            string head = ReadHead(f, 16);
            if (!head.StartsWith(ObjMagic, StringComparison.Ordinal) && !head.StartsWith(LibMagic, StringComparison.Ordinal))
                return false;
            any = true;
            foreach (var line in File.ReadAllLines(f))
            {
                var p = line.Split('\t');
                if (p.Length >= 4 && p[0] == "obj" && !stubs.Any(s => s.Module == p[1]))
                    stubs.Add(new Stub(p[1], p[3], p[2] == "1", p.Length > 4 ? p[4] : null));
            }
        }
        return any;
    }

    /// <summary>The modules of a stub library and the directories with their `.olean` files; false if `path` is not a stub.</summary>
    internal static bool TryReadLibrary(string path, out List<string> modules, out List<string> libDirs)
    {
        modules = new List<string>(); libDirs = new List<string>();
        if (!File.Exists(path) || !TryReadStubs(new[] { path }, out var stubs)) return false;
        modules = stubs.Select(s => s.Module).ToList();
        libDirs = stubs.Select(LibDirOf).Where(d => d != null).Distinct().ToList();
        return true;
    }

    static void WriteLib(string path, List<Stub> stubs) =>
        WriteAtomic(path, LibMagic + "\n" + string.Concat(stubs.Select(Line)));

    static void WriteAtomic(string path, string text, bool executable = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + "." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (executable && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(tmp, path, overwrite: true);
    }

    // ------------------------------------------------------------------
    // Executables

    /// <summary>The directory with the `.olean` files of a module compiled by Lake to `cFile` (`<build>/ir/A/B.c` → `<build>/lib/lean`).</summary>
    static string LibDirOf(Stub s)
    {
        if (s.LibDir != null) return s.LibDir;
        string dir = Path.GetDirectoryName(s.CFile);
        int depth = s.Module.Count(c => c == '.');
        for (int i = 0; i < depth && dir != null; i++) dir = Path.GetDirectoryName(dir);
        if (dir == null) return null;
        string build = Path.GetDirectoryName(dir);
        if (build == null) return null;
        foreach (var cand in new[] { Path.Combine(build, "lib", "lean"), Path.Combine(build, "lib") })
            if (Directory.Exists(cand)) return cand;
        return null;
    }

    static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    static int LinkExe(string output, List<Stub> stubs, TextWriter err)
    {
        var mains = stubs.Where(s => s.HasMain).ToList();
        if (mains.Count != 1)
        {
            err.WriteLine(mains.Count == 0
                ? "leansharp: cannot link executable: no module defines `main`"
                : "leansharp: cannot link executable: several modules define `main`: " + string.Join(", ", mains.Select(m => m.Module)));
            return 1;
        }
        var libDirs = stubs.Select(LibDirOf).Where(d => d != null).Distinct().ToList();
        WriteLauncher(output, mains[0].Module, libDirs);
        return 0;
    }

    /// <summary>
    /// Writes an executable launcher at `output` that runs the `main` function of `mainModule`
    /// with the IR interpreter; `libDirs` (directories with the `.olean`/`.ir` files of the
    /// program's modules) are put on `LEAN_PATH`.
    /// </summary>
    /// <remarks>
    /// On Windows the launcher is a real executable when possible: a copy of the .NET
    /// application host of the launcher assembly (see `AppHost`) followed by the launcher's
    /// description; otherwise a shell script, as elsewhere.
    /// </remarks>
    /// <param name="inSysroot">
    /// The launcher stays next to the sysroot's `lean.exe` (the sysroot's tools): it refers to
    /// the launcher assembly by a relative path. Other launchers (programs built by Lake, which
    /// moves them and restores them from its cache) refer to it by its absolute location.
    /// </param>
    public static void WriteLauncher(string output, string mainModule, IReadOnlyList<string> libDirs, bool inSysroot = false)
    {
        // The launcher must keep working when Lake restores it from its artifact cache (only the
        // file itself is cached) or the whole build directory is moved: library directories are
        // recorded relative to the launcher, and the one-line program the interpreter runs
        // (`<launcher>.lean`, importing the main module) is recreated when it is missing.
        string dir = Path.GetDirectoryName(output);
        var dirs = new List<string>(libDirs);
        // Lake's layout: `<build>/bin/<exe>` next to `<build>/lib/lean` (with Lake's artifact
        // cache the C files are not in the build directory, so `libDirs` can be empty)
        string sibling = Path.GetFullPath(Path.Combine(dir, "..", "lib", "lean"));
        if (Directory.Exists(sibling) && !dirs.Contains(sibling)) dirs.Add(sibling);
        var rel = dirs.Select(d => Path.GetRelativePath(dir, d).Replace('\\', '/')).ToList();
        string import = ImportLine(mainModule);
        WriteAtomic(output + ".lean", import + "\n");
        var info = new StringBuilder();
        info.Append(ExeMagic).Append('\n');
        info.Append("# Executable built by LeanSharp: the program is run by Lean's IR interpreter.\n");
        info.Append("# module\t").Append(mainModule).Append('\n');
        foreach (var d in rel) info.Append("# path\t").Append(d).Append('\n');
        info.Append("# lean\t").Append(LeanSysroot.LeanExe).Append('\n');
        if (AppHost.Create(LeanSysroot.LauncherAssemblyPath, output, anchored: !inSysroot) is byte[] exe)
        {
            AppHost.WriteIfChanged(output, exe.Concat(Encoding.UTF8.GetBytes("\n" + info)).ToArray());
            return;
        }
        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\n");
        sb.Append(info);
        // Windows: the script is run by an MSYS shell (Git Bash), also when it is started as `prog`
        // instead of `prog.exe`; `lean` gets Windows paths (`pwd -W`) in a `;`-separated list.
        bool windows = OperatingSystem.IsWindows();
        string sep = windows ? ";" : ":";
        sb.Append("d=$(cd \"$(dirname \"$0\")\" && ").Append(windows ? "pwd -W" : "pwd").Append(")\n");
        if (windows)
            sb.Append("b=$(basename \"$0\"); case \"$b\" in *.exe) ;; *) b=\"$b.exe\" ;; esac\ns=\"$d/$b.lean\"\n");
        else
            sb.Append("s=\"$d/$(basename \"$0\").lean\"\n");
        sb.Append("[ -f \"$s\" ] || printf '%s\\n' ").Append(Sh(import)).Append(" > \"$s\"\n");
        sb.Append("LEANSHARP_RUN_BUILTIN_INIT=1 LEAN_PATH=\"");
        sb.Append(string.Join(sep, rel.Select(r => Path.IsPathRooted(r) ? r : "$d/" + r)));
        sb.Append("${LEAN_PATH:+").Append(sep).Append("$LEAN_PATH}\" exec ").Append(Sh(LeanSysroot.LeanExe)).Append(" --run \"$s\" \"$@\"\n");
        WriteAtomic(output, sb.ToString(), executable: true);
    }

    static string ImportLine(string mainModule) =>
        "import " + string.Join(".", mainModule.Split('.').Select(c => "«" + c + "»"));

    /// <summary>
    /// If `req` starts a launcher written by <see cref="LinkExe"/>, returns the `lean` arguments
    /// and the `LEAN_PATH` that run the program in-process.
    /// </summary>
    internal static bool TryGetLauncher(SpawnRequest req, out string[] leanArgs, out string leanPath)
    {
        leanArgs = null; leanPath = null;
        if (!Enabled) return false;
        try
        {
            string path;
            if (req.Cmd.Contains('/') || req.Cmd.Contains(Path.DirectorySeparatorChar))
            {
                path = Path.GetFullPath(req.Cmd, req.EffectiveCwd);
                if (OperatingSystem.IsWindows() && !File.Exists(path) && File.Exists(path + ".exe")) path += ".exe";
            }
            else if (OperatingSystem.IsWindows())
            {
                // Elsewhere the system starts a launcher found on `PATH` (it is a shell script);
                // Windows cannot, so it is looked up here (e.g. `lake env leanchecker`).
                req.BuildEnvironment().TryGetValue("PATH", out var pathVar);
                path = (pathVar ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .SelectMany(d => new[] { Path.Combine(d, req.Cmd), Path.Combine(d, req.Cmd + ".exe") })
                    .FirstOrDefault(File.Exists);
                if (path == null) return false;
            }
            else return false;
            if (!TryReadLauncher(path, out var source, out var dirs, out _)) return false;
            leanArgs = new[] { "--run", source }.Concat(req.Args).ToArray();
            req.BuildEnvironment().TryGetValue("LEAN_PATH", out var existing);
            leanPath = string.Join(Path.PathSeparator, string.IsNullOrEmpty(existing) ? dirs : dirs.Append(existing));
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// If this process was started through a launcher that is an application host (see
    /// <see cref="WriteLauncher"/>), sets up `LEAN_PATH` and returns the `lean` arguments that
    /// run the program and the `lean` executable of its sysroot; null otherwise.
    /// </summary>
    internal static string[] AppHostLauncherArgs(string exe, string[] args, out string leanExe)
    {
        leanExe = null;
        try
        {
            if (!ReadHead(exe, 2).StartsWith("MZ", StringComparison.Ordinal) || !TryReadLauncher(exe, out var source, out var dirs, out leanExe))
                return null;
            var existing = Environment.GetEnvironmentVariable("LEAN_PATH");
            Environment.SetEnvironmentVariable("LEAN_PATH", string.Join(Path.PathSeparator, string.IsNullOrEmpty(existing) ? dirs : dirs.Append(existing)));
            Environment.SetEnvironmentVariable("LEANSHARP_RUN_BUILTIN_INIT", "1");
            return new[] { "--run", source }.Concat(args).ToArray();
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Reads a launcher: a script whose second line is the magic line, or an application host
    /// followed by the magic line and the description. Returns the program to run (recreated if
    /// missing), the library directories and the `lean` executable recorded in it.
    /// </summary>
    static bool TryReadLauncher(string path, out string source, out List<string> dirs, out string leanExe)
    {
        source = null; dirs = new List<string>(); leanExe = null;
        if (!File.Exists(path)) return false;
        IEnumerable<string> lines;
        string head = ReadHead(path, 64);
        int nl = head.IndexOf('\n');
        if (nl >= 0 && head.Substring(nl + 1).StartsWith(ExeMagic, StringComparison.Ordinal))
            lines = File.ReadLines(path);
        else if (head.StartsWith("MZ", StringComparison.Ordinal))
        {
            // the description at the end of an application host
            using var f = File.OpenRead(path);
            int n = (int)Math.Min(f.Length, 8192);
            f.Seek(-n, SeekOrigin.End);
            var tail = new byte[n];
            f.ReadExactly(tail);
            string text = Encoding.UTF8.GetString(tail);
            int at = text.LastIndexOf("\n" + ExeMagic + "\n", StringComparison.Ordinal);
            if (at < 0) return false;
            lines = text.Substring(at + 1).Split('\n');
        }
        else return false;
        string module = null;
        string dir = Path.GetDirectoryName(path);
        foreach (var line in lines)
        {
            if (!line.StartsWith("# ", StringComparison.Ordinal)) continue;
            var p = line.Substring(2).Split('\t');
            if (p.Length != 2) continue;
            if (p[0] == "module") module = p[1];
            else if (p[0] == "path") dirs.Add(Path.GetFullPath(p[1], dir));
            else if (p[0] == "lean") leanExe = p[1];
        }
        if (module == null) return false;
        source = path + ".lean";
        if (!File.Exists(source)) WriteAtomic(source, ImportLine(module) + "\n");
        return true;
    }
}
