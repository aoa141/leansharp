# LeanSharp TODO

State as of the last working session. See [Docs/RUNNING.md](Docs/RUNNING.md) for how to build and
run, and [Docs/DESIGN.md](Docs/DESIGN.md) for the architecture.

## 0. First thing to do on a new machine

The working tree contains source changes that have **not been built or verified together**: the
last full build was stopped by the OS for low memory. Before anything else:

- [ ] Build: `dotnet build tests/LeanSharp.TestRunner -c Release` (then `src/LeanSharp.Cli` and
      `examples/ProjectDemo`). Expect ~10 minutes and several GB of RAM for the generated assembly.
      Fix any compile errors (most likely in `src/LeanSharp/LeanProject.cs`, which has never been
      compiled, or in `src/LeanSharp.Runtime/Interp/`, where a change was interrupted mid-way).
- [ ] Rerun every test pile (commands in Docs/RUNNING.md) and compare with the table below.

## 1. Test status

Last measured results (binaries built before the unverified changes below):

| Pile (`~/Repos/lean4/tests/<pile>`) | Result | Notes |
|---|---|---|
| `elab` | 3,294 / 3,298 | remaining 4: `IO_test`, `async_select_socket`, `docstringRewrites`, `nat_size_limit` |
| `elab_fail` | 315 / 315 | |
| `compile` (interpreter half only) | 50 / 81 | mostly the `--run` bug below |
| `docparse` | 0 / 303 | all caused by the `--run` bug below |
| `server` | 0 / 4 | 2 crash (worker exits), 2 time out; not investigated |
| `lake`, `pkg`, `misc` | not run | shell-script driven; need runner support or the `bin/lean` launchers on `PATH` |

Changes in source that claim to fix failures but are **unverified in a shared build**:

- [ ] Verify the last 4 `elab` failures are fixed (hard-link counts, sticky TCP EOF,
      per-child environment/cwd via `IO/LeanLogicalProcess.cs`, per-process `LEAN_NAT_MAX_SIZE`).
- [ ] Verify the "poisoned worker" fix (profiler times were process-global; now per logical
      process). Symptom was ~20% spurious failures late in a full `elab` run with `-j 5`.
- [ ] Verify the CaDiCaL wiring still passes all `bv_*` tests after the rebuild.

Known open bugs:

- [ ] **`lean --run` resolves the user's `main` to Lake's compiled `main`.** The interpreter looks
      up compiled symbols by name only; `M_LakeMain._lean_main` is in the same assembly. Fix: use
      compiled code only for declarations from *imported* modules that are compiled in, and look
      the symbol up in that module's class (`src/LeanSharp.Runtime/Interp/IrInterpreter.cs`,
      `LeanCompiledCode.cs`). The fix appears to be written (the interpreter now asks
      `Environment.getModuleIdxFor?` through the compiled
      `l_Lean_Environment_getModuleIdxFor_x3f___boxed` and looks symbols up per module class; see
      `CompiledModules` near the end of `IrInterpreter.cs`), but the agent was cut off before
      building or testing it. Verify with `--pile compile` and `--pile docparse --filter '^arg_00'`.
      This should unblock `docparse` and most of `compile`.
- [ ] `server` pile: `init_exit*.lean` kill the worker process (an in-process `lean --server`
      child calling `IO.Process.exit`/`forceExit` must only end the child); `diags.lean` and
      `init_exit_worker.lean` hang.
- [ ] Remaining `compile` failures after the `--run` fix (e.g. the `*_mark_linear_panic` tests,
      `wait_dedicated`, `uv_cancel_releases_outside_lock`).
- [ ] The native-compile half of the `compile` pile cannot work without a C compiler. Decide
      whether to skip it permanently or emit C# for user code and compile it with Roslyn.

## 2. Not done yet

- [ ] **Build the standard library `.olean` files with LeanSharp itself.** Today the sysroot is
      the `lib/lean` directory of a native Lean build of the same commit. If the target
      environment (CloudBuild) does not allow shipping those data files, add a tool that compiles
      `Init`, `Std`, `Lean`, `Lake` from the Lean sources with LeanSharp (the `.olean` writer is
      already byte-compatible).
- [ ] `LeanProject` API (`src/LeanSharp/LeanProject.cs`) and `examples/ProjectDemo`: compile, run
      on `artifacts/lakeproj`-style project, add tests.
- [ ] Run the `lake` and `pkg` test piles. Lake targets that build native executables/shared
      libraries (`leanc`, `cc`) are unsupported.
- [ ] Windows and Linux: everything so far was run on macOS arm64 only.
- [ ] A proper test project (e.g. xunit) wrapping the piles, and CI.
- [ ] Decide how to version `gen/` (440 MB of generated C#): commit as is, compress, or generate
      in CI from a native Lean build.
- [ ] Nothing has been committed to git yet.

## 3. Performance and memory

- [ ] **Memory.** `import Lean` allocates ~4.4 GB (native: ~2 GB mmap). Ideas: remove `m_id`
      (8 bytes on every object; use a side table for `ptrAddrUnsafe`), move the scalar fields
      `s0`/`sx` out of constructor objects that have no scalars, lazy/deferred `.olean` decoding.
- [ ] **Startup.** ~2 s to initialize all 2,553 modules (JIT), ~4 s for a trivial file vs 0.3 s
      native. Options: ReadyToRun compilation of `LeanSharp.Lean`, lazy module initialization.
- [ ] **Build time.** The 12.8M-line `LeanSharp.Lean` assembly recompiles (~5–10 min, several GB)
      whenever the public surface of `LeanSharp.Runtime` changes. Options: split the generated
      code into several assemblies, or freeze a thin public runtime API.
- [ ] Generated code size: long mangled names; could shorten symbols with a name table.
- [ ] Heartbeats are counted on constructor/closure allocations only, so `maxHeartbeats` limits
      trigger at different points than native Lean.
- [ ] CaDiCaL port is ~1–3× slower than native; the arena clause mover is not ported.
- [ ] Kernel externs leak one reference count on results (performance only).
- [ ] Stack overflow in deeply recursive Lean code kills the .NET process (cannot be caught);
      native Lean reports it. Consider stack probes in generated code.

## 4. Known limitations (documented, low priority)

- `Metadata.numLinks` only counts hard links created by this process.
- File locks (`IO.FS.Handle.lock`) only work within one process.
- LLVM backend, dynamic libraries/plugins (`--plugin`, `--load-dynlib`): not supported.
- `Lean.openSSLVersion` reports a fixed OpenSSL 3.0.0 number; nothing is linked.
- State owned by compiled Lean code (global `IO.Ref`s, registered options/extensions) is shared
  by all in-process `lean`/`lake` runs of one OS process.
- `.olean` files with closures (format v3) are only readable by the process that wrote them.

## 5. Cleanup

- [ ] `checks/*` (per-area check programs written by the porting agents) may no longer compile
      on their own since Core now references other areas; convert to real tests or delete.
- [ ] `tools/gen_stubs.py` is obsolete (no stubs left).
- [ ] Uv promise helper uses reflection to find the task functions; call them directly.

## 6. Handoff notes for a new session (human or AI assistant)

The chat session that produced this repository cannot be moved to another machine: its transcript
and the assistant's memory are stored locally. Everything needed to continue is in this file,
`Docs/RUNNING.md`, `Docs/DESIGN.md` and `Docs/PORTING.md`. To continue with an AI assistant, start
a new session in the repo root and ask it to read those four files first, then work through
section 0 above.

### Goal (from the project owner)

A pure C# (.NET 10) library, usable from Microsoft CloudBuild (which only allows pure managed
code: no native binaries, no other languages), that can take a Lean 4 project directory and run
Lean on it, and that can run the Lean 4 test suite. No call to a native Lean installation.

### Machines and paths assumed so far

* Repo: `~/Repos/leansharp`. Lean checkout: `~/Repos/lean4` at commit `67a8629274`
  (4.36.0-pre) with a native build in `build/release/stage1` (provides `.olean` files, the `.ir`
  files and C files used by the emitter, the reference `lean` binary, and the tests).
* `artifacts/` is git-ignored: `artifacts/sysroot` (symlink to the native `lib/lean`),
  `artifacts/lakeproj` (tiny Lake project used for smoke tests), result files (`*.tsv`, `*.log`).
  Recreate them on a new machine (Docs/RUNNING.md, "Sysroot").
* Do not use the `lean` found on `PATH` via elan for generation: the emitter must read the
  stage1 build (an early bug came from `findSysroot` picking up elan's 4.34.1).

### How the work was organised

* `tools/EmitCSharp/EmitCSharp.lean` generates `gen/` (never edit `gen/` by hand; change the
  emitter and run `tools/regen.sh gen`). It is a port of the old `IR/EmitC.lean`; init order per
  module is taken from the native build's C files (`lib/temp/**/*.c`).
* `src/LeanSharp.Runtime/Core` is the contract everything depends on (`Obj.cs`,
  `LeanRt.Object.cs`, `LeanRt.Basics.cs`, generated `LeanRt.Apply.cs` from `tools/gen_apply.py`).
  The other areas (`Numbers`, `Strings`, `IO`, `Tasks`, `Kernel`, `Compact`, `Interp`, `Uv`,
  `Cadical`) were ported in parallel, one per directory, following `Docs/PORTING.md` and the exact
  signatures in `Docs/externs-*.txt`.
* Runtime → Lean calls go through `LeanExports` (names in `Docs/lean-exports.txt`); the host
  wires hooks in `src/LeanSharp/HostHooks.cs` (process spawn interception for `lean`, `lake`,
  `cadical`; interpreter access to compiled modules; heartbeat hooks).
* In-process subprocess state (cwd, env, exit, panic flags, profiling) is per "logical process":
  `src/LeanSharp.Runtime/IO/LeanLogicalProcess.cs`.

### Practical lessons

* Build one thing at a time. Parallel `dotnet build` invocations block each other, and the
  compiler server (VBCSCompiler) uses 4–5 GB while compiling `LeanSharp.Lean`. If a build hangs,
  `dotnet build-server shutdown`.
* Changing the public surface of `LeanSharp.Runtime` recompiles the generated assembly
  (~5–10 min). Prefer `internal` members while debugging.
* Full `elab` run: ~25 min with `-j 5`; each worker can reach 4–5 GB on `import Lean` tests.
* When a test passes alone but fails in a full run, suspect process-global state leaking between
  in-process runs (see the profiling leak in section 1).
* Useful smoke tests after any change:
  `LeanSharp.Cli --version`; a file with `def`/`theorem`/`#eval`/`by decide`; `lake build` in a
  two-module project; `--pile elab --filter '^1[0-9]{4}\.lean$'` (61 tests, ~1 min).
* Reference behaviour: run the same command with `~/Repos/lean4/build/release/stage1/bin/lean`.

### What was verified vs. only claimed

* Verified by running, on binaries built before the last round of changes: the test table in
  section 1; `lake build` on `artifacts/lakeproj` and native Lean importing its output; `.olean`
  round trip of all 12,788 library files byte-identical (compact area check program).
* Claimed by the porting agents from their own check programs, not re-run by the lead:
  per-area checks in `checks/*`, CaDiCaL byte-identical LRAT proofs versus native (`--no-arena`).
* Not built at all yet: `LeanProject.cs`, `examples/ProjectDemo`, the logical-process changes in
  `IO/`, `Tasks/`, `Kernel/TypeChecker.cs`, `Uv/`, `LeanHost.cs`/`LeanShell.cs`/`LakeShell.cs`,
  and the interpreter symbol-resolution fix.
