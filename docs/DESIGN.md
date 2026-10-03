# LeanSharp design

LeanSharp runs Lean 4 (version 4.36.0-pre, Lean commit `77f336f7ae`) on .NET 10 using only
managed code: no native libraries, no P/Invoke, no external processes for `lean`, `lake` or
`cadical`.

## How Lean is built, and how LeanSharp mirrors it

Lean 4 consists of

1. a large body of Lean code (`Init`, `Std`, `Lean`, `Lake`: parser, elaborator, tactics,
   compiler, build system, ~2,550 modules), compiled by Lean's own compiler to C, and
2. a C/C++ part: the runtime (`lean.h`, object model, reference counting, bignums, strings,
   IO, tasks), the kernel (type checker), and a few libraries (IR interpreter, `.olean`
   serialization, CaDiCaL SAT solver, libuv).

LeanSharp keeps this split:

| Lean (native)                              | LeanSharp                                                        |
|--------------------------------------------|------------------------------------------------------------------|
| Lean code → IR → C (`EmitC`)               | Lean code → IR → **C#** (`tools/EmitCSharp`, output in `gen/`)   |
| `lean.h`, `src/runtime`                    | `src/LeanSharp.Runtime/{Core,Numbers,Strings,IO,Tasks,Uv}`       |
| `src/kernel`, `src/library`                | `src/LeanSharp.Runtime/{Kernel,Interp,Compact}`                  |
| CaDiCaL                                    | `src/LeanSharp.Runtime/Cadical`                                  |
| `src/util/shell.cpp` (`lean` main)         | `src/LeanSharp/LeanShell.cs`                                     |
| `lake` executable                          | `src/LeanSharp/LakeShell.cs`                                     |

The generated C# (≈12.8M lines, one static class `M_<module>` per Lean module, method names equal
to the C symbol names) is compiled into a single assembly `LeanSharp.Lean` (~170 MB IL). The
emitter is a port of the pre-LCNF `Lean.Compiler.IR.EmitC`; module initialization order is taken
from the C files of the native build (the `.ir` files don't record declaration order).

## Object model

See `src/LeanSharp.Runtime/Core/Obj.cs`. Every Lean value is an `Obj`. Tagged pointers become
cached `Box` objects; constructor objects store up to 8 fields inline. Reference counts are kept
exactly as in C because Lean relies on them for in-place updates; memory itself is reclaimed by
the .NET GC. `ptrAddrUnsafe` is emulated with a unique id per object (`m_id`): the logical address for objects
read from an `.olean` file (as natively, where the file is mapped at that address; this also lets
the compactor recognise objects of dependency regions by a range check), and a lazily assigned
counter value for all others.

Closures can be saved (`--incr-save` snapshots): the file lists the static methods the closures
point to by assembly, type and name (`Compact/FunctionTable.cs`), the managed counterpart of the
native library-relative function addresses.

## Stack overflow and heartbeats

A .NET stack overflow cannot be caught and would take down every Lean program hosted by the OS
process. The emitter therefore puts `lean_stack_probe()` at the start of every function that can
take part in a recursion among compiled functions (it calls itself or a function later in its
module; every cycle of a module's call graph contains such a call), and `lean_apply_N` probes for
recursion through closures. The probe compares the stack pointer with a per-thread limit set when
a Lean thread starts (`Core/LeanRt.Stack.cs`); past the limit the program prints
"Stack overflow detected. Aborting." and ends with status 134, as natively.

Heartbeats follow the native rules: the counter read by `IO.getNumHeartbeats` is incremented by
the allocations native Lean makes with `lean_alloc_small_object` (constructors, thunks, refs,
big-number objects, external objects, tasks, promises; not closures, arrays or strings), and the
kernel's `check_heartbeat` has its own per-thread counter and limit (`lean --timeout`), reset
when a task starts.

## Calls between runtime and generated code

* Generated code calls runtime functions directly (`using static LeanSharp.Runtime.LeanRt`).
* The runtime calls Lean functions marked `@[export]` through `LeanExports` (a name → function
  pointer table filled by `LeanModules.RegisterExports()` at startup).
* The IR interpreter (used by `#eval`, macros, `initialize` in user files, …) finds compiled
  functions by name with reflection on the generated assembly.

## Subprocesses and logical processes

Lake spawns `lean` (and `bv_decide` spawns `cadical`). `LeanProcess.ProcessSpawnHook` intercepts
those commands and runs them in-process on a thread with redirected standard streams. Other
commands (`git`, `bash`, a C compiler, ...) are started as real OS processes.

Several Lean programs therefore share one OS process, some of them concurrently (Lake runs as
many `lean` children as there are cores). Everything that is OS-process state natively is kept per
*logical process* (`IO/LeanLogicalProcess.cs`):

* working directory (all runtime functions that take paths resolve relative paths against it),
  environment variables, `IO.appPath`, panic flags, cumulative profiling times, the kernel's
  `LEAN_NAT_MAX_SIZE`, `LEAN_ABORT_ON_NONLINEAR`;
* **the global state of the Lean libraries** (`IO/LeanGlobalRefs.cs`). Lean keeps its global state
  in `IO.Ref`s created by `builtin_initialize` (registered options and attributes, environment
  extensions, the search path, the "importing" and "run initializers" flags, ...). The compiled
  modules are initialized once per OS process; every ref created during that initialization gets
  a slot number, and when initialization is complete the values are made persistent and become
  the *initial state*. From then on reads and writes of such a ref go to the slot array of the
  current logical process (a copy of the initial state made on first use). Each program thus
  starts with the state a fresh native process would have, and programs cannot disturb each
  other (before this, two concurrent `lean` children raced on the `runInitializersRef` flag);
* **the task manager** (`Tasks/LeanTaskManager.cs`, `ProcessState`): each logical process has its
  own pool of worker threads, as natively. A shared pool deadlocks when all workers of a parent
  wait for children that need workers themselves. When a program ends its pool is shut down and
  its remaining tasks see a cancellation request;
* exit: `IO.Process.exit` unwinds the calling thread with `LeanExitException`; an exit requested
  from a task is recorded on the logical process and releases the threads blocked in task
  operations. `IO.Process.forceExit` terminates the OS process only if the program owns it
  (`LeanProgramState.TopLevelProgramOwnsProcess`, set by the command-line tool); otherwise it
  behaves like `exit`, so an in-process language server ends only itself;
* standard input: the `IO.getStdin` handle is one object, but each stream has its own reader
  (lock and buffer), so a program blocked reading its stdin does not block the others.

An `InProcessChild` gets its own logical process; a program started directly by a host
(`LeanShell`/`LakeShell.RunOnCurrentThread`, `LeanProject`) gets a fresh one when the host enters
the program (`LeanHost.EnterProgram` → `LeanProgramState.BeginProgram`). The logical process and
the redirected standard streams form the logical call context, an `AsyncLocal` that flows to
threads/timers/.NET tasks; Lean tasks capture it when they are created.

What is still shared by all programs of one OS process: the generated code's module-level
constants (immutable), the cache of decoded `.olean` regions (immutable, guarded by a per-file
lock), the interpreter's symbol index, and `Lean.manualRoot` (see `InitTimeEnvironment`).

## Executables without a C compiler

Lake builds an executable by compiling the C file Lean emits for each module and linking the
objects against the native Lean runtime. Neither exists here. `src/LeanSharp/ManagedToolchain.cs`
emulates those build steps when they operate on Lean-generated code (the spawn hook recognizes
them by their arguments and by the `// Lean compiler output` header of the C file, whatever the
compiler command is called):

* compile (`cc -c`): writes a text *stub object* naming the module and whether it defines `main`;
* archive (`ar rcs`) and shared-library link (`cc -shared`): write a *stub library* listing the
  stubs. `lean --load-dynlib` of such a library is a no-op (the interpreter runs the code of the
  modules when they are imported). Loading it as a plugin (`lean --plugin`, `Lean.loadPlugin`)
  imports its modules and runs their initializers, see below;
* executable link: writes a *launcher*, a shell script that runs
  `lean --run <launcher>.lean` (a one-line file importing the main module) with the build's
  library directories on `LEAN_PATH`. The IR interpreter executes `main` from the `.olean`/`.ir`
  files Lake built anyway. Spawning a launcher from an in-process program (`lake exe`,
  `lake test`) runs it in-process.

A native module is initialized by a function that runs its `initialize` and `builtin_initialize`
declarations. For interpreted modules Lean itself runs the `initialize` declarations at import;
`src/LeanSharp/InterpretedInit.cs` adds what is missing: the `[builtin_init]` declarations of the
interpreted modules of an environment are run once per program, before `main` of a launcher
(requested with `LEANSHARP_RUN_BUILTIN_INIT=1`) and when a stub library is loaded as a plugin.
`IO.initializing` is per program and true while they run.

Commands involving anything else (hand-written C, real object files) go to the real toolchain if
there is one. The sysroot's `leanc` launcher gives scripts the same emulation
(`lean --c=X.c X.lean; leanc -shared -o X.so X.c`); as such a C file comes without `.olean`, the
module is compiled from the source next to it. Lean's own tools that are Lean programs (`leanchecker`, `leanexport`, `leanir`) are
installed into `<sysroot>/bin` as such launchers by `LeanStdlib.Build` (on Windows as copies of
the .NET application host followed by the launcher's description, see `src/LeanSharp/AppHost.cs`),
and the shared libraries
Lake expects next to the library files (`libLake_shared` etc.) as stub libraries.

## leantar

Lake's artifact cache stores the build outputs of a module as an `.ltar` archive, written and
read by `leantar`, a Rust program (https://github.com/digama0/leangz). `src/LeanSharp/Leantar` is
a port of version 0.1.20 (the one Lean pins):

* `Lgz.cs`: the `.olean`-specific encoding (object graph as a prefix code with back references;
  hashes of names, levels and expressions are recomputed when decoding). Deterministic; its
  output is byte-identical to the Rust encoder's.
* `Ltar.cs`: the archive container and the build-trace handling.
* `LeantarCli.cs`: the command line. The spawn hook runs `leantar` in-process; the sysroot has a
  `leantar` launcher.
* `src/LeanSharp/Zstd`: a managed Zstandard decoder and encoder with dictionary support (.NET 10
  has none). The decoder handles everything the reference produces. The encoder is a lazy
  hash-chain matcher, not the reference's optimal parser: its frames are valid for any zstd
  decoder but 3-11% larger than `zstd -19`, so archives are not byte-identical to native
  `leantar`'s (about 7% larger). Archives are interchangeable in both directions.
  The dictionary `Leantar/v1.dict` (from leangz) is an embedded resource.

## Hard links

.NET 10 has no managed API for hard links. `IO.FS.hardLink` creates them by extracting a one-entry
in-memory tar archive (`System.Formats.Tar` calls `link(2)`/`CreateHardLink`). There is no managed
way to read a link count: `Metadata.numLinks` counts only the links created through `IO.FS.hardLink`
by this OS process (tracked by path, following `rename`/`removeFile`); every other file reports 1.

## Library files (`.olean`)

`import` needs the `.olean` files of `Init`, `Std`, `Lean` (and `Lake` for lakefiles). LeanSharp
reads and writes the native `.olean` format. Set `LEANSHARP_SYSROOT` (or `LeanSysroot.Root`) to a
directory containing `lib/lean/`.

The library files are built **by LeanSharp itself** from the Lean sources
(`LeanStdlib.Build`, command `build-stdlib`): it writes a Lake configuration equivalent to Lean's
`src/lakefile.toml` (library facets only, no C compilation) into the sysroot and runs the managed
Lake on it, which runs the managed `lean` for each of the ~2,560 modules. On a 24-core machine
this takes about 8 minutes and 10 GB. The result was compared with native Lean: for sampled
modules of every library, the native `lean` binary of the same commit run on the same inputs
produces byte-identical `.olean`, `.olean.server`, `.olean.private`, `.ir` and `.c` files. (The
files of a native *stage 1* build differ in a few hundred modules because those are produced by
the older stage 0 compiler.) Files from a native build of the same commit can be used as well.

### Lazy decoding

Native Lean maps an `.olean` file into memory and uses the objects in place, so data that is never
looked at (most proofs and definition bodies) costs nothing. LeanSharp's objects are managed
objects, so they have to be created from the file contents, but this happens on demand
(`Compact/LazyRegion.cs`):

* The file stays mapped (read-only) for the lifetime of the process. On Windows, where a mapped
  file cannot be replaced or deleted, it is copied into native memory instead
  (`LEANSHARP_OLEAN_MMAP=0/1` overrides the default).
* A constructor object is created as a *shell*: tag, scalar fields and identity are set, the
  object fields are null. `lean_ctor_get` treats a null field as "not read yet" and calls
  `lean_ctor_force`, which finds the region and the file position from the object's identity
  (`m_id`, its logical address), creates the field's object and stores it in the field. All
  constructor fields are read through `lean_ctor_get`, so nothing else had to change; the cost
  for ordinary objects is one null check per field read.
* All other objects are created complete. Strings, scalar arrays and big numbers are copied.
  The elements of an array are created together with the array (as shells if they are
  constructor objects), so the code that accesses array storage directly is unaffected. The
  same holds for the values of thunks, tasks, refs, promises and closures.
* Every object is created at most once: each region has a table from file offset to object
  (lock-free lookup, insertion under a lock per region), so sharing and pointer equality are
  the same as with eager decoding. Compacting a lazily decoded graph reproduces the file byte
  for byte (`checks/compact`).

A file can only be read this way if its address range is free in the process. If it is taken
(the file was rebuilt and is read a second time, or two files have the same base address), the
eager reader (`Compact/RegionReader.cs`) is used, which converts all objects in one pass; it is
also what the managed API `OleanFile.Read` uses. `LEANSHARP_OLEAN_LAZY=0` selects the eager
reader everywhere, and `LEANSHARP_TRACE_OLEAN=1` prints the number of regions and created
objects at exit.

Differences with eager decoding: a malformed file is detected when the bad object is reached,
not when the file is read (native Lean does not check at all); and, as natively, a mapped file
must not be truncated in place while a program still uses its objects (Lean and Lake replace
files by renaming, which is safe).

## Regenerating the C#

```
LEAN4=~/Repos/lean4 tools/regen.sh gen
```
reads the `.ir` and `.c` files of a build of the Lean libraries: a native build of the same Lean
commit (`build/release/stage1`), or a sysroot built by LeanSharp (`STAGE=<sysroot>`; the emitter
then runs on LeanSharp, see RUNNING.md). This is a development-time step only (changing the
emitter or the Lean version); building and running LeanSharp needs only the .NET SDK and the Lean
sources.
