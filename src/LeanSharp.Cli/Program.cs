// `leansharp` command line: `leansharp [lean] <lean args>` or `leansharp lake <lake args>`.

using LeanSharp;

if (args.Length > 0 && args[0] == "lake")
    return LakeShell.Main(args[1..]);
if (args.Length > 0 && args[0] == "lean")
    return LeanShell.Main(args[1..]);
return LeanShell.Main(args);
