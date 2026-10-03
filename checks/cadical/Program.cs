using System.Diagnostics;
using LeanSharp.Runtime.Cadical;

namespace CadicalCheck;

public static class Program
{
    // The reference solver: `CADICAL_NATIVE`, or the one of a native Lean build. Without it the
    // results are still validated (models against the formula, proofs by the LRAT checkers).
    static readonly string Native = Environment.GetEnvironmentVariable("CADICAL_NATIVE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Repos/lean4/build/release/stage1/bin/cadical" + (OperatingSystem.IsWindows() ? ".exe" : ""));
    static readonly string Dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "cli")
            return CadicalCli.Main(args[1..], Console.In, Console.Out, Console.Error);
        if (args.Length > 0 && args[0] == "lrat")
        {
            bool binary = args.Length > 3 && args[3] == "binary";
            var cnf = LratCheck.ReadCnf(args[1], out _);
            var proof = LratCheck.ReadLrat(args[2], binary);
            var err = LratCheck.CheckDirect(cnf, proof, false, out int adds);
            Console.WriteLine(err ?? $"direct: OK ({adds} additions)");
            var err2 = LratCheck.CheckLeanStyle(cnf, proof);
            Console.WriteLine(err2 ?? "lean-style: OK");
            return err == null && err2 == null ? 0 : 1;
        }
        if (args.Length > 0 && args[0] == "test")
            return RunTests(args[1..]);
        if (args.Length > 0 && args[0] == "ffi")
            return FfiTests.Run();
        if (args.Length > 0 && args[0] == "bench")
            return Bench(args[1..]);
        Console.WriteLine("usage: Check (cli <args>|lrat <cnf> <proof> [binary]|test [files] [-- extra options]|ffi|bench files)");
        return 0;
    }

    public static (int code, string stdout) RunOurs(string[] args)
    {
        var sw = new StringWriter();
        var se = new StringWriter();
        int code = CadicalCli.Main(args, TextReader.Null, sw, se);
        return (code, sw.ToString() + se.ToString());
    }

    public static (int code, string stdout) RunNative(string[] args)
    {
        var psi = new ProcessStartInfo(Native) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    static Dictionary<int, bool> ParseModel(string stdout)
    {
        var m = new Dictionary<int, bool>();
        foreach (var line in stdout.Split('\n'))
        {
            if (!line.StartsWith("v")) continue;
            foreach (var tok in line.Substring(1).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int l = int.Parse(tok);
                if (l != 0) m[Math.Abs(l)] = l > 0;
            }
        }
        return m;
    }

    static string CheckModel(List<int[]> cnf, Dictionary<int, bool> m)
    {
        foreach (var c in cnf)
        {
            bool sat = false;
            foreach (var l in c)
                if (m.TryGetValue(Math.Abs(l), out bool v) && v == (l > 0)) { sat = true; break; }
            if (!sat) return "model does not satisfy clause " + string.Join(" ", c);
        }
        return null;
    }

    static int RunTests(string[] args)
    {
        var files = new List<string>();
        var extra = new List<string>();
        bool inExtra = false;
        foreach (var a in args)
        {
            if (a == "--") { inExtra = true; continue; }
            if (inExtra) extra.Add(a); else files.Add(a);
        }
        if (files.Count == 0) files.AddRange(Directory.GetFiles(Path.Combine(Dir, "cnf"), "*.cnf").OrderBy(f => f));
        int failures = 0;
        bool haveNative = File.Exists(Native);
        if (!haveNative) Console.WriteLine($"(no native cadical at '{Native}': results are not compared with it)");
        var tmp = Path.Combine(Path.GetTempPath(), "cadical-check-" + Environment.ProcessId);
        Directory.CreateDirectory(tmp);
        foreach (var file in files)
        {
            var cnf = LratCheck.ReadCnf(file, out _);
            int? ncode = haveNative ? RunNative(new[] { "-q", "-n", file }).code : null;
            foreach (bool binary in new[] { false, true })
            {
                string proofPath = Path.Combine(tmp, Path.GetFileName(file) + (binary ? ".lrat.bin" : ".lrat"));
                var a = new List<string> { file, proofPath, "--lrat", $"--binary={(binary ? "true" : "false")}", "--quiet", "--shrink=0" };
                a.AddRange(extra);
                var sw = Stopwatch.StartNew();
                var (code, output) = RunOurs(a.ToArray());
                sw.Stop();
                string status = "ok";
                if (ncode != null && code != ncode) status = $"MISMATCH native={ncode} ours={code}";
                else if (code == 10)
                {
                    if (!output.StartsWith("s SATISFIABLE")) status = "bad output";
                    var err = CheckModel(cnf, ParseModel(output));
                    if (err != null) status = err;
                }
                else if (code == 20 && cnf.Any(c => c.Length == 0)) { /* empty proof, as native */ }
                else if (code == 20)
                {
                    if (!output.StartsWith("s UNSATISFIABLE")) status = "bad output";
                    var proof = LratCheck.ReadLrat(proofPath, binary);
                    var err = LratCheck.CheckDirect(cnf, proof, true, out _);
                    if (err == null) err = LratCheck.CheckLeanStyle(cnf, proof);
                    if (err != null) status = "LRAT: " + err;
                }
                else if (code != 0 || (ncode ?? 0) != 0) status = $"unexpected exit {code}: {output}";
                if (status != "ok") failures++;
                Console.WriteLine($"{Path.GetFileName(file),-28} bin={(binary ? 1 : 0)} res={code} {sw.ElapsedMilliseconds,6} ms {status}");
            }
        }
        Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILURES");
        return failures == 0 ? 0 : 1;
    }

    static int Bench(string[] files)
    {
        foreach (var file in files)
        {
            var sw = Stopwatch.StartNew();
            var (ncode, _) = RunNative(new[] { "-q", "-n", file });
            double tn = sw.Elapsed.TotalSeconds;
            sw.Restart();
            var (code, _) = RunOurs(new[] { "-q", "-n", file });
            double to = sw.Elapsed.TotalSeconds;
            Console.WriteLine($"{Path.GetFileName(file),-30} native {ncode} {tn,7:F2}s  managed {code} {to,7:F2}s  ratio {to / Math.Max(tn, 1e-3):F2}");
        }
        return 0;
    }
}
