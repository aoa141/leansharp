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

    sealed record Stub(string Module, string CFile, bool HasMain);

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
        string cwd;
        List<string> args;
        try
        {
            cwd = req.EffectiveCwd;
            args = ExpandResponseFiles(req.Args, cwd);
        }
        catch (Exception) { return null; }

        if (req.CommandName == "ar")
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

        // cc [-shared] -o out objs... libs...
        var objs = positional.Where(File.Exists).ToList();
        if (objs.Count == 0 || !TryReadStubs(objs, out var linked)) return null;
        if (args.Contains("-shared"))
            return _ => { WriteLib(output, linked); return 0; };
        return err => LinkExe(output, linked, err);
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

    static string Line(Stub s) => $"obj\t{s.Module}\t{(s.HasMain ? 1 : 0)}\t{s.CFile}\n";

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
                if (p.Length == 4 && p[0] == "obj" && !stubs.Any(s => s.Module == p[1]))
                    stubs.Add(new Stub(p[1], p[3], p[2] == "1"));
            }
        }
        return any;
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
    public static void WriteLauncher(string output, string mainModule, IReadOnlyList<string> libDirs)
    {
        // the program the interpreter runs: the main module's `main`
        string src = output + ".lean";
        string module = string.Join(".", mainModule.Split('.').Select(c => "«" + c + "»"));
        WriteAtomic(src, $"import {module}\n");
        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\n");
        sb.Append(ExeMagic).Append('\n');
        sb.Append("# Executable built by LeanSharp: the program is run by Lean's IR interpreter.\n");
        sb.Append("# module\t").Append(mainModule).Append('\n');
        sb.Append("# source\t").Append(src).Append('\n');
        foreach (var d in libDirs) sb.Append("# path\t").Append(d).Append('\n');
        sb.Append("# lean\t").Append(LeanSysroot.LeanExe).Append('\n');
        sb.Append("LEAN_PATH=").Append(Sh(string.Join(":", libDirs))).Append("\"${LEAN_PATH:+:$LEAN_PATH}\" exec ")
          .Append(Sh(LeanSysroot.LeanExe)).Append(" --run ").Append(Sh(src)).Append(" \"$@\"\n");
        WriteAtomic(output, sb.ToString(), executable: true);
    }

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
            if (!(req.Cmd.Contains('/') || req.Cmd.Contains(Path.DirectorySeparatorChar))) return false;
            string path = Path.GetFullPath(req.Cmd, req.EffectiveCwd);
            if (!File.Exists(path)) return false;
            string head = ReadHead(path, 64);
            int nl = head.IndexOf('\n');
            if (nl < 0 || !head.Substring(nl + 1).StartsWith(ExeMagic, StringComparison.Ordinal)) return false;
            string source = null;
            var dirs = new List<string>();
            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith("# ", StringComparison.Ordinal)) continue;
                var p = line.Substring(2).Split('\t');
                if (p.Length != 2) continue;
                if (p[0] == "source") source = p[1];
                else if (p[0] == "path") dirs.Add(p[1]);
            }
            if (source == null) return false;
            leanArgs = new[] { "--run", source }.Concat(req.Args).ToArray();
            req.BuildEnvironment().TryGetValue("LEAN_PATH", out var existing);
            leanPath = string.Join(Path.PathSeparator, string.IsNullOrEmpty(existing) ? dirs : dirs.Append(existing));
            return true;
        }
        catch (Exception) { return false; }
    }
}
