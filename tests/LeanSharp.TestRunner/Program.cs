// Runs the Lean 4 test suite (`tests/elab`, `tests/elab_fail` piles of the Lean repository)
// against LeanSharp, without shell scripts or a native Lean installation.
//
//   LeanSharp.TestRunner run --tests ~/Repos/lean4/tests --pile elab [--filter REGEX] [-j N]
//                            [--timeout SECONDS] [--results results.tsv] [--show-diffs]
//
// The coordinator starts worker processes (`LeanSharp.TestRunner worker ...`), each of which
// initializes Lean once and runs test files in-process. A worker that crashes or times out is
// replaced, and the test is reported as CRASH/TIMEOUT.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LeanSharp;
using LeanSharp.Runtime;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "worker") return Worker.Run(args[1..]);
        if (args.Length > 0 && args[0] == "run") return Coordinator.Run(args[1..]);
        if (args.Length > 0 && args[0] == "one") return Worker.RunOneCli(args[1..]);
        Console.Error.WriteLine("usage: LeanSharp.TestRunner run --tests DIR --pile elab|elab_fail [--filter RE] [-j N] [--timeout S] [--results FILE] [--show-diffs]");
        Console.Error.WriteLine("       LeanSharp.TestRunner one --tests DIR --pile elab FILE.lean");
        return 2;
    }
}

record TestResult(string Name, string Status, double Seconds, string Detail);

static class Piles
{
    /// <summary>Settings a test's `.init.sh` can define (`export X=Y`, `unset X`, `TEST_EXIT=..`, `TEST_ARGS=( .. )`).</summary>
    public sealed class Init
    {
        public Dictionary<string, string> Env = new();   // null value = unset
        public string Exit;                               // null, "nonzero" or a number
        public List<string> Args = new();
    }

    public static Init ReadInit(string testFile)
    {
        var r = new Init();
        var f = testFile + ".init.sh";
        if (!File.Exists(f)) return r;
        foreach (var raw in File.ReadAllLines(f))
        {
            var l = raw.Trim();
            if (l.StartsWith("export ") && l.Contains('='))
            {
                var kv = l.Substring(7).Split('=', 2);
                r.Env[kv[0].Trim()] = kv[1].Trim().Trim('"', '\'');
            }
            else if (l.StartsWith("unset ")) r.Env[l.Substring(6).Trim()] = null;
            else if (l.StartsWith("TEST_EXIT=")) r.Exit = l.Substring(10).Trim();
            else if (l.StartsWith("TEST_ARGS=("))
                r.Args.AddRange(l.Substring(11).TrimEnd(')').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        return r;
    }

    /// <summary>File pattern of the tests of a pile.</summary>
    public static string Pattern(string pile) => pile == "docparse" ? "*.txt" : "*.lean";

    public static bool Skip(string pile, string f)
    {
        if (File.Exists(f + ".no_test")) return true;
        var name = Path.GetFileName(f);
        if (pile == "docparse" || pile == "server") { if (name == "run_test.lean") return true; }
        if (pile == "compile")
        {
            if (File.Exists(f + ".do_interpret_test")) return false;
            if (File.Exists(f + ".no_interpret_test")) return true;
            if (File.Exists(f + ".do_interpret")) return false;
            if (File.Exists(f + ".no_interpret")) return true;
        }
        return false;
    }

    /// <summary>`lean` arguments used by the pile's `run_test.sh` (for `compile`: the interpreter half).</summary>
    public static string[] LeanArgs(string pile, string testFile, Init init)
    {
        string name = Path.GetFileName(testFile);
        switch (pile)
        {
            case "elab": return new[] { "--root=..", "-DprintMessageEndPos=true", "-Dlinter.all=false", "-DElab.inServer=true", "-Dcompiler.postponeCompile=false", name };
            case "elab_fail": return new[] { "--root=..", "-DprintMessageEndPos=true", "-Dlinter.all=false", "-DElab.inServer=true", name };
            case "compile": return new[] { "-Dlinter.all=false", "--run", name }.Concat(init.Args).ToArray();
            case "server": return new[] { "-Dlinter.all=false", "--run", name };
            case "docparse": return new[] { "-Dlinter.all=false", "--run", "run_test.lean", name };
            default: throw new ArgumentException($"unsupported pile '{pile}'");
        }
    }

    public static bool ExitOk(string pile, Init init, int exit)
    {
        if (pile == "elab_fail") return exit != 0;
        if (pile == "compile" && init.Exit != null)
            return init.Exit == "nonzero" ? exit != 0 : exit == int.Parse(init.Exit);
        return exit == 0;
    }

    public static bool NormalizeElab(string pile) => pile == "elab" || pile == "elab_fail";

    static readonly Regex s_mvar = new(@"(\?(\w|_\w+))\.[0-9]+", RegexOptions.Compiled);
    static readonly Regex s_ref = new(@"https://lean-lang\.org/doc/reference/(v?[0-9.]+(-rc[0-9]+)?|latest)", RegexOptions.Compiled);
    static readonly Regex s_meas = new(@"^measurement: (\S+) \S+( \S+)?$", RegexOptions.Compiled | RegexOptions.Multiline);

    public static string Normalize(string s, string pile = "elab")
    {
        if (NormalizeElab(pile))
        {
            s = s_mvar.Replace(s, "$1");
            s = s_ref.Replace(s, "REFERENCE");
        }
        s = s_meas.Replace(s, "measurement: $1 ...");
        return s;
    }

    public static string Expected(string testFile, out bool ignored)
    {
        ignored = File.Exists(testFile + ".out.ignored");
        var f = testFile + ".out.expected";
        return File.Exists(f) ? File.ReadAllText(f) : "";
    }

    static string StripCr(string s) => s.Replace("\r\n", "\n");

    public static TestResult Check(string pile, string testFile, string output, int exit, double secs)
    {
        string name = Path.GetFileName(testFile);
        output = Normalize(output, pile);
        string expected = Expected(testFile, out bool ignored);
        bool exitOk = ExitOk(pile, ReadInit(testFile), exit);
        bool outOk = ignored || StripCr(expected) == StripCr(output);
        if (outOk && exitOk) return new TestResult(name, "PASS", secs, "");
        var detail = new StringBuilder();
        if (!exitOk) detail.Append($"exit code {exit}; ");
        if (!outOk) detail.Append("output differs");
        return new TestResult(name, "FAIL", secs, detail.ToString()) { };
    }
}

static class Worker
{
    // protocol: one test path per stdin line; one result line per test on stdout:
    // "RESULT\t<status>\t<seconds>\t<exit>\t<base64 output>"
    public static int Run(string[] args)
    {
        string testsDir = null, pile = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--tests") testsDir = args[++i];
            else if (args[i] == "--pile") pile = args[++i];
        }
        var pileDir = Path.Combine(testsDir, pile);
        Directory.SetCurrentDirectory(pileDir);
        var realStdout = Console.OpenStandardOutput();
        var writer = new StreamWriter(realStdout, new UTF8Encoding(false)) { AutoFlush = true };
        // keep Lean's own process-level output away from the protocol channel
        LeanIO.Stdout = Stream.Null;
        LeanIO.Stderr = Stream.Null;
        LeanHost.RunWithLargeStack(() => { LeanHost.Initialize(); return 0; });
        writer.WriteLine("READY");
        string line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var (exit, output, secs) = RunOne(pile, line);
            writer.WriteLine($"RESULT\t{secs:F2}\t{exit}\t{Convert.ToBase64String(Encoding.UTF8.GetBytes(output))}");
        }
        return 0;
    }

    public static (int exit, string output, double secs) RunOne(string pile, string testFile)
    {
        var sw = Stopwatch.StartNew();
        var outStream = new SyncMemoryStream();
        int exit;
        try
        {
            if (File.Exists(testFile + ".before.sh"))
            {
                var p = Process.Start(new ProcessStartInfo("bash", new[] { "--", testFile + ".before.sh" }) { RedirectStandardOutput = true, RedirectStandardError = true });
                p.WaitForExit();
            }
            var init = Piles.ReadInit(testFile);
            var args = Piles.LeanArgs(pile, testFile, init);
            var saved = init.Env.Keys.ToDictionary(k => k, k => Environment.GetEnvironmentVariable(k));
            foreach (var kv in init.Env) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            try
            {
                exit = LeanHost.RunWithLargeStack(() =>
                {
                    using (LeanStdStreams.Redirect(Stream.Null, outStream, outStream))
                        return LeanShell.RunOnCurrentThread(args);
                });
            }
            finally
            {
                foreach (var kv in saved) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            }
        }
        catch (Exception e)
        {
            var b = Encoding.UTF8.GetBytes("\nLEANSHARP EXCEPTION: " + e + "\n");
            outStream.Write(b, 0, b.Length);
            exit = 255;
        }
        return (exit, outStream.GetText(), sw.Elapsed.TotalSeconds);
    }

    public static int RunOneCli(string[] args)
    {
        string testsDir = null, pile = "elab", file = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--tests") testsDir = args[++i];
            else if (args[i] == "--pile") pile = args[++i];
            else file = args[i];
        }
        Directory.SetCurrentDirectory(Path.Combine(testsDir, pile));
        LeanHost.RunWithLargeStack(() => { LeanHost.Initialize(); return 0; });
        var full = Path.GetFullPath(file);
        var (exit, output, secs) = RunOne(pile, full);
        Console.Write(output);
        var r = Piles.Check(pile, full, output, exit, secs);
        Console.WriteLine($"== {r.Status} ({exit}, {secs:F2}s) {r.Detail}");
        if (r.Status != "PASS")
        {
            var expected = Piles.Expected(full, out _);
            File.WriteAllText("/tmp/leansharp.expected", expected);
            File.WriteAllText("/tmp/leansharp.produced", Piles.Normalize(output, pile));
            Console.WriteLine("(diff: diff /tmp/leansharp.expected /tmp/leansharp.produced)");
        }
        return r.Status == "PASS" ? 0 : 1;
    }
}

sealed class SyncMemoryStream : Stream
{
    readonly MemoryStream m = new();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length { get { lock (m) return m.Length; } }
    public override long Position { get => Length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { lock (m) m.Write(buffer, offset, count); }
    public string GetText() { lock (m) return Encoding.UTF8.GetString(m.ToArray()); }
}

static class Coordinator
{
    public static int Run(string[] args)
    {
        string testsDir = null, pile = "elab", filter = null, resultsFile = null;
        int jobs = Math.Max(1, Environment.ProcessorCount / 2);
        double timeout = 300;
        bool showDiffs = false;
        string rerun = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tests": testsDir = args[++i]; break;
                case "--pile": pile = args[++i]; break;
                case "--filter": filter = args[++i]; break;
                case "-j": jobs = int.Parse(args[++i]); break;
                case "--timeout": timeout = double.Parse(args[++i]); break;
                case "--results": resultsFile = args[++i]; break;
                case "--show-diffs": showDiffs = true; break;
                case "--rerun": rerun = args[++i]; break;
            }
        }
        testsDir = Path.GetFullPath(testsDir);
        var pileDir = Path.Combine(testsDir, pile);
        var re = filter == null ? null : new Regex(filter);
        // --rerun FILE: only the tests that did not pass in a previous results file
        HashSet<string> only = rerun == null ? null : File.ReadAllLines(rerun).Select(l => l.Split('\t')).Where(p => p.Length > 1 && p[1] != "PASS").Select(p => p[0]).ToHashSet();
        var tests = Directory.GetFiles(pileDir, Piles.Pattern(pile))
            .Where(f => !Piles.Skip(pile, f))
            .Where(f => re == null || re.IsMatch(Path.GetFileName(f)))
            .Where(f => only == null || only.Contains(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        Console.WriteLine($"{tests.Count} tests in {pile}, {jobs} workers");
        var queue = new System.Collections.Concurrent.ConcurrentQueue<string>(tests);
        var results = new System.Collections.Concurrent.ConcurrentBag<TestResult>();
        int done = 0;
        var sw = Stopwatch.StartNew();
        var threads = Enumerable.Range(0, jobs).Select(_ => new Thread(() =>
        {
            WorkerProc w = null;
            while (queue.TryDequeue(out var t))
            {
                w ??= new WorkerProc(testsDir, pile);
                var r = w.RunTest(t, timeout, out bool workerDead);
                if (workerDead) { w.Dispose(); w = null; }
                results.Add(r);
                int n = Interlocked.Increment(ref done);
                if (r.Status != "PASS")
                    Console.WriteLine($"[{n}/{tests.Count}] {r.Status} {r.Name} ({r.Seconds:F1}s) {r.Detail}");
                else if (n % 50 == 0)
                    Console.WriteLine($"[{n}/{tests.Count}] ... {sw.Elapsed.TotalMinutes:F1} min");
                if (showDiffs && r.Status == "FAIL") Console.WriteLine(w?.LastDiff ?? "");
            }
            w?.Dispose();
        }, 16 * 1024 * 1024)).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        var all = results.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        var summary = all.GroupBy(r => r.Status).Select(g => $"{g.Key}: {g.Count()}");
        Console.WriteLine($"Done in {sw.Elapsed.TotalMinutes:F1} min. " + string.Join(", ", summary));
        if (resultsFile != null)
            File.WriteAllLines(resultsFile, all.Select(r => $"{r.Name}\t{r.Status}\t{r.Seconds:F2}\t{r.Detail}"));
        return all.All(r => r.Status == "PASS") ? 0 : 1;
    }

    sealed class WorkerProc : IDisposable
    {
        readonly Process m_proc;
        readonly string m_pile;
        readonly System.Collections.Concurrent.BlockingCollection<string> m_lines = new();
        public string LastDiff;

        public WorkerProc(string testsDir, string pile)
        {
            m_pile = pile;
            var self = Environment.ProcessPath;
            var psi = new ProcessStartInfo(self) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            var entry = typeof(Program).Assembly.Location;
            if (Path.GetFileNameWithoutExtension(self) == "dotnet") psi.ArgumentList.Add(entry);
            psi.ArgumentList.Add("worker"); psi.ArgumentList.Add("--tests"); psi.ArgumentList.Add(testsDir);
            psi.ArgumentList.Add("--pile"); psi.ArgumentList.Add(pile);
            m_proc = Process.Start(psi);
            m_proc.ErrorDataReceived += (_, _) => { };
            m_proc.BeginErrorReadLine();
            var reader = new Thread(() =>
            {
                string l;
                try { while ((l = m_proc.StandardOutput.ReadLine()) != null) m_lines.Add(l); } catch { }
                m_lines.CompleteAdding();
            }) { IsBackground = true };
            reader.Start();
            // wait for READY
            foreach (var l in m_lines.GetConsumingEnumerable()) if (l == "READY") break;
        }

        public TestResult RunTest(string test, double timeout, out bool dead)
        {
            dead = false;
            var name = Path.GetFileName(test);
            var sw = Stopwatch.StartNew();
            try { m_proc.StandardInput.WriteLine(test); m_proc.StandardInput.Flush(); }
            catch { dead = true; return new TestResult(name, "CRASH", 0, "worker unavailable"); }
            while (true)
            {
                if (!m_lines.TryTake(out var l, TimeSpan.FromSeconds(Math.Max(0.1, timeout - sw.Elapsed.TotalSeconds))))
                {
                    dead = true;
                    if (m_lines.IsCompleted) return new TestResult(name, "CRASH", sw.Elapsed.TotalSeconds, $"worker exited ({(m_proc.HasExited ? m_proc.ExitCode : -1)})");
                    return new TestResult(name, "TIMEOUT", sw.Elapsed.TotalSeconds, "");
                }
                if (!l.StartsWith("RESULT\t")) continue;
                var parts = l.Split('\t');
                double secs = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                int exit = int.Parse(parts[2]);
                var output = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
                var r = Piles.Check(m_pile, test, output, exit, secs);
                if (r.Status != "PASS")
                {
                    var exp = Piles.Expected(test, out _).Split('\n');
                    var got = Piles.Normalize(output, m_pile).Split('\n');
                    var sb = new StringBuilder();
                    for (int i = 0, shown = 0; i < Math.Max(exp.Length, got.Length) && shown < 8; i++)
                    {
                        var e = i < exp.Length ? exp[i] : "<eof>";
                        var g = i < got.Length ? got[i] : "<eof>";
                        if (e != g) { sb.AppendLine($"   - {e}\n   + {g}"); shown++; }
                    }
                    LastDiff = sb.ToString();
                }
                return r;
            }
        }

        public void Dispose()
        {
            try { if (!m_proc.HasExited) m_proc.Kill(true); } catch { }
            m_proc.Dispose();
        }
    }
}
