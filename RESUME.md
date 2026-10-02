# Resuming work on LeanSharp

A chat session cannot be moved to another machine: its transcript and the assistant's memory stay
on the machine it ran on. A new session starts with no context. This file explains how to pick
the work up again; [TODO.md](TODO.md) has the current status and open items.

## 1. Move the repository

- Commit and push (or copy the folder). `gen/` is about 440 MB of generated C#.
- `artifacts/` is git-ignored and will **not** travel with the repo. It only holds things that
  can be recreated: the sysroot built by `build-stdlib`, a tiny Lake smoke-test project, logs and
  test result files.

## 2. Set up the new machine

1. Install the **.NET 10 SDK** (per-user install is fine, see docs/RUNNING.md). Have 32 GB of RAM
   for the build if possible.
2. Get a **`lean4` checkout at commit `77f336f7ae`** (Lean 4.36.0-pre), assumed at `~/Repos/lean4`.
   Only its sources and tests are needed. A native build of it (`build/release/stage1`) is needed
   only to regenerate `gen/` or to compare behavior with the reference `lean` binary.
3. Build, then build the sysroot with LeanSharp:
   ```sh
   dotnet build src/LeanSharp.Cli -c Release
   dotnet build tests/LeanSharp.TestRunner -c Release
   dotnet src/LeanSharp.Cli/bin/Release/net10.0/LeanSharp.Cli.dll build-stdlib ~/Repos/lean4/src artifacts/selfhost
   export LEANSHARP_SYSROOT=$PWD/artifacts/selfhost
   ```

Details: [docs/RUNNING.md](docs/RUNNING.md).

## 3. First steps

```sh
dotnet test tests/LeanSharp.Tests -c Release                       # API tests, ~1 min
tools/run-checks.sh                                                # runtime check programs
tools/run-pile.sh elab 3 --filter '^1[0-9]{4}\.lean$'              # 61 tests, ~1.5 min
tools/run-pile.sh elab 3                                           # full pile, ~60 min
```

Read the "Memory" section of docs/RUNNING.md before running anything bigger: exhausting RAM takes
the machine (or the WSL VM) down. Build one thing at a time and never while tests are running.

## 4. Continuing with an AI assistant

Start a new session in the repository root and give it this prompt:

> Read `RESUME.md`, `TODO.md`, `docs/RUNNING.md`, `docs/DESIGN.md` and `docs/PORTING.md`. Then
> continue the LeanSharp work with the open items of `TODO.md`.

What each file gives it:

| File | Contents |
|---|---|
| [TODO.md](TODO.md) | Test status, unfinished work, performance ideas, known limitations, and handoff notes (goal, assumed paths, how the code is organised, practical lessons, what was verified) |
| [docs/RUNNING.md](docs/RUNNING.md) | Build, sysroot, command line, C# API, tests, memory, regenerating `gen/` |
| [docs/DESIGN.md](docs/DESIGN.md) | Architecture |
| [docs/PORTING.md](docs/PORTING.md) | Conventions of the hand-ported runtime |

## 5. Where things stand

See the table in [TODO.md](TODO.md), section 1. In short: on Linux, with a standard library that
LeanSharp built itself, the `elab`, `elab_fail`, `elab_bench`, `compile`, `docparse`, `server`,
`server_interactive` and `misc` piles of Lean's test suite pass completely; `pkg` and `lake` pass
except for tests that need native code or the `leantar` tool.

History of the port:

1. macOS session: emitter, runtime port, host, test runner; `elab` 3,294 / 3,298.
2. Linux (WSL) session, 2026-10-01: moved to Lean commit `77f336f7ae`; per-program global state,
   task managers and stdin; the standard library is built by LeanSharp itself; an emitter bug in
   scalar field offsets fixed; script-driven test piles; interpreter-backed executables; API tests.
