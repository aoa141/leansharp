// Check program for the numbers area.
//  1. SigCheck: every extern of docs/externs-numbers.txt exists with the exact C# signature.
//  2. Generated tests (gen_tests.py): differential tests against the real Lean toolchain
//     (expected.txt was produced by `lean --run ref_tests.lean`).
//  3. Hand-written tests: reference counting, representation invariants, FloatArray, boxing, panics.

using System.Numerics;
using System.Runtime.CompilerServices;
using LeanSharp.Runtime;
using static LeanSharp.Runtime.LeanRt;

static unsafe partial class Tests
{
    static readonly List<string> s_out = new();
    static int s_fail;
    static int s_pass;

    static void Main()
    {
        Console.WriteLine($"signatures ok: {SigCheck.Run()}");

        RunGenerated();
        CompareWithExpected();

        HandTests();

        Console.WriteLine($"hand tests: {s_pass} passed, {s_fail} failed");
        if (s_fail > 0) Environment.Exit(1);
    }

    // ------------------------------------------------------------------ generated-test helpers

    static Obj N(string s) => lean_cstr_to_nat(s);
    static Obj I(string s) => lean_cstr_to_int(s);
    static double D(ulong b) => BitConverter.UInt64BitsToDouble(b);
    static float F(uint b) => BitConverter.UInt32BitsToSingle(b);

    static void T(string label, string v) => s_out.Add(label + " " + v);

    static string FN(Obj o)
    {
        BigInteger v = lean_nat_to_big(o);
        if (lean_is_scalar(o) != (v <= LEAN_MAX_SMALL_NAT)) Fail($"Nat invariant violated for {v}");
        return v.ToString();
    }

    static string FI(Obj o)
    {
        BigInteger v;
        if (lean_is_scalar(o))
        {
            if (lean_unbox(o) > uint.MaxValue) Fail($"Int box payload out of range: {lean_unbox(o)}");
            v = lean_scalar_to_int(o);
        }
        else
        {
            v = Unsafe.As<MpzObj>(o).m_value;
            if (v >= int.MinValue && v <= int.MaxValue) Fail($"Int invariant violated for {v}");
        }
        return v.ToString();
    }

    static string FB(byte b)
    {
        if (b > 1) Fail($"Bool result {b}");
        return b != 0 ? "true" : "false";
    }

    static string FU(byte v) => v.ToString();
    static string FU(ushort v) => v.ToString();
    static string FU(uint v) => v.ToString();
    static string FU(ulong v) => v.ToString();
    static string FS8(byte v) => unchecked((sbyte)v).ToString();
    static string FS16(ushort v) => unchecked((short)v).ToString();
    static string FS32(uint v) => unchecked((int)v).ToString();
    static string FS64(ulong v) => unchecked((long)v).ToString();

    static string FF(double x) => lean_string_to_net(lean_float_to_string(x)) + " " + lean_float_to_bits(x);
    static string FF32(float x) => lean_string_to_net(lean_float32_to_string(x)) + " " + lean_float32_to_bits(x);

    static string FFrexp(Obj p) => FF(lean_unbox_float(lean_ctor_get(p, 0))) + " " + FI(lean_ctor_get(p, 1));
    static string FFrexp32(Obj p) => FF32(lean_unbox_float32(lean_ctor_get(p, 0))) + " " + FI(lean_ctor_get(p, 1));

    /// <summary>Lines `float.<fn>.<n> <text> <bits>` (or `f32.`) whose bit patterns differ by at most two.</summary>
    static bool DiffersInLastBit(string expected, string actual)
    {
        if (!expected.StartsWith("float.") && !expected.StartsWith("f32.")) return false;
        var e = expected.Split(' ');
        var a = actual.Split(' ');
        if (e.Length != 3 || a.Length != 3 || e[0] != a[0]) return false;
        if (!ulong.TryParse(e[2], out var eb) || !ulong.TryParse(a[2], out var ab)) return false;
        return (eb > ab ? eb - ab : ab - eb) <= 2;
    }

    static void CompareWithExpected()
    {
        // The transcendental `Float` functions come from the platform's C library and differ in
        // the last digit between macOS (expected.txt) and Linux/glibc (expected.linux.txt); both
        // files were produced by native Lean on that platform.
        // On Windows there is no such reference: .NET uses the Universal C Runtime while native
        // Lean links mingw-w64's math library, and each differs from glibc in the last bit of
        // some results (and from each other). There the Linux file is used, and a difference of
        // up to two units in the last place of a floating point result is accepted.
        bool windows = OperatingSystem.IsWindows();
        string path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsLinux() || windows ? "expected.linux.txt" : "expected.txt");
        var expected = File.ReadAllLines(path);
        int mismatches = 0, lastBit = 0;
        if (expected.Length != s_out.Count)
            Console.WriteLine($"line count differs: expected {expected.Length}, got {s_out.Count}");
        int n = Math.Min(expected.Length, s_out.Count);
        for (int i = 0; i < n; i++)
        {
            if (expected[i] != s_out[i])
            {
                if (windows && DiffersInLastBit(expected[i], s_out[i])) { lastBit++; continue; }
                if (mismatches < 60) Console.WriteLine($"MISMATCH\n  expected: {expected[i]}\n  actual:   {s_out[i]}");
                mismatches++;
            }
        }
        if (lastBit > 0) Console.WriteLine($"{lastBit} floating point results differ from glibc's in the last bits (platform C library)");
        Console.WriteLine($"generated tests: {n - mismatches}/{expected.Length} match");
        if (mismatches > 0 || expected.Length != s_out.Count) s_fail++;
    }

    // ------------------------------------------------------------------ hand-written tests

    static void Fail(string msg)
    {
        Console.WriteLine("FAIL: " + msg);
        s_fail++;
    }

    static void Check(bool c, string msg)
    {
        if (c) s_pass++; else Fail(msg);
    }

    static void Eq<T>(T actual, T expected, string msg)
    {
        if (EqualityComparer<T>.Default.Equals(actual, expected)) s_pass++;
        else Fail($"{msg}: expected {expected}, got {actual}");
    }

    static void Throws(Action a, string msg)
    {
        try { a(); Fail(msg + ": no exception"); }
        catch (LeanPanicException) { s_pass++; }
    }

    static string S(Obj o) => lean_string_to_net(o);

    static void HandTests()
    {
        // --- representation invariants
        Check(lean_is_scalar(lean_nat_add(N("9223372036854775806"), N("1"))), "2^63-1 is boxed");
        Check(!lean_is_scalar(lean_nat_add(N("9223372036854775807"), N("1"))), "2^63 is big");
        Check(lean_is_scalar(lean_nat_sub(N("9223372036854775808"), N("1"))), "big - 1 normalizes to box");
        Eq(lean_unbox(lean_int_neg(lean_box(1))), 0xFFFFFFFFUL, "Int -1 box payload");
        Eq(lean_unbox(lean_int64_to_int(int.MinValue)), 0x80000000UL, "Int INT_MIN box payload");
        Check(!lean_is_scalar(lean_int64_to_int((long)int.MinValue - 1)), "INT_MIN-1 is big");
        Check(!lean_is_scalar(lean_int_neg(lean_int64_to_int(int.MinValue))), "-INT_MIN is big");
        Check(lean_is_scalar(lean_int_neg(lean_int64_to_int(-(long)int.MinValue))), "-(2^31) is small");
        Check(lean_is_scalar(lean_int_sub(lean_int64_to_int(2147483648L), lean_box(1))), "2^31 - 1 is small");
        Eq(lean_int64_of_int(lean_int64_to_int(long.MinValue)), 0x8000000000000000UL, "Int64.ofInt min");
        Check(lean_nat_eq(lean_box(5000), lean_box(5000)), "uncached boxes compare by value");
        Check(lean_int_eq(lean_int64_to_int(-5000), lean_int_neg(lean_box(5000))), "negative boxes compare by value");

        // --- reference counting
        {
            var big = N("100000000000000000000000");
            Eq(big.m_rc, 1, "fresh mpz rc");
            var r = lean_nat_mod(big, lean_box(0));
            Check(ReferenceEquals(r, big), "n % 0 returns n");
            Eq(big.m_rc, 2, "n % 0 increments rc");
            var i = lean_nat_to_int(big);
            Check(ReferenceEquals(i, big), "Int.ofNat of big nat reuses object");
            var bi = I("-100000000000000000000000");
            var m = lean_int_mod(bi, lean_box(0));
            Check(ReferenceEquals(m, bi) && bi.m_rc == 2, "Int.tmod by 0 returns a1 with inc");
            var em = lean_int_emod(bi, lean_box(0));
            Check(ReferenceEquals(em, bi) && bi.m_rc == 3, "Int.emod by 0 returns a1 with inc");
            var abs = lean_nat_abs(bi);
            Eq(bi.m_rc, 3, "natAbs borrows its argument");
            Eq(FN(abs), "100000000000000000000000", "natAbs big");
            var pos = I("100000000000000000000000");
            var abs2 = lean_nat_abs(pos);
            Check(!lean_is_scalar(abs2), "natAbs positive big");
            Eq(pos.m_rc, 1, "natAbs positive big: inc then consumed by int_to_nat");
            var n = N("100000000000000000000000");
            var ns = lean_int_neg_succ_of_nat(n);
            Eq(FI(ns), "-100000000000000000000001", "negSucc big");
            Eq(n.m_rc, 0, "negSucc consumes its argument");
            var u = N("18446744073709551617");
            Eq(lean_uint64_of_nat_mk(u), 1UL, "UInt64.ofBitVec value");
            Eq(u.m_rc, 0, "of_nat_mk consumes its argument");
        }

        // --- exact division
        Eq(FN(lean_nat_div_exact(N("340282366920938463463374607431768211456"), N("18446744073709551616"))), "18446744073709551616", "Nat.divExact big");
        Eq(FN(lean_nat_div_exact(lean_box(42), lean_box(6))), "7", "Nat.divExact small");
        Eq(FN(lean_nat_div_exact(lean_box(42), lean_box(0))), "0", "Nat.divExact by 0");
        Eq(FI(lean_int_div_exact(I("-340282366920938463463374607431768211456"), I("18446744073709551616"))), "-18446744073709551616", "Int.divExact big");
        Eq(FI(lean_int_div_exact(I("-2147483648"), I("2147483648"))), "-1", "Int.divExact INT_MIN / 2^31");
        Eq(FI(lean_int_div_exact(I("0"), I("2147483648"))), "0", "Int.divExact 0 / big");
        Eq(FI(lean_int_div_exact(I("-12"), I("4"))), "-3", "Int.divExact small");
        Eq(FI(lean_int_div_exact(I("-2147483648"), I("-1"))), "2147483648", "Int.divExact INT_MIN / -1");

        // --- panics
        Throws(() => lean_nat_pow(lean_box(2), N("18446744073709551616")), "Nat.pow huge exponent");
        Throws(() => lean_nat_pow(lean_box(2), lean_box(1UL << 33)), "Nat.pow exponent > UINT_MAX");
        Throws(() => lean_nat_shiftl(lean_box(1), N("18446744073709551616")), "Nat.shiftl huge");
        Eq(FN(lean_nat_shiftl(lean_box(0), N("18446744073709551616"))), "0", "0 <<< huge");
        Eq(FN(lean_nat_shiftr(lean_box(12345), lean_box(1UL << 40))), "0", "small >>> 2^40");
        Eq(FN(lean_nat_shiftr(N("100000000000000000000000"), N("100000000000000000000000"))), "0", "big >>> big");
        Eq(FN(lean_nat_pow(N("1"), lean_box(1UL << 40))), "1", "1 ^ 2^40");

        // --- misc Nat
        Eq(FN(lean_nat_succ(N("18446744073709551615"))), "18446744073709551616", "succ");
        Eq(FN(lean_nat_succ(N("9223372036854775807"))), "9223372036854775808", "succ at boundary");
        Eq(lean_nat_size_in_bytes(lean_box(3)), 8UL, "size_in_bytes small");
        Eq(lean_nat_size_in_bytes(N("18446744073709551616")), 16UL, "size_in_bytes big");
        Eq(FN(lean_nat_mul(N("4294967296"), N("4294967296"))), "18446744073709551616", "mul overflow");
        Eq(FN(lean_nat_mul(N("3037000499"), N("3037000499"))), "9223372030926249001", "mul near boundary");
        Eq(FN(lean_nat_mul(N("3037000500"), N("3037000500"))), "9223372037000250000", "mul just over");

        // --- Int conversions
        Eq(FI(lean_int64_to_int_sint(0x8000000000000000UL)), "-9223372036854775808", "Int64.toInt min");
        Eq(FI(lean_isize_to_int(0xFFFFFFFFFFFFFFFFUL)), "-1", "ISize.toInt -1");
        Eq(FI(lean_int8_to_int(0x80)), "-128", "Int8.toInt");
        Eq(FI(lean_cstr_to_int("-99999999999999999999")), "-99999999999999999999", "cstr_to_int");
        Eq(lean_int32_of_int(I("-4294967297")), 0xFFFFFFFFU, "Int32.ofInt wraps");
        Eq(lean_isize_of_nat(N("18446744073709551617")), 1UL, "ISize.ofNat wraps");

        // --- boxing
        Eq(lean_unbox_uint64(lean_box_uint64(0xDEADBEEFCAFEBABEUL)), 0xDEADBEEFCAFEBABEUL, "box uint64");
        Eq(lean_unbox_usize(lean_box_usize(0xFFFFFFFFFFFFFFFFUL)), 0xFFFFFFFFFFFFFFFFUL, "box usize");
        Eq(lean_unbox_uint32(lean_box_uint32(0xFFFFFFFFU)), 0xFFFFFFFFU, "box uint32");
        Check(lean_is_scalar(lean_box_uint32(7)), "boxed uint32 is scalar");
        Eq(lean_unbox_float(lean_box_float(-1.25)), -1.25, "box float");
        Eq(lean_unbox_float32(lean_box_float32(3.5f)), 3.5f, "box float32");
        {
            var b = lean_box_uint64(5);
            Check(!lean_is_scalar(b) && lean_obj_tag(b) == 0 && lean_ctor_num_objs(b) == 0 && b.m_cs_sz == 8, "boxed uint64 layout");
            Eq(lean_ctor_get_uint64(b, 0), 5UL, "boxed uint64 via C offset");
            var f = lean_box_float32(1.0f);
            Eq(f.m_cs_sz, (ushort)4, "boxed float32 scalar size");
        }

        // --- Float strings (known C outputs)
        Eq(S(lean_float_to_string(0.0078125)), "0.007812", "%f round-half-even");
        Eq(S(lean_float_to_string(0.0234375)), "0.023438", "%f round-half-even up");
        Eq(S(lean_float_to_string(-1e-9)), "-0.000000", "%f negative tiny");
        Eq(S(lean_float_to_string(double.NaN)), "NaN", "NaN");
        Eq(S(lean_float_to_string(double.NegativeInfinity)), "-inf", "-inf");
        Eq(S(lean_float_to_string(1e23)), "99999999999999991611392.000000", "1e23");
        Eq(S(lean_float32_to_string(0.1f)), "0.100000", "Float32 0.1");
        Eq(lean_float_to_bits(lean_float_of_bits(0xFFF0000000000001UL)), 0x7ff8000000000000UL, "NaN canonical");
        Eq(BitConverter.DoubleToUInt64Bits(lean_float_of_bits(0xFFF0000000000001UL)), 0x7ff8000000000000UL, "of_bits NaN quiet");
        Eq(lean_float32_to_bits(lean_float32_of_bits(0xFF800001U)), 0x7fc00000U, "NaN32 canonical");
        Eq(round(-0.4).ToString("R") + (double.IsNegative(round(-0.4)) ? "-" : "+"), "-0-", "round(-0.4) = -0");
        Eq(round(0.49999999999999994), 0.0, "round just below half");
        Eq(round(-2.5), -3.0, "round half away");
        Eq(roundf(2.5f), 3.0f, "roundf half away");
        Eq(BitConverter.SingleToUInt32Bits(lean_uint64_to_float32(0x1000001000000001UL)), (187U << 23) | 1U, "uint64->float32 sticky (just above tie)");
        Eq(BitConverter.SingleToUInt32Bits(lean_uint64_to_float32(0x1000001000000000UL)), (187U << 23), "uint64->float32 tie to even");
        Eq(BitConverter.SingleToUInt32Bits(lean_int64_to_float32(unchecked((ulong)-0x1000001000000001L))), (1U << 31) | (187U << 23) | 1U, "int64->float32 negative sticky");
        Eq(lean_uint64_to_float(0xFFFFFFFFFFFFFFFFUL), 18446744073709551616.0, "uint64 max -> float");
        Eq(lean_uint64_to_float(0x8000000000000401UL), 9223372036854777856.0, "uint64 -> float rounding up");
        Eq(lean_uint64_to_float(0x8000000000000400UL), 9223372036854775808.0, "uint64 -> float tie to even");
        Eq(fma(0.1, 10.0, -1.0), 5.551115123125783e-17, "fma is fused");

        // --- FloatArray
        {
            var arr = lean_alloc_sarray(8, 0, 0);
            for (int k = 0; k < 10; k++) arr = lean_float_array_push(arr, k * 0.5);
            Eq(lean_unbox(lean_float_array_size(arr)), 10UL, "FloatArray size");
            Eq(lean_float_array_get(arr, lean_box(3)), 1.5, "FloatArray get");
            Eq(lean_float_array_get(arr, lean_box(10)), 0.0, "FloatArray get out of bounds");
            Eq(lean_float_array_get(arr, N("100000000000000000000000")), 0.0, "FloatArray get big index");
            Eq(lean_float_array_fget(arr, lean_box(9)), 4.5, "FloatArray fget");
            Eq(lean_float_array_uget(arr, 2), 1.0, "FloatArray uget");
            lean_inc(arr);
            var arr2 = lean_float_array_set(arr, lean_box(0), 42.0);
            Check(!ReferenceEquals(arr, arr2), "set on shared array copies");
            Eq(lean_float_array_get(arr, lean_box(0)), 0.0, "original unchanged");
            Eq(lean_float_array_get(arr2, lean_box(0)), 42.0, "copy updated");
            Eq(arr.m_rc, 1, "copy decrements the original");
            var arr3 = lean_float_array_uset(arr2, 1, 7.0);
            Check(ReferenceEquals(arr2, arr3), "uset on exclusive array is in place");
            var arr4 = lean_float_array_set(arr3, lean_box(100), 1.0);
            Check(ReferenceEquals(arr3, arr4), "set out of bounds is a no-op");
            var arr5 = lean_float_array_fset(arr4, lean_box(2), -1.0);
            var data = lean_float_array_data(arr5);
            Eq(lean_array_size(data), 10UL, "FloatArray.data size");
            Eq(lean_unbox_float(lean_array_get_core(data, 0)), 42.0, "data[0]");
            Eq(lean_unbox_float(lean_array_get_core(data, 1)), 7.0, "data[1]");
            Eq(lean_unbox_float(lean_array_get_core(data, 2)), -1.0, "data[2]");
            var back = lean_float_array_mk(data);
            Eq(lean_sarray_size(back), 10UL, "FloatArray.mk size");
            Eq(lean_float_array_uget(back, 9), 4.5, "FloatArray.mk element");
            Eq(lean_sarray_elem_size(back), 8U, "FloatArray elem size");
        }

        // --- lean_internal_*
        Eq(S(lean_internal_get_build_type(lean_box(0))), "Release", "build type");
        Eq(lean_internal_is_multi_thread(lean_box(0)), (byte)1, "multi thread");
        Eq(lean_internal_is_stage0(lean_box(0)), (byte)0, "stage0");
        Eq(lean_internal_get_believer_trust_level(lean_box(0)), 1024U, "trust level");
        Check(lean_internal_get_hardware_concurrency(lean_box(0)) >= 1, "hardware concurrency");
        Check(lean_is_scalar(lean_internal_set_max_heartbeat(200000)) && LeanRuntimeSettings.MaxHeartbeat == 200000, "set max heartbeat");
        Check(lean_is_scalar(lean_internal_set_max_memory(1 << 20)) && LeanRuntimeSettings.MaxMemory == 1 << 20, "set max memory");
        lean_internal_enable_debug(lean_mk_string("foo"));
        Check(LeanRuntimeSettings.IsDebugEnabled("foo") && !LeanRuntimeSettings.IsDebugEnabled("bar"), "enable debug");
        lean_internal_set_exit_on_panic(1);
        Check(g_exit_on_panic, "set exit on panic");
        lean_internal_set_exit_on_panic(0);
        Check(!g_exit_on_panic, "reset exit on panic");
    }
}
