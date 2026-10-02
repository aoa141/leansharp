# Building and running LeanSharp

Everything here works on Linux (developed on Ubuntu under WSL 2) and macOS. Windows is expected to
work for the library and the in-process test piles but has not been run yet.

## Requirements

* **.NET 10 SDK** (10.0.3xx or later). If it is not installed system-wide:
  ```sh
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ~/.dotnet
  export PATH=$HOME/.dotnet:$PATH
  ```
* **The Lean sources** of Lean **4.36.0-pre, commit `77f336f7ae`**: a checkout of
  [lean4](https://github.com/leanprover/lean4) at that commit (assumed at `~/Repos/lean4` below).
  LeanSharp compiles the standard library from `src/` and runs the tests in `tests/`. Nothing in
  that checkout has to be built; a native Lean is only needed to *regenerate* `gen/`.
* **RAM:** 32 GB recommended. Compiling the generated `LeanSharp.Lean` assembly (12.8M lines)
  peaks at about 22 GB in the C# compiler on a 24-core machine; a Lean process that
  `import`s `Lean` uses 1–3 GB. See "Memory" below.

## Build

```sh
dotnet build src/LeanSharp.Cli -c Release            # runtime + generated code + host + CLI
dotnet build tests/LeanSharp.TestRunner -c Release   # runner for Lean's test suite
dotnet build tests/LeanSharp.Tests -c Release        # API tests (xunit)
dotnet build examples/ProjectDemo -c Release         # API example
```

The first build takes 1–10 minutes depending on the machine (almost all of it in
`LeanSharp.Lean`). Later builds are fast unless the public API of `src/LeanSharp.Runtime` changes,
which recompiles the generated assembly. Do not run several builds at once, and do not build while
tests are running (see "Memory").

### Faster startup: precompiled build

A plain build is pure IL, and a large part of every start is JIT compilation (the initializers
of 2,500 modules, then the elaborator). `tools/publish.sh` publishes the command-line tool and
the test runner with ReadyToRun precompilation into `artifacts/publish`:

```sh
tools/publish.sh                                    # ~40 s; linux-x64, win-x64, osx-arm64, ... as first argument
dotnet artifacts/publish/cli/LeanSharp.Cli.dll --version
```

| | plain build | precompiled | native Lean |
|---|---|---|---|
| `lean --version` | 1.5 s | 0.4 s | |
| a one-line file | 3.3 s | 1.1 s | 1.2 s |
| a file with `import Lean` | 5.4 s | 3.3 s | 3.7 s |

The precompiled assemblies are still .NET assemblies run by the .NET runtime, but they contain
machine code for one platform (generated from the IL by the .NET SDK) and are twice as large;
whether that is acceptable where "managed code only" is required is a policy question, so it is
opt-in. To run the test piles with the precompiled runner:
`LEANSHARP_RUNNER=$PWD/artifacts/publish/testrunner/LeanSharp.TestRunner.dll tools/run-pile.sh pkg 3`.

`LEANSHARP_TRACE_STARTUP=1` prints the time of each initialization step.

## Sysroot (the Lean library files)

`import` needs the compiled library files of `Init`, `Std`, `Lean` and `Lake`. LeanSharp looks for
them in `<sysroot>/lib/lean/`, where the sysroot is, in order:

1. `LeanSysroot.Root` (set from C#),
2. the `LEANSHARP_SYSROOT` environment variable,
3. a directory `lean-sysroot` next to the LeanSharp assemblies.

Build the sysroot with LeanSharp itself from the Lean sources (about 8 minutes on 24 cores, 10 GB;
incremental afterwards):

```sh
CLI="dotnet src/LeanSharp.Cli/bin/Release/net10.0/LeanSharp.Cli.dll"
$CLI build-stdlib ~/Repos/lean4/src artifacts/selfhost
export LEANSHARP_SYSROOT=$PWD/artifacts/selfhost
```

or from C#: `LeanStdlib.Build("/path/to/lean4/src", "/path/to/sysroot")`. On machines with less
memory, limit the parallelism with `LEAN_NUM_THREADS=8` (Lake runs that many `lean` jobs).

The `lib/lean` directory of a native Lean build of exactly this commit works too
(`ln -s ~/Repos/lean4/build/release/stage1/lib/lean <sysroot>/lib/lean`); a different version
fails with "incompatible header".

LeanSharp writes `lean` and `lake` launcher scripts into `<sysroot>/bin` (Lake locates its Lean
installation through them, and they make the sysroot usable from a shell:
`PATH=$LEANSHARP_SYSROOT/bin:$PATH lake build`).

## Command line

```sh
$CLI --version                 # same options as `lean`
$CLI MyFile.lean               # elaborate a file (messages on stdout, exit code like lean)
$CLI --run Main.lean arg1      # run `main` with the interpreter
$CLI lake build                # run Lake in the current directory (lean subprocesses run in-process)
$CLI lake env lean Foo.lean
$CLI build-stdlib <lean4/src> <sysroot> [targets...]
$CLI leantar -x Foo.ltar       # Lake's archive tool (managed port of leantar)
$CLI leanc -o prog prog.c      # "link" a Lean-generated C file into an interpreter-backed executable
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

Every `LeanProject` call runs as its own *logical process*: its own working directory,
environment, standard streams, worker threads and copy of Lean's global state. Calls can be made
concurrently from several threads; the host's working directory and environment are not touched.

Host process settings that matter (see `src/LeanSharp.Cli/LeanSharp.Cli.csproj`):

* `<ServerGarbageCollection>true</ServerGarbageCollection>` makes imports 2–3× faster but uses
  more memory; `DOTNET_GCHeapHardLimit` bounds the managed heap.
* `System.IO.DisableFileLocking=true` (runtime host option) avoids .NET's implicit file locks.
* Lean code needs deep stacks: the entry points above already run on large-stack threads. If you
  call `LeanShell.RunOnCurrentThread` yourself, use `LeanHost.RunWithLargeStack`.

Environment variables: `LEANSHARP_SYSROOT`, `LEANSHARP_OLEAN_CACHE=0` (disable the in-process
cache of `.olean` files), `LEANSHARP_OLEAN_LAZY=0` (convert every object of an `.olean` file
when it is read instead of on demand; see DESIGN.md, "Lazy decoding"), `LEANSHARP_OLEAN_MMAP=0/1`
(copy the files into memory / map them; mapping is the default except on Windows),
`LEANSHARP_TRACE_OLEAN=1` (regions, created objects and memory at exit), `LEAN_NUM_THREADS`.

Example program:

```sh
dotnet examples/ProjectDemo/bin/Release/net10.0/ProjectDemo.dll <sysroot> <project-dir> [File.lean]
```

## Tests

### API tests

```sh
dotnet test tests/LeanSharp.Tests -c Release     # ~1 minute; uses LEANSHARP_SYSROOT or artifacts/selfhost
```

### Lean's test suite

The runner executes the test "piles" of the Lean repository and compares with the
`.out.expected` files. Most piles run in-process in worker processes that initialize Lean once;
the piles driven by shell scripts (`pkg`, `misc`, `misc_dir`, `lake`) run their scripts with
`bash`, with the sysroot's `lean`/`lake` launchers first on `PATH`.

```sh
tools/run-pile.sh elab 4            # log: artifacts/logs/elab.log, results: artifacts/elab.tsv
tools/run-pile.sh elab_fail 3
tools/run-pile.sh lake 3 --timeout 900
```

or directly:

```sh
export LEANSHARP_SYSROOT=$PWD/artifacts/selfhost
R="dotnet tests/LeanSharp.TestRunner/bin/Release/net10.0/LeanSharp.TestRunner.dll"

$R run --tests ~/Repos/lean4/tests --pile elab -j 4 --results artifacts/elab.tsv   # ~60 min
$R run --tests ~/Repos/lean4/tests --pile elab --filter '^bv_' --show-diffs        # subset by regex
$R run --tests ~/Repos/lean4/tests --pile elab --rerun artifacts/elab.tsv          # only previous failures
$R one --tests ~/Repos/lean4/tests --pile elab ~/Repos/lean4/tests/elab/1986.lean  # one test, prints output
```

Piles: `elab`, `elab_fail`, `elab_bench`, `compile`, `compile_bench` (interpreter half only),
`docparse`, `server`, `server_interactive`, and the script piles `pkg`, `misc`, `misc_dir`, `lake`.

Options: `-j N` workers (default: bounded by cores and by memory), `--mem GB` managed heap limit
per worker (default: 75% of RAM divided by the number of workers; a worker is replaced when its
live heap exceeds 70% of the limit), `--timeout SECONDS` per test (default 300), `--show-diffs`, `--include-unsupported` (script piles:
also run the tests of scenarios LeanSharp does not support by design, which are skipped otherwise;
the list with reasons is `s_unsupported` in the runner),
`--results FILE` (TSV: name, status, seconds, detail; for script piles the output of every test is
saved in `FILE.out/`). Statuses: `PASS`, `FAIL`, `CRASH` (worker died), `TIMEOUT`. After `one`, the
expected and produced outputs are in the temp directory (`leansharp.expected`,
`leansharp.produced`).

Current results and open problems are in [TODO.md](../TODO.md).

## Memory

Exhausting the machine's memory is the main operational hazard (under WSL 2 it takes down the
whole VM):

* The C# compiler needs up to ~22 GB for `LeanSharp.Lean`. Never build while tests run. The
  project disables the shared compiler server for that assembly; if memory stays high after a
  build, run `dotnet build-server shutdown`.
* A test worker grows to several GB. Use the runner's defaults or a small `-j` (3–4 on a 32 GB
  machine); do not pass `-j` equal to the number of cores.
* Run long jobs in the background with their output in a file on disk (`tools/run-pile.sh` does
  that), so that results survive a crash of the terminal session.

## Debugging a hang

```sh
dotnet tool install -g dotnet-stack
tools/stacks.sh <pid>          # managed stacks of all threads, grouped
```

## Regenerating the generated C# (`gen/`)

Only needed when changing the emitter or the Lean version. The emitter is a Lean program that
reads the `.ir` and `.c` files of a build of the Lean libraries. Either use a native build of the
same Lean commit (`cmake --preset release && make -C build/release -j` in the lean4 checkout):

```sh
LEAN4=~/Repos/lean4 tools/regen.sh gen          # ~1 minute
mv gen/externs.txt docs/runtime-externs.txt; mv gen/exports.txt docs/lean-exports.txt; mv gen/manifest.tsv docs/modules.tsv
```

or run it with LeanSharp on a sysroot built by `build-stdlib` (no native Lean involved; ~2 minutes,
~14 GB):

```sh
STAGE=$PWD/artifacts/selfhost tools/regen.sh /tmp/gen-self
dotnet build tests/LeanSharp.TestRunner -c Release --artifacts-path /tmp/stage2 -p:LeanSharpGenDir=/tmp/gen-self
```

This closes the bootstrap loop: LeanSharp compiles the Lean sources to `.ir`, the emitter
(running on LeanSharp) translates that to C#, and the result is the next LeanSharp. A build made
this way passed the `elab_fail` and `compile` piles and 644 `elab` tests. Run by LeanSharp or by
native Lean, the emitter produces identical output for the same input. `gen/` in the repository
was generated from a native stage 1 build; the self-generated code differs from it in ~350
modules because native stage 1 files are compiled by the older stage 0 compiler.

Then update the commit hash in `src/LeanSharp.Runtime/IO/LeanRt.IO.Misc.cs` and
`src/LeanSharp.Runtime/Compact/OleanFile.cs`, and implement any new function listed in
`docs/runtime-externs.txt`. `docs/runtime-externs.txt` lists every runtime function the generated
code calls (the surface `src/LeanSharp.Runtime` must implement); `docs/lean-exports.txt` lists the
Lean functions the runtime may call back. See [PORTING.md](PORTING.md) for the runtime conventions.

## Repository layout

| Path | Contents |
|---|---|
| `src/LeanSharp.Runtime` | Object model, runtime, kernel, IR interpreter, `.olean` I/O, CaDiCaL, libuv equivalents |
| `src/LeanSharp.Lean` | Project compiling the generated sources in `gen/` |
| `src/LeanSharp` | Host: initialization, `lean`/`lake` entry points, `LeanProject`, `LeanStdlib`, in-process subprocesses |
| `src/LeanSharp.Cli` | Command-line tool |
| `gen/` | Generated C# (do not edit) |
| `tools/` | `EmitCSharp` (IR → C# emitter, a Lean program), `regen.sh`, `run-pile.sh`, `run-checks.sh`, `publish.sh`, `stacks.sh` |
| `tests/LeanSharp.Tests` | API tests (xunit) |
| `tests/LeanSharp.TestRunner` | Runner for Lean's test piles |
| `examples/ProjectDemo` | `LeanProject` API example |
| `checks/` | Per-area check programs from the porting phase |
| `docs/` | Design, porting guide, extern/export lists, this file |
| `artifacts/` | Git-ignored: sysroot, logs, test results |
