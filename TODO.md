# LeanSharp TODO

State as of 2026-10-01 (Linux/WSL session). See [Docs/RUNNING.md](Docs/RUNNING.md) for how to
build and run, and [Docs/DESIGN.md](Docs/DESIGN.md) for the architecture.

## 1. Test status

Measured on Linux x64 (Ubuntu 26.04 under WSL 2, 24 cores, 30 GB), Lean commit `77f336f7ae`, with
the standard library **built by LeanSharp itself** (`artifacts/selfhost`). Command for every row:
`tools/run-pile.sh <pile> 3`.

| Pile (`~/Repos/lean4/tests/<pile>`) | Result | Notes |
|---|---|---|
| `elab` | 3,304 / 3,304 | |
| `elab_fail` | 315 / 315 | |
| `elab_bench` | 70 / 70 | |
| `compile` (interpreter half) | 82 / 82 | |
| `compile_bench` (interpreter half) | 27 / 29 | `incr_header_save` (needs `--mem 16`, see 3), `incr_header_load` (snapshot with closures, see 4) |
| `docparse` | 303 / 303 | |
| `server` | 4 / 4 | |
| `server_interactive` | 154 / 154 | |
| `misc` | 5 / 5 | |
| `misc_dir` | 2 / 3 | `rc_sticky` is a hand-written C test of the native runtime |
| `pkg` | 43 / 44 | `def_clash` expects a native linker error; 3 more are excluded as in Lean's CMake |
| `lake` | 88 / 94 | see below |

API tests (`dotnet test tests/LeanSharp.Tests`): 9 / 9. Runtime check programs
(`tools/run-checks.sh`): 11 / 11.

The 6 `lake` failures, by cause:

- Need a real C toolchain and the native Lean runtime (hand-written C linked with Lean code, or
  the text of a native linker error): `examples/precompile`, `examples/reverse-ffi`,
  `tests/8448`, `tests/externLib`, `tests/precompileLink`.
- `tests/challenge-olean-issue`: the test forges an invalid `Nat` with `unsafeCast`. Native Lean's
  kernel accepts the forged proof when *building* (the comparator then rejects it); LeanSharp's
  kernel already rejects it during the build. Not a bug, but the output differs.

## 2. Not done yet

- [ ] **Windows.** Never run. The code has Windows branches (paths, environment, launchers are
      empty `.exe` placeholders that only work in-process), but nothing was verified: WSL cannot
      start Windows programs on this machine. macOS was the original development platform and
      has not been rerun since the changes of this session.
- [ ] **CI.** There is none. A draft GitHub Actions workflow was removed at the owner's request
      (it ran on every push and sent failure mails). If one is added again: compiling
      `LeanSharp.Lean` needs a lot of memory, so hosted runners may be too small.
- [ ] The compiled half of the `compile`/`compile_bench` piles (`lean --c` + `leanc`). The
      sysroot now has a `leanc` launcher that produces an interpreter-backed executable, so the
      runner could do it; it would exercise little that the interpreter half does not.
- [ ] Decide how to version `gen/` (440 MB of generated C#): commit as is (current state),
      compress, or generate in CI from a native Lean build.
- [ ] `gen/` in the repository comes from a native stage 1 build. Regenerating it with LeanSharp
      alone works (Docs/RUNNING.md, "Regenerating"; a build from the self-generated code passed
      `elab_fail`, `compile` and 644 `elab` tests), but the full test suite has not been run on
      such a build, and moving to a *new* Lean version this way (build the new sources with the
      old LeanSharp, emit, rebuild) has not been tried.

## 3. Performance and memory

- [ ] **Zstandard encoder.** `src/LeanSharp/Zstd` compresses with a lazy hash-chain matcher;
      `.ltar` archives are about 7% larger than native `leantar`'s (which uses `zstd -19`), and
      level 19 runs at 10-30 MB/s. An optimal parser would close the gap.

- [ ] **Memory.** `import Lean` allocates ~4.4 GB (native: ~2 GB mmap); a test worker reaches
      5–8 GB on a 30 GB machine. Ideas: remove `m_id` (8 bytes on every object; use a side table
      for `ptrAddrUnsafe`), move the scalar fields `s0`/`sx` out of constructor objects that have
      no scalars, lazy/deferred `.olean` decoding.
- [ ] **Compiling `LeanSharp.Lean`** peaks at ~22 GB in the C# compiler on a 24-core machine
      (about one minute). Options: split the generated code into several assemblies, or freeze a
      thin public runtime API.
- [ ] **Startup.** ~1.5 s for `--version`, ~4 s for a trivial file vs 0.3 s native (JIT of the
      module initializers). Options: ReadyToRun compilation of `LeanSharp.Lean`, lazy module
      initialization. This dominates the script-driven piles (every `lean`/`lake` call in a test
      script is a new process).
- [ ] **Snapshots (`--incr-save`, `--incr-header-save`).** Saving maps every object of every
      imported region through one big dictionary (`ObjectCompactor.TryDep`): 29 s and 8.4 GB for
      `import Lean` vs 1.9 s natively. Idea: store the logical address of region objects in
      `m_id` when reading (unless the region's address range overlaps another one), so that the
      lookup is a range check.
- [ ] Scalar field access in generated code now goes through `lean_ctor_get_uint8(o, offset)`
      (which reads the number of object fields) instead of the `_s` variants; the emitter could
      keep the fast variants for constructors without `USize` fields when it knows the layout.
- [ ] Interpreter-backed executables (`lake exe`) start a full `lean --run` (2–4 s) and
      interpret the program; emitting C# for user code and compiling it with Roslyn would be the
      fast alternative.
- [ ] Generated code size: long mangled names; could shorten symbols with a name table.
- [ ] Heartbeats are counted on constructor/closure allocations only, so `maxHeartbeats` limits
      trigger at different points than native Lean.
- [ ] CaDiCaL port is ~1–3× slower than native; the arena clause mover is not ported.
- [ ] Kernel externs leak one reference count on results (performance only).
- [ ] Stack overflow in deeply recursive Lean code kills the .NET process (cannot be caught);
      native Lean reports it. Consider stack probes in generated code.
- [ ] The test runner replaces a worker when its live heap exceeds 70% of its limit; with 4
      workers on 30 GB that happens after almost every test that imports `Lean`.

## 4. Known limitations (documented, low priority)

- `Metadata.numLinks` only counts hard links created by this process.
- File locks (`IO.FS.Handle.lock`) only work within one process.
- LLVM backend and native dynamic libraries/plugins: not supported.
- Executables built by Lake are launchers that run the program with the IR interpreter
  (`src/LeanSharp/ManagedToolchain.cs`). The `builtin_initialize` declarations of their modules
  run before `main`, after all `initialize` declarations (natively the two kinds are interleaved
  in declaration order).
- Plugins (`lean --plugin`, `Lean.loadPlugin`) must be libraries built from Lean modules by the
  managed toolchain (Lake, or the `leanc` launcher). Loading one imports its modules and runs
  their initializers with the interpreter (`src/LeanSharp/InterpretedInit.cs`); the plugin's
  dependencies outside the library must be on the search path. Programs that link hand-written C, or whose behavior
  depends on native symbol visibility, do not work. On Windows the launcher only works when
  started from an in-process program (e.g. `lake exe`).
- `leantar` is a managed port (`src/LeanSharp/Leantar`). Archives are interchangeable with the
  native tool's in both directions but not byte-identical (different zstd encoder).
- `Lean.openSSLVersion` reports a fixed OpenSSL 3.0.0 number; nothing is linked.
- `Lean.manualRoot` (computed from `LEAN_MANUAL_ROOT` at initialization) is shared by the
  programs running concurrently in one OS process; all other global state of the Lean libraries
  is per program.
- `.olean` files with closures (format v3, used by `--incr-save` snapshots) are only readable by
  the process that wrote them.
- The kernel rejects some forged values that native Lean's kernel accepts through undefined
  behavior (see `tests/challenge-olean-issue` above).

## 5. Cleanup

- [ ] `.gitignore` ends with a few generic names (`bin`, `env`, `tmp`, `toolchains`, ...) that
      look like they were added by accident; they could hide real directories.
- [ ] `Docs/externs-*.txt` are the per-area extern lists of the porting phase; they are not
      regenerated (`Docs/runtime-externs.txt` is the current list).

## 6. Handoff notes for a new session (human or AI assistant)

Everything needed to continue is in this file, `RESUME.md`, `Docs/RUNNING.md`, `Docs/DESIGN.md`
and `Docs/PORTING.md`.

### Goal (from the project owner)

A pure C# (.NET 10) library, usable from Microsoft CloudBuild (which only allows pure managed
code: no native binaries, no other languages), that can take a Lean 4 project directory and run
Lean on it, and that can run the Lean 4 test suite. No call to a native Lean installation.

Status against that goal: the library builds and runs with only the .NET SDK and the Lean
*sources*; it builds Lean's standard library itself, builds Lake projects (including
executables and test drivers, run by the interpreter), and passes Lean's elaboration, server and
most Lake/package tests on Linux.

### Machines and paths assumed so far

* Repo: `~/Repos/leansharp`. Lean checkout: `~/Repos/lean4` at commit `77f336f7ae`
  (4.36.0-pre). Its native build in `build/release/stage1` is only used to regenerate `gen/`
  and as the reference `lean` binary for comparisons.
* .NET SDK: `~/.dotnet` on the WSL machine (not on `PATH` by default).
* `artifacts/` is git-ignored: `artifacts/selfhost` (the sysroot built by `build-stdlib`),
  `artifacts/lakeproj` (tiny Lake project used for smoke tests), `artifacts/logs`, result files
  (`*.tsv`). Recreate them on a new machine (Docs/RUNNING.md).
* Do not use the `lean` found on `PATH` via elan for generation: the emitter must read the
  stage1 build (an early bug came from `findSysroot` picking up elan's 4.34.1).

### How the work was organised

* `tools/EmitCSharp/EmitCSharp.lean` generates `gen/` (never edit `gen/` by hand; change the
  emitter and run `tools/regen.sh gen`). It is a port of the old `IR/EmitC.lean`; init order per
  module is taken from the native build's C files (`lib/temp/**/*.c`).
* `src/LeanSharp.Runtime/Core` is the contract everything depends on (`Obj.cs`,
  `LeanRt.Object.cs`, `LeanRt.Basics.cs`, generated `LeanRt.Apply.cs` from `tools/gen_apply.py`).
  The other areas (`Numbers`, `Strings`, `IO`, `Tasks`, `Kernel`, `Compact`, `Interp`, `Uv`,
  `Cadical`) were ported one per directory, following `Docs/PORTING.md` and the exact
  signatures in `Docs/externs-*.txt`.
* Runtime → Lean calls go through `LeanExports` (names in `Docs/lean-exports.txt`); the host
  wires hooks in `src/LeanSharp/HostHooks.cs` (process spawn interception for `lean`, `lake`,
  `cadical`, compiler/linker steps and launchers; interpreter access to compiled modules;
  heartbeat hooks).
* Everything that is per OS process natively is per *logical process* here
  (`src/LeanSharp.Runtime/IO/LeanLogicalProcess.cs`, `IO/LeanGlobalRefs.cs`): see
  Docs/DESIGN.md, "Subprocesses and logical processes".

### Practical lessons

* **Memory is the hazard.** Exhausting RAM kills the WSL VM and the session (it happened twice
  with `-j 8`). Never build while tests run; run piles with `tools/run-pile.sh <pile> 3`; keep
  logs on disk. See Docs/RUNNING.md, "Memory".
* Changing the public surface of `LeanSharp.Runtime` recompiles the generated assembly
  (~1 minute on the 24-core machine, 5–10 on a laptop). Prefer `internal` members while debugging.
* When a test passes alone but fails in a full run, or fails only under Lake's parallelism,
  suspect state shared between in-process programs. Bugs of this kind fixed so far: global
  `IO.Ref`s, the task manager, the stdin handle, the interpreter's cache of `initialize`
  results, the cache of decoded `.olean` regions, profiling times.
* A hang: `tools/stacks.sh <pid>` prints the managed stacks grouped by identical stack.
* When output differs from the expected file, reproduce with a small Lean file and run the same
  file with the native binary (`~/Repos/lean4/build/release/stage1/bin/lean`); a wrong value in
  compiled code can be an emitter bug (the `USize`/scalar offset bug was found that way).
* Useful smoke tests after any change: `dotnet test tests/LeanSharp.Tests -c Release`;
  `tools/run-pile.sh elab 3 --filter '^1[0-9]{4}\.lean$'` (61 tests, ~1.5 min);
  `tools/run-pile.sh pkg 3` (5 min; exercises Lake, executables, tools).

### What was verified in the Linux session (2026-10-01)

* All piles were run on commit `26055fb` (after the plugin and initializer changes). After the
  `leantar` port and the launcher change, `compile`, `misc`, `misc_dir`, `pkg`, the full `lake`
  pile, the API tests and a 437-test `elab` subset were rerun; the other in-process piles were
  not (the change does not touch the runtime). The script-driven piles run without elan on
  `PATH`, so no native Lean tool can take part.
* `leantar`: the lgz encoder's output is byte-identical to the native tool's on 60 modules;
  archives of 51 modules were packed and unpacked with the native and the managed tool in every
  combination; `tools/run-checks.sh zstd leantar` cross-checks the Zstandard codec against
  libzstd 1.5.7 (through Python's `compression.zstd`).
* A from-scratch `build-stdlib` (7 min 42 s, 12 GB peak) gives the same 15,352 files byte for
  byte as an earlier one made before the emitter fix.
* The self-built standard library: for sampled modules of every library the native `lean` of the
  same commit, given the same inputs, writes byte-identical `.olean`, `.olean.server`,
  `.olean.private`, `.ir` and `.c` files.
