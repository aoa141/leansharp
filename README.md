# LeanSharp

A pure .NET (C#, .NET 10) implementation of [Lean 4](https://github.com/leanprover/lean4).
LeanSharp runs the Lean elaborator, kernel, compiler front end and Lake build tool without any
native code, so Lean can be used where only managed .NET code may run (e.g. Microsoft CloudBuild).

Status: **work in progress** (see "Status" below).

## How it works

Lean is mostly written in Lean. LeanSharp translates Lean's compiled intermediate representation
(IR) of `Init`, `Std`, `Lean` and `Lake` to C# (`gen/`, ~12.8M lines), and ports the C/C++ parts
(runtime, kernel, IR interpreter, `.olean` serialization, CaDiCaL, libuv) by hand to
`src/LeanSharp.Runtime`. See [Docs/DESIGN.md](Docs/DESIGN.md).

| Project | Contents |
|---------|----------|
| `src/LeanSharp.Runtime` | Object model, runtime, kernel, IR interpreter, `.olean` reader/writer, SAT solver |
| `src/LeanSharp.Lean`    | The Lean libraries/compiler translated to C# (generated from `gen/`) |
| `src/LeanSharp`         | Host library: initialization, `lean` and `lake` entry points, in-process subprocesses |
| `src/LeanSharp.Cli`     | Command-line tool (`LeanSharp.Cli lean ...`, `LeanSharp.Cli lake ...`) |
| `tools/EmitCSharp`      | The IR → C# emitter (development only; needs a native Lean build of the same commit) |

## Quick start

Requirements: the .NET 10 SDK, 16 GB of free RAM for the build, and the Lean library files
(`.olean`) of Lean 4.36.0-pre (commit `67a8629274`). Full instructions:
[Docs/RUNNING.md](Docs/RUNNING.md).

```sh
dotnet build src/LeanSharp.Cli -c Release            # first build takes ~5-10 minutes

# sysroot = directory containing lib/lean/*.olean (from a native Lean build of the same commit)
export LEANSHARP_SYSROOT=/path/to/sysroot

CLI="dotnet src/LeanSharp.Cli/bin/Release/net10.0/LeanSharp.Cli.dll"
$CLI MyFile.lean          # like `lean MyFile.lean`
$CLI --run Main.lean      # run `main` with the interpreter
$CLI lake build           # like `lake build`, everything in-process
```

From C#:

```csharp
LeanSysroot.Root = "/path/to/sysroot";
var project = new LeanProject("/src/MyProject");
LeanResult build = project.Build();                    // lake build
LeanResult check = project.CheckFile("Scratch.lean");  // lake env lean Scratch.lean
Console.WriteLine(build.ExitCode + "\n" + build.Output);
```

Running Lean's own test suite against LeanSharp:

```sh
dotnet build tests/LeanSharp.TestRunner -c Release
dotnet tests/LeanSharp.TestRunner/bin/Release/net10.0/LeanSharp.TestRunner.dll \
    run --tests ~/Repos/lean4/tests --pile elab -j 5
```

## Status

Work in progress; see [TODO.md](TODO.md) for the full list.

| Lean test pile | Passing (last measured) |
|---|---|
| `elab` | 3,294 / 3,298 |
| `elab_fail` | 315 / 315 |
| `compile` (interpreter half) | 50 / 81 |
| `docparse` | 0 / 303 (one known bug in `lean --run`) |
| `server` | 0 / 4 |

* Working: elaboration, kernel type checking, tactics, `#eval` (IR interpreter), `bv_decide`
  (C# port of CaDiCaL), `lake build` of Lean libraries with in-process `lean`, reading and writing
  native-compatible `.olean` files.
* Not yet: building the standard library `.olean` files with LeanSharp itself (they currently come
  from a native Lean build), the LSP server tests, Lake/pkg test piles, native executables.
* Only tested on macOS arm64 so far.

## Documentation

* [Docs/RUNNING.md](Docs/RUNNING.md) — building, sysroot setup, CLI, C# API, running the tests,
  regenerating the C#.
* [Docs/DESIGN.md](Docs/DESIGN.md) — architecture.
* [Docs/PORTING.md](Docs/PORTING.md) — conventions of the hand-ported runtime.
* [TODO.md](TODO.md) — open work and known limitations.
