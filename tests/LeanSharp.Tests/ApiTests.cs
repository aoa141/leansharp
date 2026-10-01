// End-to-end tests of the public API: every test runs Lean in-process through `LeanProject`.

using LeanSharp;
using Xunit;

namespace LeanSharp.Tests;

/// <summary>Locates the sysroot and creates scratch project directories.</summary>
public sealed class LeanFixture : IDisposable
{
    public string Root { get; }
    public string SkipReason { get; }

    public LeanFixture()
    {
        string sysroot = Environment.GetEnvironmentVariable("LEANSHARP_SYSROOT");
        if (string.IsNullOrEmpty(sysroot))
        {
            // artifacts/selfhost of the repository this test assembly was built in
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var c = Path.Combine(d.FullName, "artifacts", "selfhost");
                if (File.Exists(Path.Combine(c, "lib", "lean", "Init.olean"))) { sysroot = c; break; }
            }
        }
        if (string.IsNullOrEmpty(sysroot) || !File.Exists(Path.Combine(sysroot, "lib", "lean", "Init.olean")))
            SkipReason = "no Lean sysroot: set LEANSHARP_SYSROOT or run `LeanSharp.Cli build-stdlib <lean4/src> artifacts/selfhost`";
        else
            LeanSysroot.Root = sysroot;
        Root = Path.Combine(Path.GetTempPath(), "leansharp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public LeanProject NewProject(params (string path, string text)[] files)
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, text);
        }
        return new LeanProject(dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

public class ApiTests : IClassFixture<LeanFixture>
{
    readonly LeanFixture m_fx;
    public ApiTests(LeanFixture fx) => m_fx = fx;

    [SkippableFact]
    public void Version()
    {
        Skip.If(m_fx.SkipReason != null, m_fx.SkipReason);
        var r = m_fx.NewProject().Lean("--version");
        Assert.Equal(0, r.ExitCode);
        Assert.StartsWith("Lean (version 4.", r.Stdout);
        Assert.Contains(LeanStdlib.LeanVersion, r.Stdout);
    }

    [SkippableFact]
    public void ElaboratesDefinitionsTheoremsAndEval()
    {
        Skip.If(m_fx.SkipReason != null, m_fx.SkipReason);
        var p = m_fx.NewProject(("T.lean", """
            def fib : Nat → Nat | 0 => 0 | 1 => 1 | n+2 => fib n + fib (n+1)
            theorem fib10 : fib 10 = 55 := by decide
            theorem add_comm' (a b : Nat) : a + b = b + a := by omega
            #eval fib 20
            #check fib10
            """));
        var r = p.RunFile("T.lean");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("6765\nfib10 : fib 10 = 55\n", r.Stdout);
    }

    [SkippableFact]
    public void ReportsErrorsWithExitCode1()
    {
        Skip.If(m_fx.SkipReason != null, m_fx.SkipReason);
        var p = m_fx.NewProject(("Bad.lean", "theorem wrong : 1 = 2 := rfl\n"));
        var r = p.RunFile("Bad.lean");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("Bad.lean:1:25: error", r.Output);
    }

    [SkippableFact]
    public void RunsMainWithTheInterpreter()
    {
        Skip.If(m_fx.SkipReason != null, m_fx.SkipReason);
        var p = m_fx.NewProject(("Main.lean", """
            def main (args : List String) : IO UInt32 := do
              IO.println s!"hello {args}"
              return 7
            """));
        var r = p.RunMain("Main.lean", "a", "b");
        Assert.Equal("hello [a, b]\n", r.Stdout);
        Assert.Equal(7, r.ExitCode);
    }

    [SkippableFact]
    public void BvDecideUsesTheManagedSatSolver()
    {
        Skip.If(m_fx.SkipReason != null, m_fx.SkipReason);
        var p = m_fx.NewProject(("Bv.lean", """
            import Std.Tactic.BVDecide
            example (x y : BitVec 16) : x &&& y = y &&& x := by bv_decide
            example (x : BitVec 8) : x + 1 ≠ x := by bv_decide
            """));
        var r = p.RunFile("Bv.lean");
        Assert.True(r.Success, r.Output);
    }

    [SkippableFact]
    public void ReadsStandardInput()
    {
        Skip.If(m_fx.SkipReason != null, m_fx.SkipReason);
        var r = m_fx.NewProject().LeanWithInput("#eval 1 + 1\n", "--stdin");
        Assert.Equal("2\n", r.Stdout);
    }

    [SkippableFact]
    public void BuildsALakeProjectAndChecksAFile()
    {
        Skip.If(m_fx.SkipReason != null, m_fx.SkipReason);
        var p = m_fx.NewProject(
            ("lakefile.toml", "name = \"demo\"\ndefaultTargets = [\"Demo\"]\n\n[[lean_lib]]\nname = \"Demo\"\n"),
            ("Demo.lean", "import Demo.Basic\nimport Demo.Thm\n"),
            ("Demo/Basic.lean", "def double (n : Nat) : Nat := n + n\n"),
            ("Demo/Thm.lean", "import Demo.Basic\ntheorem double_eq (n : Nat) : double n = 2 * n := by unfold double; omega\n"),
            ("Scratch.lean", "import Demo\n#check double_eq\n#eval double 21\n"));
        var build = p.Build();
        Assert.True(build.Success, build.Output);
        Assert.True(File.Exists(Path.Combine(p.Directory, ".lake", "build", "lib", "lean", "Demo", "Thm.olean")));

        var check = p.CheckFile("Scratch.lean");
        Assert.True(check.Success, check.Output);
        Assert.Equal("double_eq (n : Nat) : double n = 2 * n\n42\n", check.Stdout);

        // a second build has nothing to do
        var again = p.Build();
        Assert.True(again.Success, again.Output);
        Assert.DoesNotContain("Built Demo", again.Output);
    }

    [SkippableFact]
    public void LakeBuildFailsOnAnError()
    {
        Skip.If(m_fx.SkipReason != null, m_fx.SkipReason);
        var p = m_fx.NewProject(
            ("lakefile.toml", "name = \"bad\"\ndefaultTargets = [\"Bad\"]\n\n[[lean_lib]]\nname = \"Bad\"\n"),
            ("Bad.lean", "example : False := trivial\n"));
        var build = p.Build();
        Assert.False(build.Success);
        Assert.Contains("error", build.Output);
    }

    [SkippableFact]
    public void ProgramsDoNotShareWorkingDirectoryEnvironmentOrGlobalState()
    {
        Skip.If(m_fx.SkipReason != null, m_fx.SkipReason);
        // each file registers the same option: with shared global state the second one would fail
        const string text = """
            import Lean
            register_option leansharp.test.opt : Nat := { defValue := 1, descr := "test" }
            #eval do IO.println (← IO.currentDir).fileName.get!.length
            #eval show IO Unit from do IO.println ((← IO.getEnv "LEANSHARP_TEST_VAR").getD "unset")
            """;
        var p1 = m_fx.NewProject(("A.lean", text));
        var p2 = m_fx.NewProject(("A.lean", text));
        p1.Environment["LEANSHARP_TEST_VAR"] = "one";
        LeanResult r1 = null, r2 = null;
        var t1 = new Thread(() => r1 = p1.RunFile("A.lean"));
        var t2 = new Thread(() => r2 = p2.RunFile("A.lean"));
        t1.Start(); t2.Start(); t1.Join(); t2.Join();
        Assert.True(r1.Success && r2.Success, r1.Output + "\n--\n" + r2.Output);
        Assert.Equal("32\none\n", r1.Stdout + r1.Stderr);
        Assert.Equal("32\nunset\n", r2.Stdout + r2.Stderr);
        Assert.Equal(Environment.CurrentDirectory, Directory.GetCurrentDirectory());
    }
}
