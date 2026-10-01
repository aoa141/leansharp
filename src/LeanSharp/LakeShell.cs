// Entry point of the Lake build tool (the `main` function of the `LakeMain` module).

using LeanSharp.Compiled;
using LeanSharp.Runtime;

namespace LeanSharp;

public static unsafe class LakeShell
{
    /// <summary>Runs `lake` with the given arguments on the current (large-stack) thread.</summary>
    public static int RunOnCurrentThread(string[] argv)
    {
        LeanHost.Initialize();
        using (LeanHost.EnterProgram())
            return Run(argv);
    }

    static int Run(string[] argv)
    {
        LeanPaths.AppPath = LeanSysroot.LakeExe;
        lock (typeof(LakeShell))
        {
            var r0 = M_LakeMain.initialize(1);
            if (LeanRt.lean_io_result_is_error(r0)) { LeanRt.lean_io_result_show_error(r0); return 1; }
        }
        LeanRt.lean_io_mark_end_initialization();
        Obj args = LeanRt.lean_box(0);
        for (int k = argv.Length - 1; k >= 0; k--)
            args = LeanRt.lean_mk_list_cons(LeanRt.lean_mk_string(argv[k]), args);
        try
        {
            Obj r = M_LakeMain._lean_main(args);
            if (LeanRt.lean_io_result_is_ok(r))
                return (int)LeanRt.lean_unbox_uint32(LeanRt.lean_io_result_get_value(r));
            LeanRt.lean_io_result_show_error(r);
            return 1;
        }
        catch (LeanExitException e)
        {
            return e.ExitCode;
        }
    }

    public static int Main(string[] argv)
    {
        try { return LeanHost.RunWithLargeStack(() => RunOnCurrentThread(argv)); }
        catch (LeanExitException e) { return e.ExitCode; }
    }
}
