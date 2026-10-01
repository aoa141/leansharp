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
the .NET GC. `ptrAddrUnsafe` is emulated with a lazily assigned unique id per object.

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
  stubs. `lean --load-dynlib` and `lean --plugin` of such a library are no-ops: the interpreter
  runs the code of the modules when they are imported (this is what Lake's `precompileModules`
  needs; a plugin that is not imported has no effect);
* executable link: writes a *launcher*, a shell script that runs
  `lean --run <launcher>.lean` (a one-line file importing the main module) with the build's
  library directories on `LEAN_PATH`. The IR interpreter executes `main` from the `.olean`/`.ir`
  files Lake built anyway. Spawning a launcher from an in-process program (`lake exe`,
  `lake test`) runs it in-process.

Commands involving anything else (hand-written C, real object files) go to the real toolchain if
there is one. Lean's own tools that are Lean programs (`leanchecker`, `leanexport`, `leanir`) are
installed into `<sysroot>/bin` as such launchers by `LeanStdlib.Build`, and the shared libraries
Lake expects next to the library files (`libLake_shared` etc.) as stub libraries.

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

## Regenerating the C#

```
LEAN4=~/Repos/lean4 tools/regen.sh gen
```
reads the `.ir` and `.c` files of a build of the Lean libraries: a native build of the same Lean
commit (`build/release/stage1`), or a sysroot built by LeanSharp (`STAGE=<sysroot>`; the emitter
then runs on LeanSharp, see RUNNING.md). This is a development-time step only (changing the
emitter or the Lean version); building and running LeanSharp needs only the .NET SDK and the Lean
sources.
