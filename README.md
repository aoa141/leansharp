# LeanSharp

A pure .NET (C#, .NET 10) implementation of [Lean 4](https://github.com/leanprover/lean4).
LeanSharp runs the Lean elaborator, kernel, compiler front end and Lake build tool without any
native code, so Lean can be used where only managed .NET code may run (e.g. Microsoft CloudBuild).

Status: **work in progress** (see "Status" below). It needs only the .NET SDK and the Lean
sources: it compiles Lean's standard library itself.

## How it works

Lean is mostly written in Lean. LeanSharp translates Lean's compiled intermediate representation
(IR) of `Init`, `Std`, `Lean` and `Lake` to C# (`gen/`, ~12.8M lines), and ports the C/C++ parts
(runtime, kernel, IR interpreter, `.olean` serialization, CaDiCaL, libuv) by hand to
`src/LeanSharp.Runtime`. See [Docs/DESIGN.md](Docs/DESIGN.md).

| Project | Contents |
|---------|----------|
| `src/LeanSharp.Runtime` | Object model, runtime, kernel, IR interpreter, `.olean` reader/writer, SAT solver |
| `src/LeanSharp.Lean`    | The Lean libraries/compiler translated to C# (generated from `gen/`) |
| `src/LeanSharp`         | Host library: initialization, `lean` and `lake` entry points, `LeanProject`, `LeanStdlib`, in-process subprocesses |
| `src/LeanSharp.Cli`     | Command-line tool (`LeanSharp.Cli lean ...`, `LeanSharp.Cli lake ...`) |
| `tools/EmitCSharp`      | The IR → C# emitter (development only; needs a native Lean build of the same commit) |

## Quick start

Requirements: the .NET 10 SDK, 32 GB of RAM for the build, and a checkout of the Lean sources at
Lean 4.36.0-pre (commit `77f336f7ae`) — only the sources: nothing native is built or run. Full
instructions: [Docs/RUNNING.md](Docs/RUNNING.md).

```sh
dotnet build src/LeanSharp.Cli -c Release            # first build: 1-10 minutes

CLI="dotnet src/LeanSharp.Cli/bin/Release/net10.0/LeanSharp.Cli.dll"

# build Lean's standard library (Init, Std, Lean, Lake) with LeanSharp: ~8 min on 24 cores
$CLI build-stdlib ~/Repos/lean4/src artifacts/selfhost
export LEANSHARP_SYSROOT=$PWD/artifacts/selfhost

$CLI MyFile.lean          # like `lean MyFile.lean`
$CLI --run Main.lean      # run `main` with the interpreter
$CLI lake build           # like `lake build`, everything in-process
$CLI lake exe myprog      # executables are run by the interpreter
```

From C#:

```csharp
LeanSysroot.Root = "/path/to/sysroot";                 // or LeanStdlib.Build(leanSrc, sysroot)
var project = new LeanProject("/src/MyProject");
LeanResult build = project.Build();                    // lake build
LeanResult check = project.CheckFile("Scratch.lean");  // lake env lean Scratch.lean
Console.WriteLine(build.ExitCode + "\n" + build.Output);
```

Tests:

```sh
dotnet test tests/LeanSharp.Tests -c Release           # API tests
dotnet build tests/LeanSharp.TestRunner -c Release
tools/run-pile.sh elab 3                               # a pile of Lean's own test suite
tools/run-checks.sh                                    # check programs of the hand-ported runtime
```

## Status

Work in progress; see [TODO.md](TODO.md) for details and open items. Lean's own test suite on
Linux x64, using the standard library built by LeanSharp itself:

| Lean test pile | Passing |
|---|---|
| `elab` | 3,304 / 3,304 |
| `elab_fail` | 315 / 315 |
| `elab_bench` | 70 / 70 |
| `compile` (interpreter half) | 82 / 82 |
| `compile_bench` (interpreter half) | 27 / 29 |
| `docparse` | 303 / 303 |
| `server`, `server_interactive` (LSP) | 4 / 4, 154 / 154 |
| `misc`, `misc_dir` | 5 / 5, 2 / 3 |
| `pkg` (Lake packages) | 42 / 44 |
| `lake` (Lake's own tests) | 84 / 94 |

* Working: elaboration, kernel type checking, tactics, `#eval` (IR interpreter), `bv_decide`
  (C# port of CaDiCaL), the language server, `lake build`/`lake exe`/`lake test` with in-process
  `lean`, reading and writing native-compatible `.olean` files, building the standard library
  from source (the output is byte-identical to what native Lean writes for the same inputs).
* Not supported: anything that needs native code — linking hand-written C, native plugins, the
  LLVM backend, Lake's `leantar` cache format. Executables are launchers that run the program
  with the interpreter.
* Platforms: developed on macOS arm64 and Linux x64 (WSL 2). Windows has not been run.

## Documentation

* [Docs/RUNNING.md](Docs/RUNNING.md) — building, sysroot setup, CLI, C# API, running the tests,
  regenerating the C#.
* [Docs/DESIGN.md](Docs/DESIGN.md) — architecture.
* [Docs/PORTING.md](Docs/PORTING.md) — conventions of the hand-ported runtime.
* [TODO.md](TODO.md) — open work and known limitations.
