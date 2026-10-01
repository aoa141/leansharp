// Managed port of the CaDiCaL SAT solver (version 2.1.2, as vendored by Lean 4).
// Utility types replacing the C++ standard library containers and small helpers
// from 'util.hpp', 'random.hpp', 'ema.hpp', 'reluctant.hpp', 'reap.hpp', 'heap.hpp',
// 'radix.hpp' and 'queue.hpp'.

using System.Runtime.CompilerServices;
using System.Numerics;

namespace LeanSharp.Runtime.Cadical;

/// <summary>Minimal growable array (replacement for 'std::vector'). Elements are
/// accessible by reference; 'a' is the backing array and 'n' the size.</summary>
public sealed class Vec<T>
{
    public T[] a;
    public int n;

    static readonly T[] s_empty = new T[0];

    public Vec() { a = s_empty; }
    public Vec(int capacity) { a = capacity == 0 ? s_empty : new T[capacity]; }

    public ref T this[int i]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref a[i];
    }
    public ref T this[long i]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref a[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int size() => n;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool empty() => n == 0;
    public int capacity() => a.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void push_back(T x)
    {
        if (n == a.Length) Grow(n + 1);
        a[n++] = x;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Grow(int min)
    {
        int cap = a.Length == 0 ? 4 : a.Length * 2;
        if (cap < min) cap = min;
        Array.Resize(ref a, cap);
    }

    public void reserve(int cap)
    {
        if (cap > a.Length) Array.Resize(ref a, cap);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T pop_back()
    {
        T x = a[--n];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) a[n] = default;
        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T back() => ref a[n - 1];

    public void clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && n > 0) Array.Clear(a, 0, n);
        n = 0;
    }

    /// <summary>Resize to 'm' elements. New elements are default-initialized.</summary>
    public void resize(int m)
    {
        if (m > a.Length) Grow(m);
        if (m > n) Array.Clear(a, n, m - n);
        else if (m < n && RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(a, m, n - m);
        n = m;
    }

    public void resize(int m, T v)
    {
        if (m > a.Length) Grow(m);
        for (int i = n; i < m; i++) a[i] = v;
        if (m < n && RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(a, m, n - m);
        n = m;
    }

    /// <summary>Shrink to 'm' elements (m &lt;= n), like 'resize' for a smaller size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void shrink(int m)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && m < n) Array.Clear(a, m, n - m);
        n = m;
    }

    public void shrink_to_fit()
    {
        if (a.Length > n) Array.Resize(ref a, n);
    }

    /// <summary>Release all memory ('erase_vector').</summary>
    public void erase()
    {
        a = s_empty; n = 0;
    }

    public Span<T> AsSpan() => new Span<T>(a, 0, n);
    public Span<T> AsSpan(int start, int len) => new Span<T>(a, start, len);

    public void swap(Vec<T> other)
    {
        (a, other.a) = (other.a, a);
        (n, other.n) = (other.n, n);
    }

    public void assign(Vec<T> other)
    {
        if (a.Length < other.n) a = new T[other.n];
        else if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && n > other.n) Array.Clear(a, other.n, n - other.n);
        Array.Copy(other.a, a, other.n);
        n = other.n;
    }

    public void reverse() => Array.Reverse(a, 0, n);

    /// <summary>Remove element at position i preserving order.</summary>
    public void erase_at(int i)
    {
        Array.Copy(a, i + 1, a, i, n - i - 1);
        n--;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) a[n] = default;
    }

    public void insert_at(int i, T x)
    {
        if (n == a.Length) Grow(n + 1);
        Array.Copy(a, i, a, i + 1, n - i);
        a[i] = x;
        n++;
    }

    public T[] ToArray()
    {
        var r = new T[n];
        Array.Copy(a, r, n);
        return r;
    }

    public Enumerator GetEnumerator() => new Enumerator(this);

    public struct Enumerator
    {
        readonly T[] a; readonly int n; int i;
        public Enumerator(Vec<T> v) { a = v.a; n = v.n; i = -1; }
        public bool MoveNext() => ++i < n;
        public T Current => a[i];
    }
}

/// <summary>Bit vector replacement for 'std::vector&lt;bool&gt;'.</summary>
public sealed class BoolVec
{
    public bool[] a = new bool[0];
    public int n;
    public int size() => n;
    public bool empty() => n == 0;
    public bool this[int i] { get => a[i]; set => a[i] = value; }
    public void push_back(bool b)
    {
        if (n == a.Length) Array.Resize(ref a, Math.Max(4, 2 * a.Length));
        a[n++] = b;
    }
    public void resize(int m, bool v = false)
    {
        if (m > a.Length) Array.Resize(ref a, Math.Max(m, 2 * a.Length));
        for (int i = n; i < m; i++) a[i] = v;
        if (m < n) Array.Clear(a, m, n - m);
        n = m;
    }
    public void clear() { Array.Clear(a, 0, n); n = 0; }
}

public static class CUtil
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double relative(double a, double b) => b != 0 ? a / b : 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double percent(double a, double b) => relative(100 * a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int sign(int lit) => (lit > 0 ? 1 : 0) - (lit < 0 ? 1 : 0);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint bign(int lit) => 1u + (lit < 0 ? 1u : 0u);
    public static bool is_power_of_two(uint n) => n != 0 && (n & (n - 1)) == 0;
    public static bool contained(long c, long l, long u) => l <= c && c <= u;
    public static bool parity(uint a) => (BitOperations.PopCount(a) & 1) != 0;

    public static bool has_suffix(string str, string suffix) =>
        str.Length > suffix.Length && str.EndsWith(suffix, StringComparison.Ordinal);
    public static bool has_prefix(string str, string prefix) => str.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>'parse_int_str' from 'util.cpp'.</summary>
    public static bool parse_int_str(string val_str, out int val)
    {
        val = 0;
        if (val_str == "true") { val = 1; return true; }
        if (val_str == "false") { val = 0; return true; }
        int p = 0;
        int sign = 1;
        if (p < val_str.Length && val_str[p] == '-') { sign = -1; p++; }
        int Next() => p < val_str.Length ? val_str[p++] : (p++ == val_str.Length ? 0 : 0);
        int ch = Next();
        if (!(ch >= '0' && ch <= '9')) return false;
        long bound = -(long)int.MinValue;
        long mantissa = ch - '0';
        while ((ch = Next()) >= '0' && ch <= '9')
        {
            if (bound / 10 < mantissa) mantissa = bound; else mantissa *= 10;
            int digit = ch - '0';
            if (bound - digit < mantissa) mantissa = bound; else mantissa += digit;
        }
        int exponent = 0;
        if (ch == 'e')
        {
            while ((ch = Next()) >= '0' && ch <= '9')
                exponent = exponent != 0 ? 10 : ch - '0';
            if (ch != 0) return false;
        }
        else if (ch != 0) return false;
        long val64 = mantissa;
        for (int i = 0; i < exponent; i++) val64 *= 10;
        if (sign < 0) { val64 = -val64; if (val64 < int.MinValue) val64 = int.MinValue; }
        else if (val64 > int.MaxValue) val64 = int.MaxValue;
        val = (int)val64;
        return true;
    }

    public static bool is_color_option(string arg) => arg switch
    {
        "--color" or "--colors" or "--colour" or "--colours" or "--color=1" or "--colors=1" or "--colour=1"
            or "--colours=1" or "--color=true" or "--colors=true" or "--colour=true" or "--colours=true" => true,
        _ => false
    };

    public static bool is_no_color_option(string arg) => arg switch
    {
        "--no-color" or "--no-colors" or "--no-colour" or "--no-colours" or "--color=0" or "--colors=0"
            or "--colour=0" or "--colours=0" or "--color=false" or "--colors=false" or "--colour=false"
            or "--colours=false" => true,
        _ => false
    };

    static readonly ulong[] primes = {
        1111111111111111111ul, 2222222222222222249ul, 3333333333333333347ul,
        4444444444444444537ul, 5555555555555555621ul, 6666666666666666677ul,
        7777777777777777793ul, 8888888888888888923ul, 9999999999999999961ul,
    };

    public static ulong hash_string(string str)
    {
        ulong res = 0; int i = 0;
        foreach (char c in str)
        {
            res += (byte)c;
            res *= primes[i++];
            if (i == primes.Length) i = 0;
        }
        return res;
    }

    /// <summary>Stable sort of a span with a comparison (like 'std::stable_sort').</summary>
    public static void stable_sort<T>(Span<T> s, Comparison<T> less_cmp)
    {
        // merge sort (insertion sort for short arrays)
        int n = s.Length;
        if (n < 2) return;
        if (n <= 16)
        {
            for (int i = 1; i < n; i++)
            {
                T x = s[i]; int j = i - 1;
                while (j >= 0 && less_cmp(x, s[j]) < 0) { s[j + 1] = s[j]; j--; }
                s[j + 1] = x;
            }
            return;
        }
        var tmp = new T[n];
        MergeSort(s, tmp, less_cmp);
    }

    static void MergeSort<T>(Span<T> s, T[] tmp, Comparison<T> cmp)
    {
        int n = s.Length;
        if (n <= 16)
        {
            for (int i = 1; i < n; i++)
            {
                T x = s[i]; int j = i - 1;
                while (j >= 0 && cmp(x, s[j]) < 0) { s[j + 1] = s[j]; j--; }
                s[j + 1] = x;
            }
            return;
        }
        int mid = n / 2;
        MergeSort(s.Slice(0, mid), tmp, cmp);
        MergeSort(s.Slice(mid), tmp, cmp);
        if (cmp(s[mid], s[mid - 1]) >= 0) return;
        s.CopyTo(tmp);
        int a = 0, b = mid, k = 0;
        while (a < mid && b < n)
        {
            if (cmp(tmp[b], tmp[a]) < 0) s[k++] = tmp[b++];
            else s[k++] = tmp[a++];
        }
        while (a < mid) s[k++] = tmp[a++];
        while (b < n) s[k++] = tmp[b++];
    }

    /// <summary>Radix sort by an unsigned 64-bit rank (stable), as 'rsort' in 'radix.hpp'.</summary>
    public static void rsort<T>(Span<T> s, Func<T, ulong> rank)
    {
        int n = s.Length;
        if (n <= 1) return;
        var ranks = new ulong[n];
        ulong lower = ~0ul, upper = 0;
        for (int i = 0; i < n; i++) { ulong r = rank(s[i]); ranks[i] = r; lower &= r; upper |= r; }
        var tmpv = new T[n];
        var tmpr = new ulong[n];
        var count = new int[256];
        T[] srcV = null; ulong[] srcR = ranks;
        // copy span to array for ping-pong
        var arrV = s.ToArray();
        srcV = arrV;
        T[] dstV = tmpv; ulong[] dstR = tmpr;
        for (int shift = 0; shift < 64; shift += 8)
        {
            ulong mask = 0xFFul << shift;
            if ((lower & mask) == (upper & mask)) continue;
            Array.Clear(count);
            bool sorted = true; uint last = 0;
            for (int i = 0; i < n; i++)
            {
                uint m = (uint)((srcR[i] >> shift) & 0xFF);
                if (sorted && last > m) sorted = false; else last = m;
                count[m]++;
            }
            if (sorted) continue;
            int pos = 0;
            for (int j = 0; j < 256; j++) { int d = count[j]; count[j] = pos; pos += d; }
            for (int i = 0; i < n; i++)
            {
                uint m = (uint)((srcR[i] >> shift) & 0xFF);
                int k = count[m]++;
                dstV[k] = srcV[i]; dstR[k] = srcR[i];
            }
            (srcV, dstV) = (dstV, srcV);
            (srcR, dstR) = (dstR, srcR);
        }
        new Span<T>(srcV).CopyTo(s);
    }

    /// <summary>'MSORT': standard sort below the limit, radix sort above.</summary>
    public static void msort<T>(int limit, Span<T> s, Func<T, ulong> rank)
    {
        if (s.Length <= limit)
        {
            // std::sort with less-than on rank (not stable, but use stable for determinism)
            stable_sort(s, (x, y) => rank(x).CompareTo(rank(y)));
        }
        else rsort(s, rank);
    }
}

/// <summary>'random.hpp'</summary>
public struct CRandom
{
    ulong state;

    public CRandom(ulong seed) { state = seed; }
    public ulong seed() => state;
    public void add(ulong a)
    {
        if ((state += a) == 0) state = 1;
        next();
    }
    public ulong next()
    {
        state *= 6364136223846793005ul;
        state += 1442695040888963407ul;
        return state;
    }
    public uint generate() { next(); return (uint)(state >> 32); }
    public int generate_int() => (int)generate();
    public bool generate_bool() => generate() < 2147483648u;
    public double generate_double() => generate() / 4294967295.0;
    public int pick_int(int l, int r)
    {
        uint delta = 1u + (uint)r - (uint)l;
        uint tmp = generate(), scaled;
        if (delta != 0)
        {
            double fraction = tmp / 4294967296.0;
            scaled = (uint)(delta * fraction);
        }
        else scaled = tmp;
        return (int)(scaled + (uint)l);
    }
    public int pick_log(int l, int r)
    {
        uint delta = 1u + (uint)r - (uint)l;
        int log_delta = delta != 0 ? 0 : 32;
        while (log_delta < 32 && (1u << log_delta) < delta) log_delta++;
        int log_res = pick_int(0, log_delta);
        uint tmp = generate();
        if (log_res < 32) tmp &= (1u << log_res) - 1;
        if (delta != 0) tmp %= delta;
        return (int)(l + tmp);
    }
    public double pick_double(double l, double r) => (r - l) * generate_double() + l;
}

/// <summary>'ema.hpp'</summary>
public struct EMA
{
    public ulong updated;
    public double value, biased, alpha, beta, exp;

    public EMA(double a)
    {
        updated = 0; value = 0; biased = 0; alpha = a; beta = 1 - a; exp = beta != 0 ? 1 : 0;
    }

    public static implicit operator double(EMA e) => e.value;

    public void update(double y)
    {
        double old_biased = biased;
        double delta = y - old_biased;
        double scaled_delta = alpha * delta;
        double new_biased = old_biased + scaled_delta;
        biased = new_biased;
        double old_exp = exp;
        double new_value;
        if (old_exp != 0)
        {
            double new_exp = old_exp * beta;
            exp = new_exp;
            double div = 1 - new_exp;
            new_value = new_biased / div;
        }
        else new_value = new_biased;
        value = new_value;
    }

    public static EMA Init(int window) => new EMA(1.0 / window);
}

/// <summary>'averages.hpp'</summary>
public struct AveragesSet
{
    public EMA glue_fast, glue_slow, trail_fast, trail_slow, size, jump, level;
}

public sealed class Averages
{
    public long swapped;
    public AveragesSet current, saved;
}

/// <summary>'reluctant.hpp' (Luby sequence for stable mode restarts).</summary>
public sealed class Reluctant
{
    ulong u, v, limit;
    ulong period, countdown;
    bool trigger, limited;

    public void enable(int p, long l)
    {
        u = v = 1;
        period = countdown = (ulong)p;
        trigger = false;
        if (l <= 0) limited = false;
        else { limited = true; limit = (ulong)l; }
    }
    public void disable() { period = 0; trigger = false; }
    public void tick()
    {
        if (period == 0) return;
        if (trigger) return;
        if (--countdown != 0) return;
        if ((u & (0 - u)) == v) { u = u + 1; v = 1; }
        else v = 2 * v;
        if (limited && v >= limit) u = v = 1;
        countdown = v * period;
        trigger = true;
    }
    /// <summary>'operator bool' (consumes the trigger).</summary>
    public bool Triggered()
    {
        if (!trigger) return false;
        trigger = false;
        return true;
    }
}

/// <summary>Radix heap 'reap.hpp'.</summary>
public sealed class Reap
{
    int num_elements;
    uint last_deleted;
    uint min_bucket = 32;
    uint max_bucket;
    readonly Vec<uint>[] buckets = new Vec<uint>[33];

    public Reap() { for (int i = 0; i < 33; i++) buckets[i] = new Vec<uint>(); }

    public void init()
    {
        for (int i = 0; i < 33; i++) buckets[i].clear();
        min_bucket = 32;
    }
    public void release()
    {
        num_elements = 0; last_deleted = 0; min_bucket = 32; max_bucket = 0;
    }
    public bool empty() => num_elements == 0;
    public int size() => num_elements;

    static uint lz(uint x) => x != 0 ? (uint)BitOperations.LeadingZeroCount(x) : 32u;

    public void push(uint e)
    {
        uint diff = e ^ last_deleted;
        uint bucket = 32 - lz(diff);
        buckets[bucket].push_back(e);
        if (min_bucket > bucket) min_bucket = bucket;
        if (max_bucket < bucket) max_bucket = bucket;
        num_elements++;
    }

    public uint pop()
    {
        uint i = min_bucket;
        for (; ; )
        {
            var s = buckets[i];
            if (s.empty()) { min_bucket = ++i; continue; }
            uint res;
            if (i != 0)
            {
                res = uint.MaxValue;
                int q = 0;
                for (int p = 0; p < s.n; p++)
                {
                    uint tmp = s.a[p];
                    if (tmp >= res) continue;
                    res = tmp; q = p;
                }
                for (int p = 0; p < s.n; p++)
                {
                    if (p == q) continue;
                    uint other = s.a[p];
                    uint diff = other ^ res;
                    uint j = 32 - lz(diff);
                    buckets[j].push_back(other);
                    if (min_bucket > j) min_bucket = j;
                }
                s.clear();
                if (i != 0 && max_bucket == i)
                {
                    if (s.empty()) max_bucket = i - 1;
                }
            }
            else
            {
                res = last_deleted;
                buckets[0].pop_back();
            }
            if (min_bucket == i)
            {
                if (s.empty()) min_bucket = Math.Min(i + 1, 32u);
            }
            --num_elements;
            last_deleted = res;
            return res;
        }
    }

    public void clear()
    {
        for (int i = 0; i < 33; i++) buckets[i].clear();
        num_elements = 0; last_deleted = 0; min_bucket = 32; max_bucket = 0;
    }
}

/// <summary>Binary heap 'heap.hpp' with a pluggable comparison.</summary>
public sealed class Heap
{
    public const uint invalid_heap_position = uint.MaxValue;
    readonly Vec<uint> array = new Vec<uint>();
    readonly Vec<uint> pos = new Vec<uint>();
    /// <summary>less(a,b): a has lower priority than b.</summary>
    public Func<uint, uint, bool> less;

    public Heap(Func<uint, uint, bool> less) { this.less = less; }

    ref uint index(uint e)
    {
        if (e >= (uint)pos.n) pos.resize((int)e + 1, invalid_heap_position);
        return ref pos.a[e];
    }
    bool has_parent(uint e) => index(e) > 0;
    bool has_left(uint e) => 2L * index(e) + 1 < array.n;
    bool has_right(uint e) => 2L * index(e) + 2 < array.n;
    uint parent(uint e) => array.a[(index(e) - 1) / 2];
    uint left(uint e) => array.a[2 * index(e) + 1];
    uint right(uint e) => array.a[2 * index(e) + 2];
    void exchange(uint a, uint b)
    {
        ref uint i = ref index(a);
        ref uint j = ref index(b);
        (array.a[i], array.a[j]) = (array.a[j], array.a[i]);
        (i, j) = (j, i);
    }
    void up(uint e)
    {
        uint p;
        while (has_parent(e) && less((p = parent(e)), e)) exchange(p, e);
    }
    void down(uint e)
    {
        while (has_left(e))
        {
            uint c = left(e);
            if (has_right(e))
            {
                uint r = right(e);
                if (less(c, r)) c = r;
            }
            if (!less(e, c)) break;
            exchange(e, c);
        }
    }

    public int size() => array.n;
    public bool empty() => array.n == 0;
    public bool contains(uint e)
    {
        if (e >= (uint)pos.n) return false;
        return pos.a[e] != invalid_heap_position;
    }
    public void push_back(uint e)
    {
        int i = array.n;
        array.push_back(e);
        index(e) = (uint)i;
        up(e);
        down(e);
    }
    public uint front() => array.a[0];
    public uint pop_front()
    {
        uint res = array.a[0], last = array.a[array.n - 1];
        if (array.n > 1) exchange(res, last);
        index(res) = invalid_heap_position;
        array.pop_back();
        if (array.n > 1) down(last);
        return res;
    }
    public void update(uint e)
    {
        up(e);
        down(e);
    }
    public void clear() { array.clear(); pos.clear(); }
    public void erase() { array.erase(); pos.erase(); }
    public void shrink() { array.shrink_to_fit(); pos.shrink_to_fit(); }
    public Vec<uint> elements => array;
}

/// <summary>'queue.hpp'</summary>
public struct Link
{
    public int prev, next;
}

public sealed class CQueue
{
    public int first, last;
    public int unassigned;
    public long bumped;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void dequeue(Link[] links, int idx)
    {
        ref Link l = ref links[idx];
        if (l.prev != 0) links[l.prev].next = l.next; else first = l.next;
        if (l.next != 0) links[l.next].prev = l.prev; else last = l.prev;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void enqueue(Link[] links, int idx)
    {
        ref Link l = ref links[idx];
        if ((l.prev = last) != 0) links[last].next = idx; else first = idx;
        last = idx;
        l.next = 0;
    }
}

/// <summary>Exception used for fatal errors and API contract violations.</summary>
public sealed class CadicalException : Exception
{
    public CadicalException(string msg) : base(msg) { }
}
