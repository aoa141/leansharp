// Finite State Entropy: table descriptions, decoding tables, encoding tables.
using System.Runtime.CompilerServices;

namespace LeanSharp.Zstd;

internal struct FseDEntry
{
    public ushort NewState;
    public byte Symbol;
    public byte NbBits;
}

/// <summary>Decoding table for one sequence symbol type, with baselines and extra bits folded in.</summary>
internal struct SeqEntry
{
    public uint Base;
    public ushort NewState;
    public byte NbBits;
    public byte ExtraBits;
}

internal sealed class SeqTable
{
    public SeqEntry[] E;
    public int Log;

    /// <param name="bases">baselines per symbol, or null for offset codes</param>
    public static SeqTable Build(short[] norm, int maxSym, int log, uint[] bases, byte[] bits)
    {
        var fse = new FseDEntry[1 << log];
        Fse.BuildDTable(norm, maxSym, log, fse);
        var e = new SeqEntry[fse.Length];
        for (int i = 0; i < e.Length; i++)
        {
            int s = fse[i].Symbol;
            e[i].NewState = fse[i].NewState;
            e[i].NbBits = fse[i].NbBits;
            if (bases != null) { e[i].Base = bases[s]; e[i].ExtraBits = bits[s]; }
            else { e[i].Base = 1u << s; e[i].ExtraBits = (byte)s; }
        }
        return new SeqTable { E = e, Log = log };
    }

    public static SeqTable Rle(int sym, uint[] bases, byte[] bits)
    {
        var e = new SeqEntry[1];
        if (bases != null) { e[0].Base = bases[sym]; e[0].ExtraBits = bits[sym]; }
        else { e[0].Base = 1u << sym; e[0].ExtraBits = (byte)sym; }
        return new SeqTable { E = e, Log = 0 };
    }
}

internal sealed class FseCTable
{
    public int Log;
    public ushort[] StateTable;
    public int[] DeltaNbBits;
    public int[] DeltaFindState;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Init(int sym)
    {
        int d = DeltaNbBits[sym];
        int nb = (d + (1 << 15)) >> 16;
        int value = (nb << 16) - d;
        return StateTable[(value >> nb) + DeltaFindState[sym]];
    }
}

/// <summary>An FSE distribution usable by the encoder (for "repeat" mode bookkeeping).</summary>
internal sealed class FseTableState
{
    public short[] Norm;
    public int Log;
    public int MaxSym;
    public FseCTable CT;

    public static FseTableState Create(short[] norm, int maxSym, int log)
    {
        return new FseTableState { Norm = norm, Log = log, MaxSym = maxSym, CT = Fse.BuildCTable(norm, maxSym, log) };
    }
}

/// <summary>Forward (LSB first) bit writer whose output is read backward by the decoder.</summary>
internal struct BitWriter
{
    public byte[] Buf;
    public int Pos;
    ulong _acc;
    int _n;

    public BitWriter(byte[] buf, int pos) { Buf = buf; Pos = pos; _acc = 0; _n = 0; }

    /// <summary>Adds up to 32 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(uint value, int nbBits)
    {
        _acc |= ((ulong)value & ((1UL << nbBits) - 1)) << _n;
        _n += nbBits;
        if (_n >= 32)
        {
            Zc.Write32(Buf, Pos, (uint)_acc);
            Pos += 4;
            _acc >>= 32;
            _n -= 32;
        }
    }

    /// <summary>Writes the end mark and flushes; returns the end position.</summary>
    public int Close()
    {
        _acc |= 1UL << _n;
        _n++;
        while (_n > 0)
        {
            Buf[Pos++] = (byte)_acc;
            _acc >>= 8;
            _n -= 8;
        }
        return Pos;
    }
}

internal static class Fse
{
    static uint PeekBits(ReadOnlySpan<byte> s, int bitPos, int n)
    {
        int i = bitPos >> 3;
        ulong v = 0;
        for (int k = 0; k < 5; k++)
            if (i + k < s.Length) v |= (ulong)s[i + k] << (8 * k);
        return (uint)((v >> (bitPos & 7)) & ((1UL << n) - 1));
    }

    /// <summary>
    /// Reads an FSE table description. <paramref name="maxSymbol"/> is the largest symbol allowed on
    /// input and the largest symbol present on output. Returns the number of bytes consumed.
    /// </summary>
    public static int ReadNCount(ReadOnlySpan<byte> src, short[] norm, ref int maxSymbol, int maxLog, out int tableLog)
    {
        if (src.Length < 1) throw new ZstdException("truncated FSE table description");
        int bitPos = 0;
        int al = (int)PeekBits(src, bitPos, 4) + 5;
        bitPos += 4;
        if (al > maxLog) throw new ZstdException("FSE accuracy log too large");
        tableLog = al;
        int remaining = (1 << al) + 1;
        int threshold = 1 << al;
        int nbBits = al + 1;
        int sym = 0;
        bool prev0 = false;
        int totalBits = src.Length * 8;
        while (remaining > 1 && sym <= maxSymbol)
        {
            if (prev0)
            {
                while (true)
                {
                    int r = (int)PeekBits(src, bitPos, 2);
                    bitPos += 2;
                    if (bitPos > totalBits) throw new ZstdException("truncated FSE table description");
                    for (int k = 0; k < r && sym <= maxSymbol; k++) norm[sym++] = 0;
                    if (r != 3) break;
                    if (sym > maxSymbol) break;
                }
                if (sym > maxSymbol) throw new ZstdException("FSE table: too many symbols");
            }
            int max = (2 * threshold - 1) - remaining;
            int count;
            int low = (int)PeekBits(src, bitPos, nbBits - 1);
            if (low < max)
            {
                count = low;
                bitPos += nbBits - 1;
            }
            else
            {
                int v = (int)PeekBits(src, bitPos, nbBits);
                if (v >= threshold) v -= max;
                count = v;
                bitPos += nbBits;
            }
            if (bitPos > totalBits) throw new ZstdException("truncated FSE table description");
            count--;
            remaining -= count < 0 ? -count : count;
            norm[sym++] = (short)count;
            prev0 = count == 0;
            while (remaining < threshold) { nbBits--; threshold >>= 1; }
        }
        if (remaining != 1) throw new ZstdException("corrupt FSE table description");
        for (int i = sym; i < norm.Length; i++) norm[i] = 0;
        maxSymbol = sym - 1;
        return (bitPos + 7) >> 3;
    }

    public static void BuildDTable(short[] norm, int maxSym, int log, FseDEntry[] table)
    {
        int size = 1 << log;
        int high = size - 1;
        Span<ushort> next = stackalloc ushort[maxSym + 1];
        for (int s = 0; s <= maxSym; s++)
        {
            if (norm[s] == -1)
            {
                if (high < 0) throw new ZstdException("corrupt FSE table");
                table[high--].Symbol = (byte)s;
                next[s] = 1;
            }
            else next[s] = (ushort)norm[s];
        }
        int mask = size - 1, step = (size >> 1) + (size >> 3) + 3, pos = 0;
        for (int s = 0; s <= maxSym; s++)
        {
            for (int i = 0; i < norm[s]; i++)
            {
                table[pos].Symbol = (byte)s;
                pos = (pos + step) & mask;
                while (pos > high) pos = (pos + step) & mask;
            }
        }
        if (pos != 0) throw new ZstdException("corrupt FSE table");
        for (int u = 0; u < size; u++)
        {
            int s = table[u].Symbol;
            uint ns = next[s]++;
            int nb = log - Zc.HighBit(ns);
            table[u].NbBits = (byte)nb;
            table[u].NewState = (ushort)((ns << nb) - size);
        }
    }

    public static FseCTable BuildCTable(short[] norm, int maxSym, int log)
    {
        int size = 1 << log;
        var ct = new FseCTable
        {
            Log = log,
            StateTable = new ushort[size],
            DeltaNbBits = new int[maxSym + 1],
            DeltaFindState = new int[maxSym + 1],
        };
        Span<byte> tableSymbol = stackalloc byte[size];
        Span<int> cumul = stackalloc int[maxSym + 2];
        int high = size - 1;
        cumul[0] = 0;
        for (int u = 1; u <= maxSym + 1; u++)
        {
            if (norm[u - 1] == -1)
            {
                cumul[u] = cumul[u - 1] + 1;
                tableSymbol[high--] = (byte)(u - 1);
            }
            else cumul[u] = cumul[u - 1] + norm[u - 1];
        }
        int mask = size - 1, step = (size >> 1) + (size >> 3) + 3, pos = 0;
        for (int s = 0; s <= maxSym; s++)
        {
            for (int i = 0; i < norm[s]; i++)
            {
                tableSymbol[pos] = (byte)s;
                pos = (pos + step) & mask;
                while (pos > high) pos = (pos + step) & mask;
            }
        }
        for (int u = 0; u < size; u++)
        {
            int s = tableSymbol[u];
            ct.StateTable[cumul[s]++] = (ushort)(size + u);
        }
        int total = 0;
        for (int s = 0; s <= maxSym; s++)
        {
            int n = norm[s];
            if (n == 0)
            {
                ct.DeltaNbBits[s] = ((log + 1) << 16) - (1 << log);
            }
            else if (n == -1 || n == 1)
            {
                ct.DeltaNbBits[s] = (log << 16) - (1 << log);
                ct.DeltaFindState[s] = total - 1;
                total++;
            }
            else
            {
                int maxBitsOut = log - Zc.HighBit((uint)(n - 1));
                int minStatePlus = n << maxBitsOut;
                ct.DeltaNbBits[s] = (maxBitsOut << 16) - minStatePlus;
                ct.DeltaFindState[s] = total - n;
                total += n;
            }
        }
        return ct;
    }

    /// <summary>Encoding table of a single-symbol (RLE) distribution: zero bits per symbol.</summary>
    public static FseCTable BuildRleCTable(int sym)
    {
        return new FseCTable
        {
            Log = 0,
            StateTable = new ushort[1],
            DeltaNbBits = new int[sym + 1],
            DeltaFindState = new int[sym + 1],
        };
    }

    public static int OptimalTableLog(int maxLog, int total, int maxSym)
    {
        int maxBitsSrc = Zc.HighBit((uint)(total - 1)) - 2;
        int minBits = Math.Min(Zc.HighBit((uint)total) + 1, Zc.HighBit((uint)Math.Max(maxSym, 1)) + 2);
        int log = maxLog;
        if (maxBitsSrc < log) log = maxBitsSrc;
        if (minBits > log) log = minBits;
        if (log < 5) log = 5;
        if (log > maxLog) log = maxLog;
        return log;
    }

    /// <summary>
    /// Normalizes a histogram to a distribution summing to 2^log, every present symbol getting
    /// at least one slot (rare symbols use the "less than one" probability -1). Integer only.
    /// </summary>
    public static void Normalize(ReadOnlySpan<int> count, int maxSym, int total, int log, short[] norm)
    {
        int size = 1 << log;
        int lowThreshold = total >> log;
        int sum = 0;
        for (int s = 0; s <= maxSym; s++)
        {
            int c = count[s];
            if (c == 0) norm[s] = 0;
            else if (c <= lowThreshold) { norm[s] = -1; sum++; }
            else
            {
                int p = (int)((long)c * size / total);
                if (p < 1) p = 1;
                norm[s] = (short)p;
                sum += p;
            }
        }
        for (int s = maxSym + 1; s < norm.Length; s++) norm[s] = 0;
        int diff = size - sum;
        while (diff > 0)
        {
            // give a slot to the most under-represented symbol (largest count/norm)
            int best = -1;
            long bc = 0, bn = 1;
            for (int s = 0; s <= maxSym; s++)
            {
                if (count[s] == 0) continue;
                long n = norm[s] < 0 ? 1 : norm[s];
                // gain of going n -> n+1 is about count/(n + 1/2); compare count/(2n+1)
                long c = count[s], d = 2 * n + 1;
                if (best < 0 || c * bn > bc * d) { best = s; bc = c; bn = d; }
            }
            norm[best] = (short)((norm[best] < 0 ? 1 : norm[best]) + 1);
            diff--;
        }
        while (diff < 0)
        {
            int best = -1;
            long bc = 0, bn = 1;
            for (int s = 0; s <= maxSym; s++)
            {
                if (norm[s] < 2) continue;
                long c = count[s], d = 2 * (long)norm[s] - 1;
                if (best < 0 || c * bn < bc * d) { best = s; bc = c; bn = d; }
            }
            norm[best]--;
            diff++;
        }
    }

    /// <summary>Writes an FSE table description; returns the number of bytes written.</summary>
    public static int WriteNCount(byte[] dst, int pos, short[] norm, int maxSym, int log)
    {
        int start = pos;
        ulong acc = 0;
        int nb = 0;
        int size = 1 << log;
        acc |= (ulong)(log - 5) << nb; nb += 4;
        int remaining = size + 1, threshold = size, nbBits = log + 1;
        int symbol = 0, alphabet = maxSym + 1;
        bool prev0 = false;
        while (symbol < alphabet && remaining > 1)
        {
            if (prev0)
            {
                int st = symbol;
                while (symbol < alphabet && norm[symbol] == 0) symbol++;
                if (symbol == alphabet) break;
                while (symbol >= st + 3)
                {
                    st += 3;
                    acc |= 3UL << nb; nb += 2;
                    while (nb >= 8) { dst[pos++] = (byte)acc; acc >>= 8; nb -= 8; }
                }
                acc |= (ulong)(symbol - st) << nb; nb += 2;
                while (nb >= 8) { dst[pos++] = (byte)acc; acc >>= 8; nb -= 8; }
            }
            {
                int count = norm[symbol++];
                int max = (2 * threshold - 1) - remaining;
                remaining -= count < 0 ? -count : count;
                count++;
                if (count >= threshold) count += max;
                acc |= (ulong)(uint)count << nb;
                nb += nbBits;
                if (count < max) nb--;
                prev0 = count == 1;
                if (remaining < 1) throw new InvalidOperationException("FSE normalization error");
                while (remaining < threshold) { nbBits--; threshold >>= 1; }
            }
            // keep only the valid low bits (a short count leaves one stale bit above nb)
            acc &= (1UL << nb) - 1;
            while (nb >= 8) { dst[pos++] = (byte)acc; acc >>= 8; nb -= 8; }
        }
        if (remaining != 1) throw new InvalidOperationException("FSE normalization error");
        if (nb > 0) dst[pos++] = (byte)acc;
        return pos - start;
    }

    /// <summary>Fixed point (8 fractional bits) cost in bits of a symbol with the given normalized count.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int BitCost(int norm, int log)
    {
        uint n = norm < 0 ? 1u : (uint)norm;
        int hb = Zc.HighBit(n);
        int log2 = hb * 256 + (int)(((n << 8) >> hb) - 256);
        return log * 256 - log2;
    }
}
