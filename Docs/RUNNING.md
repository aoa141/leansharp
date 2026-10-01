# Building and running LeanSharp

## Requirements

* **.NET 10 SDK** (10.0.3xx or later). Nothing else is needed to build and run.
* **RAM:** at least 16 GB free is recommended. Compiling the generated `LeanSharp.Lean` assembly
  (12.8M lines) takes several GB, and running tests that `import Lean` takes ~4–5 GB per process.
* **Lean library files (`.olean`)** for Lean **4.36.0-pre, commit `67a8629274`** — see "Sysroot".
* Only needed to *regenerate* the C# in `gen/` or to run Lean's test suite: a checkout of
  [lean4](https://github.com/leanprover/lean4) at that commit (assumed at `~/Repos/lean4` below).

## Build

```sh
dotnet build src/LeanSharp.Cli -c Release            # runtime + generated code + host + CLI
dotnet build tests/LeanSharp.TestRunner -c Release   # test runner
dotnet build examples/ProjectDemo -c Release         # API example
```

The first build takes about 5–10 minutes (almost all of it in `LeanSharp.Lean`). Later builds are
fast unless the public API of `src/LeanSharp.Runtime` changes, which recompiles the generated
assembly. If a build seems stuck or was killed, run `dotnet build-server shutdown` and retry; do not
run several builds at once.

## Sysroot (the Lean library files)

`import` needs the compiled library files of `Init`, `Std`, `Lean` and `Lake`. LeanSharp looks for
them in `<sysroot>/lib/lean/`, where the sysroot is, in order:

1. `LeanSysroot.Root` (set from C#),
2. the `LEANSHARP_SYSROOT` environment variable,
3. a directory `lean-sysroot` next to the LeanSharp assemblies.

Currently the files must come from a **native Lean build of the same commit** (LeanSharp reads the
native format directly). With a native build at `~/Repos/lean4/build/release/stage1`:

```sh
mkdir -p artifacts/sysroot/lib artifacts/sysroot/bin
ln -sfn ~/Repos/lean4/build/release/stage1/lib/lean artifacts/sysroot/lib/lean   # or copy the directory
export LEANSHARP_SYSROOT=$PWD/artifacts/sysroot
```

To move to another machine without a native Lean build, copy that `lib/lean` directory (about
2.9 GB; only the `.olean*` and `.ir*` files are needed). On first use LeanSharp writes `lean` and
`lake` launcher scripts into `<sysroot>/bin`. A toolchain installed by `elan` only works if it is
exactly this commit; a different version fails with "incompatible header".

## Command line

```sh
CLI="dotnet src/LeanSharp.Cli/bin/Release/net10.0/LeanSharp.Cli.dll"

$CLI --version                 # same options as `lean`
$CLI MyFile.lean               # elaborate a file (messages on stdout, exit code like lean)
$CLI --run Main.lean arg1      # run `main` with the interpreter
$CLI lake build                # run Lake in the current directory (lean subprocesses run in-process)
$CLI lake env lean Foo.lean
```

`LeanSharp.Cli lean <args>` and `LeanSharp.Cli <args>` are the same. `LEAN_PATH` and the other
environment variables understood by Lean/Lake work as usual.

## From C#

Reference `src/LeanSharp/LeanSharp.csproj`.

```csharp
using LeanSharp;

LeanSysroot.Root = "/path/to/sysroot";

// Lake project
var project = new LeanProject("/src/MyProject");
LeanResult build = project.Build();                  // lake build
LeanResult check = project.CheckFile("Scratch.lean"); // lake env lean Scratch.lean
LeanResult run   = project.RunMain("Main.lean", "arg");
Console.WriteLine(build.ExitCode + "\n" + build.Output);

// or the raw entry points (exit code; output goes to the process's stdout/stderr)
int rc  = LeanShell.Main(new[] { "MyFile.lean" });
int rc2 = LakeShell.Main(new[] { "build" });
```

Host process settings that matter (see `src/LeanSharp.Cli/LeanSharp.Cli.csproj`):

* `<ServerGarbageCollection>true</ServerGarbageCollection>` makes imports 2–3× faster.
* `System.IO.DisableFileLocking=true` (runtime host option) avoids .NET's implicit file locks.
* Lean code needs deep stacks: the entry points above already run on large-stack threads. If you
  call `LeanShell.RunOnCurrentThread` yourself, use `LeanHost.RunWithLargeStack`.

Environment variables: `LEANSHARP_SYSROOT`, `LEANSHARP_OLEAN_CACHE=0` (disable the in-process
cache of decoded `.olean` files), `LEAN_NUM_THREADS`.

Example program:

```sh
dotnet examples/ProjectDemo/bin/Release/net10.0/ProjectDemo.dll <sysroot> <project-dir> [File.lean]
```

## Running Lean's test suite

The runner executes the test "piles" of the Lean repository in-process (worker processes that
initialize Lean once), comparing output with the `.out.expected` files.

```sh
export LEANSHARP_SYSROOT=$PWD/artifacts/sysroot
R="dotnet tests/LeanSharp.TestRunner/bin/Release/net10.0/LeanSharp.TestRunner.dll"

$R run --tests ~/Repos/lean4/tests --pile elab -j 5 --results artifacts/elab.tsv   # ~25 min
$R run --tests ~/Repos/lean4/tests --pile elab_fail -j 4
$R run --tests ~/Repos/lean4/tests --pile compile -j 4 --show-diffs                # interpreter half
$R run --tests ~/Repos/lean4/tests --pile docparse -j 4
$R run --tests ~/Repos/lean4/tests --pile server -j 2

$R run --tests ~/Repos/lean4/tests --pile elab --filter '^bv_' --show-diffs        # subset by regex
$R run --tests ~/Repos/lean4/tests --pile elab --rerun artifacts/elab.tsv          # only previous failures
$R one --tests ~/Repos/lean4/tests --pile elab ~/Repos/lean4/tests/elab/1986.lean  # one test, prints output
```

Options: `-j N` workers (each may use 4–5 GB on tests that `import Lean`; lower it if memory is
tight), `--timeout SECONDS` per test (default 300), `--show-diffs`, `--results FILE` (TSV:
name, status, seconds, detail). Statuses: `PASS`, `FAIL`, `CRASH` (worker died), `TIMEOUT`.
After `one`, the expected and produced outputs are in `/tmp/leansharp.expected` and
`/tmp/leansharp.produced`.

Current results and open problems are in [TODO.md](../TODO.md).

## Regenerating the generated C# (`gen/`)

Only needed when changing the emitter or the Lean version. Requires a native build of the same
Lean commit (`cmake --preset release && make -C build/release` in the lean4 checkout):

```sh
LEAN4=~/Repos/lean4 tools/regen.sh gen
mv gen/externs.txt Docs/runtime-externs.txt; mv gen/exports.txt Docs/lean-exports.txt; mv gen/manifest.tsv Docs/modules.tsv
```

`Docs/runtime-externs.txt` lists every runtime function the generated code calls (the surface
`src/LeanSharp.Runtime` must implement); `Docs/lean-exports.txt` lists the Lean functions the
runtime may call back. See [PORTING.md](PORTING.md) for the runtime conventions.

## Repository layout

| Path | Contents |
|---|---|
| `src/LeanSharp.Runtime` | Object model, runtime, kernel, IR interpreter, `.olean` I/O, CaDiCaL, libuv equivalents |
| `src/LeanSharp.Lean` | Project compiling the generated sources in `gen/` |
| `src/LeanSharp` | Host: initialization, `lean`/`lake` entry points, `LeanProject`, in-process subprocesses |
| `src/LeanSharp.Cli` | Command-line tool |
| `gen/` | Generated C# (do not edit) |
| `tools/EmitCSharp` | IR → C# emitter (a Lean program) and `regen.sh` |
| `tests/LeanSharp.TestRunner` | Runner for Lean's test piles |
| `examples/ProjectDemo` | `LeanProject` API example |
| `checks/` | Per-area check programs from the porting phase |
| `Docs/` | Design, porting guide, extern/export lists, this file |
