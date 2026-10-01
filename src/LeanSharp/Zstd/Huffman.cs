// Huffman coding of literals: table descriptions, decoding and encoding.
namespace LeanSharp.Zstd;

internal sealed class HufDTable
{
    public int Log;
    /// <summary>symbol | nbBits &lt;&lt; 8, indexed by the next <see cref="Log"/> bits.</summary>
    public ushort[] Table;
}

internal sealed class HufCTable
{
    public readonly byte[] Lens = new byte[256];
    public readonly ushort[] Codes = new ushort[256];
    public int MaxBits;
    public int MaxSym;
}

internal static unsafe class Huffman
{
    public const int MaxTableLog = 12;      // accepted when decoding
    public const int EncTableLog = 11;      // produced when encoding (format limit)

    // ---------------------------------------------------------------- decoding

    /// <summary>Reads a Huffman tree description. <paramref name="lens"/> (256 entries) optionally receives code lengths.</summary>
    public static HufDTable ReadTable(ReadOnlySpan<byte> src, out int consumed, byte[] lens, out int tableLogOut)
    {
        if (src.Length < 1) throw new ZstdException("truncated Huffman table");
        Span<byte> w = stackalloc byte[256];
        int hb = src[0];
        int n;
        if (hb >= 128)
        {
            n = hb - 127;
            int bytes = (n + 1) / 2;
            if (src.Length < 1 + bytes) throw new ZstdException("truncated Huffman table");
            for (int i = 0; i < n; i++)
            {
                int b = src[1 + i / 2];
                w[i] = (byte)((i & 1) == 0 ? b >> 4 : b & 15);
            }
            consumed = 1 + bytes;
        }
        else
        {
            if (hb == 0 || src.Length < 1 + hb) throw new ZstdException("truncated Huffman table");
            n = DecodeWeights(src.Slice(1, hb), w);
            consumed = 1 + hb;
        }
        uint total = 0;
        Span<int> rank = stackalloc int[MaxTableLog + 2];
        rank.Clear();
        for (int i = 0; i < n; i++)
        {
            if (w[i] > MaxTableLog) throw new ZstdException("corrupt Huffman table (weight)");
            rank[w[i]]++;
            total += (1u << w[i]) >> 1;
        }
        if (total == 0) throw new ZstdException("corrupt Huffman table");
        int tableLog = Zc.HighBit(total) + 1;
        if (tableLog > MaxTableLog) throw new ZstdException("corrupt Huffman table (log)");
        uint rest = (1u << tableLog) - total;
        if ((rest & (rest - 1)) != 0) throw new ZstdException("corrupt Huffman table (not a power of 2)");
        int last = Zc.HighBit(rest) + 1;
        w[n] = (byte)last;
        rank[last]++;
        n++;
        if (rank[1] < 2 || (rank[1] & 1) != 0) throw new ZstdException("corrupt Huffman table");

        var table = new ushort[1 << tableLog];
        Span<int> start = stackalloc int[MaxTableLog + 2];
        int run = 0;
        for (int r = 1; r <= tableLog; r++) { start[r] = run; run += rank[r] << (r - 1); }
        if (lens != null) Array.Clear(lens);
        for (int s = 0; s < n; s++)
        {
            int wt = w[s];
            if (wt == 0) continue;
            int len = 1 << (wt - 1);
            int nb = tableLog + 1 - wt;
            ushort e = (ushort)(s | (nb << 8));
            table.AsSpan(start[wt], len).Fill(e);
            start[wt] += len;
            if (lens != null) lens[s] = (byte)nb;
        }
        tableLogOut = tableLog;
        return new HufDTable { Log = tableLog, Table = table };
    }

    static uint PeekBack(ReadOnlySpan<byte> s, int bitPos, int n)
    {
        // bits [bitPos, bitPos+n) of the little-endian stream; bits below 0 read as zero
        if (n == 0) return 0;
        int shiftUp = 0;
        if (bitPos < 0)
        {
            shiftUp = -bitPos;
            if (shiftUp >= n) return 0;
            n -= shiftUp;
            bitPos = 0;
        }
        int i = bitPos >> 3;
        ulong v = 0;
        for (int k = 0; k < 4; k++)
            if (i + k < s.Length) v |= (ulong)s[i + k] << (8 * k);
        return (uint)(((v >> (bitPos & 7)) & ((1UL << n) - 1)) << shiftUp);
    }

    /// <summary>FSE-compressed Huffman weights (two interleaved states). Returns the number of weights.</summary>
    static int DecodeWeights(ReadOnlySpan<byte> src, Span<byte> w)
    {
        short[] norm = new short[256];
        int maxSym = 255;
        int used = Fse.ReadNCount(src, norm, ref maxSym, 6, out int log);
        var dt = new FseDEntry[1 << log];
        Fse.BuildDTable(norm, maxSym, log, dt);
        var bs = src.Slice(used);
        if (bs.Length == 0 || bs[bs.Length - 1] == 0) throw new ZstdException("corrupt Huffman weights");
        int bitPos = (bs.Length - 1) * 8 + Zc.HighBit(bs[bs.Length - 1]);
        bitPos -= log; int s1 = (int)PeekBack(bs, bitPos, log);
        bitPos -= log; int s2 = (int)PeekBack(bs, bitPos, log);
        if (bitPos < 0) throw new ZstdException("corrupt Huffman weights");
        int n = 0;
        while (true)
        {
            if (n >= 254) throw new ZstdException("corrupt Huffman weights (too many)");
            w[n++] = dt[s1].Symbol;
            int nb = dt[s1].NbBits;
            bitPos -= nb;
            s1 = dt[s1].NewState + (int)PeekBack(bs, bitPos, nb);
            if (bitPos < 0) { w[n++] = dt[s2].Symbol; break; }
            if (n >= 254) throw new ZstdException("corrupt Huffman weights (too many)");
            w[n++] = dt[s2].Symbol;
            nb = dt[s2].NbBits;
            bitPos -= nb;
            s2 = dt[s2].NewState + (int)PeekBack(bs, bitPos, nb);
            if (bitPos < 0) { w[n++] = dt[s1].Symbol; break; }
        }
        return n;
    }

    static ulong LoadTop(byte* p, long bitPos)
    {
        if (bitPos <= 0) return 0;
        long hi = (bitPos - 1) >> 3;
        ulong v = 0;
        for (int i = 0; i < 8; i++)
        {
            long bi = hi - i;
            if (bi < 0) break;
            v |= (ulong)p[bi] << (56 - 8 * i);
        }
        return v << (int)((hi + 1) * 8 - bitPos);
    }

    public static void DecodeStream(ReadOnlySpan<byte> src, Span<byte> dst, HufDTable t)
    {
        int len = src.Length;
        if (len == 0) throw new ZstdException("corrupt literals (empty stream)");
        byte lastByte = src[len - 1];
        if (lastByte == 0) throw new ZstdException("corrupt literals (no end mark)");
        long bitPos = (long)(len - 1) * 8 + Zc.HighBit(lastByte);
        int n = dst.Length, o = 0;
        int shift = 64 - t.Log;
        fixed (byte* p = src)
        fixed (byte* d = dst)
        fixed (ushort* tb = t.Table)
        {
            while (o + 4 <= n && bitPos >= 64)
            {
                long bi = ((bitPos - 1) >> 3) - 7;
                ulong v = Zc.Read64(p + bi) << (int)((bi + 8) * 8 - bitPos);
                uint x = tb[v >> shift]; d[o] = (byte)x; int nb = (int)(x >> 8); v <<= nb; int used = nb;
                x = tb[v >> shift]; d[o + 1] = (byte)x; nb = (int)(x >> 8); v <<= nb; used += nb;
                x = tb[v >> shift]; d[o + 2] = (byte)x; nb = (int)(x >> 8); v <<= nb; used += nb;
                x = tb[v >> shift]; d[o + 3] = (byte)x; used += (int)(x >> 8);
                o += 4;
                bitPos -= used;
            }
            while (o < n)
            {
                ulong v = LoadTop(p, bitPos);
                uint x = tb[v >> shift];
                d[o++] = (byte)x;
                bitPos -= x >> 8;
                if (bitPos < 0) throw new ZstdException("corrupt literals (stream too short)");
            }
        }
        if (bitPos != 0) throw new ZstdException("corrupt literals (stream not fully consumed)");
    }

    public static void Decode4(ReadOnlySpan<byte> src, Span<byte> dst, HufDTable t)
    {
        if (src.Length < 10) throw new ZstdException("corrupt literals (jump table)");
        int s1 = src[0] | (src[1] << 8), s2 = src[2] | (src[3] << 8), s3 = src[4] | (src[5] << 8);
        long s4 = (long)src.Length - 6 - s1 - s2 - s3;
        if (s4 < 1) throw new ZstdException("corrupt literals (jump table)");
        int seg = (dst.Length + 3) / 4;
        if (seg * 3 > dst.Length) throw new ZstdException("corrupt literals (too few for 4 streams)");
        int p = 6;
        DecodeStream(src.Slice(p, s1), dst.Slice(0, seg), t); p += s1;
        DecodeStream(src.Slice(p, s2), dst.Slice(seg, seg), t); p += s2;
        DecodeStream(src.Slice(p, s3), dst.Slice(2 * seg, seg), t); p += s3;
        DecodeStream(src.Slice(p, (int)s4), dst.Slice(3 * seg), t);
    }

    // ---------------------------------------------------------------- encoding

    /// <summary>Assigns canonical codes matching the decoder's table layout from code lengths.</summary>
    public static void AssignCodes(HufCTable t)
    {
        int maxBits = 0, maxSym = 0;
        for (int s = 0; s < 256; s++)
            if (t.Lens[s] != 0) { maxSym = s; if (t.Lens[s] > maxBits) maxBits = t.Lens[s]; }
        t.MaxBits = maxBits;
        t.MaxSym = maxSym;
        Span<int> rank = stackalloc int[MaxTableLog + 2];
        rank.Clear();
        for (int s = 0; s <= maxSym; s++)
            if (t.Lens[s] != 0) rank[maxBits + 1 - t.Lens[s]]++;
        Span<int> start = stackalloc int[MaxTableLog + 2];
        int run = 0;
        for (int w = 1; w <= maxBits; w++) { start[w] = run; run += rank[w] << (w - 1); }
        for (int s = 0; s <= maxSym; s++)
        {
            int l = t.Lens[s];
            if (l == 0) { t.Codes[s] = 0; continue; }
            int w = maxBits + 1 - l;
            t.Codes[s] = (ushort)(start[w] >> (w - 1));
            start[w] += 1 << (w - 1);
        }
    }

    /// <summary>Builds a length-limited Huffman code for a histogram with at least two symbols present.</summary>
    public static HufCTable Build(ReadOnlySpan<int> count, int maxSym)
    {
        Span<long> keys = stackalloc long[256];
        int n = 0;
        for (int s = 0; s <= maxSym; s++)
            if (count[s] > 0) keys[n++] = ((long)count[s] << 8) | (uint)s;
        keys = keys.Slice(0, n);
        keys.Sort();
        Span<long> wt = stackalloc long[2 * n];
        Span<int> parent = stackalloc int[2 * n];
        for (int i = 0; i < n; i++) wt[i] = keys[i] >> 8;
        int li = 0, ii = n, next = n;
        while (next < 2 * n - 1)
        {
            int a = (li < n && (ii >= next || wt[li] <= wt[ii])) ? li++ : ii++;
            int b = (li < n && (ii >= next || wt[li] <= wt[ii])) ? li++ : ii++;
            wt[next] = wt[a] + wt[b];
            parent[a] = next;
            parent[b] = next;
            next++;
        }
        Span<int> depth = stackalloc int[2 * n];
        depth[2 * n - 2] = 0;
        int maxLen = 0;
        for (int i = 2 * n - 3; i >= 0; i--)
        {
            depth[i] = depth[parent[i]] + 1;
            if (i < n && depth[i] > maxLen) maxLen = depth[i];
        }
        if (maxLen > EncTableLog)
        {
            const int L = EncTableLog;
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                if (depth[i] > L) depth[i] = L;
                k += 1 << (L - depth[i]);
            }
            while (k > (1 << L))
            {
                // lengthen the least frequent symbol that is not yet at the limit
                int i = 0;
                while (depth[i] >= L) i++;
                depth[i]++;
                k -= 1 << (L - depth[i]);
            }
            int left = (1 << L) - k;
            while (left > 0)
            {
                for (int i = n - 1; i >= 0 && left > 0; i--)
                {
                    int gain = 1 << (L - depth[i]);
                    if (depth[i] > 1 && gain <= left) { depth[i]--; left -= gain; }
                }
            }
        }
        var t = new HufCTable();
        for (int i = 0; i < n; i++) t.Lens[(int)(keys[i] & 255)] = (byte)depth[i];
        AssignCodes(t);
        return t;
    }

    /// <summary>Writes the tree description; returns its size or -1 when it cannot be represented.</summary>
    public static int WriteTable(HufCTable t, byte[] dst, int pos)
    {
        int n = t.MaxSym;   // number of weights written (the last symbol's weight is implied)
        Span<byte> w = stackalloc byte[256];
        for (int s = 0; s < n; s++) w[s] = (byte)(t.Lens[s] == 0 ? 0 : t.MaxBits + 1 - t.Lens[s]);
        int fse = n > 1 ? CompressWeights(w.Slice(0, n), dst, pos + 1) : 0;
        if (fse > 1 && fse < n / 2)
        {
            dst[pos] = (byte)fse;
            return 1 + fse;
        }
        if (n > 128) return -1;
        dst[pos] = (byte)(127 + n);
        w[n] = 0;
        for (int i = 0; i < n; i += 2) dst[pos + 1 + i / 2] = (byte)((w[i] << 4) | w[i + 1]);
        return 1 + (n + 1) / 2;
    }

    /// <summary>FSE-compresses Huffman weights; returns 0 when not worthwhile.</summary>
    static int CompressWeights(ReadOnlySpan<byte> w, byte[] dst, int pos)
    {
        int n = w.Length;
        Span<int> count = stackalloc int[13];
        count.Clear();
        int maxW = 0, maxCount = 0;
        for (int i = 0; i < n; i++)
        {
            int c = ++count[w[i]];
            if (w[i] > maxW) maxW = w[i];
            if (c > maxCount) maxCount = c;
        }
        if (maxCount == n || maxCount == 1) return 0;
        int log = Fse.OptimalTableLog(6, n, maxW);
        short[] norm = new short[13];
        Fse.Normalize(count, maxW, n, log, norm);
        int start = pos;
        pos += Fse.WriteNCount(dst, pos, norm, maxW, log);
        FseCTable ct = Fse.BuildCTable(norm, maxW, log);
        var bw = new BitWriter(dst, pos);
        int st1 = 0, st2 = 0;
        bool init1 = false, init2 = false;
        for (int i = n - 1; i >= 0; i--)
        {
            int sym = w[i];
            if ((i & 1) == 0)
            {
                if (!init1) { st1 = ct.Init(sym); init1 = true; }
                else
                {
                    int nb = (st1 + ct.DeltaNbBits[sym]) >> 16;
                    bw.Add((uint)st1, nb);
                    st1 = ct.StateTable[(st1 >> nb) + ct.DeltaFindState[sym]];
                }
            }
            else
            {
                if (!init2) { st2 = ct.Init(sym); init2 = true; }
                else
                {
                    int nb = (st2 + ct.DeltaNbBits[sym]) >> 16;
                    bw.Add((uint)st2, nb);
                    st2 = ct.StateTable[(st2 >> nb) + ct.DeltaFindState[sym]];
                }
            }
        }
        bw.Add((uint)st2, log);
        bw.Add((uint)st1, log);
        return bw.Close() - start;
    }

    /// <summary>Encodes one Huffman stream; returns the end position.</summary>
    public static int EncodeStream(byte* src, int n, HufCTable t, byte[] dst, int pos)
    {
        ulong acc = 0;
        int nb = 0;
        fixed (byte* lens = t.Lens)
        fixed (ushort* codes = t.Codes)
        fixed (byte* d = dst)
        {
            if (pos + (long)n * 2 + 16 > dst.Length) throw new InvalidOperationException("zstd encoder: scratch too small");
            for (int i = n - 1; i >= 0; i--)
            {
                int s = src[i];
                acc |= (ulong)codes[s] << nb;
                nb += lens[s];
                if (nb >= 32)
                {
                    System.Runtime.CompilerServices.Unsafe.WriteUnaligned(d + pos, BitConverter.IsLittleEndian ? (uint)acc : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((uint)acc));
                    pos += 4;
                    acc >>= 32;
                    nb -= 32;
                }
            }
            acc |= 1UL << nb;
            nb++;
            while (nb > 0) { d[pos++] = (byte)acc; acc >>= 8; nb -= 8; }
        }
        return pos;
    }
}
