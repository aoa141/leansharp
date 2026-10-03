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
        // the launcher scripts in `<sysroot>/bin` (used by the script-driven piles) start this program
        LeanSysroot.LauncherAssembly = typeof(Program).Assembly.Location;
        if (args.Length > 0 && (args[0] == "lean" || args[0] == "lake"))
        {
            LeanProgramState.TopLevelProgramOwnsProcess = true;
            return args[0] == "lean" ? LeanShell.Main(args[1..]) : LakeShell.Main(args[1..]);
        }
        if (args.Length > 0 && args[0] == "leantar") return LeanSharp.Leantar.LeantarCli.Main(args[1..], Console.In, Console.Out, Console.Error);
        if (args.Length > 0 && args[0] == "leanc") return ManagedToolchain.Leanc(args[1..], Console.Error);
        if (args.Length > 0 && args[0] == "worker") return Worker.Run(args[1..]);
        // test names and diffs are not ASCII; the default on Windows is the console's code page
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length > 0 && args[0] == "run") return Coordinator.Run(args[1..]);
        if (args.Length > 0 && args[0] == "one") return Worker.RunOneCli(args[1..]);
        Console.Error.WriteLine("usage: LeanSharp.TestRunner run --tests DIR --pile PILE [--filter RE] [-j N] [--mem GB] [--timeout S] [--results FILE] [--rerun FILE] [--show-diffs] [--include-unsupported]");
        Console.Error.WriteLine("       piles run in-process: elab elab_fail elab_bench compile compile_bench docparse server server_interactive");
        Console.Error.WriteLine("       piles run through their shell scripts: pkg misc misc_dir lake");
        Console.Error.WriteLine("       LeanSharp.TestRunner one --tests DIR --pile elab FILE.lean");
        return 2;
    }
}

record TestResult(string Name, string Status, double Seconds, string Detail);

static class Shell
{
    static string s_bash;

    /// <summary>
    /// The `bash` that runs the test scripts: `LEANSHARP_BASH` if set. On Windows it must be an
    /// MSYS bash (Git for Windows), as in Lean's own test setup; the `bash.exe` of the Windows
    /// directories starts WSL, which cannot run the Windows launchers.
    /// </summary>
    public static string Bash => s_bash ??= Find();

    static string Find()
    {
        var env = Environment.GetEnvironmentVariable("LEANSHARP_BASH");
        if (!string.IsNullOrEmpty(env)) return env;
        if (!OperatingSystem.IsWindows()) return "bash";
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        // Git for Windows: `<git>\cmd\git.exe` or `<git>\bin\git.exe` on PATH, bash in `<git>\bin`
        var candidates = dirs.Where(d => File.Exists(Path.Combine(d, "git.exe"))).Select(d => Path.Combine(d, "..", "bin", "bash.exe"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"))
            .Concat(dirs.Where(d => !d.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase) && !d.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase))
                .Select(d => Path.Combine(d, "bash.exe")));
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);
        return "bash";
    }

    /// <summary>A path as passed to scripts in environment variables (forward slashes on Windows too).</summary>
    public static string ScriptPath(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
}

static class Piles
{
    /// <summary>Settings a test's `.init.sh` can define (`export X=Y`, `unset X`, `TEST_EXIT=..`, `TEST_ARGS=( .. )`).</summary>
    public sealed class Init
    {
        public Dictionary<string, string> Env = new();   // null value = unset
        public string Exit;                               // null, "nonzero" or a number
        public List<string> Args = new();                 // TEST_ARGS: arguments of the program
        public List<string> LeanArgs = new();             // TEST_LEAN_ARGS: extra `lean` options
    }

    public static Init ReadInit(string testFile)
    {
        var r = new Init();
        var f = testFile + ".init.sh";
        if (!File.Exists(f)) return r;
        int skipDepth = 0; // inside `if [[ -n $TEST_BENCH ]]; then ... fi` (benchmark-only settings)
        foreach (var raw in File.ReadAllLines(f))
        {
            var l = raw.Trim();
            if (l.StartsWith("if "))
            {
                if (skipDepth > 0 || l.Contains("$TEST_BENCH")) skipDepth++;
                continue;
            }
            if (l == "fi") { if (skipDepth > 0) skipDepth--; continue; }
            if (skipDepth > 0) continue;
            if (l.StartsWith("export ") && l.Contains('='))
            {
                var kv = l.Substring(7).Split('=', 2);
                r.Env[kv[0].Trim()] = kv[1].Trim().Trim('"', '\'');
            }
            else if (l.StartsWith("unset ")) r.Env[l.Substring(6).Trim()] = null;
            else if (l.StartsWith("TEST_EXIT=")) r.Exit = l.Substring(10).Trim();
            else if (l.StartsWith("TEST_ARGS=("))
                r.Args = l.Substring(11).TrimEnd(')').Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            else if (l.StartsWith("TEST_LEAN_ARGS=("))
                r.LeanArgs = l.Substring(16).TrimEnd(')').Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return r;
    }

    /// <summary>File pattern of the tests of a pile.</summary>
    public static string Pattern(string pile) => pile == "docparse" ? "*.txt" : "*.lean";

    /// <summary>The pile whose `run_test.sh` conventions `pile` follows.</summary>
    static string Kind(string pile) => pile switch
    {
        "elab_bench" => "elab",
        "compile_bench" => "compile",
        _ => pile,
    };

    public static bool Skip(string pile, string f)
    {
        if (File.Exists(f + ".no_test")) return true;
        var name = Path.GetFileName(f);
        // files some tests create next to themselves (e.g. `compile_bench/incr_header_load`)
        if (name.StartsWith("_tmp_", StringComparison.Ordinal)) return true;
        if (pile == "docparse" || pile == "server" || pile == "server_interactive") { if (name == "run_test.lean") return true; }
        if (Kind(pile) == "compile")
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
        switch (Kind(pile))
        {
            case "server_interactive": return new[] { "-Dlinter.all=false", "--run", "run_test.lean", name };
            case "elab": return new[] { "--root=..", "-DprintMessageEndPos=true", "-Dlinter.all=false", "-DElab.inServer=true", "-Dcompiler.postponeCompile=false" }.Concat(init.LeanArgs).Append(name).ToArray();
            case "elab_fail": return new[] { "--root=..", "-DprintMessageEndPos=true", "-Dlinter.all=false", "-DElab.inServer=true" }.Concat(init.LeanArgs).Append(name).ToArray();
            case "compile": return new[] { "-Dlinter.all=false", "--run", name }.Concat(init.Args).ToArray();
            case "server": return new[] { "-Dlinter.all=false", "--run", name };
            case "docparse": return new[] { "-Dlinter.all=false", "--run", "run_test.lean", name };
            default: throw new ArgumentException($"unsupported pile '{pile}'");
        }
    }

    public static bool ExitOk(string pile, Init init, int exit)
    {
        if (pile == "elab_fail") return exit != 0;
        if ((Kind(pile) == "compile" || pile == "server_interactive") && init.Exit != null)
            return init.Exit == "nonzero" ? exit != 0 : exit == int.Parse(init.Exit);
        return exit == 0;
    }

    public static bool NormalizeElab(string pile) => Kind(pile) == "elab" || pile == "elab_fail" || pile == "server_interactive";

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
        long recycleAt = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--tests") testsDir = args[++i];
            else if (args[i] == "--pile") pile = args[++i];
            else if (args[i] == "--recycle-at") recycleAt = long.Parse(args[++i]);
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
        // test names are sent as UTF-8 (not in the console's code page, as `Console.In` reads on Windows)
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while ((line = stdin.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var (exit, output, secs) = RunOne(pile, line);
            // heap size after the test (after a full collection if it looks large), so the
            // coordinator can replace a worker that has grown too much
            long heap = GC.GetTotalMemory(false);
            if (recycleAt > 0 && heap > recycleAt) heap = GC.GetTotalMemory(true);
            writer.WriteLine($"RESULT\t{secs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}\t{exit}\t{Convert.ToBase64String(Encoding.UTF8.GetBytes(output))}\t{heap}");
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
            RunHook(testFile + ".before.sh");
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
            RunHook(testFile + ".after.sh");
        }
        catch (Exception e)
        {
            var b = Encoding.UTF8.GetBytes("\nLEANSHARP EXCEPTION: " + e + "\n");
            outStream.Write(b, 0, b.Length);
            exit = 255;
        }
        return (exit, outStream.GetText(), sw.Elapsed.TotalSeconds);
    }

    /// <summary>Runs a test's `.before.sh`/`.after.sh` script, if it exists.</summary>
    static void RunHook(string script)
    {
        if (!File.Exists(script)) return;
        var psi = new ProcessStartInfo(Shell.Bash, new[] { "--", Shell.ScriptPath(script) }) { RedirectStandardOutput = true, RedirectStandardError = true };
        // `lean`/`lake` in the script are the launchers of the sysroot
        psi.Environment["PATH"] = LeanSysroot.BinDir + Path.PathSeparator + Path.GetDirectoryName(Environment.ProcessPath) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["LEANSHARP_SYSROOT"] = LeanSysroot.Root;
        var p = Process.Start(psi);
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        p.WaitForExit();
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
            var exp = Path.Combine(Path.GetTempPath(), "leansharp.expected");
            var got = Path.Combine(Path.GetTempPath(), "leansharp.produced");
            File.WriteAllText(exp, expected);
            File.WriteAllText(got, Piles.Normalize(output, pile));
            Console.WriteLine($"(diff: diff {exp} {got})");
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
        // every worker holds a Lean process image (several GB once tests `import Lean`): the
        // default number of workers is bounded by memory as well as by cores. Exhausting the
        // machine's memory can take down the whole machine (it did under WSL).
        long totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        int jobs = (int)Math.Max(1, Math.Min(Environment.ProcessorCount / 2, totalMem / (6L << 30)));
        double memGb = 0; // per worker; 0 = derive from the machine's memory
        double timeout = 300;
        bool showDiffs = false, includeUnsupported = false;
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
                case "--include-unsupported": includeUnsupported = true; break;
                case "--mem": memGb = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
            }
        }
        testsDir = Path.GetFullPath(testsDir);
        var pileDir = Path.Combine(testsDir, pile);
        var re = filter == null ? null : new Regex(filter);
        // --rerun FILE: only the tests that did not pass in a previous results file
        HashSet<string> only = rerun == null ? null : File.ReadAllLines(rerun).Select(l => l.Split('\t')).Where(p => p.Length > 1 && p[1] != "PASS").Select(p => p[0]).ToHashSet();
        if (ScriptPiles.IsScriptPile(pile))
            return ScriptPiles.Run(testsDir, pile, re, only, jobs, timeout, resultsFile, showDiffs, includeUnsupported);
        var tests = Directory.GetFiles(pileDir, Piles.Pattern(pile))
            .Where(f => !Piles.Skip(pile, f))
            .Where(f => re == null || re.IsMatch(Path.GetFileName(f)))
            .Where(f => only == null || only.Contains(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        // A Windows checkout without the privilege to create symbolic links has, for a test that
        // is a link to another test, a file containing the target's name: copy the target over it.
        if (OperatingSystem.IsWindows())
            foreach (var f in tests)
            {
                if (new FileInfo(f).Length >= 260) continue;
                var target = File.ReadAllText(f);
                if (target.Length == 0 || target.IndexOfAny(new[] { '\n', ' ', ':' }) >= 0) continue;
                var full = Path.GetFullPath(target, Path.GetDirectoryName(f));
                if (!File.Exists(full)) continue;
                Console.WriteLine($"{Path.GetFileName(f)}: replacing the checked-out link by a copy of {target}");
                File.Copy(full, f, overwrite: true);
            }
        // hard limit of a worker's managed heap (it fails with an out-of-memory error instead of
        // starving the machine); a worker is replaced once its live heap exceeds 70% of it
        long memLimit = memGb > 0 ? (long)(memGb * (1L << 30)) : Math.Max(3L << 30, (long)(totalMem * 0.75) / jobs);
        Console.WriteLine($"{tests.Count} tests in {pile}, {jobs} workers, {memLimit / (double)(1L << 30):F1} GB per worker");
        var queue = new System.Collections.Concurrent.ConcurrentQueue<string>(tests);
        var results = new System.Collections.Concurrent.ConcurrentBag<TestResult>();
        int done = 0;
        var sw = Stopwatch.StartNew();
        var threads = Enumerable.Range(0, jobs).Select(_ => new Thread(() =>
        {
            WorkerProc w = null;
            while (queue.TryDequeue(out var t))
            {
                w ??= new WorkerProc(testsDir, pile, memLimit);
                var r = w.RunTest(t, timeout, out bool workerDead);
                if (workerDead) { w.Dispose(); w = null; }
                results.Add(r);
                int n = Interlocked.Increment(ref done);
                if (r.Status != "PASS")
                    Console.WriteLine($"[{n}/{tests.Count}] {r.Status} {r.Name} ({r.Seconds:F1}s) {r.Detail}");
                else if (n % 50 == 0)
                    Console.WriteLine($"[{n}/{tests.Count}] ... {sw.Elapsed.TotalMinutes:F1} min");
                if (showDiffs && r.Status == "FAIL") Console.WriteLine(w?.LastDiff ?? "");
                if (w != null && w.LastHeap > memLimit * 7 / 10) { w.Dispose(); w = null; }
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
        public long LastHeap;

        public WorkerProc(string testsDir, string pile, long memLimit)
        {
            m_pile = pile;
            var self = Environment.ProcessPath;
            var psi = new ProcessStartInfo(self) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
            var entry = typeof(Program).Assembly.Location;
            if (Path.GetFileNameWithoutExtension(self) == "dotnet") psi.ArgumentList.Add(entry);
            psi.ArgumentList.Add("worker"); psi.ArgumentList.Add("--tests"); psi.ArgumentList.Add(testsDir);
            psi.ArgumentList.Add("--pile"); psi.ArgumentList.Add(pile);
            psi.ArgumentList.Add("--recycle-at"); psi.ArgumentList.Add((memLimit * 7 / 10).ToString());
            psi.Environment["DOTNET_GCHeapHardLimit"] = "0x" + memLimit.ToString("x");
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
                LastHeap = parts.Length > 4 ? long.Parse(parts[4]) : 0;
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

/// <summary>
/// Piles and test directories that are driven by shell scripts (`pkg`, `misc`, `misc_dir`, `lake`):
/// the scripts are run with `bash` exactly like Lean's CMake test suite does, with the `lean` and
/// `lake` launchers of the LeanSharp sysroot first on `PATH`. A test passes if its script exits
/// with 0.
/// </summary>
static class ScriptPiles
{
    public static bool IsScriptPile(string pile) => pile is "pkg" or "misc" or "misc_dir" or "lake";

    /// <summary>
    /// Tests of scenarios LeanSharp does not support by design (key: `pile/test`, value: why).
    /// They are not run unless `--include-unsupported` is given.
    /// </summary>
    static readonly Dictionary<string, string> s_unsupported = new(StringComparer.Ordinal)
    {
        // hand-written C linked with Lean code, or with the native Lean runtime
        ["lake/examples/precompile"] = "links a C implementation of an `@[extern]` function",
        ["lake/examples/reverse-ffi"] = "C program linked against the native Lean runtime",
        ["lake/tests/8448"] = "links a hand-written C object into a shared library",
        ["lake/tests/externLib"] = "links an external C library",
        ["misc_dir/rc_sticky"] = "C test of the native runtime (lean.h)",
        // expect the diagnostics of a native linker
        ["lake/tests/precompileLink"] = "expects a native linker error (`-lBogus`)",
        ["pkg/def_clash"] = "expects a native linker symbol clash",
        // different, but sound, behavior
        ["lake/tests/challenge-olean-issue"] = "forges a `Nat` with `unsafeCast`; the managed kernel rejects it already at build time",
    };

    /// <summary>Like <see cref="s_unsupported"/>, on Windows only.</summary>
    static readonly Dictionary<string, string> s_unsupportedOnWindows = new(StringComparer.Ordinal)
    {
        // `which` of Git Bash only lists files that look executable (a PE image or `#!`); the
        // stub `libleanshared.dll` is a text file
        ["lake/tests/env"] = "ends by finding the native `libleanshared.dll` on `PATH` with `which`",
    };

    static string UnsupportedReason(string key) =>
        s_unsupported.TryGetValue(key, out var r) ? r
        : OperatingSystem.IsWindows() && s_unsupportedOnWindows.TryGetValue(key, out r) ? r + " (Windows)" : null;

    /// <summary>(test name, working directory, bash command) of every test of the pile.</summary>
    static IEnumerable<(string name, string dir, string cmd)> Enumerate(string testsDir, string pile)
    {
        var pileDir = Path.Combine(testsDir, pile);
        switch (pile)
        {
            case "misc":
                foreach (var f in Directory.GetFiles(pileDir, "*.sh").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(f);
                    if (name == "run_test.sh" || File.Exists(f + ".no_test")) continue;
                    yield return (name, pileDir, $"source \"$TEST_DIR/util.sh\"; source run_test.sh {Quote(name)}");
                }
                break;
            case "pkg":
            case "misc_dir":
                foreach (var d in Directory.GetDirectories(pileDir).OrderBy(d => d, StringComparer.Ordinal))
                {
                    if (!File.Exists(Path.Combine(d, "run_test.sh"))) continue;
                    // excluded in tests/CMakeLists.txt as flaky/nondeterministic
                    if (pile == "pkg" && Path.GetFileName(d) is "signal" or "test_extern" or "user_ext") continue;
                    yield return (Path.GetFileName(d), d, "source \"$TEST_DIR/util.sh\"; source run_test.sh");
                }
                break;
            case "lake":
                foreach (var sub in new[] { "examples", "tests" })
                {
                    var root = Path.Combine(pileDir, sub);
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in Directory.GetFiles(root, "test.sh", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                    {
                        // as in tests/CMakeLists.txt
                        if (Regex.IsMatch(f, "lake-packages|bootstrap|toolchain|online")) continue;
                        var d = Path.GetDirectoryName(f);
                        yield return (Path.GetRelativePath(pileDir, d).Replace('\\', '/'), d, "set -eu; LAKE=lake ./test.sh");
                    }
                }
                break;
        }
    }

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static int Run(string testsDir, string pile, Regex filter, HashSet<string> only, int jobs, double timeout, string resultsFile, bool showOutput, bool includeUnsupported)
    {
        var all0 = Enumerate(testsDir, pile)
            .Where(t => filter == null || filter.IsMatch(t.name))
            .Where(t => only == null || only.Contains(t.name)).ToList();
        var tests = includeUnsupported ? all0 : all0.Where(t => UnsupportedReason(pile + "/" + t.name) == null).ToList();
        foreach (var t in all0.Except(tests))
            Console.WriteLine($"not run (unsupported scenario): {t.name} -- {UnsupportedReason(pile + "/" + t.name)}");
        // make sure the launchers exist and point to this program
        var bin = LeanSysroot.BinDir;
        _ = LeanSysroot.Root; LeanSysroot.Root = LeanSysroot.Root;
        var dotnetDir = Path.GetDirectoryName(Environment.ProcessPath);
        Console.WriteLine($"{tests.Count} tests in {pile}, {jobs} at a time, sysroot {LeanSysroot.Root}");
        var queue = new System.Collections.Concurrent.ConcurrentQueue<(string name, string dir, string cmd)>(tests);
        var results = new System.Collections.Concurrent.ConcurrentBag<TestResult>();
        var outputs = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        int done = 0;
        var sw = Stopwatch.StartNew();
        var threads = Enumerable.Range(0, jobs).Select(_ => new Thread(() =>
        {
            while (queue.TryDequeue(out var t))
            {
                var r = RunOne(testsDir, bin, dotnetDir, t, timeout, out string output);
                results.Add(r);
                outputs[t.name] = output;
                int n = Interlocked.Increment(ref done);
                Console.WriteLine($"[{n}/{tests.Count}] {r.Status} {r.Name} ({r.Seconds:F1}s) {r.Detail}");
                if (showOutput && r.Status != "PASS")
                {
                    var lines = output.Split('\n');
                    Console.WriteLine(string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 40)).Select(l => "   | " + l)));
                }
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        var all = results.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        Console.WriteLine($"Done in {sw.Elapsed.TotalMinutes:F1} min. " + string.Join(", ", all.GroupBy(r => r.Status).Select(g => $"{g.Key}: {g.Count()}")));
        if (resultsFile != null)
        {
            File.WriteAllLines(resultsFile, all.Select(r => $"{r.Name}\t{r.Status}\t{r.Seconds:F2}\t{r.Detail}"));
            var outDir = resultsFile + ".out";
            Directory.CreateDirectory(outDir);
            foreach (var kv in outputs) File.WriteAllText(Path.Combine(outDir, kv.Key.Replace('/', '_') + ".txt"), kv.Value);
        }
        return all.All(r => r.Status == "PASS") ? 0 : 1;
    }

    static TestResult RunOne(string testsDir, string bin, string dotnetDir, (string name, string dir, string cmd) t, double timeout, out string output)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(Shell.Bash) { WorkingDirectory = t.dir, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(t.cmd + " 2>&1");
        var env = psi.Environment;
        string sysroot = LeanSysroot.Root;
        // No other Lean installation may leak into the tests: elan's proxies (`leanc`,
        // `leanchecker`, ...) would silently run a native toolchain.
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(d => !d.Replace('\\', '/').Contains("/.elan/"));
        env["PATH"] = bin + Path.PathSeparator + dotnetDir + Path.PathSeparator + string.Join(Path.PathSeparator, path);
        env["LEANSHARP_SYSROOT"] = sysroot;
        // the variables of `tests/with_env.sh.in`
        env["STAGE"] = "1";
        env["TEST_DIR"] = Shell.ScriptPath(testsDir);
        env["SRC_DIR"] = Shell.ScriptPath(Path.GetFullPath(Path.Combine(testsDir, "..", "src")));
        env["SCRIPT_DIR"] = Shell.ScriptPath(Path.GetFullPath(Path.Combine(testsDir, "..", "script")));
        env["BUILD_DIR"] = Shell.ScriptPath(sysroot);
        env["LEAN_CC"] = "cc";
        env["LEANC_OPTS"] = "";
        env["CXX"] = "c++";
        // the user's git configuration must not rewrite the files in the tests' repositories
        // (`core.autocrlf=true`, common on Windows, makes Lake's checkouts fail)
        env["GIT_CONFIG_COUNT"] = "1";
        env["GIT_CONFIG_KEY_0"] = "core.autocrlf";
        env["GIT_CONFIG_VALUE_0"] = "false";
        env.Remove("LEAN_PATH"); env.Remove("LEAN_SYSROOT"); env.Remove("LAKE"); env.Remove("LAKE_HOME"); env.Remove("ELAN_TOOLCHAIN");
        var sb = new StringBuilder();
        using var p = Process.Start(psi);
        p.StandardInput.Close();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.Append(e.Data).Append('\n'); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.Append(e.Data).Append('\n'); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        bool exited = p.WaitForExit(TimeSpan.FromSeconds(timeout));
        if (!exited)
        {
            try { p.Kill(true); } catch { }
            p.WaitForExit(5000);
            lock (sb) output = sb.ToString();
            return new TestResult(t.name, "TIMEOUT", sw.Elapsed.TotalSeconds, "");
        }
        p.WaitForExit();
        lock (sb) output = sb.ToString();
        return p.ExitCode == 0
            ? new TestResult(t.name, "PASS", sw.Elapsed.TotalSeconds, "")
            : new TestResult(t.name, "FAIL", sw.Elapsed.TotalSeconds, $"exit code {p.ExitCode}");
    }
}
