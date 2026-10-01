# LeanSharp porting guide

LeanSharp is a pure C# (.NET 10) implementation of Lean 4. The Lean compiler, elaborator and
standard library (written in Lean) are translated to C# from Lean's compiled IR by
`tools/EmitCSharp` (≈12.8M lines, one static class per Lean module, in `gen/`). What Lean
implements in C/C++ (`lean.h`, `src/runtime`, `src/kernel`, `src/library`, `src/util` of the Lean
repository at `~/Repos/lean4/src`) is ported by hand to `src/LeanSharp.Runtime`.

The Lean version is the one in `~/Repos/lean4` (4.36.0-pre, commit 67a8629274). Always port from
those sources.

## Rules

* Pure managed C#. No P/Invoke, no native libraries, no NuGet dependencies in the runtime.
  `unsafe` code and function pointers (`delegate*`) are fine.
* Port faithfully, including reference counting (see below): Lean relies on exact RCs for
  destructive updates. When in doubt, mirror the C code line by line.
* All externs are `public static` methods of `public static unsafe partial class LeanRt`
  (namespace `LeanSharp.Runtime`), named exactly like the C function.
* **Signatures must match `Docs/externs-<area>.txt` exactly** (these are the C# signatures the
  generated code calls). Type mapping: `uint8_t`→`byte` (also for `bool` results!), `uint16_t`→
  `ushort`, `uint32_t`→`uint`, `uint64_t`/`size_t`→`ulong`, `double`→`double`, `float`→`float`,
  `lean_object*`→`Obj`. Erased and `void` (IO world) parameters have already been removed from
  those signatures. Never return `bool` from an extern whose Lean type is `Bool`/`UInt8`: return
  `byte`. Internal helpers can use any types.
* Functions from `lean.h` that the generated code does not call directly may still be needed by
  other runtime code; port them where useful, with the same names.
* Put your code in your own area directory (`src/LeanSharp.Runtime/<Area>/`). Do not edit files
  in `Core/` or other areas. If you need something from another area that does not exist yet,
  write a private helper in your area (e.g. `static class NumbersInternal`) and mention it in
  your report. If you believe `Core/` needs a change, describe it in your report.
* Build check: create `checks/<area>/Check.csproj` (console app, `<Compile Include>` of
  `../../src/LeanSharp.Runtime/Core/**/*.cs` and `../../src/LeanSharp.Runtime/<Area>/**/*.cs`,
  `<EnableDefaultCompileItems>false</EnableDefaultCompileItems>`) with a `Program.cs` that
  exercises your functions (compare against known values from the C semantics). The root
  `Directory.Build.props` applies (net10.0, unsafe allowed). Do not add your area to other
  projects; the main `src/LeanSharp.Runtime/LeanSharp.Runtime.csproj` picks up all `.cs` files.
* Don't commit to git.

## Object model (see `src/LeanSharp.Runtime/Core/*.cs`)

`Obj` is the base class of every Lean value (`lean_object*`). Header fields: `m_rc`, `m_tag`,
`m_other`, `m_cs_sz`, `m_id`.

| C                         | C#                                                                  |
|---------------------------|---------------------------------------------------------------------|
| `lean_box(n)` tagged ptr  | `Box` (tag `LeanBoxTag`=255, `m_rc`=0). `lean_box`, `lean_unbox`, `lean_is_scalar` |
| ctor object               | `Ctor` .. `Ctor8`, `CtorN` (object fields `f0`..`f7`, `rest`); scalar area in `s0` (bytes 0..7, little endian) and `sx` (bytes 8..). `m_other` = #object fields, `m_cs_sz` = scalar size. Use `lean_alloc_ctor`, `lean_ctor_get/set`, `lean_ctor_get_uint8(o, offset)` (C-style offsets incl. `8*numObjs`) or the `_s` variants (offset relative to the scalar area). |
| closure                   | `Closure` (`m_fun` is a `delegate*<Obj,...,Obj>` with `m_arity` params, or `delegate*<Obj[],Obj>` if arity > 16; `m_objs` fixed args). `lean_alloc_closure(fnptr, arity, nfixed)`, `lean_apply_1..16`, `lean_apply_n`. To make a closure from C# code: `lean_alloc_closure((delegate*<Obj, Obj>)&MyStaticFn, 1, 0)` |
| array                     | `ArrayObj` (`m_data` = Obj[] whose length is the capacity, `m_size`) |
| scalar array              | `SArrayObj` (`m_data` = byte[] of capacity*elemSize bytes, `m_size`, `m_capacity`; elem size = `lean_sarray_elem_size`) |
| string                    | `StrObj` (`m_data` UTF-8 bytes **with terminating NUL**, `m_size` includes the NUL, `m_length` = #code points, capacity = `m_data.Length`) |
| mpz (big nat/int)         | `MpzObj` (`System.Numerics.BigInteger m_value`). Invariant as in C: a Nat/Int that fits in a small scalar is always boxed. |
| thunk / task / promise / ref / external | `ThunkObj`, `TaskObj`, `PromiseObj`, `RefObj`, `ExternalObj` (+ `ExternalClass`) |

* **Reference counting.** `m_rc > 0` single-threaded, `< 0` multi-threaded (atomic ops), `0`
  persistent (never mutated, RC ops are no-ops). `lean_inc`, `lean_dec`, `lean_inc_ref`,
  `lean_dec_ref`, `lean_is_exclusive`, `lean_mark_mt`, `lean_mark_persistent` behave as in C.
  When an RC drops to zero the object's children are released (no memory is freed; the GC does
  that). "Free" functions (`lean_free_object`, `lean_del_object`) are no-ops.
* Arguments are *owned* unless the Lean declaration marks them borrowed (`@&`) — exactly as in C.
  Port the C `lean_inc`/`lean_dec` calls.
* **Identity.** `lean_ptr_addr(o)` gives a unique stable pseudo-address. Use `ReferenceEquals` to
  compare pointers.
* **Nat/Int.** Small values are `Box`; big values `MpzObj`. Helpers in `LeanRt.Basics.cs`:
  `lean_usize_to_nat`, `lean_uint64_to_nat`, `lean_big_to_nat(BigInteger)`, `lean_nat_to_big(Obj)`,
  `lean_alloc_mpz`. For `Int`, small values are boxed as in C (`lean_int64_to_int` etc. — see
  lean.h: small ints are `lean_box((size_t)(int)v)` for values fitting in `int` on 64-bit).
* **Strings.** `lean_mk_string(string)`, `lean_string_to_net(Obj)`, `lean_string_span(Obj)`,
  `lean_mk_string_from_bytes(ReadOnlySpan<byte>)`, `lean_mk_string_unchecked(bytes, size, len)`.
* **Misc helpers:** `MkArray`, `MkList`, `ListToManaged`, `lean_mk_option_some/none`,
  `lean_mk_pair`, `MkByteArray`, `ByteArraySpan`, `LeanHash.Mix` (= `lean_uint64_mix_hash`),
  `LeanHash.HashStr` (= `hash_str`, MurmurHash64A).
* **IO results.** `lean_io_result_mk_ok(v)`, `lean_io_result_mk_error(err)` (the world token has
  been erased: an IO function returns an `EStateM.Result`-like ctor with one field).
* **Panics.** `lean_panic_fn(default, msg)`; `throw lean_internal_panic("msg")` for internal
  errors. `LeanIO.Stdout/Stderr/Stdin` are the process-level streams.

## Calling Lean code from the runtime

Functions implemented in Lean and marked `@[export sym]` are listed in `Docs/lean-exports.txt`
(`sym`, C# function-pointer type, Lean name, defining class). The runtime cannot reference the
generated assemblies directly; use the export table and cache the pointer:

```csharp
static delegate*<uint, Obj, Obj> s_mkOtherError;
static Obj MkOtherError(uint errno, Obj msg) {
    if (s_mkOtherError == null) s_mkOtherError = (delegate*<uint, Obj, Obj>)LeanExports.Get("lean_mk_io_error_other_error");
    return s_mkOtherError(errno, msg);
}
```

Ownership conventions of exported functions are those of the Lean definition (owned args unless
`@&`). Check the Lean source when unsure (`~/Repos/lean4/src/Lean/...`).

## Areas

| Area | Directory | Extern list | C/C++ sources |
|------|-----------|-------------|---------------|
| numbers | `Numbers/` | `externs-numbers.txt` | lean.h (Nat, Int, UIntN, IntN, Float, Float32, Bool, boxing), runtime/object.cpp (big nats/ints), runtime/mpz.cpp, runtime/mpn.cpp (semantics only; use BigInteger) |
| strings_arrays | `Strings/` | `externs-strings_arrays.txt` | lean.h (arrays, sarrays, ByteArray, FloatArray, strings), object.cpp, utf8.cpp, sharecommon.cpp, byteslice.cpp |
| io | `IO/` | `externs-io.txt`, `externs-llvm.txt` (stubs) | io.cpp, process.cpp, platform.cpp, interrupt.cpp, stack_overflow.cpp, library/time_task.cpp, util/*.cpp |
| tasks | `Tasks/` | `externs-tasks.txt` | object.cpp (thunks, tasks, task manager, promises), thread.cpp, mutex.cpp, io.cpp (ST refs) |
| kernel | `Kernel/` | `externs-kernel.txt` | src/kernel/*, src/library/* (not ir_interpreter/llvm), src/util/* as needed |
| compact | `Compact/` | `externs-compact.txt` | compact.cpp, library/module.cpp, `Lean/Environment.lean` (olean format) |
| uv | `Uv/` | `externs-uv.txt` | runtime/uv/*.cpp (use System.Net.Sockets / timers) |
