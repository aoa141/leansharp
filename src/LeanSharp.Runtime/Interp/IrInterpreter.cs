// Port of library/ir_interpreter.cpp: a simple interpreter for λRC IR code.
//
// The interpreter mainly consists of a homogeneous stack of `Value`s, which are either unboxed
// values or pointers to boxed objects. The IR type system tells us which member is active at any
// time. IR variables are mapped to stack slots by adding the current base pointer to the variable
// index. Further stacks are used for storing join points and call stack metadata. The interpreted
// IR (`Lean.IR.Decl` objects) is taken directly from the environment. Whenever possible, we switch
// to compiled code (the C# translation of the Lean libraries, see `LeanCompiledCode`), which is
// also how external functions are called. We always call the "boxed" versions of compiled
// functions, which have a homogeneous ABI (`Obj` parameters and result).

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using static LeanSharp.Runtime.LeanRt;

namespace LeanSharp.Runtime.Interp;

/// <summary>`Lean.IR.IRType` (constructor indices; all but `struct`/`union` are boxed scalars).</summary>
internal enum IrType : byte
{
    Float, UInt8, UInt16, UInt32, UInt64, USize, Irrelevant, Object, TObject, Float32, Struct, Union, Tagged, Void
}

/// <summary>`Lean.IR.Expr` constructor indices.</summary>
internal enum ExprKind : byte { Ctor, Reset, Reuse, Proj, UProj, SProj, FAp, PAp, Ap, Box, Unbox, Lit, IsShared }

/// <summary>`Lean.IR.FnBody` constructor indices.</summary>
internal enum FnBodyKind : byte { VDecl, JDecl, Set, SetTag, USet, SSet, Inc, Dec, Del, Case, Ret, Jmp, Unreachable }

/// <summary>Value stored in an interpreter variable slot (C++ `union value`). Floats are stored as
/// their bit patterns in `N`.</summary>
internal struct Value
{
    public ulong N;
    public Obj O;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Num(ulong n) => new Value { N = n };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Value Of(Obj o) => new Value { O = o };
    public static Value FromFloat(double f) => new Value { N = BitConverter.DoubleToUInt64Bits(f) };
    public static Value FromFloat32(float f) => new Value { N = BitConverter.SingleToUInt32Bits(f) };
    public double Float => BitConverter.UInt64BitsToDouble(N);
    public float Float32 => BitConverter.UInt32BitsToSingle((uint)N);
}

/// <summary>Result of looking up compiled code for a declaration (C++ `native_symbol_cache_entry`).</summary>
internal sealed unsafe class NativeEntry
{
    public static readonly NativeEntry None = new();
    /// <summary>Function pointer (`delegate*&lt;Obj, ..., Obj&gt;` or `delegate*&lt;Obj[], Obj&gt;`) of a compiled function.</summary>
    public void* Fn;
    /// <summary>True iff we chose the boxed version of a function where the IR uses the unboxed version.</summary>
    public bool Boxed;
    /// <summary>Constant stored in a static field (initialized by the module initializer).</summary>
    public FieldInfo Field;
    /// <summary>Constant exposed as a static property (lazily initialized closed term).</summary>
    public void* GetterFn;
    public Type GetterType;
    public string Symbol;
    /// <summary>Module class the lookup was done in (the global cache is validated against it).</summary>
    public Type Cls;

    public bool Found => Fn != null || Field != null || GetterFn != null;
}

/// <summary>Accessors of the IR data types (`Lean.Compiler.IR.Basic`). VarId/JoinPointId are
/// represented by their `Nat` index.</summary>
internal static class Ir
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Obj F(Obj o, int i) => lean_ctor_get(o, (uint)i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Idx(Obj nat) => (int)lean_unbox(nat);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Size(Obj arr) => (int)Unsafe.As<ArrayObj>(arr).m_size;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Obj At(Obj arr, int i) => Unsafe.As<ArrayObj>(arr).m_data[i];

    public static IrType ToType(Obj o)
    {
        if (!lean_is_scalar(o)) throw new InterpreterException("unsupported IRType");
        return (IrType)lean_unbox(o);
    }

    public static bool TypeIsScalar(IrType t) =>
        t != IrType.Object && t != IrType.Tagged && t != IrType.TObject && t != IrType.Irrelevant && t != IrType.Void;

    // Arg
    public static bool ArgIsIrrelevant(Obj a) => lean_is_scalar(a);
    public static Obj ArgVarId(Obj a) => F(a, 0);

    // CtorInfo: name, cidx, size, usize, ssize
    public static int CtorTag(Obj c) => Idx(F(c, 1));
    public static int CtorSize(Obj c) => Idx(F(c, 2));
    public static int CtorUSize(Obj c) => Idx(F(c, 3));
    public static int CtorSSize(Obj c) => Idx(F(c, 4));

    // Expr
    public static ExprKind ExprTag(Obj e) => (ExprKind)lean_obj_tag(e);

    // Param: x, ty (objects), borrow (scalar)
    public static Obj ParamVar(Obj p) => F(p, 0);
    public static IrType ParamType(Obj p) => ToType(F(p, 1));
    public static bool ParamBorrow(Obj p) => lean_ctor_get_uint8_s(p, 0) != 0;

    // FnBody
    public static FnBodyKind BodyTag(Obj b) => (FnBodyKind)lean_obj_tag(b);

    // Decl: fdecl (f, xs, type, body, info) | extern (f, xs, type, ext)
    public const uint DeclFun = 0, DeclExtern = 1;
    public static uint DeclTag(Obj d) => lean_obj_tag(d);
    public static Obj DeclName(Obj d) => F(d, 0);
    public static Obj DeclParams(Obj d) => F(d, 1);
    public static IrType DeclType(Obj d) => ToType(F(d, 2));

    public static Obj DeclFunBody(Obj d)
    {
        if (DeclTag(d) != DeclFun)
            throw new InterpreterException($"(interpreter) IR of declaration '{IrName.ToString(DeclName(d))}' not available; this may point to a missing `meta` check in a metaprogram");
        return F(d, 3);
    }
}

internal sealed unsafe class IrInterpreter : IDisposable
{
    // stack of IR variable slots
    Value[] m_args = new Value[256];
    int m_argSize;
    // stack of join points (`jdecl` nodes)
    Obj[] m_jps = new Obj[64];
    int m_jpSize;

    struct Frame
    {
        public Obj Fn;
        // base pointers into the stacks above
        public int ArgBp;
        public int JpBp;
    }
    Frame[] m_frames = new Frame[64];
    int m_frameCount;

    readonly Obj m_env;
    readonly Obj m_opts;
    // if `false`, use IR code where possible
    readonly bool m_preferNative;

    struct ConstantCacheEntry
    {
        public bool IsScalar;
        public Value Val;
    }
    // caches values of nullary functions ("constants")
    readonly Dictionary<Obj, ConstantCacheEntry> m_constantCache = new(NameComparer.Instance);

    sealed class SymbolCacheEntry
    {
        public Obj Decl;
        public NativeEntry Native;
    }
    // looking up IR from .oleans is slow enough to warrant its own cache; but as local IR can be
    // backtracked, this cache needs to be local as well. Caches lookup successes _and_ failures.
    readonly Dictionary<Obj, SymbolCacheEntry> m_symbolCache = new(NameComparer.Instance);

    [ThreadStatic] static IrInterpreter t_interpreter;

    // Caches native symbol lookup successes _and_ failures; we assume no compiled code is loaded or
    // unloaded after the interpreter is first invoked, so this can be a global cache.
    static readonly Dictionary<Obj, NativeEntry> s_nativeSymbolCache = new(NameComparer.Instance);
    static readonly object s_nativeLock = new();

    // Constants (lacking native declarations) initialized by `lean_run_init`. They are the
    // global state of the interpreted code (`initialize foo : IO.Ref _ ← ...` of a user module),
    // so each logical process has its own: natively every `lean` process runs the initializers
    // of the modules it imports itself.
    static Dictionary<Obj, Obj> InitGlobals
    {
        get
        {
            var p = LeanContext.Proc;
            var d = (Dictionary<Obj, Obj>)p.InterpreterInitGlobals;
            if (d != null) return d;
            Interlocked.CompareExchange(ref p.InterpreterInitGlobals, new Dictionary<Obj, Obj>(NameComparer.Instance), null);
            return (Dictionary<Obj, Obj>)p.InterpreterInitGlobals;
        }
    }

    public const bool DefaultPreferNative = true;
    static Obj s_preferNativeName;

    static Obj PreferNativeName
    {
        get
        {
            var n = s_preferNativeName;
            if (n == null)
            {
                n = IrName.Mk("interpreter", "prefer_native");
                lean_mark_persistent(n);
                s_preferNativeName = n;
            }
            return n;
        }
    }

    public IrInterpreter(Obj env, Obj opts)
    {
        m_env = env;
        m_opts = opts;
        Obj optName = PreferNativeName;
        lean_inc(opts); lean_inc(optName);
        m_preferNative = InterpExports.OptionsGetBool(opts, optName, DefaultPreferNative ? (byte)1 : (byte)0) != 0;
    }

    public void Dispose()
    {
        foreach (var kv in m_constantCache)
        {
            if (!kv.Value.IsScalar && kv.Value.Val.O != null) lean_dec(kv.Value.Val.O);
            lean_dec(kv.Key);
        }
        m_constantCache.Clear();
        foreach (var kv in m_symbolCache)
        {
            lean_dec(kv.Value.Decl);
            lean_dec(kv.Key);
        }
        m_symbolCache.Clear();
    }

    /// <summary>Drop the global native symbol cache (used when the compiled-code hooks change).</summary>
    public static void ResetGlobalCaches()
    {
        lock (s_nativeLock) s_nativeSymbolCache.Clear();
        CompiledModules.Reset();
    }

    /// <summary>Run `f` with the interpreter of the current thread if it uses the same environment
    /// and options, or with a fresh one (C++ `with_interpreter`).</summary>
    public static T With<T>(Obj env, Obj opts, Func<IrInterpreter, T> f)
    {
        var cur = t_interpreter;
        if (cur != null && ReferenceEquals(cur.m_env, env) && ReferenceEquals(cur.m_opts, opts))
            return f(cur);
        // We changed threads or the closure was stored and called in a different context.
        // The caches contain data from the Environment, so we cannot reuse them when changing it.
        var interp = new IrInterpreter(env, opts);
        t_interpreter = interp;
        try { return f(interp); }
        finally
        {
            t_interpreter = cur;
            interp.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Stacks

    ref Frame CurFrame
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref m_frames[m_frameCount - 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int Slot(Obj v)
    {
        // variables are 1-indexed
        int i = CurFrame.ArgBp + Ir.Idx(v) - 1;
        // we don't know the frame size (unless we do an additional IR pass), so we extend it dynamically
        if (i >= m_argSize)
        {
            if (i >= m_args.Length) Array.Resize(ref m_args, Math.Max(m_args.Length * 2, i + 1));
            m_argSize = i + 1;
        }
        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    Value GetVar(Obj v)
    {
        // NOTE: `Slot` may reallocate `m_args`, so it must be evaluated before reading the field
        int i = Slot(v);
        return m_args[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void SetVar(Obj v, Value x)
    {
        int i = Slot(v);
        m_args[i] = x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void PushArg(Value v)
    {
        if (m_argSize == m_args.Length) Array.Resize(ref m_args, m_args.Length * 2);
        m_args[m_argSize++] = v;
    }

    void ShrinkArgs(int size)
    {
        if (size < m_argSize) Array.Clear(m_args, size, m_argSize - size);
        m_argSize = size;
    }

    void ShrinkJps(int size)
    {
        if (size < m_jpSize) Array.Clear(m_jps, size, m_jpSize - size);
        m_jpSize = size;
    }

    // specify argument base pointer explicitly because we've usually already pushed some function arguments
    void PushFrame(Obj decl, int argBp)
    {
        if (m_frameCount == m_frames.Length) Array.Resize(ref m_frames, m_frames.Length * 2);
        m_frames[m_frameCount++] = new Frame { Fn = Ir.DeclName(decl), ArgBp = argBp, JpBp = m_jpSize };
    }

    void PopFrame()
    {
        ref var f = ref CurFrame;
        ShrinkArgs(f.ArgBp);
        ShrinkJps(f.JpBp);
        f = default;
        m_frameCount--;
    }

    /// <summary>Restore the stacks after an exception escaped from nested evaluation (the C++
    /// interpreter leaves them in an inconsistent state in that case).</summary>
    void RestoreStacks(int argSize, int jpSize, int frameCount)
    {
        while (m_frameCount > frameCount) m_frames[--m_frameCount] = default;
        if (m_jpSize > jpSize) ShrinkJps(jpSize);
        if (m_argSize > argSize) ShrinkArgs(argSize);
    }

    // ------------------------------------------------------------------
    // Values

    static Obj BoxT(Value v, IrType t)
    {
        switch (t)
        {
            case IrType.Float: return InterpNum.BoxFloat(v.Float);
            case IrType.Float32: return InterpNum.BoxFloat32(v.Float32);
            case IrType.UInt8: return lean_box(v.N);
            case IrType.UInt16: return lean_box(v.N);
            case IrType.UInt32: return lean_box(v.N);
            case IrType.UInt64: return InterpNum.BoxUInt64(v.N);
            case IrType.USize: return InterpNum.BoxUInt64(v.N);
            case IrType.Object:
            case IrType.Tagged:
            case IrType.TObject:
            case IrType.Irrelevant:
            case IrType.Void:
                // a slot that was never assigned (e.g. an erased world token) holds no object
                return v.O ?? lean_box(0);
            default:
                throw new InterpreterException("not implemented yet");
        }
    }

    static Value UnboxT(Obj o, IrType t)
    {
        switch (t)
        {
            case IrType.Float: return Value.FromFloat(InterpNum.UnboxFloat(o));
            case IrType.Float32: return Value.FromFloat32(InterpNum.UnboxFloat32(o));
            case IrType.UInt8: return Value.Num(lean_unbox(o));
            case IrType.UInt16: return Value.Num(lean_unbox(o));
            case IrType.UInt32: return Value.Num((uint)lean_unbox(o));
            case IrType.UInt64: return Value.Num(InterpNum.UnboxUInt64(o));
            case IrType.USize: return Value.Num(InterpNum.UnboxUInt64(o));
            case IrType.Struct:
            case IrType.Union:
                throw new InterpreterException("not implemented yet");
            default:
                throw new InterpreterException("(interpreter) cannot unbox a value of non-scalar type");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    Value EvalArg(Obj a)
    {
        // an "irrelevant" argument is type- or proof-erased; we can use an arbitrary value for it
        return Ir.ArgIsIrrelevant(a) ? Value.Of(lean_box(0)) : GetVar(Ir.ArgVarId(a));
    }

    /// <summary>Allocate constructor object with given tag and arguments.</summary>
    Obj AllocCtor(Obj info, Obj args)
    {
        int tag = Ir.CtorTag(info);
        // number of boxed object fields
        int size = Ir.CtorSize(info);
        // number of unboxed USize fields (whose byte size the IR is ignorant of)
        int usize = Ir.CtorUSize(info);
        // byte size of all other unboxed fields
        int ssize = Ir.CtorSSize(info);
        if (size == 0 && usize == 0 && ssize == 0)
        {
            // a constructor without data is optimized to a tagged pointer
            return lean_box((ulong)tag);
        }
        Obj o = lean_alloc_ctor((uint)tag, (uint)size, (uint)(usize * 8 + ssize));
        int n = Ir.Size(args);
        for (int i = 0; i < n; i++)
            lean_ctor_set(o, (uint)i, EvalArg(Ir.At(args, i)).O);
        return o;
    }

    /// <summary>Return closure pointing to interpreter stub taking interpreter data, declaration to
    /// be called, and partially applied arguments (all owned).</summary>
    Obj MkStubClosure(Obj d, int n, Obj[] args)
    {
        int clsSize = 3 + Ir.Size(Ir.DeclParams(d));
        Obj cls = lean_alloc_closure(GetStub(clsSize), (uint)clsSize, (uint)(3 + n));
        lean_inc(m_env); lean_closure_set(cls, 0, m_env);
        lean_inc(m_opts); lean_closure_set(cls, 1, m_opts);
        lean_inc(d); lean_closure_set(cls, 2, d);
        for (int i = 0; i < n; i++)
            lean_closure_set(cls, (uint)(3 + i), args[i]);
        return cls;
    }

    Value EvalExpr(Obj e, IrType t)
    {
        switch (Ir.ExprTag(e))
        {
            case ExprKind.Ctor:
                return Value.Of(AllocCtor(Ir.F(e, 0), Ir.F(e, 1)));
            case ExprKind.Reset:
            {
                // release fields if unique reference in preparation for `Reuse` below
                Obj o = GetVar(Ir.F(e, 1)).O;
                if (lean_is_exclusive(o))
                {
                    int n = Ir.Idx(Ir.F(e, 0));
                    for (int i = 0; i < n; i++) lean_ctor_release(o, (uint)i);
                    return Value.Of(o);
                }
                lean_dec_ref(o);
                return Value.Of(lean_box(0));
            }
            case ExprKind.Reuse:
            {
                // reuse dead allocation if possible
                Obj o = GetVar(Ir.F(e, 0)).O;
                Obj info = Ir.F(e, 1);
                Obj args = Ir.F(e, 2);
                // check if `Reset` above had a unique reference it consumed
                if (lean_is_scalar(o))
                {
                    // fall back to regular allocation
                    return Value.Of(AllocCtor(info, args));
                }
                // create new constructor object in-place
                if (lean_ctor_get_uint8_s(e, 0) != 0) // updtHeader
                    lean_ctor_set_tag(o, (uint)Ir.CtorTag(info));
                int n = Ir.Size(args);
                for (int i = 0; i < n; i++)
                    lean_ctor_set(o, (uint)i, EvalArg(Ir.At(args, i)).O);
                return Value.Of(o);
            }
            case ExprKind.Proj: // object field access
                return Value.Of(lean_ctor_get(GetVar(Ir.F(e, 1)).O, (uint)Ir.Idx(Ir.F(e, 0))));
            case ExprKind.UProj: // USize field access
                return Value.Num(lean_ctor_get_usize(GetVar(Ir.F(e, 1)).O, (uint)Ir.Idx(Ir.F(e, 0))));
            case ExprKind.SProj:
            {
                // other unboxed field access
                uint offset = (uint)(Ir.Idx(Ir.F(e, 0)) * 8 + Ir.Idx(Ir.F(e, 1)));
                Obj o = GetVar(Ir.F(e, 2)).O;
                switch (t)
                {
                    case IrType.Float: return Value.FromFloat(lean_ctor_get_float(o, offset));
                    case IrType.Float32: return Value.FromFloat32(lean_ctor_get_float32(o, offset));
                    case IrType.UInt8: return Value.Num(lean_ctor_get_uint8(o, offset));
                    case IrType.UInt16: return Value.Num(lean_ctor_get_uint16(o, offset));
                    case IrType.UInt32: return Value.Num(lean_ctor_get_uint32(o, offset));
                    case IrType.UInt64: return Value.Num(lean_ctor_get_uint64(o, offset));
                }
                throw new InterpreterException("invalid instruction");
            }
            case ExprKind.FAp:
            {
                // saturated ("full") application of top-level function
                Obj args = Ir.F(e, 1);
                if (Ir.Size(args) > 0) return Call(Ir.F(e, 0), args);
                // nullary function ("constant")
                return Load(Ir.F(e, 0), t);
            }
            case ExprKind.PAp:
            {
                // unsaturated (partial) application of top-level function
                Obj fn = Ir.F(e, 0);
                Obj args = Ir.F(e, 1);
                int n = Ir.Size(args);
                var sym = LookupSymbol(fn);
                if (sym.Native.Fn != null)
                {
                    // point closure directly at compiled code
                    Obj cls = lean_alloc_closure(sym.Native.Fn, (uint)Ir.Size(Ir.DeclParams(sym.Decl)), (uint)n);
                    for (int i = 0; i < n; i++)
                        lean_closure_set(cls, (uint)i, EvalArg(Ir.At(args, i)).O);
                    return Value.Of(cls);
                }
                // point closure at interpreter stub
                var xs = new Obj[n];
                for (int i = 0; i < n; i++) xs[i] = EvalArg(Ir.At(args, i)).O;
                return Value.Of(MkStubClosure(sym.Decl, n, xs));
            }
            case ExprKind.Ap:
            {
                // (saturated or unsaturated) application of closure; mostly handled by runtime
                Obj args = Ir.F(e, 1);
                int n = Ir.Size(args);
                var xs = new Obj[n];
                for (int i = 0; i < n; i++) xs[i] = EvalArg(Ir.At(args, i)).O;
                return Value.Of(lean_apply_n(GetVar(Ir.F(e, 0)).O, (uint)n, xs));
            }
            case ExprKind.Box: // box unboxed value
                return Value.Of(BoxT(GetVar(Ir.F(e, 1)), Ir.ToType(Ir.F(e, 0))));
            case ExprKind.Unbox: // unbox boxed value
                return UnboxT(GetVar(Ir.F(e, 0)).O, t);
            case ExprKind.Lit:
            {
                // load numeric or string literal
                Obj lit = Ir.F(e, 0);
                Obj v = Ir.F(lit, 0);
                if (lean_obj_tag(lit) == 0)
                {
                    switch (t)
                    {
                        case IrType.Float: return Value.FromFloat(InterpNum.FloatOfNat(v));
                        case IrType.Float32: return Value.FromFloat32(InterpNum.Float32OfNat(v));
                        case IrType.UInt8:
                        case IrType.UInt16:
                        case IrType.UInt32:
                        case IrType.USize:
                            return Value.Num(InterpNum.USizeOfNat(v));
                        case IrType.UInt64:
                            return Value.Num(InterpNum.UInt64OfNat(v));
                        // `nat` literal
                        case IrType.Object:
                        case IrType.Tagged:
                        case IrType.TObject:
                            lean_inc(v);
                            return Value.Of(v);
                    }
                    throw new InterpreterException("invalid instruction");
                }
                lean_inc(v);
                return Value.Of(v);
            }
            case ExprKind.IsShared:
                return Value.Num(lean_is_exclusive(GetVar(Ir.F(e, 0)).O) ? 0UL : 1UL);
        }
        throw new InterpreterException($"unexpected instruction kind {(int)Ir.ExprTag(e)}");
    }

    void CheckSystem()
    {
        if (RuntimeHelpers.TryEnsureSufficientExecutionStack()) return;
        var sb = new StringBuilder();
        sb.Append("deep recursion was detected at 'interpreter' (potential solution: increase stack space in your system)\n");
        sb.Append("interpreter stacktrace:\n");
        for (int i = 0; i < m_frameCount; i++)
            sb.Append('#').Append(i + 1).Append(' ').Append(IrName.ToString(m_frames[m_frameCount - i - 1].Fn)).Append('\n');
        throw new InterpreterStackOverflowException(sb.ToString());
    }

    Value EvalBody(Obj b0)
    {
        CheckSystem();
        Obj b = b0;
        while (true)
        {
            switch (Ir.BodyTag(b))
            {
                case FnBodyKind.VDecl:
                {
                    // variable declaration
                    Obj x = Ir.F(b, 0);
                    Obj e = Ir.F(b, 2);
                    Obj cont = Ir.F(b, 3);
                    // tail recursion?
                    if (Ir.ExprTag(e) == ExprKind.FAp && Ir.BodyTag(cont) == FnBodyKind.Ret &&
                        !Ir.ArgIsIrrelevant(Ir.F(cont, 0)) && Ir.Idx(Ir.ArgVarId(Ir.F(cont, 0))) == Ir.Idx(x) &&
                        IrName.Eq(Ir.F(e, 0), CurFrame.Fn))
                    {
                        // tail recursion! copy argument values to parameter slots and reset `b`
                        Obj args = Ir.F(e, 1);
                        int n = Ir.Size(args);
                        // argument and parameter slots may overlap, so first copy arguments to end of stack
                        int oldSize = m_argSize;
                        for (int i = 0; i < n; i++) PushArg(EvalArg(Ir.At(args, i)));
                        // now copy to parameter slots
                        int bp = CurFrame.ArgBp;
                        for (int i = 0; i < n; i++) m_args[bp + i] = m_args[oldSize + i];
                        ShrinkArgs(bp + n);
                        b = b0;
                        CheckSystem();
                        break;
                    }
                    Value v = EvalExpr(e, Ir.ToType(Ir.F(b, 1)));
                    // NOTE: the slot must be computed *after* `EvalExpr` because the stack may get resized
                    SetVar(x, v);
                    b = cont;
                    break;
                }
                case FnBodyKind.JDecl:
                {
                    // join-point declaration; store in stack slot just like variables
                    int i = CurFrame.JpBp + Ir.Idx(Ir.F(b, 0));
                    if (i >= m_jpSize)
                    {
                        if (i >= m_jps.Length) Array.Resize(ref m_jps, Math.Max(m_jps.Length * 2, i + 1));
                        m_jpSize = i + 1;
                    }
                    m_jps[i] = b;
                    b = Ir.F(b, 3);
                    break;
                }
                case FnBodyKind.Set:
                {
                    // set boxed field of unique reference
                    Obj o = GetVar(Ir.F(b, 0)).O;
                    lean_ctor_set(o, (uint)Ir.Idx(Ir.F(b, 1)), EvalArg(Ir.F(b, 2)).O);
                    b = Ir.F(b, 3);
                    break;
                }
                case FnBodyKind.SetTag:
                {
                    // set constructor tag of unique reference
                    Obj o = GetVar(Ir.F(b, 0)).O;
                    lean_ctor_set_tag(o, (uint)Ir.Idx(Ir.F(b, 1)));
                    b = Ir.F(b, 2);
                    break;
                }
                case FnBodyKind.USet:
                {
                    // set USize field of unique reference
                    Obj o = GetVar(Ir.F(b, 0)).O;
                    lean_ctor_set_usize(o, (uint)Ir.Idx(Ir.F(b, 1)), GetVar(Ir.F(b, 2)).N);
                    b = Ir.F(b, 3);
                    break;
                }
                case FnBodyKind.SSet:
                {
                    // set other unboxed field of unique reference
                    Obj o = GetVar(Ir.F(b, 0)).O;
                    uint offset = (uint)(Ir.Idx(Ir.F(b, 1)) * 8 + Ir.Idx(Ir.F(b, 2)));
                    Value v = GetVar(Ir.F(b, 3));
                    switch (Ir.ToType(Ir.F(b, 4)))
                    {
                        case IrType.Float: lean_ctor_set_float(o, offset, v.Float); break;
                        case IrType.Float32: lean_ctor_set_float32(o, offset, v.Float32); break;
                        case IrType.UInt8: lean_ctor_set_uint8(o, offset, (byte)v.N); break;
                        case IrType.UInt16: lean_ctor_set_uint16(o, offset, (ushort)v.N); break;
                        case IrType.UInt32: lean_ctor_set_uint32(o, offset, (uint)v.N); break;
                        case IrType.UInt64: lean_ctor_set_uint64(o, offset, v.N); break;
                        default: throw new InterpreterException("invalid instruction");
                    }
                    b = Ir.F(b, 5);
                    break;
                }
                case FnBodyKind.Inc:
                {
                    // increment reference counter
                    Obj o = GetVar(Ir.F(b, 0)).O;
                    if (o != null) lean_inc_n(o, (ulong)Ir.Idx(Ir.F(b, 1)));
                    b = Ir.F(b, 2);
                    break;
                }
                case FnBodyKind.Dec:
                {
                    // decrement reference counter
                    int n = Ir.Idx(Ir.F(b, 1));
                    Obj o = GetVar(Ir.F(b, 0)).O;
                    if (o != null)
                        for (int i = 0; i < n; i++) lean_dec(o);
                    b = Ir.F(b, 2);
                    break;
                }
                case FnBodyKind.Del:
                    // delete object of unique reference
                    lean_del_object(GetVar(Ir.F(b, 0)).O);
                    b = Ir.F(b, 1);
                    break;
                case FnBodyKind.Case:
                {
                    // branch according to constructor tag
                    Obj alts = Ir.F(b, 3);
                    Value v = GetVar(Ir.F(b, 1));
                    uint tag = Ir.TypeIsScalar(Ir.ToType(Ir.F(b, 2))) ? (uint)v.N : lean_obj_tag(v.O);
                    int n = Ir.Size(alts);
                    Obj next = null;
                    for (int i = 0; i < n; i++)
                    {
                        Obj a = Ir.At(alts, i);
                        if (lean_obj_tag(a) == 0)
                        {
                            if (tag == (uint)Ir.CtorTag(Ir.F(a, 0))) { next = Ir.F(a, 1); break; }
                        }
                        else { next = Ir.F(a, 0); break; }
                    }
                    if (next == null) throw new InterpreterException("incomplete case");
                    b = next;
                    break;
                }
                case FnBodyKind.Ret:
                    return EvalArg(Ir.F(b, 0));
                case FnBodyKind.Jmp:
                {
                    // jump to join-point
                    Obj jp = m_jps[CurFrame.JpBp + Ir.Idx(Ir.F(b, 0))];
                    Obj ps = Ir.F(jp, 1);
                    Obj args = Ir.F(b, 1);
                    int n = Ir.Size(ps);
                    for (int i = 0; i < n; i++)
                        SetVar(Ir.ParamVar(Ir.At(ps, i)), EvalArg(Ir.At(args, i)));
                    b = Ir.F(jp, 2);
                    break;
                }
                case FnBodyKind.Unreachable:
                    throw new InterpreterException("unreachable code");
                default:
                    throw new InterpreterException($"unexpected instruction kind {(int)Ir.BodyTag(b)}");
            }
        }
    }

    // ------------------------------------------------------------------
    // Symbols

    /// <summary>Retrieve the IR declaration from the environment (owned result).</summary>
    Obj GetDecl(Obj fn)
    {
        lean_inc(m_env); lean_inc(fn);
        Obj d = InterpExports.TakeOption(InterpExports.FindEnvDecl(m_env, fn));
        if (d == null) throw new InterpreterException($"(interpreter) unknown declaration '{IrName.ToString(fn)}'");
        return d;
    }

    bool HasInitAttribute(Obj fn)
    {
        lean_inc(m_env); lean_inc(fn);
        Obj r = InterpExports.GetInitFnNameFor(m_env, fn);
        bool some = !lean_is_scalar(r);
        lean_dec(r);
        return some;
    }

    string GetSymbolStem(Obj fn)
    {
        lean_inc(m_env); lean_inc(fn);
        return InterpExports.TakeString(InterpExports.GetSymbolStem(m_env, fn));
    }

    static string MkMangledBoxedName(string s) =>
        InterpExports.TakeString(InterpExports.MkMangledBoxedName(lean_mk_string(s)));

    static readonly Type s_objType = typeof(Obj);

    static bool IsConstType(Type t) =>
        t == s_objType || t == typeof(byte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong) ||
        t == typeof(double) || t == typeof(float);

    /// <summary>Look for compiled code named `symbol` usable for a declaration with `arity` IR
    /// parameters: a function taking/returning only `Obj` (`Obj[]` if arity &gt; 16) or a constant.</summary>
    static NativeEntry TryMember(string symbol, int arity, Type cls)
    {
        var m = LeanCompiledCode.Find(symbol, cls);
        if (m == null) return null;
        if (arity > 0)
        {
            if (m.Kind != CompiledMemberKind.Method) return null;
            var mi = m.Method;
            if (mi.ReturnType != s_objType || mi.IsGenericMethodDefinition) return null;
            var ps = mi.GetParameters();
            if (arity > LEAN_CLOSURE_MAX_ARGS)
            {
                if (ps.Length != 1 || ps[0].ParameterType != typeof(Obj[])) return null;
            }
            else
            {
                if (ps.Length != arity) return null;
                foreach (var p in ps) if (p.ParameterType != s_objType || p.ParameterType.IsByRef) return null;
            }
            return new NativeEntry { Fn = (void*)mi.MethodHandle.GetFunctionPointer(), Symbol = symbol };
        }
        switch (m.Kind)
        {
            case CompiledMemberKind.Field:
                if (!IsConstType(m.Field.FieldType)) return null;
                return new NativeEntry { Field = m.Field, Symbol = symbol };
            case CompiledMemberKind.Property:
            {
                var g = m.Method;
                if (g.GetParameters().Length != 0 || !IsConstType(g.ReturnType)) return null;
                return new NativeEntry { GetterFn = (void*)g.MethodHandle.GetFunctionPointer(), GetterType = g.ReturnType, Symbol = symbol };
            }
            default:
                return null;
        }
    }

    /// <summary>Look for the compiled code of `fn` in `cls`, the class of the module defining it.</summary>
    NativeEntry FindNative(Obj fn, Obj decl, Type cls)
    {
        int arity = Ir.Size(Ir.DeclParams(decl));
        string mangled = GetSymbolStem(fn);
        string boxedMangled = MkMangledBoxedName(mangled);
        // check for boxed version first
        var e = TryMember(LeanNameMangling.ShortName(boxedMangled), arity, cls);
        if (e != null)
        {
            e.Boxed = true;
            return e;
        }
        // `@[export]` affects only the unboxed symbol name
        lean_inc(m_env); lean_inc(fn);
        Obj exportName = InterpExports.TakeOption(InterpExports.GetExportNameFor(m_env, fn));
        if (exportName != null)
        {
            mangled = IrName.GetString(exportName);
            lean_dec(exportName);
        }
        else mangled = LeanNameMangling.ShortName(mangled);
        // if there is no boxed version, there are no unboxed parameters, so use default version
        return TryMember(mangled, arity, cls) ?? new NativeEntry();
    }

    /// <summary>The class holding the compiled code of the *imported* declaration `fn`, or null if
    /// `fn` is local to the module being elaborated or its module is not a compiled one.
    ///
    /// In C, a declaration of the current file never has a native symbol, and the executable only
    /// contains the symbols of the modules linked into it. The generated assembly however contains
    /// all of `Init`/`Std`/`Lean`/`Lake` (e.g. Lake's `main`), so a symbol must only be used if the
    /// environment says that the declaration comes from that very module.</summary>
    Type CompiledClassOf(Obj fn)
    {
        var mods = m_modules ??= CompiledModules.For(m_env);
        if (!mods.Available) return null;
        // `Environment.getModuleIdxFor?`: `none` for declarations of the current module. This is
        // also what `findInterpDecl` uses to locate the IR (it covers auxiliary IR declarations
        // through `ModuleData.extraConstNames`).
        lean_inc(m_env); lean_inc(fn);
        Obj idx = CompiledModules.GetModuleIdxFor(m_env, fn);
        if (lean_is_scalar(idx)) return null;
        ulong i = lean_unbox(lean_ctor_get(idx, 0));
        lean_dec(idx);
        return mods.ClassAt(i);
    }

    CompiledModules m_modules;

    /// <summary>Return cached lookup result for given unmangled function name in the compiled code.</summary>
    SymbolCacheEntry LookupSymbol(Obj fn)
    {
        if (m_symbolCache.TryGetValue(fn, out var e)) return e;
        Obj decl = GetDecl(fn);
        NativeEntry ne = NativeEntry.None;
        try
        {
            if (m_preferNative || Ir.DeclTag(decl) == Ir.DeclExtern || HasInitAttribute(fn))
            {
                Type cls = CompiledClassOf(fn);
                if (cls != null)
                {
                    // The cache is global, but whether (and where) a name has compiled code depends
                    // on the environment: validate the entry against the defining module's class.
                    bool cached;
                    lock (s_nativeLock) cached = s_nativeSymbolCache.TryGetValue(fn, out ne) && ne.Cls == cls;
                    if (!cached)
                    {
                        ne = FindNative(fn, decl, cls);
                        ne.Cls = cls;
                        lock (s_nativeLock)
                        {
                            if (!s_nativeSymbolCache.ContainsKey(fn)) lean_inc(fn);
                            s_nativeSymbolCache[fn] = ne;
                        }
                    }
                }
                else ne = NativeEntry.None;
            }
        }
        catch
        {
            lean_dec(decl);
            throw;
        }
        e = new SymbolCacheEntry { Decl = decl, Native = ne };
        lean_inc(fn);
        m_symbolCache[fn] = e;
        return e;
    }

    static Value ReadNativeConstant(NativeEntry ne)
    {
        Type ty;
        if (ne.Field != null)
        {
            object v = ne.Field.GetValue(null);
            ty = ne.Field.FieldType;
            if (ty == s_objType) return Value.Of((Obj)v);
            if (ty == typeof(double)) return Value.FromFloat((double)v);
            if (ty == typeof(float)) return Value.FromFloat32((float)v);
            if (ty == typeof(byte)) return Value.Num((byte)v);
            if (ty == typeof(ushort)) return Value.Num((ushort)v);
            if (ty == typeof(uint)) return Value.Num((uint)v);
            return Value.Num((ulong)v);
        }
        ty = ne.GetterType;
        void* f = ne.GetterFn;
        if (ty == s_objType) return Value.Of(((delegate*<Obj>)f)());
        if (ty == typeof(double)) return Value.FromFloat(((delegate*<double>)f)());
        if (ty == typeof(float)) return Value.FromFloat32(((delegate*<float>)f)());
        if (ty == typeof(byte)) return Value.Num(((delegate*<byte>)f)());
        if (ty == typeof(ushort)) return Value.Num(((delegate*<ushort>)f)());
        if (ty == typeof(uint)) return Value.Num(((delegate*<uint>)f)());
        return Value.Num(((delegate*<ulong>)f)());
    }

    /// <summary>Evaluate nullary function ("constant").</summary>
    Value Load(Obj fn, IrType t)
    {
        if (m_constantCache.TryGetValue(fn, out var cached)) return cached.Val;
        Obj g;
        bool found;
        var initGlobals = InitGlobals;
        lock (initGlobals) found = initGlobals.TryGetValue(fn, out g);
        if (found)
        {
            // persistent, so no `inc` needed
            return Ir.TypeIsScalar(t) ? UnboxT(g, t) : Value.Of(g);
        }
        var e = LookupSymbol(fn);
        if (e.Native.Found)
        {
            // we can assume that all compiled code has been initialized (see e.g. `evalConst`)
            return ReadNativeConstant(e.Native);
        }
        // no compiled code, so might be part of the current module
        lean_inc(m_env); lean_inc(fn);
        Obj init = InterpExports.GetRegularInitFnNameFor(m_env, fn);
        bool hasInit = !lean_is_scalar(init);
        lean_dec(init);
        if (hasInit)
        {
            // We don't know whether `[init]` decls can be re-executed, so let's not.
            throw new InterpreterException($"cannot evaluate `[init]` declaration '{IrName.ToString(fn)}' in the same module");
        }
        Obj body = Ir.DeclFunBody(e.Decl);
        // `Unreachable` can be from `mkDummyExternDecl`, which may mean that we failed to run the
        // initializer, suggesting some incorrect `meta` phase setup.
        if (Ir.BodyTag(body) == FnBodyKind.Unreachable)
        {
            // A `builtin_initialize x : T ← ...` constant of a module that has no compiled code.
            // Natively such a module is only usable compiled (its initializer runs the
            // `[builtin_init]` function); in LeanSharp user modules are always interpreted
            // (interpreter-backed executables, "precompiled" modules), so the initializer is
            // run on first use.
            lean_inc(m_env); lean_inc(fn);
            Obj anyInit = InterpExports.GetInitFnNameFor(m_env, fn);
            if (!lean_is_scalar(anyInit) && !lean_is_scalar(lean_ctor_get(anyInit, 0)))
            {
                Obj initDecl = lean_ctor_get(anyInit, 0);
                Obj res = RunInit(fn, initDecl);
                lean_dec(anyInit);
                bool ok = lean_io_result_is_ok(res);
                lean_dec(res);
                if (ok)
                {
                    lock (initGlobals) found = initGlobals.TryGetValue(fn, out g);
                    if (found) return Ir.TypeIsScalar(t) ? UnboxT(g, t) : Value.Of(g);
                }
                throw new InterpreterException($"(interpreter) failed to run the initializer of '{IrName.ToString(fn)}'");
            }
            lean_dec(anyInit);
            throw new InterpreterException($"(interpreter) cannot evaluate constant '{IrName.ToString(fn)}': its IR body is `unreachable` (missing initializer?)");
        }
        PushFrame(e.Decl, m_argSize);
        Value r = EvalBody(body);
        PopFrame();
        lean_inc(fn);
        m_constantCache[fn] = new ConstantCacheEntry { IsScalar = Ir.TypeIsScalar(t), Val = r };
        return r;
    }

    Value Call(Obj fn, Obj args)
    {
        int oldSize = m_argSize;
        Value r;
        var e = LookupSymbol(fn);
        if (e.Native.Fn != null)
        {
            Obj ps = Ir.DeclParams(e.Decl);
            int n = Ir.Size(args);
            var args2 = new Obj[n];
            for (int i = 0; i < n; i++)
            {
                Obj p = Ir.At(ps, i);
                IrType t = Ir.ParamType(p);
                args2[i] = BoxT(EvalArg(Ir.At(args, i)), t);
                if (e.Native.Boxed && Ir.ParamBorrow(p))
                {
                    // If we chose the boxed version where the IR chose the unboxed one, we need to
                    // manually increment originally borrowed parameters because the wrapper will
                    // decrement these after the call. (Borrowed parameters are never scalars.)
                    lean_inc(args2[i]);
                }
            }
            PushFrame(e.Decl, oldSize);
            Obj o = lean_curry(e.Native.Fn, n, args2);
            IrType rt = Ir.DeclType(e.Decl);
            if (Ir.TypeIsScalar(rt))
            {
                // NOTE: this unboxing does not exist in the IR, so we should manually consume `o`
                r = UnboxT(o, rt);
                lean_dec(o);
            }
            else r = Value.Of(o);
        }
        else
        {
            if (Ir.DeclTag(e.Decl) == Ir.DeclExtern)
            {
                string mangled = GetSymbolStem(fn);
                string boxedMangled = MkMangledBoxedName(mangled);
                throw new InterpreterException(
                    $"Could not find native implementation of external declaration '{IrName.ToString(fn)}' (symbols '{boxedMangled}' or '{mangled}').\n" +
                    "For declarations from `Init`, `Std`, or `Lean`, you need to set `supportInterpreter := true` " +
                    "in the relevant `lean_exe` statement in your `lakefile.lean`.");
            }
            // evaluate args in old stack frame
            int n = Ir.Size(args);
            for (int i = 0; i < n; i++) PushArg(EvalArg(Ir.At(args, i)));
            PushFrame(e.Decl, oldSize);
            r = EvalBody(Ir.DeclFunBody(e.Decl));
        }
        PopFrame();
        return r;
    }

    // ------------------------------------------------------------------
    // Closure stubs

    // closure stub: args = env, opts, decl, params... (all owned)
    Obj StubM(Obj[] args)
    {
        Obj d = args[2];
        int oldSize = m_argSize, oldJp = m_jpSize, oldFrames = m_frameCount;
        try
        {
            int n = Ir.Size(Ir.DeclParams(d));
            for (int i = 0; i < n; i++) PushArg(Value.Of(args[3 + i]));
            PushFrame(d, oldSize);
            Obj r = EvalBody(Ir.DeclFunBody(d)).O;
            PopFrame();
            return r;
        }
        catch
        {
            RestoreStacks(oldSize, oldJp, oldFrames);
            throw;
        }
        finally
        {
            lean_dec(d);
        }
    }

    // static closure stub
    static Obj StubMAux(Obj[] args)
    {
        Obj env = args[0], opts = args[1];
        try
        {
            var cur = t_interpreter;
            if (cur != null && ReferenceEquals(cur.m_env, env) && ReferenceEquals(cur.m_opts, opts))
                return cur.StubM(args);
            var interp = new IrInterpreter(env, opts);
            t_interpreter = interp;
            try { return interp.StubM(args); }
            finally
            {
                t_interpreter = cur;
                interp.Dispose();
            }
        }
        finally
        {
            lean_dec(env);
            lean_dec(opts);
        }
    }

    static Obj Stub1(Obj x_1) => StubMAux(new Obj[] { x_1 });
    static Obj Stub2(Obj x_1, Obj x_2) => StubMAux(new Obj[] { x_1, x_2 });
    static Obj Stub3(Obj x_1, Obj x_2, Obj x_3) => StubMAux(new Obj[] { x_1, x_2, x_3 });
    static Obj Stub4(Obj x_1, Obj x_2, Obj x_3, Obj x_4) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4 });
    static Obj Stub5(Obj x_1, Obj x_2, Obj x_3, Obj x_4, Obj x_5) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4, x_5 });
    static Obj Stub6(Obj x_1, Obj x_2, Obj x_3, Obj x_4, Obj x_5, Obj x_6) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4, x_5, x_6 });
    static Obj Stub7(Obj x_1, Obj x_2, Obj x_3, Obj x_4, Obj x_5, Obj x_6, Obj x_7) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4, x_5, x_6, x_7 });
    static Obj Stub8(Obj x_1, Obj x_2, Obj x_3, Obj x_4, Obj x_5, Obj x_6, Obj x_7, Obj x_8) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4, x_5, x_6, x_7, x_8 });
    static Obj Stub9(Obj x_1, Obj x_2, Obj x_3, Obj x_4, Obj x_5, Obj x_6, Obj x_7, Obj x_8, Obj x_9) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4, x_5, x_6, x_7, x_8, x_9 });
    static Obj Stub10(Obj x_1, Obj x_2, Obj x_3, Obj x_4, Obj x_5, Obj x_6, Obj x_7, Obj x_8, Obj x_9, Obj x_10) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4, x_5, x_6, x_7, x_8, x_9, x_10 });
    static Obj Stub11(Obj x_1, Obj x_2, Obj x_3, Obj x_4, Obj x_5, Obj x_6, Obj x_7, Obj x_8, Obj x_9, Obj x_10, Obj x_11) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4, x_5, x_6, x_7, x_8, x_9, x_10, x_11 });
    static Obj Stub12(Obj x_1, Obj x_2, Obj x_3, Obj x_4, Obj x_5, Obj x_6, Obj x_7, Obj x_8, Obj x_9, Obj x_10, Obj x_11, Obj x_12) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4, x_5, x_6, x_7, x_8, x_9, x_10, x_11, x_12 });
    static Obj Stub13(Obj x_1, Obj x_2, Obj x_3, Obj x_4, Obj x_5, Obj x_6, Obj x_7, Obj x_8, Obj x_9, Obj x_10, Obj x_11, Obj x_12, Obj x_13) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4, x_5, x_6, x_7, x_8, x_9, x_10, x_11, x_12, x_13 });
    static Obj Stub14(Obj x_1, Obj x_2, Obj x_3, Obj x_4, Obj x_5, Obj x_6, Obj x_7, Obj x_8, Obj x_9, Obj x_10, Obj x_11, Obj x_12, Obj x_13, Obj x_14) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4, x_5, x_6, x_7, x_8, x_9, x_10, x_11, x_12, x_13, x_14 });
    static Obj Stub15(Obj x_1, Obj x_2, Obj x_3, Obj x_4, Obj x_5, Obj x_6, Obj x_7, Obj x_8, Obj x_9, Obj x_10, Obj x_11, Obj x_12, Obj x_13, Obj x_14, Obj x_15) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4, x_5, x_6, x_7, x_8, x_9, x_10, x_11, x_12, x_13, x_14, x_15 });
    static Obj Stub16(Obj x_1, Obj x_2, Obj x_3, Obj x_4, Obj x_5, Obj x_6, Obj x_7, Obj x_8, Obj x_9, Obj x_10, Obj x_11, Obj x_12, Obj x_13, Obj x_14, Obj x_15, Obj x_16) => StubMAux(new Obj[] { x_1, x_2, x_3, x_4, x_5, x_6, x_7, x_8, x_9, x_10, x_11, x_12, x_13, x_14, x_15, x_16 });

    static void* GetStub(int n) => n switch
    {
        1 => (delegate*<Obj, Obj>)&Stub1,
        2 => (delegate*<Obj, Obj, Obj>)&Stub2,
        3 => (delegate*<Obj, Obj, Obj, Obj>)&Stub3,
        4 => (delegate*<Obj, Obj, Obj, Obj, Obj>)&Stub4,
        5 => (delegate*<Obj, Obj, Obj, Obj, Obj, Obj>)&Stub5,
        6 => (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj>)&Stub6,
        7 => (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj>)&Stub7,
        8 => (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj>)&Stub8,
        9 => (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj>)&Stub9,
        10 => (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj>)&Stub10,
        11 => (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj>)&Stub11,
        12 => (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj>)&Stub12,
        13 => (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj>)&Stub13,
        14 => (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj>)&Stub14,
        15 => (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj>)&Stub15,
        16 => (delegate*<Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj, Obj>)&Stub16,
        _ => (delegate*<Obj[], Obj>)&StubMAux,
    };

    // ------------------------------------------------------------------
    // Entry points

    /// <summary>A variant of `Call` designed for external uses:
    /// takes (owned) objects instead of IR args, supports under- and over-application, and
    /// supports "calling" (evaluating) nullary constants.</summary>
    public Obj CallBoxed(Obj fn, int n, Obj[] args)
    {
        int oldSize = m_argSize, oldJp = m_jpSize, oldFrames = m_frameCount;
        try
        {
            var e = LookupSymbol(fn);
            int arity = Ir.Size(Ir.DeclParams(e.Decl));
            Obj r;
            if (arity == 0)
            {
                IrType t = Ir.DeclType(e.Decl);
                r = BoxT(Load(fn, t), t);
                if (!Ir.TypeIsScalar(t)) lean_inc(r);
            }
            else
            {
                // First allocate a closure with zero fixed parameters. This is slightly wasteful in
                // the under-application case, but simpler to handle.
                if (e.Native.Fn != null)
                {
                    // `LookupSymbol` always prefers the boxed version for compiled functions
                    r = lean_alloc_closure(e.Native.Fn, (uint)arity, 0);
                }
                else
                {
                    // `LookupSymbol` does not prefer the boxed version for interpreted functions, so check manually.
                    lean_inc(m_env); lean_inc(fn);
                    Obj dBoxed = InterpExports.TakeOption(InterpExports.FindEnvDeclBoxed(m_env, fn));
                    r = MkStubClosure(dBoxed ?? e.Decl, 0, null);
                    if (dBoxed != null) lean_dec(dBoxed);
                }
            }
            if (n > 0) r = lean_apply_n(r, (uint)n, args);
            return r;
        }
        catch
        {
            RestoreStacks(oldSize, oldJp, oldFrames);
            throw;
        }
    }

    public uint RunMain(Obj args)
    {
        Obj mainName = IrName.Mk("main");
        Obj d = GetDecl(mainName);
        int nparams = Ir.Size(Ir.DeclParams(d));
        lean_dec(d);
        var callArgs = new List<Obj>();
        if (nparams == 2)
        {
            // List String -> IO _
            lean_inc(args);
            callArgs.Add(args);
        }
        else if (nparams != 1)
            throw new InterpreterException("(interpreter) unexpected signature of 'main'");
        callArgs.Add(lean_io_mk_world());
        Obj w = CallBoxed(mainName, callArgs.Count, callArgs.ToArray());
        if (lean_io_result_is_ok(w))
        {
            uint ret = 0;
            // `IO UInt32` or `IO (P)Unit`
            if (MainReturnsUInt32(mainName))
                ret = (uint)lean_unbox(lean_io_result_get_value(w));
            lean_dec_ref(w);
            lean_dec(mainName);
            return ret;
        }
        InterpExports.IoResultShowError(w);
        lean_dec_ref(w);
        lean_dec(mainName);
        return 1;
    }

    /// <summary>`m_env.get("main").get_type()` is `[_ →] IO UInt32`?</summary>
    bool MainReturnsUInt32(Obj mainName)
    {
        lean_inc(m_env);
        Obj kenv = InterpExports.ToKernelEnv(m_env);
        lean_inc(mainName);
        Obj ci = InterpExports.TakeOption(InterpExports.EnvironmentFind(kenv, mainName));
        if (ci == null) return false;
        try
        {
            // ConstantInfo.<kind>Info val; val.toConstantVal.type
            Obj ty = Ir.F(Ir.F(Ir.F(ci, 0), 0), 2);
            const uint ExprConst = 4, ExprApp = 5, ExprForall = 7;
            if (lean_obj_tag(ty) == ExprForall) ty = Ir.F(ty, 2); // binding_body
            if (lean_obj_tag(ty) != ExprApp) return false;
            Obj arg = Ir.F(ty, 1); // app_arg
            return lean_obj_tag(arg) == ExprConst && IrName.IsAtomic(Ir.F(arg, 0), "UInt32");
        }
        finally { lean_dec(ci); }
    }

    public Obj RunInit(Obj decl, Obj initDecl)
    {
        try
        {
            Obj r = CallBoxed(initDecl, 1, new Obj[] { lean_io_mk_world() });
            if (lean_io_result_is_ok(r))
            {
                Obj o = lean_io_result_get_value(r);
                lean_mark_persistent(o);
                lean_dec_ref(r);
                var e = LookupSymbol(decl);
                if (e.Native.Field != null && e.Native.Field.FieldType == s_objType && !e.Native.Field.IsInitOnly)
                    e.Native.Field.SetValue(null, o);
                else
                {
                    var initGlobals = InitGlobals;
                    lock (initGlobals)
                    {
                        if (!initGlobals.ContainsKey(decl)) lean_inc(decl);
                        initGlobals[decl] = o;
                    }
                }
                return lean_io_result_mk_ok(lean_box(0));
            }
            return r;
        }
        catch (InterpreterException ex)
        {
            return InterpExports.IoResultMkError(ex.Message);
        }
    }

    /// <summary>Run `fn` using the "boxed" ABI, i.e. with all-owned parameters (C++ `run_boxed`).</summary>
    public static Obj RunBoxed(Obj env, Obj opts, Obj fn, int n, Obj[] args)
    {
        lean_inc(env); lean_inc(fn);
        Obj declWithSorry = InterpExports.TakeOption(InterpExports.DeclGetSorryDep(env, fn));
        if (declWithSorry != null)
        {
            string s = IrName.ToString(declWithSorry);
            lean_dec(declWithSorry);
            throw new InterpreterException($"cannot evaluate code because '{s}' uses 'sorry' and/or contains errors");
        }
        return With(env, opts, interp => interp.CallBoxed(fn, n, args));
    }
}

/// <summary>Maps the module indices of an environment to the classes of the compiled modules.
///
/// There is no `@[export]` for `Environment.getModuleIdxFor?` / `Environment.allImportedModuleNames`
/// and their data lives in private structure fields behind a `Std.HashMap`, so instead of relying
/// on object layouts we call the compiled Lean functions themselves, found by C symbol in the class
/// of `Lean.Environment` (their `___boxed` versions take owned `Obj` arguments):
/// `l_Lean_Environment_getModuleIdxFor_x3f___boxed (env, name) : Option ModuleIdx`,
/// `l_Lean_Environment_header___boxed (env) : EnvironmentHeader` and
/// `l_Lean_Environment_allImportedModuleNames___boxed (env) : Array Name`.</summary>
internal sealed unsafe class CompiledModules
{
    static readonly CompiledModules s_unavailable = new();
    static readonly object s_lock = new();
    static bool s_resolved;
    static delegate*<Obj, Obj, Obj> s_getModuleIdxFor;
    static delegate*<Obj, Obj> s_header, s_moduleNames;
    static CompiledModules s_last;

    WeakReference<Obj> m_header;
    Obj m_names;          // Array Name, indexed by ModuleIdx
    Type[] m_classes;
    bool[] m_known;

    public bool Available => m_names != null;

    public static void Reset()
    {
        lock (s_lock) { s_resolved = false; s_getModuleIdxFor = null; s_header = null; s_moduleNames = null; s_last = null; }
    }

    static void* Helper(Type cls, string symbol, int arity)
    {
        var m = LeanCompiledCode.Find(symbol, cls);
        var mi = m?.Method;
        if (m == null || m.Kind != CompiledMemberKind.Method || mi.ReturnType != typeof(Obj) ||
            mi.GetParameters().Length != arity || mi.GetParameters().Any(p => p.ParameterType != typeof(Obj)))
            throw new InvalidOperationException($"LeanSharp: compiled function '{symbol}' not found in {cls.FullName} (needed by the interpreter to locate compiled declarations)");
        return (void*)mi.MethodHandle.GetFunctionPointer();
    }

    static bool Resolve()
    {
        if (s_resolved) return s_getModuleIdxFor != null;
        lock (s_lock)
        {
            if (s_resolved) return s_getModuleIdxFor != null;
            Type cls = LeanCompiledCode.ModuleClassResolver?.Invoke("Lean.Environment");
            if (cls != null)
            {
                s_header = (delegate*<Obj, Obj>)Helper(cls, "l_Lean_Environment_header___boxed", 1);
                s_moduleNames = (delegate*<Obj, Obj>)Helper(cls, "l_Lean_Environment_allImportedModuleNames___boxed", 1);
                s_getModuleIdxFor = (delegate*<Obj, Obj, Obj>)Helper(cls, "l_Lean_Environment_getModuleIdxFor_x3f___boxed", 2);
            }
            s_resolved = true;
            return s_getModuleIdxFor != null;
        }
    }

    /// <summary>`env.getModuleIdxFor? n` (owned arguments and result).</summary>
    public static Obj GetModuleIdxFor(Obj env, Obj n) => s_getModuleIdxFor(env, n);

    /// <summary>Module table of `env` (borrowed). Environments of the same import share their
    /// header object, so the last table is reused while the header is the same.</summary>
    public static CompiledModules For(Obj env)
    {
        if (!Resolve()) return s_unavailable;
        lean_inc(env);
        Obj header = s_header(env);
        var last = Volatile.Read(ref s_last);
        if (last == null || !last.m_header.TryGetTarget(out var h) || !ReferenceEquals(h, header))
        {
            lean_inc(env);
            Obj names = s_moduleNames(env);
            int n = (int)lean_array_size(names);
            last = new CompiledModules
            {
                m_header = new WeakReference<Obj>(header),
                m_names = names,
                m_classes = new Type[n],
                m_known = new bool[n],
            };
            Volatile.Write(ref s_last, last);
        }
        lean_dec(header);
        return last;
    }

    /// <summary>Class of the compiled module with index `i`, or null if that module is not compiled.</summary>
    public Type ClassAt(ulong i)
    {
        if (i >= (ulong)m_classes.Length) return null;
        if (!Volatile.Read(ref m_known[i]))
        {
            m_classes[i] = LeanCompiledCode.ModuleClassResolver?.Invoke(IrName.ToString(lean_array_get_core(m_names, i)));
            Volatile.Write(ref m_known[i], true);
        }
        return m_classes[i];
    }
}

