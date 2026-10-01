# Resuming work on LeanSharp

The chat session that built this repository cannot be moved to another machine: its transcript and
the assistant's memory are stored locally on the original machine. A new session starts with no
context. This file explains how to pick the work up again.

## 1. Move the repository

Nothing has been committed to git yet; everything is in the working tree.

- Commit and push (or copy the folder). `gen/` is about 440 MB of generated C#.
- `artifacts/` is git-ignored and will **not** travel with the repo. It only holds things that
  can be recreated: the test sysroot (a symlink), a tiny Lake smoke-test project, and test result
  files.

## 2. Set up the new machine

1. Install the **.NET 10 SDK**. Have at least 16 GB of free RAM for the build.
2. Get a **`lean4` checkout at commit `67a8629274`** (Lean 4.36.0-pre), assumed at `~/Repos/lean4`.
   It provides the test files, and — with a native build in `build/release/stage1` — the `.olean`
   library files, the reference `lean` binary, and the inputs for regenerating `gen/`.
   If you cannot build Lean natively there, copy `build/release/stage1/lib/lean` (about 2.9 GB)
   from the original machine.
3. Create the sysroot:
   ```sh
   mkdir -p artifacts/sysroot/lib artifacts/sysroot/bin
   ln -sfn ~/Repos/lean4/build/release/stage1/lib/lean artifacts/sysroot/lib/lean
   export LEANSHARP_SYSROOT=$PWD/artifacts/sysroot
   ```

Details: [Docs/RUNNING.md](Docs/RUNNING.md).

## 3. First steps

The latest source changes were never built together (the last build was stopped by the OS for low
memory). So start here:

```sh
dotnet build tests/LeanSharp.TestRunner -c Release     # ~10 min, several GB of RAM
dotnet build src/LeanSharp.Cli -c Release
dotnet build examples/ProjectDemo -c Release

CLI="dotnet src/LeanSharp.Cli/bin/Release/net10.0/LeanSharp.Cli.dll"
$CLI --version                                         # smoke test

R="dotnet tests/LeanSharp.TestRunner/bin/Release/net10.0/LeanSharp.TestRunner.dll"
$R run --tests ~/Repos/lean4/tests --pile elab --filter '^1[0-9]{4}\.lean$' -j 4   # 61 tests, ~1 min
$R run --tests ~/Repos/lean4/tests --pile elab -j 5 --results artifacts/elab.tsv    # full, ~25 min
```

Fix any compile errors first; the most likely places are `src/LeanSharp/LeanProject.cs` (never
compiled) and `src/LeanSharp.Runtime/Interp/` (a fix was written but never built). Then rerun
every test pile and compare with the table in [TODO.md](TODO.md), section 1.

Build one thing at a time. If a build hangs or was killed, run `dotnet build-server shutdown` and
retry.

## 4. Continuing with an AI assistant

Start a new session in the repository root and give it this prompt:

> Read `RESUME.md`, `TODO.md`, `Docs/RUNNING.md`, `Docs/DESIGN.md` and `Docs/PORTING.md`. Then
> continue the LeanSharp work starting with section 0 of `TODO.md`: rebuild, rerun the test piles,
> and work through the open items.

What each file gives it:

| File | Contents |
|---|---|
| [TODO.md](TODO.md) | Test status, open bugs, unfinished work, performance ideas, known limitations, and handoff notes (goal, assumed paths, how the code is organised, practical lessons, what was verified versus only claimed) |
| [Docs/RUNNING.md](Docs/RUNNING.md) | Build, sysroot, command line, C# API, test runner, regenerating `gen/` |
| [Docs/DESIGN.md](Docs/DESIGN.md) | Architecture |
| [Docs/PORTING.md](Docs/PORTING.md) | Conventions of the hand-ported runtime |

## 5. Where things stood

Measured on the last successful build:

| Lean test pile | Passing |
|---|---|
| `elab` | 3,294 / 3,298 |
| `elab_fail` | 315 / 315 |
| `compile` (interpreter half) | 50 / 81 |
| `docparse` | 0 / 303 |
| `server` | 0 / 4 |

- Working: `lean file.lean`, `#eval`, `bv_decide`, `lake build` of Lean libraries (all in-process),
  reading and writing native-compatible `.olean` files.
- Written but unverified: fixes for the last four `elab` failures, the `lean --run` symbol bug
  (behind the `docparse` and most `compile` failures), per-child environment and working
  directory, and the `LeanProject` API.
- Not started: building the standard library `.olean` files with LeanSharp itself, the `server`,
  `lake` and `pkg` test piles, memory and startup optimisation, Windows and Linux.
