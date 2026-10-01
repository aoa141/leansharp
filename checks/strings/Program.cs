// Checks for the strings_arrays area. Expected hash values were computed with the real Lean
// (`#eval "hello".hash` etc. with ~/Repos/lean4/build/release/stage1/bin/lean).

using System.Text;
using LeanSharp.Runtime;
using static LeanSharp.Runtime.LeanRt;

unsafe
{
    int failures = 0;
    int checks = 0;

    void Check(bool cond, string what)
    {
        checks++;
        if (!cond) { failures++; Console.WriteLine("FAIL: " + what); }
    }

    void Eq<T>(T actual, T expected, string what)
    {
        checks++;
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            failures++;
            Console.WriteLine($"FAIL: {what}: expected {expected}, got {actual}");
        }
    }

    Obj S(string s) => lean_mk_string(s);
    string N(Obj o) => lean_string_to_net(o);
    Obj B(ulong n) => lean_box(n);

    var panics = new List<string>();
    PanicHandler = msg => panics.Add(msg);

    // ------------------------------------------------------------------
    // Hashes (reference values from real Lean)
    Eq(lean_string_hash(S("hello")), 9821865621596011261UL, "\"hello\".hash");
    Eq(lean_string_hash(S("")), 9877294847684254529UL, "\"\".hash");
    Eq(lean_string_hash(S("héllo€😀")), 15938592973456854268UL, "\"héllo€😀\".hash");
    Eq(lean_string_hash(S("The quick brown fox jumps over the lazy dog")), 15530722894793056304UL, "fox.hash");
    Eq(lean_byte_array_hash(MkByteArray(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })), 7591760769200274683UL, "ByteArray.hash");
    Eq(lean_byte_array_hash(MkByteArray(Array.Empty<byte>())), 9877294847684254529UL, "ByteArray.empty.hash");

    // ------------------------------------------------------------------
    // UTF-8 positions on "héllo€😀" (h=1, é=2, llo=3, €=3, 😀=4 bytes => 13 bytes, 7 chars)
    var u = S("héllo€😀");
    Eq(lean_unbox(lean_string_utf8_byte_size(u)), 13UL, "utf8ByteSize");
    Eq(lean_unbox(lean_string_length(u)), 7UL, "length");
    Eq(lean_unbox(lean_string_utf8_next(u, B(1))), 3UL, "next 1");
    Eq(lean_unbox(lean_string_utf8_next(u, B(9))), 13UL, "next 9 (4-byte char)");
    Eq(lean_unbox(lean_string_utf8_next(u, B(13))), 14UL, "next at end");
    Eq(lean_unbox(lean_string_utf8_next(u, B(100))), 101UL, "next out of bounds");
    Eq(lean_unbox(lean_string_utf8_next_fast(u, B(6))), 9UL, "next_fast 6 (€)");
    Eq(lean_unbox(lean_string_utf8_next_fast(u, B(0))), 1UL, "next_fast 0");
    Eq(lean_unbox(lean_string_utf8_prev(u, B(10))), 9UL, "prev 10");
    Eq(lean_unbox(lean_string_utf8_prev(u, B(13))), 9UL, "prev 13");
    Eq(lean_unbox(lean_string_utf8_prev(u, B(3))), 1UL, "prev 3");
    Eq(lean_unbox(lean_string_utf8_prev(u, B(0))), 0UL, "prev 0");
    Eq(lean_unbox(lean_string_utf8_prev(u, B(20))), 19UL, "prev out of bounds");
    Eq(N(lean_string_utf8_extract(u, B(1), B(9))), "éllo€", "extract 1 9");
    Eq(N(lean_string_utf8_extract(u, B(2), B(9))), "", "extract from non-first byte");
    Eq(N(lean_string_utf8_extract(u, B(1), B(10))), "éllo€😀", "extract end in middle of char");
    Eq(N(lean_string_utf8_extract(u, B(5), B(3))), "", "extract b >= e");
    Eq(N(lean_string_utf8_extract(u, B(0), B(1000))), "héllo€😀", "extract e past end");
    Eq(lean_string_len(lean_string_utf8_extract(u, B(1), B(9))), 5UL, "extract length");
    Eq(N(lean_string_utf8_extract_fast(u, B(3), B(6))), "llo", "extract_fast");
    Eq(lean_string_utf8_get(u, B(1)), 233u, "get 1");
    Eq(lean_string_utf8_get(u, B(2)), 65u, "get 2 (continuation byte)");
    Eq(lean_string_utf8_get(u, B(9)), 128512u, "get 9");
    Eq(lean_string_utf8_get(u, B(13)), 65u, "get at end");
    Eq(lean_string_utf8_get_fast(u, B(6)), 0x20ACu, "get_fast €");
    Eq(lean_string_utf8_get_fast(u, B(0)), (uint)'h', "get_fast h");
    Eq(lean_string_utf8_get_bang(u, B(9)), 128512u, "get! 9");
    panics.Clear();
    Eq(lean_string_utf8_get_bang(u, B(13)), 65u, "get! at end");
    Eq(panics.Count == 1 ? panics[0] : "", "Error: invalid `String.Pos` at `String.get!`", "get! panic message");
    var opt = lean_string_utf8_get_opt(u, B(6));
    Check(!lean_is_scalar(opt) && lean_obj_tag(opt) == 1 && lean_unbox(lean_ctor_get(opt, 0)) == 0x20AC, "get? 6");
    Check(lean_is_scalar(lean_string_utf8_get_opt(u, B(2))), "get? 2 = none");
    Check(lean_is_scalar(lean_string_utf8_get_opt(u, B(13))), "get? end = none");
    Eq(lean_string_utf8_at_end(u, B(13)), (byte)1, "atEnd 13");
    Eq(lean_string_utf8_at_end(u, B(12)), (byte)0, "atEnd 12");
    Eq(lean_string_is_valid_pos(u, B(2)), (byte)0, "isValid 2");
    Eq(lean_string_is_valid_pos(u, B(3)), (byte)1, "isValid 3");
    Eq(lean_string_is_valid_pos(u, B(13)), (byte)1, "isValid 13");
    Eq(lean_string_is_valid_pos(u, B(14)), (byte)0, "isValid 14");
    Eq(lean_string_get_byte_fast(u, B(1)), (byte)0xC3, "getUTF8Byte");
    Eq(lean_string_uget_byte_fast(u, 0), (byte)'h', "ugetUTF8Byte");

    // big (non-scalar) positions
    var bigPos = lean_big_to_nat(new System.Numerics.BigInteger(ulong.MaxValue) + 5);
    Eq(lean_string_utf8_get(u, bigPos), 65u, "get big pos");
    Check(lean_nat_to_big(lean_string_utf8_next(u, bigPos)) == new System.Numerics.BigInteger(ulong.MaxValue) + 6, "next big pos");
    Check(lean_nat_to_big(lean_string_utf8_prev(u, bigPos)) == new System.Numerics.BigInteger(ulong.MaxValue) + 4, "prev big pos");
    Eq(lean_string_utf8_at_end(u, bigPos), (byte)1, "atEnd big pos");
    Eq(N(lean_string_utf8_extract(u, B(0), bigPos)), "héllo€😀", "extract big end");
    Check(lean_nat_to_big(lean_string_utf8_next(u, B(LEAN_MAX_SMALL_NAT))) == new System.Numerics.BigInteger(LEAN_MAX_SMALL_NAT) + 1, "next max small nat");

    // set
    Eq(N(lean_string_utf8_set(S("héllo€😀"), B(1), 'Z')), "hZllo€😀", "set 1 'Z'");
    Eq(N(lean_string_utf8_set(S("héllo€😀"), B(0), 'Z')), "Zéllo€😀", "set ascii");
    Eq(N(lean_string_utf8_set(S("héllo€😀"), B(9), 'x')), "héllo€x", "set 4-byte -> 1-byte");
    Eq(N(lean_string_utf8_set(S("héllo€😀"), B(2), 'x')), "héllo€😀", "set non-first byte");
    Eq(N(lean_string_utf8_set(S("abc"), B(1), 0x1F600)), "a😀c", "set ascii -> emoji");
    Eq(lean_string_len(lean_string_utf8_set(S("abc"), B(1), 0x1F600)), 3UL, "set keeps length");
    {
        var sh = S("abc");
        lean_inc(sh); // shared
        var r = lean_string_utf8_set(sh, B(0), 'X');
        Check(!ReferenceEquals(r, sh), "set on shared copies");
        Eq(N(sh), "abc", "set on shared leaves original");
        Eq(N(r), "Xbc", "set on shared result");
        Eq(sh.m_rc, 1, "set on shared decrements original");
        var ex = S("abc");
        var r2 = lean_string_utf8_set(ex, B(0), 'X');
        Check(ReferenceEquals(r2, ex), "set on exclusive is in place");
    }

    // ------------------------------------------------------------------
    // push / append
    {
        var s = S("");
        var s0 = s;
        s = lean_string_push(s, 'a');
        s = lean_string_push(s, 0xE9);
        s = lean_string_push(s, 0x20AC);
        s = lean_string_push(s, 0x1F600);
        Eq(N(s), "aé€😀", "push multi-byte");
        Eq(lean_string_len(s), 4UL, "push length");
        Eq(lean_string_size(s), 11UL, "push size (incl NUL)");
        Eq(lean_string_cstr(s)[10], (byte)0, "push NUL terminator");
        var cap = lean_string_capacity(s);
        var s2 = lean_string_push(s, 'b');
        if (cap >= 12 + 5) Check(ReferenceEquals(s, s2), "push in place when capacity suffices");

        var shared = S("abc");
        lean_inc(shared);
        var p = lean_string_push(shared, 'd');
        Check(!ReferenceEquals(p, shared), "push on shared copies");
        Eq(N(shared), "abc", "push shared original unchanged");
        Eq(N(p), "abcd", "push shared result");
        Eq(lean_string_capacity(p), 2UL * (4 + 5), "push shared capacity = mk_capacity(sz+5)");
        Eq(shared.m_rc, 1, "push shared dec");

        var a = S("héllo");
        var b = S(" wörld€");
        var ab = lean_string_append(a, b);
        Eq(N(ab), "héllo wörld€", "append");
        Eq(lean_string_len(ab), 12UL, "append length");
        Eq(lean_string_size(ab), (ulong)Encoding.UTF8.GetByteCount("héllo wörld€") + 1, "append size");
        Eq(N(b), " wörld€", "append s2 untouched");

        var x = S("xy");
        lean_inc(x);
        var xx = lean_string_append(x, x);
        Eq(N(xx), "xyxy", "append self (shared)");
        Eq(N(x), "xy", "append self original");
        Eq(N(lean_string_append(S(""), S(""))), "", "append empty");
    }

    // ------------------------------------------------------------------
    // comparisons
    Eq(lean_string_dec_eq(S("abc"), S("abc")), (byte)1, "decEq equal");
    Eq(lean_string_dec_eq(S("abc"), S("abd")), (byte)0, "decEq different");
    Eq(lean_string_dec_eq(S("abc"), S("ab")), (byte)0, "decEq different size");
    Eq(lean_string_dec_lt(S("ab"), S("abc")), (byte)1, "lt prefix");
    Eq(lean_string_dec_lt(S("abc"), S("ab")), (byte)0, "lt prefix rev");
    Eq(lean_string_dec_lt(S("abc"), S("abc")), (byte)0, "lt equal");
    Eq(lean_string_dec_lt(S("a"), S("é")), (byte)1, "lt utf8 bytes");
    Eq(lean_string_compare(S("a"), S("b")), (byte)0, "compare lt");
    Eq(lean_string_compare(S("b"), S("b")), (byte)1, "compare eq");
    Eq(lean_string_compare(S("ba"), S("b")), (byte)2, "compare gt");
    Eq(lean_string_memcmp(S("xxhello"), S("hello!"), B(2), B(0), B(5)), (byte)1, "memcmp equal");
    Eq(lean_string_memcmp(S("xxhello"), S("hellO!"), B(2), B(0), B(5)), (byte)0, "memcmp different");

    // slices: String.Slice = (str, start, end)
    Obj Slice(Obj s, ulong b, ulong e)
    {
        var r = lean_alloc_ctor(0, 3, 0);
        lean_ctor_set(r, 0, s); lean_ctor_set(r, 1, B(b)); lean_ctor_set(r, 2, B(e));
        return r;
    }
    Eq(lean_slice_hash(Slice(S("xxhelloyy"), 2, 7)), 9821865621596011261UL, "slice hash = \"hello\".hash");
    Eq(lean_slice_dec_lt(Slice(S("xab"), 1, 2), Slice(S("ab"), 0, 2)), (byte)1, "slice lt");
    Eq(lean_slice_dec_lt(Slice(S("ab"), 0, 2), Slice(S("xab"), 1, 3)), (byte)0, "slice lt equal");

    // ------------------------------------------------------------------
    // List Char conversions
    {
        var l = lean_string_data(S("aé😀"));
        var cs = ListToManaged(l).Select(o => (uint)lean_unbox(o)).ToArray();
        Check(cs.SequenceEqual(new uint[] { 'a', 0xE9, 0x1F600 }), "String.toList");
        var s = lean_string_mk(l);
        Eq(N(s), "aé😀", "String.mk");
        Eq(lean_string_len(s), 3UL, "String.mk length");
        Eq(N(lean_string_mk(lean_box(0))), "", "String.mk []");
    }

    // ------------------------------------------------------------------
    // UTF-8 / ByteArray conversions
    {
        var bytes = lean_string_to_utf8(S("hé"));
        Check(ByteArraySpan(bytes).SequenceEqual(new byte[] { 0x68, 0xC3, 0xA9 }), "toUTF8");
        Eq(lean_string_validate_utf8(bytes), (byte)1, "validateUTF8 ok");
        Eq(lean_string_validate_utf8(MkByteArray(new byte[] { 0x68, 0xC3 })), (byte)0, "validateUTF8 truncated");
        Eq(lean_string_validate_utf8(MkByteArray(new byte[] { 0xED, 0xA0, 0x80 })), (byte)0, "validateUTF8 surrogate");
        Eq(lean_string_validate_utf8(MkByteArray(new byte[] { 0xC0, 0x80 })), (byte)0, "validateUTF8 overlong");
        var s = lean_string_from_utf8_unchecked(bytes);
        Eq(N(s), "hé", "fromUTF8 unchecked");
        Eq(lean_string_len(s), 2UL, "fromUTF8 length");
        var lossy = lean_decode_lossy_utf8(MkByteArray(new byte[] { 0x61, 0xFF, 0x62, 0xE2, 0x82, 0x63, 0x80, 0x80, 0x64 }));
        // 0xFF -> FFFD; E2 82 (truncated) -> FFFD (skipping continuation 82); 63 ok; 80 80 -> one FFFD; 64
        Eq(N(lossy), "a�b�c�d", "decodeLossyUTF8");
        Eq(lean_string_len(lossy), 7UL, "decodeLossyUTF8 length");
    }

    Eq(N(lean_string_of_usize(0)), "0", "USize.repr 0");
    Eq(N(lean_string_of_usize(ulong.MaxValue)), "18446744073709551615", "USize.repr max");

    // ------------------------------------------------------------------
    // Arrays
    {
        var a = lean_mk_empty_array();
        var caps = new List<ulong>();
        for (ulong i = 0; i < 10; i++)
        {
            a = lean_array_push(a, B(i));
            caps.Add(lean_array_capacity(a));
        }
        Check(caps.SequenceEqual(new ulong[] { 2, 2, 6, 6, 6, 6, 14, 14, 14, 14 }), "push capacity growth: " + string.Join(",", caps));
        Eq(lean_unbox(lean_array_get_size(a)), 10UL, "Array.size");
        Eq(lean_array_size(a), 10UL, "Array.usize");
        Eq(lean_unbox(lean_array_fget(a, B(7))), 7UL, "fget");
        Eq(lean_unbox(lean_array_uget(a, 3)), 3UL, "uget");
        Eq(lean_unbox(lean_array_get(B(99), a, B(4))), 4UL, "get! in bounds");
        panics.Clear();
        Eq(lean_unbox(lean_array_get(B(99), a, B(10))), 99UL, "get! out of bounds default");
        Eq(panics.Count == 1 ? panics[0] : "", "Error: index out of bounds", "get! panic message");
        Eq(lean_unbox(lean_array_get(B(98), a, bigPos)), 98UL, "get! big index");
        Eq(lean_unbox(lean_array_get_borrowed(B(97), a, B(100))), 97UL, "get!Borrowed out of bounds");

        // shared push copies with non-expanded capacity if enough space
        lean_inc(a);
        var a2 = lean_array_push(a, B(10));
        Check(!ReferenceEquals(a, a2), "push on shared copies");
        Eq(lean_array_size(a), 10UL, "push on shared original size");
        Eq(lean_array_size(a2), 11UL, "push on shared new size");
        Eq(lean_array_capacity(a2), 30UL, "push on shared expands (cap < 2*sz+1)");
        Eq(a.m_rc, 1, "push on shared dec original");

        // set / uset / fset
        var s1 = S("x"); var s2 = S("y");
        var arr = lean_mk_array(B(3), s1);
        Eq(s1.m_rc, 3, "mk_array inc_n");
        arr = lean_array_set(arr, B(1), s2);
        Eq(s1.m_rc, 2, "set dec old");
        Check(ReferenceEquals(lean_array_get_core(arr, 1), s2), "set value");
        panics.Clear();
        var s3 = S("z");
        lean_inc(s3);
        var arr2 = lean_array_set(arr, B(5), s3);
        Check(ReferenceEquals(arr, arr2), "set out of bounds returns array");
        Eq(s3.m_rc, 1, "set out of bounds decs value");
        Eq(panics.Count, 1, "set out of bounds panics");
        arr = lean_array_fset(arr, B(2), S("w"));
        Eq(s1.m_rc, 1, "fset dec old");
        arr = lean_array_uswap(arr, 0, 2);
        Eq(N(lean_array_get_core(arr, 0)), "w", "uswap");
        arr = lean_array_swap(arr, B(0), B(7));
        Eq(N(lean_array_get_core(arr, 0)), "w", "swapIfInBounds out of bounds");
        arr = lean_array_fswap(arr, B(0), B(1));
        Eq(N(lean_array_get_core(arr, 0)), "y", "fswap");
        arr = lean_array_pop(arr);
        Eq(lean_array_size(arr), 2UL, "pop");
        Eq(s1.m_rc, 0, "pop dec (released)");
        var empty = lean_array_pop(lean_mk_empty_array());
        Eq(lean_array_size(empty), 0UL, "pop empty");
        var zero = S("zero");
        var arr0 = lean_mk_array(B(0), zero);
        Eq(zero.m_rc, 0, "mk_array 0 decs value");
        Eq(lean_array_capacity(lean_mk_empty_array_with_capacity(B(17))), 17UL, "emptyWithCapacity");
        var sz = lean_array_sz(lean_mk_array(B(4), B(0)));
        Eq(lean_unbox(sz), 4UL, "array_sz");

        // linearity mark propagation
        var lin = lean_array_mark_linear(lean_mk_array(B(2), B(1)));
        Check(lean_array_is_marked_linear(lin), "mark linear");
        lean_inc(lin);
        var lin2 = lean_array_push(lin, B(3));
        Check(lean_array_is_marked_linear(lin2), "linear mark copied");
        Check(!ReferenceEquals(lin, lin2), "linear array copied when shared");
        Check(lean_sarray_elem_size(lean_sarray_mark_linear(lean_alloc_sarray(8, 0, 0))) == 8, "sarray elem size ignores mark");
    }

    // Array.mk / toList through LeanExports (fake Lean implementations)
    {
        LeanExports.Register("lean_list_to_array", (nint)(delegate*<Obj, Obj, Obj>)&FakeExports.ListToArray);
        LeanExports.Register("lean_array_to_list_impl", (nint)(delegate*<Obj, Obj, Obj>)&FakeExports.ArrayToList);
        var arr = lean_array_mk(MkList(new[] { B(1), B(2), B(3) }));
        Eq(lean_array_size(arr), 3UL, "Array.mk");
        var l = lean_array_to_list(arr);
        Check(ListToManaged(l).Select(o => lean_unbox(o)).SequenceEqual(new ulong[] { 1, 2, 3 }), "Array.toList");
    }

    // ------------------------------------------------------------------
    // ByteArray
    {
        var ba = lean_mk_empty_byte_array(B(0));
        for (int i = 0; i < 5; i++) ba = lean_byte_array_push(ba, (byte)(10 + i));
        Eq(lean_unbox(lean_byte_array_size(ba)), 5UL, "ByteArray.size");
        Eq(lean_sarray_size(ba), 5UL, "sarray_size");
        Eq(lean_sarray_capacity(ba), 6UL, "ByteArray push capacity (min_cap*2)");
        Eq(lean_byte_array_get(ba, B(2)), (byte)12, "ByteArray.get!");
        Eq(lean_byte_array_get(ba, B(20)), (byte)0, "ByteArray.get! oob");
        Eq(lean_byte_array_get(ba, bigPos), (byte)0, "ByteArray.get! big");
        Eq(lean_byte_array_fget(ba, B(4)), (byte)14, "ByteArray.get");
        Eq(lean_byte_array_uget(ba, 0), (byte)10, "ByteArray.uget");
        ba = lean_byte_array_set(ba, B(1), 99);
        Eq(lean_byte_array_uget(ba, 1), (byte)99, "ByteArray.set!");
        var ba2 = lean_byte_array_set(ba, B(50), 1);
        Check(ReferenceEquals(ba, ba2), "ByteArray.set! oob");
        lean_inc(ba);
        var ba3 = lean_byte_array_fset(ba, B(0), 1);
        Check(!ReferenceEquals(ba, ba3), "ByteArray.set shared copies");
        Eq(lean_byte_array_uget(ba, 0), (byte)10, "ByteArray.set shared orig");
        Eq(lean_byte_array_uget(ba3, 0), (byte)1, "ByteArray.set shared new");
        var ba4 = lean_byte_array_uset(ba3, 4, 7);
        Eq(lean_byte_array_uget(ba4, 4), (byte)7, "ByteArray.uset");
        var data = lean_byte_array_data(MkByteArray(new byte[] { 3, 4 }));
        Eq(lean_array_size(data), 2UL, "ByteArray.data size");
        Eq(lean_unbox(lean_array_get_core(data, 1)), 4UL, "ByteArray.data elem");
        var mk = lean_byte_array_mk(data);
        Check(ByteArraySpan(mk).SequenceEqual(new byte[] { 3, 4 }), "ByteArray.mk");
        Eq(lean_sarray_dec_eq(MkByteArray(new byte[] { 1, 2 }), MkByteArray(new byte[] { 1, 2 })), (byte)1, "ByteArray.beq");
        Eq(lean_sarray_dec_eq(MkByteArray(new byte[] { 1, 2 }), MkByteArray(new byte[] { 1, 3 })), (byte)0, "ByteArray.beq ne");
        Eq(lean_sarray_dec_eq(MkByteArray(new byte[] { 1, 2 }), MkByteArray(new byte[] { 1 })), (byte)0, "ByteArray.beq size");

        // copySlice src srcOff dest destOff len exact
        var src = MkByteArray(new byte[] { 1, 2, 3, 4, 5 });
        var dst = MkByteArray(new byte[] { 9, 9, 9 });
        var cs = lean_byte_array_copy_slice(src, B(1), dst, B(2), B(10), 1);
        Check(ByteArraySpan(cs).SequenceEqual(new byte[] { 9, 9, 2, 3, 4, 5 }), "copySlice");
        Eq(lean_sarray_capacity(cs), 6UL, "copySlice exact capacity");
        var cs2 = lean_byte_array_copy_slice(src, B(9), MkByteArray(new byte[] { 7 }), B(0), B(1), 0);
        Check(ByteArraySpan(cs2).SequenceEqual(new byte[] { 7 }), "copySlice src_off oob");
        var cs3 = lean_byte_array_copy_slice(src, B(0), MkByteArray(new byte[] { 7 }), B(100), B(2), 0);
        Check(ByteArraySpan(cs3).SequenceEqual(new byte[] { 7, 1, 2 }), "copySlice dest_off clamped");
        Eq(lean_sarray_capacity(cs3), 6UL, "copySlice non-exact capacity doubles");

        // ByteSlice (bytearray, start, end)
        Obj BS(Obj a, ulong b, ulong e)
        {
            var r = lean_alloc_ctor(0, 3, 0);
            lean_ctor_set(r, 0, a); lean_ctor_set(r, 1, B(b)); lean_ctor_set(r, 2, B(e));
            return r;
        }
        Eq(lean_byteslice_beq(BS(src, 1, 3), BS(MkByteArray(new byte[] { 2, 3 }), 0, 2)), (byte)1, "ByteSlice.beq");
        Eq(lean_byteslice_beq(BS(src, 1, 3), BS(src, 2, 4)), (byte)0, "ByteSlice.beq ne");

        var fa = lean_mk_empty_float_array(B(4));
        Eq(lean_sarray_elem_size(fa), 8u, "FloatArray elem size");
        Eq(lean_sarray_capacity(fa), 4UL, "FloatArray capacity");
    }

    // ------------------------------------------------------------------
    // Names
    {
        // Mimic Lean's Name.mkStr / Name.mkNum hashing (Name.hash for .str is mixHash p.hash s.hash)
        ulong NameHash(Obj n) => lean_is_scalar(n) ? 1723UL : lean_ctor_get_uint64_s(n, 0);
        Obj Str(Obj p, string s)
        {
            var r = lean_alloc_ctor(1, 2, 8);
            var so = S(s);
            lean_ctor_set(r, 0, p); lean_ctor_set(r, 1, so);
            lean_ctor_set_uint64_s(r, 0, LeanHash.Mix(NameHash(p), lean_string_hash(so)));
            return r;
        }
        Obj Num(Obj p, ulong n)
        {
            var r = lean_alloc_ctor(2, 2, 8);
            lean_ctor_set(r, 0, p); lean_ctor_set(r, 1, B(n));
            lean_ctor_set_uint64_s(r, 0, LeanHash.Mix(NameHash(p), n));
            return r;
        }
        var anon = lean_box(0);
        var n1 = Num(Str(Str(anon, "Lean"), "Meta"), 5);
        var n2 = Num(Str(Str(anon, "Lean"), "Meta"), 5);
        var n3 = Num(Str(Str(anon, "Lean"), "Elab"), 5);
        var n4 = Str(Str(anon, "Lean"), "Meta");
        Eq(lean_name_eq(n1, n2), (byte)1, "Name.beq equal");
        Eq(lean_name_eq(n1, n3), (byte)0, "Name.beq different");
        Eq(lean_name_eq(n1, n4), (byte)0, "Name.beq prefix");
        Eq(lean_name_eq(anon, anon), (byte)1, "Name.beq anonymous");
        Eq(lean_name_eq(anon, n4), (byte)0, "Name.beq anonymous vs str");
        // same hash, different structure (forged hash) must still compare structurally
        var f1 = Str(anon, "a");
        var f2 = Str(anon, "b");
        lean_ctor_set_uint64_s(f2, 0, lean_ctor_get_uint64_s(f1, 0));
        Eq(lean_name_eq(f1, f2), (byte)0, "Name.beq forged hash");
        // big numeric components
        var big1 = lean_big_to_nat(System.Numerics.BigInteger.Pow(2, 70));
        var big2 = lean_big_to_nat(System.Numerics.BigInteger.Pow(2, 70));
        var nb1 = lean_alloc_ctor(2, 2, 8); lean_ctor_set(nb1, 0, anon); lean_ctor_set(nb1, 1, big1); lean_ctor_set_uint64_s(nb1, 0, 42);
        var nb2 = lean_alloc_ctor(2, 2, 8); lean_ctor_set(nb2, 0, anon); lean_ctor_set(nb2, 1, big2); lean_ctor_set_uint64_s(nb2, 0, 42);
        Eq(lean_name_eq(nb1, nb2), (byte)1, "Name.beq big num");
        // large boxed numbers (not cached boxes) are compared by value
        var nl1 = lean_alloc_ctor(2, 2, 8); lean_ctor_set(nl1, 0, anon); lean_ctor_set(nl1, 1, B(1UL << 40)); lean_ctor_set_uint64_s(nl1, 0, 42);
        var nl2 = lean_alloc_ctor(2, 2, 8); lean_ctor_set(nl2, 0, anon); lean_ctor_set(nl2, 1, B(1UL << 40)); lean_ctor_set_uint64_s(nl2, 0, 42);
        Eq(lean_name_eq(nl1, nl2), (byte)1, "Name.beq large boxed num");
    }

    // ------------------------------------------------------------------
    // ShareCommon
    {
        Obj Pair(Obj a, Obj b) => lean_mk_pair(a, b);
        // two structurally equal but physically different trees
        var t = Pair(Pair(S("a"), B(1)), Pair(S("a"), B(1)));
        Check(!ReferenceEquals(lean_ctor_get(t, 0), lean_ctor_get(t, 1)), "sharecommon precondition");
        var r = lean_sharecommon_quick(t);
        Check(ReferenceEquals(lean_ctor_get(r, 0), lean_ctor_get(r, 1)), "shareCommon' shares equal subterms");
        Check(ReferenceEquals(lean_ctor_get(lean_ctor_get(r, 0), 0), lean_ctor_get(lean_ctor_get(t, 0), 0)), "shareCommon' reuses terminal strings");
        Eq(lean_ctor_get(r, 0).m_rc, 2, "shareCommon' rc of shared subterm");
        Eq(t.m_rc, 1, "shareCommon' borrows argument");

        // sharecommon eq/hash are shallow (children by identity)
        var sa = S("q");
        var c1 = Pair(sa, B(3));
        lean_inc(sa);
        var c2 = Pair(sa, B(3));
        Eq(lean_sharecommon_eq(c1, c2), (byte)1, "sharecommon_eq shallow equal");
        Eq(lean_sharecommon_hash(c1), lean_sharecommon_hash(c2), "sharecommon_hash consistent");
        var c3 = Pair(S("q"), B(3));
        Eq(lean_sharecommon_eq(c1, c3), (byte)0, "sharecommon_eq children by identity");
        Eq(lean_sharecommon_eq(S("hello"), S("hello")), (byte)1, "sharecommon_eq strings");
        Eq(lean_sharecommon_hash(S("hello")), lean_sharecommon_hash(S("hello")), "sharecommon_hash strings");
        Eq(lean_sharecommon_eq(S("hello"), S("hellp")), (byte)0, "sharecommon_eq strings ne");
        var big = System.Numerics.BigInteger.Pow(3, 80);
        Eq(lean_sharecommon_eq(lean_alloc_mpz(big), lean_alloc_mpz(big)), (byte)1, "sharecommon_eq mpz");
        Eq(lean_sharecommon_hash(lean_alloc_mpz(big)), lean_sharecommon_hash(lean_alloc_mpz(big)), "sharecommon_hash mpz");
        var sc1 = lean_alloc_ctor(0, 1, 16); lean_ctor_set(sc1, 0, B(1)); lean_ctor_set_uint64_s(sc1, 8, 77);
        var sc2 = lean_alloc_ctor(0, 1, 16); lean_ctor_set(sc2, 0, B(1)); lean_ctor_set_uint64_s(sc2, 8, 78);
        Eq(lean_sharecommon_eq(sc1, sc2), (byte)0, "sharecommon_eq scalar area");
        lean_ctor_set_uint64_s(sc2, 8, 77);
        Eq(lean_sharecommon_eq(sc1, sc2), (byte)1, "sharecommon_eq scalar area equal");

        // arrays: elements shared
        var arr = MkArray(new[] { Pair(S("k"), B(1)), Pair(S("k"), B(1)) });
        var ra = lean_sharecommon_quick(arr);
        Check(ReferenceEquals(lean_array_get_core(ra, 0), lean_array_get_core(ra, 1)), "shareCommon' in arrays");

        // State-based version with a fake StateFactory using managed dictionaries
        var r2 = FakeState.Run(Pair(Pair(S("a"), B(1)), Pair(S("a"), B(1))));
        Check(ReferenceEquals(lean_ctor_get(r2, 0), lean_ctor_get(r2, 1)), "State.shareCommon shares equal subterms");
    }

    Eq(SigCheck.Run(), File.ReadAllLines(Path.Combine(Environment.GetEnvironmentVariable("HOME"), "Repos/leansharp/Docs/externs-strings_arrays.txt")).Count(l => l.Trim().Length > 0), "all externs present");
    Console.WriteLine($"{checks - failures}/{checks} checks passed");
    return failures == 0 ? 0 : 1;
}

static unsafe class FakeExports
{
    public static Obj ListToArray(Obj type, Obj l)
    {
        var xs = LeanRt.ListToManaged(l);
        foreach (var x in xs) LeanRt.lean_inc(x);
        LeanRt.lean_dec(l);
        return LeanRt.MkArray(xs);
    }

    public static Obj ArrayToList(Obj type, Obj a)
    {
        var xs = new List<Obj>();
        for (ulong i = 0; i < LeanRt.lean_array_size(a); i++) xs.Add(LeanRt.lean_array_uget(a, i));
        LeanRt.lean_dec(a);
        return LeanRt.MkList(xs);
    }
}

/// <summary>A fake `ShareCommon.StateFactory` backed by an external object holding managed maps.</summary>
static unsafe class FakeState
{
    sealed class Maps
    {
        public readonly Dictionary<Obj, Obj> Map = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<Obj, Obj> Set = new(new Cmp());
    }

    sealed class Cmp : IEqualityComparer<Obj>
    {
        public bool Equals(Obj a, Obj b) => LeanRt.lean_sharecommon_eq(a, b) != 0;
        public int GetHashCode(Obj o) => (int)LeanRt.lean_sharecommon_hash(o);
    }

    static readonly ExternalClass s_cls = new(_ => { }, null);
    static Maps Get(Obj o) => (Maps)((ExternalObj)o).m_data;

    // mapFind? (m : Map) (k : Object) : Option Object  -- owned args
    static Obj MapFind(Obj m, Obj k)
    {
        var r = Get(m).Map.TryGetValue(k, out var v) ? Some(v) : LeanRt.lean_box(0);
        LeanRt.lean_dec(m); LeanRt.lean_dec(k);
        return r;
    }
    static Obj MapInsert(Obj m, Obj k, Obj v) { Get(m).Map[k] = v; return m; }
    static Obj SetFind(Obj s, Obj o)
    {
        var r = Get(s).Set.TryGetValue(o, out var v) ? Some(v) : LeanRt.lean_box(0);
        LeanRt.lean_dec(s); LeanRt.lean_dec(o);
        return r;
    }
    static Obj SetInsert(Obj s, Obj o) { Get(s).Set[o] = o; return s; }

    static Obj Some(Obj v) { LeanRt.lean_inc(v); return LeanRt.lean_mk_option_some(v); }

    public static Obj Run(Obj a)
    {
        var tc = LeanRt.lean_alloc_ctor(0, 5, 0);
        LeanRt.lean_ctor_set(tc, 0, LeanRt.lean_box(0));
        LeanRt.lean_ctor_set(tc, 1, LeanRt.lean_alloc_closure((delegate*<Obj, Obj, Obj>)&MapFind, 2, 0));
        LeanRt.lean_ctor_set(tc, 2, LeanRt.lean_alloc_closure((delegate*<Obj, Obj, Obj, Obj>)&MapInsert, 3, 0));
        LeanRt.lean_ctor_set(tc, 3, LeanRt.lean_alloc_closure((delegate*<Obj, Obj, Obj>)&SetFind, 2, 0));
        LeanRt.lean_ctor_set(tc, 4, LeanRt.lean_alloc_closure((delegate*<Obj, Obj, Obj>)&SetInsert, 2, 0));
        var maps = new Maps();
        var m = new ExternalObj { m_tag = LeanRt.LeanExternal, m_class = s_cls, m_data = maps };
        LeanRt.lean_inc(m);
        var st = LeanRt.lean_mk_pair(m, m);
        var res = LeanRt.lean_state_sharecommon(tc, st, a);
        return LeanRt.lean_ctor_get(res, 0);
    }
}
