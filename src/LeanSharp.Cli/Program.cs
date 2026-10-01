// `leansharp` command line:
//   leansharp [lean] <lean args>
//   leansharp lake <lake args>
//   leansharp leanc <cc args>                                   "compile"/"link" Lean-generated C files (see ManagedToolchain)
//   leansharp build-stdlib <lean4/src> <sysroot> [targets...]   build the Lean library files with LeanSharp

using LeanSharp;

LeanSharp.Runtime.LeanProgramState.TopLevelProgramOwnsProcess = true;
if (args.Length > 0 && args[0] == "lake")
    return LakeShell.Main(args[1..]);
if (args.Length > 0 && args[0] == "lean")
    return LeanShell.Main(args[1..]);
if (args.Length > 0 && args[0] == "leanc")
    return ManagedToolchain.Leanc(args[1..], Console.Error);
if (args.Length > 0 && args[0] == "build-stdlib")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: leansharp build-stdlib <lean4/src> <sysroot> [targets...]");
        return 2;
    }
    var r = LeanStdlib.Build(args[1], args[2], args[3..], onOutputLine: Console.WriteLine);
    return r.ExitCode;
}
return LeanShell.Main(args);
