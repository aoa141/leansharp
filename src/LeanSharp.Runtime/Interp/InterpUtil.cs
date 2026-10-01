// Helpers for the IR interpreter (port of library/ir_interpreter.cpp): Lean `Name` objects,
// boxing of scalars, and wrappers around the Lean functions exported with `@[export]` that the
// C++ interpreter calls (`lean_ir_find_env_decl`, `lean_get_symbol_stem`, ...).

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using static LeanSharp.Runtime.LeanRt;

namespace LeanSharp.Runtime.Interp;

/// <summary>Errors raised by the interpreter (C++ `lean::exception`). Converted to `Except.error` /
/// IO errors by `lean_eval_const` and `lean_run_init`.</summary>
public class InterpreterException : Exception
{
    public InterpreterException(string msg) : base(msg) { }
}

/// <summary>Stack overflow in interpreted code (C++ `throwable` from `check_system`). Like in C++,
/// it is *not* caught by `lean_eval_const`/`lean_run_init`.</summary>
public sealed class InterpreterStackOverflowException : Exception
{
    public InterpreterStackOverflowException(string msg) : base(msg) { }
}

/// <summary>Lean `Name` objects (`Name.anonymous` = box(0), `.str p s` = ctor 1, `.num p n` = ctor 2,
/// both with the computed `hash : UInt64` field in the scalar area).</summary>
internal static class IrName
{
    public const ulong AnonymousHash = 1723;

    public static ulong Hash(Obj n) => lean_is_scalar(n) ? AnonymousHash : lean_ctor_get_uint64_s(n, 0);

    /// <summary>`Name.str p s` (takes ownership of `p` and `s`).</summary>
    public static Obj MkStr(Obj p, Obj s)
    {
        var r = lean_alloc_ctor(1, 2, 8);
        lean_ctor_set(r, 0, p);
        lean_ctor_set(r, 1, s);
        lean_ctor_set_uint64_s(r, 0, LeanHash.Mix(Hash(p), LeanHash.HashStr(lean_string_span(s), 11)));
        return r;
    }

    /// <summary>`Name.num p v` (takes ownership of `p`).</summary>
    public static Obj MkNum(Obj p, ulong v)
    {
        var r = lean_alloc_ctor(2, 2, 8);
        lean_ctor_set(r, 0, p);
        lean_ctor_set(r, 1, lean_usize_to_nat(v));
        lean_ctor_set_uint64_s(r, 0, LeanHash.Mix(Hash(p), v));
        return r;
    }

    /// <summary>Build a hierarchical name from dot-separated string components, e.g. "interpreter.prefer_native".</summary>
    public static Obj Mk(params string[] components)
    {
        Obj r = lean_box(0);
        foreach (var c in components) r = MkStr(r, lean_mk_string(c));
        return r;
    }

    public static Obj Mk(string dotted) => Mk(dotted.Split('.'));

    /// <summary>Structural equality (`Name.beq`).</summary>
    public static bool Eq(Obj a, Obj b)
    {
        while (true)
        {
            if (ReferenceEquals(a, b)) return true;
            bool sa = lean_is_scalar(a), sb = lean_is_scalar(b);
            if (sa || sb) return false; // anonymous is the only scalar name
            if (a.m_tag != b.m_tag) return false;
            if (lean_ctor_get_uint64_s(a, 0) != lean_ctor_get_uint64_s(b, 0)) return false;
            Obj fa = lean_ctor_get(a, 1), fb = lean_ctor_get(b, 1);
            if (a.m_tag == 1)
            {
                if (!lean_string_span(fa).SequenceEqual(lean_string_span(fb))) return false;
            }
            else
            {
                if (lean_nat_to_big(fa) != lean_nat_to_big(fb)) return false;
            }
            a = lean_ctor_get(a, 0);
            b = lean_ctor_get(b, 0);
        }
    }

    /// <summary>Is `n` the name `.str .anonymous s`?</summary>
    public static bool IsAtomic(Obj n, string s)
    {
        if (lean_is_scalar(n) || n.m_tag != 1) return false;
        if (!lean_is_scalar(lean_ctor_get(n, 0))) return false;
        return lean_string_to_net(lean_ctor_get(n, 1)) == s;
    }

    /// <summary>Last string component (C++ `name::get_string`).</summary>
    public static string GetString(Obj n) =>
        !lean_is_scalar(n) && n.m_tag == 1 ? lean_string_to_net(lean_ctor_get(n, 1)) : ToString(n);

    public static string ToString(Obj n)
    {
        if (n == null) return "<null>";
        if (lean_is_scalar(n)) return "[anonymous]";
        var parts = new List<string>();
        while (!lean_is_scalar(n))
        {
            Obj f = lean_ctor_get(n, 1);
            parts.Add(n.m_tag == 1 ? lean_string_to_net(f) : lean_nat_to_big(f).ToString());
            n = lean_ctor_get(n, 0);
        }
        parts.Reverse();
        return string.Join(".", parts);
    }

    public static string[] Components(Obj n)
    {
        var parts = new List<string>();
        while (!lean_is_scalar(n))
        {
            Obj f = lean_ctor_get(n, 1);
            parts.Add(n.m_tag == 1 ? lean_string_to_net(f) : lean_nat_to_big(f).ToString());
            n = lean_ctor_get(n, 0);
        }
        parts.Reverse();
        return parts.ToArray();
    }
}

/// <summary>Dictionary comparer for `Name` objects (structural).</summary>
internal sealed class NameComparer : IEqualityComparer<Obj>
{
    public static readonly NameComparer Instance = new();
    public bool Equals(Obj a, Obj b) => IrName.Eq(a, b);
    public int GetHashCode(Obj n) { ulong h = IrName.Hash(n); return (int)(h ^ (h >> 32)); }
}

/// <summary>Port of `Lean.Name.mangle` (Lean/Compiler/NameMangling.lean), used to compute C symbol
/// names of module initializers.</summary>
public static class LeanNameMangling
{
    static bool IsAlpha(int c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    static bool IsDigit(int c) => c >= '0' && c <= '9';

    static void PushHex(StringBuilder sb, int n, uint val)
    {
        for (int k = n; k > 0; k--)
        {
            uint i = (val >> (4 * (k - 1))) & 15;
            sb.Append((char)(i < 10 ? i + 48 : i + 87));
        }
    }

    /// <summary>`String.Internal.mangle`.</summary>
    public static string MangleString(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            int c = char.ConvertToUtf32(s, i);
            if (c > 0xFFFF) i++;
            if (IsAlpha(c) || IsDigit(c)) sb.Append((char)c);
            else if (c == '_') sb.Append("__");
            else if (c < 0x100) { sb.Append("_x"); PushHex(sb, 2, (uint)c); }
            else if (c < 0x10000) { sb.Append("_u"); PushHex(sb, 4, (uint)c); }
            else { sb.Append("_U"); PushHex(sb, 8, (uint)c); }
        }
        return sb.ToString();
    }

    static bool CheckLowerHex(int k, string s, int p)
    {
        for (; k > 0; k--, p++)
        {
            if (p >= s.Length) return false;
            char ch = s[p];
            if (!(IsDigit(ch) || (ch >= 'a' && ch <= 'f'))) return false;
        }
        return true;
    }

    static bool CheckDisambiguation(string s, int p)
    {
        while (p < s.Length)
        {
            char b = s[p];
            if (b == '_') { p++; continue; }
            if (b == 'x') return CheckLowerHex(2, s, p + 1);
            if (b == 'u') return CheckLowerHex(4, s, p + 1);
            if (b == 'U') return CheckLowerHex(8, s, p + 1);
            return IsDigit(b);
        }
        return true;
    }

    /// <summary>A name component: a string or a number.</summary>
    public readonly struct Component
    {
        public readonly string Str; public readonly BigInteger Num; public readonly bool IsNum;
        public Component(string s) { Str = s; Num = default; IsNum = false; }
        public Component(BigInteger n) { Str = null; Num = n; IsNum = true; }
    }

    /// <summary>`Name.mangleAux` over a list of components (outermost first).</summary>
    public static string MangleAux(IReadOnlyList<Component> cs)
    {
        string r = "";
        for (int i = 0; i < cs.Count; i++)
        {
            var c = cs[i];
            if (c.IsNum)
                r = i == 0 ? c.Num.ToString() + "_" : r + "_" + c.Num.ToString() + "_";
            else
            {
                string m = MangleString(c.Str);
                if (i == 0) r = CheckDisambiguation(m, 0) ? "00" + m : m;
                else
                {
                    var prev = cs[i - 1];
                    bool need = (!prev.IsNum && prev.Str.Length > 0 && prev.Str[^1] == '_') || CheckDisambiguation(m, 0);
                    r = r + (need ? "_00" : "_") + m;
                }
            }
        }
        return r;
    }

    public static string Mangle(IReadOnlyList<Component> cs, string pre = "l_") => pre + MangleAux(cs);

    /// <summary>`Name.mangle` of a `Name` object.</summary>
    public static string Mangle(Obj name, string pre = "l_")
    {
        var cs = new List<Component>();
        Obj n = name;
        while (!lean_is_scalar(n))
        {
            Obj f = lean_ctor_get(n, 1);
            cs.Add(n.m_tag == 1 ? new Component(lean_string_to_net(f)) : new Component(lean_nat_to_big(f)));
            n = lean_ctor_get(n, 0);
        }
        cs.Reverse();
        return Mangle(cs, pre);
    }

    /// <summary>Components of a module name as printed by `Name.toString` (handles `«»` escapes).</summary>
    public static List<Component> ParseModuleName(string s)
    {
        var cs = new List<Component>();
        var cur = new StringBuilder();
        bool inEsc = false;
        foreach (char ch in s)
        {
            if (inEsc) { if (ch == '»') inEsc = false; else cur.Append(ch); }
            else if (ch == '«') inEsc = true;
            else if (ch == '.') { cs.Add(new Component(cur.ToString())); cur.Clear(); }
            else cur.Append(ch);
        }
        cs.Add(new Component(cur.ToString()));
        return cs;
    }

    /// <summary>`mkModuleInitializationStem` without package (the part after `initialize_`).</summary>
    public static string ModuleInitStem(string moduleName) => MangleAux(ParseModuleName(moduleName));

    /// <summary>`mkMangledBoxedName`.</summary>
    public static string MkMangledBoxedName(string s) => s.EndsWith("__", StringComparison.Ordinal) ? s + "_00__boxed" : s + "___boxed";

    /// <summary>`EmitCSharp.shortName`: .NET metadata names are limited, so very long C symbols
    /// are shortened deterministically by the C# emitter.</summary>
    public static string ShortName(string c)
    {
        if (c.Length <= 480) return c;
        // `String.hash` = `hash_str(size, str, 11)`; `Nat.toDigits 16` = lowercase hex without padding
        ulong h = LeanHash.HashStr(Encoding.UTF8.GetBytes(c), 11);
        return c.Substring(0, 400) + "_H" + h.ToString("x");
    }
}

/// <summary>Scalar boxing as in lean.h (private copy: the Numbers area may not be available).</summary>
internal static class InterpNum
{
    public static Obj BoxUInt64(ulong v) { var r = lean_alloc_ctor(0, 0, 8); lean_ctor_set_uint64_s(r, 0, v); return r; }
    public static Obj BoxFloat(double v) { var r = lean_alloc_ctor(0, 0, 8); lean_ctor_set_float_s(r, 0, v); return r; }
    public static Obj BoxFloat32(float v) { var r = lean_alloc_ctor(0, 0, 4); lean_ctor_set_float32_s(r, 0, v); return r; }
    public static ulong UnboxUInt64(Obj o) => lean_ctor_get_uint64_s(o, 0);
    public static double UnboxFloat(Obj o) => lean_ctor_get_float_s(o, 0);
    public static float UnboxFloat32(Obj o) => lean_ctor_get_float32_s(o, 0);

    /// <summary>`lean_usize_of_nat` (wraps modulo 2^64 for big numbers).</summary>
    public static ulong USizeOfNat(Obj n) =>
        lean_is_scalar(n) ? lean_unbox(n) : (ulong)(lean_nat_to_big(n) & ulong.MaxValue);

    public static ulong UInt64OfNat(Obj n) => USizeOfNat(n);

    /// <summary>`lean_float_of_nat` (borrowed here; the caller handles RC).</summary>
    public static double FloatOfNat(Obj n) => lean_is_scalar(n) ? (double)lean_unbox(n) : (double)lean_nat_to_big(n);
    public static float Float32OfNat(Obj n) => lean_is_scalar(n) ? (float)lean_unbox(n) : (float)lean_nat_to_big(n);
}

/// <summary>Lean functions (`@[export]`) used by the interpreter. All arguments are owned, like in
/// the Lean definitions; results are owned.</summary>
internal static unsafe class InterpExports
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void* P(ref void* cell, string name)
    {
        var p = cell;
        if (p == null) { p = (void*)LeanExports.Get(name); cell = p; }
        return p;
    }

    static void* c_find, c_findBoxed, c_stem, c_initFn, c_regInitFn, c_exportName, c_sorry, c_envFind,
        c_boxedName, c_userError, c_errToString, c_toKernel, c_optBool;

    public static Obj FindEnvDecl(Obj env, Obj n) => ((delegate*<Obj, Obj, Obj>)P(ref c_find, "lean_ir_find_env_decl"))(env, n);
    public static Obj FindEnvDeclBoxed(Obj env, Obj n) => ((delegate*<Obj, Obj, Obj>)P(ref c_findBoxed, "lean_ir_find_env_decl_boxed"))(env, n);
    public static Obj GetSymbolStem(Obj env, Obj n) => ((delegate*<Obj, Obj, Obj>)P(ref c_stem, "lean_get_symbol_stem"))(env, n);
    public static Obj MkMangledBoxedName(Obj s) => ((delegate*<Obj, Obj>)P(ref c_boxedName, "lean_mk_mangled_boxed_name"))(s);
    public static Obj GetInitFnNameFor(Obj env, Obj n) => ((delegate*<Obj, Obj, Obj>)P(ref c_initFn, "lean_get_init_fn_name_for"))(env, n);
    public static Obj GetRegularInitFnNameFor(Obj env, Obj n) => ((delegate*<Obj, Obj, Obj>)P(ref c_regInitFn, "lean_get_regular_init_fn_name_for"))(env, n);
    public static Obj GetExportNameFor(Obj env, Obj n) => ((delegate*<Obj, Obj, Obj>)P(ref c_exportName, "lean_get_export_name_for"))(env, n);
    public static Obj DeclGetSorryDep(Obj env, Obj n) => ((delegate*<Obj, Obj, Obj>)P(ref c_sorry, "lean_decl_get_sorry_dep"))(env, n);
    public static byte OptionsGetBool(Obj opts, Obj n, byte def) => ((delegate*<Obj, Obj, byte, byte>)P(ref c_optBool, "lean_options_get_bool"))(opts, n, def);
    public static Obj MkIoUserError(Obj s) => ((delegate*<Obj, Obj>)P(ref c_userError, "lean_mk_io_user_error"))(s);
    public static Obj IoErrorToString(Obj e) => ((delegate*<Obj, Obj>)P(ref c_errToString, "lean_io_error_to_string"))(e);
    public static Obj ToKernelEnv(Obj env) => ((delegate*<Obj, Obj>)P(ref c_toKernel, "lean_elab_environment_to_kernel_env"))(env);
    public static Obj EnvironmentFind(Obj kenv, Obj n) => ((delegate*<Obj, Obj, Obj>)P(ref c_envFind, "lean_environment_find"))(kenv, n);

    // ------------------------------------------------------------------
    // Convenience wrappers with borrowed arguments (they `inc` like C++ `to_obj_arg()`)

    /// <summary>Some value of an owned `Option` (inc'd), or null for `none`. Consumes `opt`.</summary>
    public static Obj TakeOption(Obj opt)
    {
        if (lean_is_scalar(opt)) return null;
        Obj v = lean_ctor_get(opt, 0);
        lean_inc(v);
        lean_dec(opt);
        return v;
    }

    public static string TakeString(Obj s)
    {
        string r = lean_string_to_net(s);
        lean_dec(s);
        return r;
    }

    /// <summary>`io_result_mk_error(char const*)`.</summary>
    public static Obj IoResultMkError(string msg) => lean_io_result_mk_error(MkIoUserError(lean_mk_string(msg)));

    /// <summary>`lean_io_result_show_error` (borrowed).</summary>
    public static void IoResultShowError(Obj r)
    {
        Obj err = lean_io_result_get_error(r);
        lean_inc(err);
        string s = TakeString(IoErrorToString(err));
        // (the stderr of the current logical process: in-process children have their own)
        LeanStdStreams.WriteProcessStderr("uncaught exception: " + s + "\n");
    }
}
