# LeanSharp design

LeanSharp runs Lean 4 (version 4.36.0-pre, Lean commit `67a8629274`) on .NET 10 using only
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

## Subprocesses

Lake spawns `lean` (and `bv_decide` spawns `cadical`). `LeanProcess.ProcessSpawnHook` intercepts
those commands and runs them in-process on a thread with redirected standard streams.

Several Lean programs therefore share one OS process, so what is OS-process state natively is kept
per *logical process* (`IO/LeanLogicalProcess.cs`): working directory (all runtime functions that
take paths resolve relative paths against it), environment variables, `IO.appPath`, a pending
`IO.Process.exit` from a task, panic flags, cumulative profiling times and the kernel's
`LEAN_NAT_MAX_SIZE`. An `InProcessChild` gets its own logical process; a program started directly
by a host (`LeanShell`/`LakeShell.RunOnCurrentThread`) gets a fresh one that inherits working
directory and environment when its `main` starts (`lean_io_mark_end_initialization`). The logical
process and the redirected standard streams form the logical call context, an `AsyncLocal` that
flows to threads/timers/.NET tasks; Lean tasks capture it when they are created because the worker
threads of the task manager are shared by all programs. State owned by the compiled Lean code
(global `IO.Ref`s such as registered options or environment extensions) is still shared.

## Hard links

.NET 10 has no managed API for hard links. `IO.FS.hardLink` creates them by extracting a one-entry
in-memory tar archive (`System.Formats.Tar` calls `link(2)`/`CreateHardLink`). There is no managed
way to read a link count: `Metadata.numLinks` counts only the links created through `IO.FS.hardLink`
by this OS process (tracked by path, following `rename`/`removeFile`); every other file reports 1.

## Library files (`.olean`)

`import` needs the `.olean` files of `Init`, `Std`, `Lean` (and `Lake` for lakefiles). LeanSharp
reads and writes the native `.olean` format, so it can use the files produced by the matching
native Lean build, or files produced by LeanSharp itself. Set `LEANSHARP_SYSROOT` (or
`LeanSysroot.Root`) to a directory containing `lib/lean/`.

## Regenerating the C#

```
LEAN4=~/Repos/lean4 tools/regen.sh gen
```
requires a native build of the same Lean commit (`build/release/stage1`). This is a
development-time step only; building and running LeanSharp needs only the .NET SDK.
