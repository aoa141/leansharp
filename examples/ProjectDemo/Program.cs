// Example: build a Lake project and check a file with LeanSharp, entirely in-process.
//   dotnet run -c Release -- <sysroot> <project-dir> [file.lean]
using LeanSharp;

if (args.Length < 2) { Console.Error.WriteLine("usage: ProjectDemo <sysroot> <project-dir> [file.lean]"); return 2; }
LeanSysroot.Root = args[0];
var project = new LeanProject(args[1]);

var build = project.Build();
Console.WriteLine($"lake build -> {build.ExitCode}\n{build.Output}");
if (args.Length > 2)
{
    var check = project.CheckFile(args[2]);
    Console.WriteLine($"lean {args[2]} -> {check.ExitCode}\n{check.Output}");
}
return build.ExitCode;
