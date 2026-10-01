// Zstandard frame encoder: hash-chain LZ77 with lazy matching, FSE-coded sequences,
// Huffman-coded literals, dictionary support. Deterministic (integer arithmetic only).
using System.Numerics;
using System.Runtime.CompilerServices;

namespace LeanSharp.Zstd;

public static class ZstdEncoder
{
    /// <summary>Compresses <paramref name="src"/> into one frame. <paramref name="level"/> is a hint (higher = try harder).</summary>
    public static byte[] Compress(ReadOnlySpan<byte> src, ZstdDictionary dictionary = null, int level = 19)
    {
        return new Enc(dictionary, level).Run(src);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Hash4(uint v, int log) => (v * 2654435761u) >> (32 - log);

    static readonly FseTableState DefLL = FseTableState.Create(Zc.LLDefaultNorm, Zc.MaxLL, Zc.LLDefaultLog);
    static readonly FseTableState DefML = FseTableState.Create(Zc.MLDefaultNorm, Zc.MaxML, Zc.MLDefaultLog);
    static readonly FseTableState DefOF = FseTableState.Create(Zc.OffDefaultNorm, 28, Zc.OffDefaultLog);

    static readonly byte[] LLCodeTable = MakeCodeTable(Zc.LLBase, 64, 0);
    static readonly byte[] MLCodeTable = MakeCodeTable(Zc.MLBase, 128, 3);

    static byte[] MakeCodeTable(uint[] bases, int n, int bias)
    {
        var t = new byte[n];
        for (int v = 0; v < n; v++)
        {
            int c = 0;
            while (c + 1 < bases.Length && bases[c + 1] <= (uint)(v + bias)) c++;
            t[v] = (byte)c;
        }
        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int LLCode(int ll) => ll < 64 ? LLCodeTable[ll] : Zc.HighBit((uint)ll) + 19;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int MLCode(int ml) { int b = ml - 3; return b < 128 ? MLCodeTable[b] : Zc.HighBit((uint)b) + 36; }

    const int CombinedCacheMax = 1 << 20;
    [ThreadStatic] static byte[] t_combined;
    [ThreadStatic] static ZstdDictionary t_combinedDict;

    sealed unsafe class Enc
    {
        readonly ZstdDictionary _dict;
        readonly int _depth, _lazy, _windowLogMax, _sufficient;

        // input (dictionary content followed by the source)
        byte* _b;
        int* _hp, _cp, _dhp, _dcp;
        int _dHashLog, _ownLow;
        /// <summary>Estimated cost of a literal in 1/16 bit (from the previous block), to reject short far matches.</summary>
        int _litCost = 104;
        int _total, _dictLen, _window;
        bool _single;

        // match finder
        int[] _head, _chain;
        int _hashLog, _chainMask, _chainSize, _nextToUpdate;
        readonly int[] _rep = new int[3];

        // sequence store of the current block
        int[] _seqLL, _seqML, _seqOff;
        byte[] _lit;
        int _nSeq, _nLit;
        byte[] _llc, _mlc, _ofc;

        // entropy state (committed = what the decoder has after the last emitted compressed block)
        HufCTable _huf, _hufPending;
        FseTableState _llPrev, _ofPrev, _mlPrev, _llPend, _ofPend, _mlPend;

        byte[] _scratch;
        byte[] _dst;
        int _dp;
        readonly int[] _count = new int[256];
        readonly short[] _normTmp = new short[64];
        readonly byte[] _hdrTmp = new byte[512];

        public Enc(ZstdDictionary dict, int level)
        {
            _dict = dict;
            // (search depth, lazy steps, max window log)
            if (level <= 1) { _depth = 2; _lazy = 0; _windowLogMax = 21; }
            else if (level == 2) { _depth = 3; _lazy = 0; _windowLogMax = 21; }
            else if (level == 3) { _depth = 4; _lazy = 1; _windowLogMax = 21; }
            else if (level == 4) { _depth = 6; _lazy = 1; _windowLogMax = 21; }
            else if (level <= 6) { _depth = 8; _lazy = 2; _windowLogMax = 22; }
            else if (level <= 9) { _depth = 16; _lazy = 2; _windowLogMax = 22; }
            else if (level <= 12) { _depth = 32; _lazy = 2; _windowLogMax = 23; }
            else if (level <= 15) { _depth = 48; _lazy = 2; _windowLogMax = 23; }
            else if (level <= 18) { _depth = 64; _lazy = 2; _windowLogMax = 23; }
            else if (level == 19) { _depth = 96; _lazy = 2; _windowLogMax = 23; }
            else { _depth = 256; _lazy = 2; _windowLogMax = 25; }
            _sufficient = level <= 4 ? 32 : level <= 12 ? 128 : 512;
        }

        public byte[] Run(ReadOnlySpan<byte> src)
        {
            int srcLen = src.Length;
            byte[] dictContent = _dict?.Content;
            _dictLen = dictContent == null ? 0 : dictContent.Length;
            if ((long)_dictLen + srcLen > int.MaxValue - 64) throw new ArgumentException("input too large");
            _total = _dictLen + srcLen;
            _single = srcLen <= (1 << _windowLogMax);
            _window = _single ? Math.Max(srcLen, 1) : 1 << _windowLogMax;

            _rep[0] = 1; _rep[1] = 4; _rep[2] = 8;
            if (_dict != null && _dict.HasEntropy)
            {
                _rep[0] = _dict.Rep[0]; _rep[1] = _dict.Rep[1]; _rep[2] = _dict.Rep[2];
                _huf = _dict.HufC;
                _llPrev = _dict.LLC; _ofPrev = _dict.OFC; _mlPrev = _dict.MLC;
            }

            int blockCap = Math.Min(srcLen, Zc.BlockMax);
            int maxSeq = blockCap / 4 + 2;
            _seqLL = new int[maxSeq]; _seqML = new int[maxSeq]; _seqOff = new int[maxSeq];
            _llc = new byte[maxSeq]; _mlc = new byte[maxSeq]; _ofc = new byte[maxSeq];
            _lit = new byte[blockCap + 8];
            _scratch = new byte[blockCap * 2 + maxSeq * 12 + 2048];
            _dst = new byte[Math.Min((long)srcLen + 64, Math.Max(srcLen / 3 + 4096, 64))];
            _dp = 0;

            // match finder tables (positions index the dictionary content followed by the source;
            // the dictionary has its own shared tables, see ZstdDictionary.GetPrepared)
            long reach = Math.Min(srcLen, _window);
            int chainLog = 1;
            while ((1L << chainLog) < reach) chainLog++;
            _chainSize = 1 << chainLog;
            _chainMask = _chainSize - 1;
            int srcLog = 1;
            while ((1L << srcLog) < srcLen) srcLog++;
            _hashLog = Math.Clamp(srcLog - 1, 8, 22);
            _ownLow = Math.Max(_dictLen, 1);
            _nextToUpdate = _ownLow;
            int[] dHead = null, dChain = null;
            if (srcLen >= 16)
            {
                _chain = GC.AllocateUninitializedArray<int>(_chainSize);
                _head = new int[1 << _hashLog];
                if (_dictLen >= 8)
                {
                    _dHashLog = 1;
                    while ((1 << _dHashLog) < _dictLen) _dHashLog++;
                    _dHashLog = Math.Clamp(_dHashLog, 8, 22);
                    _dict.GetPrepared(_dHashLog, out dHead, out dChain);
                }
            }

            WriteFrameHeader(srcLen);

            byte[] combined = null;
            if (_dictLen > 0)
            {
                if (srcLen <= CombinedCacheMax)
                {
                    // small inputs: reuse a per-thread buffer that already holds the dictionary content
                    if (t_combinedDict != _dict || t_combined.Length < _total)
                    {
                        t_combined = null;
                        t_combinedDict = null;
                        byte[] nb = GC.AllocateUninitializedArray<byte>(_dictLen + Math.Min(CombinedCacheMax, Math.Max(srcLen * 2, 1 << 16)));
                        dictContent.CopyTo(nb, 0);
                        t_combined = nb;
                        t_combinedDict = _dict;
                    }
                    combined = t_combined;
                }
                else
                {
                    combined = GC.AllocateUninitializedArray<byte>(_total);
                    dictContent.CopyTo(combined, 0);
                }
                src.CopyTo(combined.AsSpan(_dictLen));
            }
            fixed (byte* pc = combined)
            fixed (byte* ps = src)
            fixed (int* head = _head)
            fixed (int* chain = _chain)
            fixed (int* dh = dHead)
            fixed (int* dc = dChain)
            {
                _b = _dictLen > 0 ? pc : ps;
                _dhp = dh; _dcp = dc;
                int bs = _dictLen;
                Span<int> savedRep = stackalloc int[3];
                while (true)
                {
                    int blockLen = Math.Min(Zc.BlockMax, _total - bs);
                    bool last = bs + blockLen == _total;
                    int be = bs + blockLen;
                    int csize = -1;
                    if (blockLen >= 16)
                    {
                        savedRep[0] = _rep[0]; savedRep[1] = _rep[1]; savedRep[2] = _rep[2];
                        _nSeq = 0; _nLit = 0;
                        FindMatches(bs, be, head, chain);
                        csize = EncodeBlock();
                        if (csize >= blockLen) csize = -1;
                        if (csize < 0) { _rep[0] = savedRep[0]; _rep[1] = savedRep[1]; _rep[2] = savedRep[2]; }
                    }
                    if (csize < 0)
                    {
                        EnsureDst(3 + blockLen);
                        WriteBlockHeader(last, 0, blockLen);
                        new ReadOnlySpan<byte>(_b + bs, blockLen).CopyTo(_dst.AsSpan(_dp));
                        _dp += blockLen;
                    }
                    else
                    {
                        EnsureDst(3 + csize);
                        WriteBlockHeader(last, 2, csize);
                        Buffer.BlockCopy(_scratch, 0, _dst, _dp, csize);
                        _dp += csize;
                        _huf = _hufPending;
                        _llPrev = _llPend; _ofPrev = _ofPend; _mlPrev = _mlPend;
                    }
                    bs = be;
                    if (last) break;
                }
            }
            return _dst.AsSpan(0, _dp).ToArray();
        }

        void EnsureDst(int extra)
        {
            long need = (long)_dp + extra;
            if (need <= _dst.Length) return;
            if (need > Array.MaxLength) throw new InvalidOperationException("zstd encoder: output too large");
            long cap = Math.Min(Math.Max(need, (long)_dst.Length * 2), Array.MaxLength);
            Array.Resize(ref _dst, (int)cap);
        }

        void WriteBlockHeader(bool last, int type, int size)
        {
            int v = (last ? 1 : 0) | (type << 1) | (size << 3);
            _dst[_dp++] = (byte)v;
            _dst[_dp++] = (byte)(v >> 8);
            _dst[_dp++] = (byte)(v >> 16);
        }

        void WriteFrameHeader(int srcLen)
        {
            EnsureDst(18);
            Zc.Write32(_dst, _dp, Zc.FrameMagic);
            _dp += 4;
            uint id = _dict == null ? 0 : _dict.Id;
            int didFlag = id == 0 ? 0 : id < 256 ? 1 : id < 65536 ? 2 : 3;
            int fcsFlag;
            if (_single) fcsFlag = srcLen < 256 ? 0 : srcLen < 65536 + 256 ? 1 : 2;
            else fcsFlag = 2;
            _dst[_dp++] = (byte)((fcsFlag << 6) | (_single ? 0x20 : 0) | didFlag);
            if (!_single) _dst[_dp++] = (byte)((_windowLogMax - 10) << 3);
            int didSize = didFlag == 3 ? 4 : didFlag;
            for (int i = 0; i < didSize; i++) _dst[_dp++] = (byte)(id >> (8 * i));
            switch (fcsFlag)
            {
                case 0: _dst[_dp++] = (byte)srcLen; break;
                case 1: { int v = srcLen - 256; _dst[_dp++] = (byte)v; _dst[_dp++] = (byte)(v >> 8); break; }
                default: Zc.Write32(_dst, _dp, (uint)srcLen); _dp += 4; break;
            }
        }

        // ------------------------------------------------------------ match finder

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Count(byte* b, int p, int m, int end)
        {
            int start = p;
            while (p <= end - 8)
            {
                ulong x = Zc.Read64(b + p) ^ Zc.Read64(b + m);
                if (x != 0) return p - start + (BitOperations.TrailingZeroCount(x) >> 3);
                p += 8; m += 8;
            }
            while (p < end && b[p] == b[m]) { p++; m++; }
            return p - start;
        }

        /// <summary>Encodes an offset as an offset code value given the literal length; updates the repeat offsets.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int RepCode(int ll, int off)
        {
            int r0 = _rep[0], r1 = _rep[1], r2 = _rep[2];
            if (ll != 0)
            {
                if (off == r0) return 1;
                if (off == r1) { _rep[1] = r0; _rep[0] = off; return 2; }
                if (off == r2) { _rep[2] = r1; _rep[1] = r0; _rep[0] = off; return 3; }
            }
            else
            {
                if (off == r1) { _rep[1] = r0; _rep[0] = off; return 1; }
                if (off == r2) { _rep[2] = r1; _rep[1] = r0; _rep[0] = off; return 2; }
                if (off == r0 - 1) { _rep[2] = r1; _rep[1] = r0; _rep[0] = off; return 3; }
            }
            _rep[2] = r1; _rep[1] = r0; _rep[0] = off;
            return off + 3;
        }

        void Store(int anchor, int start, int mlen, int off)
        {
            int ll = start - anchor;
            if (ll > 0)
            {
                fixed (byte* l = _lit) Buffer.MemoryCopy(_b + anchor, l + _nLit, ll, ll);
                _nLit += ll;
            }
            int n = _nSeq++;
            _seqLL[n] = ll;
            _seqML[n] = mlen;
            _seqOff[n] = RepCode(ll, off);
        }

        void FindMatches(int bs, int be, int* head, int* chain)
        {
            byte* b = _b;
            _hp = head; _cp = chain;
            int ip = bs, anchor = bs;
            int ilimit = be - 8;
            int dictLen = _dictLen, window = _window;
            // the dictionary may be referenced only while the frame output does not exceed the window
            bool dictOk = _single || (be - dictLen) <= window;
            int lazy = _lazy, sufficient = _sufficient;
            int validLow = dictOk ? 0 : dictLen;   // lowest index a match may start at (besides the window)

            while (ip < ilimit)
            {
                int mlen = 0, moff = 0, start = ip, offCost = 0;
                int r1 = _rep[0];
                {
                    int p = ip + 1;
                    int low = dictOk ? 0 : Math.Max(dictLen, p - window);
                    if (p - r1 >= low && Zc.Read32(b + p) == Zc.Read32(b + p - r1))
                    {
                        mlen = Count(b, p + 4, p + 4 - r1, be) + 4;
                        moff = r1; start = p; offCost = 0;
                    }
                }
                {
                    int l2 = Search(ip, be, dictOk, out int o2);
                    if (l2 > mlen)
                    {
                        mlen = l2; moff = o2; start = ip;
                        offCost = o2 == r1 ? 0 : Zc.HighBit((uint)o2 + 3);
                    }
                }
                if (mlen < 4)
                {
                    ip += ((ip - anchor) >> 8) + 1;
                    continue;
                }
                if (lazy > 0 && mlen < sufficient)
                {
                    while (ip < ilimit)
                    {
                        ip++;
                        {
                            int low = dictOk ? 0 : Math.Max(dictLen, ip - window);
                            if (offCost != 0 && ip - r1 >= low && Zc.Read32(b + ip) == Zc.Read32(b + ip - r1))
                            {
                                int l = Count(b, ip + 4, ip + 4 - r1, be) + 4;
                                if (l * 3 > mlen * 3 - offCost + 1) { mlen = l; moff = r1; offCost = 0; start = ip; }
                            }
                            int l2 = Search(ip, be, dictOk, out int o2);
                            if (l2 >= 4)
                            {
                                int c2 = o2 == r1 ? 0 : Zc.HighBit((uint)o2 + 3);
                                if (l2 * 4 - c2 > mlen * 4 - offCost + 4)
                                {
                                    mlen = l2; moff = o2; offCost = c2; start = ip;
                                    continue;
                                }
                            }
                        }
                        if (lazy >= 2 && ip < ilimit)
                        {
                            ip++;
                            int low = dictOk ? 0 : Math.Max(dictLen, ip - window);
                            if (offCost != 0 && ip - r1 >= low && Zc.Read32(b + ip) == Zc.Read32(b + ip - r1))
                            {
                                int l = Count(b, ip + 4, ip + 4 - r1, be) + 4;
                                if (l * 4 > mlen * 4 - offCost + 1) { mlen = l; moff = r1; offCost = 0; start = ip; }
                            }
                            int l2 = Search(ip, be, dictOk, out int o2);
                            if (l2 >= 4)
                            {
                                int c2 = o2 == r1 ? 0 : Zc.HighBit((uint)o2 + 3);
                                if (l2 * 4 - c2 > mlen * 4 - offCost + 7)
                                {
                                    mlen = l2; moff = o2; offCost = c2; start = ip;
                                    continue;
                                }
                            }
                        }
                        break;
                    }
                }
                // extend backward
                if (moff != r1)
                {
                    while (start > anchor && start - moff > validLow && b[start - 1] == b[start - 1 - moff])
                    {
                        start--;
                        mlen++;
                    }
                }
                Store(anchor, start, mlen, moff);
                ip = anchor = start + mlen;

                // immediate repeat of the second offset (literal length 0)
                while (ip <= ilimit)
                {
                    int r2 = _rep[1];
                    int low = dictOk ? 0 : Math.Max(dictLen, ip - window);
                    if (ip - r2 >= low && Zc.Read32(b + ip) == Zc.Read32(b + ip - r2))
                    {
                        int l = Count(b, ip + 4, ip + 4 - r2, be) + 4;
                        Store(ip, ip, l, r2);
                        ip += l;
                        anchor = ip;
                        continue;
                    }
                    break;
                }
            }
            int rest = be - anchor;
            if (rest > 0)
            {
                fixed (byte* l = _lit) Buffer.MemoryCopy(b + anchor, l + _nLit, rest, rest);
                _nLit += rest;
            }
        }

        int Search(int cur, int end, bool dictOk, out int offset)
        {
            byte* b = _b;
            int* head = _hp;
            int* chain = _cp;
            int hashLog = _hashLog, chainMask = _chainMask;
            // insert every position up to cur, then walk the chain of cur's hash
            int i = _nextToUpdate;
            for (; i < cur; i++)
            {
                uint hi = Hash4(Zc.Read32(b + i), hashLog);
                chain[i & chainMask] = head[hi];
                head[hi] = i;
            }
            uint first = Zc.Read32(b + cur);
            uint h = Hash4(first, hashLog);
            int cand = head[h];
            if (i == cur)
            {
                chain[cur & chainMask] = cand;
                head[h] = cur;
                _nextToUpdate = cur + 1;
            }
            else
            {
                while (cand >= cur && cand > 0) cand = chain[cand & chainMask];
            }
            int low = Math.Max(_ownLow, cur - _chainSize + 1);
            if (!dictOk) low = Math.Max(low, cur - _window);
            int best = 3, bestOff = 0;
            int attempts = _depth;
            int maxLen = end - cur;
            int sufficient = _sufficient;
            int litCost = _litCost;
            while (cand >= low && attempts-- > 0)
            {
                if (b[cand + best] == b[cur + best] && Zc.Read32(b + cand) == first)
                {
                    int len = Count(b, cur + 4, cand + 4, end) + 4;
                    if (len > best && (len >= 8 || len * litCost >= (Zc.HighBit((uint)(cur - cand)) + K) * 16))
                    {
                        best = len;
                        bestOff = cur - cand;
                        if (len >= maxLen || len >= sufficient) goto done;
                    }
                }
                int next = chain[cand & chainMask];
                if (next >= cand) break;
                cand = next;
            }
            if (dictOk && _dhp != null)
            {
                // the dictionary's own (shared, read-only) tables
                int* dchain = _dcp;
                cand = _dhp[Hash4(first, _dHashLog)];
                while (cand > 0 && attempts-- > 0)
                {
                    if (b[cand + best] == b[cur + best] && Zc.Read32(b + cand) == first)
                    {
                        int len = Count(b, cur + 4, cand + 4, end) + 4;
                        if (len > best && (len >= 8 || len * litCost >= (Zc.HighBit((uint)(cur - cand)) + K) * 16))
                        {
                            best = len;
                            bestOff = cur - cand;
                            if (len >= maxLen || len >= sufficient) break;
                        }
                    }
                    cand = dchain[cand];
                }
            }
        done:
            offset = bestOff;
            return best > 3 ? best : 0;
        }

        /// <summary>Fixed overhead (bits) assumed for a sequence when weighing a short match against its literals.</summary>
        const int K = 12;

        // ------------------------------------------------------------ block encoding

        /// <summary>Encodes the current sequence store into the scratch buffer; returns the size.</summary>
        int EncodeBlock()
        {
            _hufPending = _huf;
            _llPend = _llPrev; _ofPend = _ofPrev; _mlPend = _mlPrev;
            int pos = EncodeLiterals(0);
            if (_nLit >= 64) _litCost = Math.Clamp((int)((long)pos * 128 / _nLit), 16, 128);
            pos = EncodeSequences(pos);
            return pos;
        }

        int RawLiterals(int pos, int type, int n)
        {
            byte[] d = _scratch;
            if (n < 32) d[pos++] = (byte)(type | (n << 3));
            else if (n < 4096)
            {
                int v = type | (1 << 2) | (n << 4);
                d[pos++] = (byte)v; d[pos++] = (byte)(v >> 8);
            }
            else
            {
                int v = type | (3 << 2) | (n << 4);
                d[pos++] = (byte)v; d[pos++] = (byte)(v >> 8); d[pos++] = (byte)(v >> 16);
            }
            if (type == 0)
            {
                Buffer.BlockCopy(_lit, 0, d, pos, n);
                pos += n;
            }
            else d[pos++] = _lit[0];
            return pos;
        }

        int EncodeLiterals(int pos)
        {
            int n = _nLit;
            if (n <= (_huf != null ? 6 : 63)) return RawLiterals(pos, 0, n);
            int[] count = _count;
            Array.Clear(count);
            fixed (byte* l = _lit)
            fixed (int* c = count)
            {
                for (int i = 0; i < n; i++) c[l[i]]++;
            }
            int maxSym = 255, largest = 0;
            while (count[maxSym] == 0) maxSym--;
            for (int s = 0; s <= maxSym; s++) if (count[s] > largest) largest = count[s];
            if (largest == n) return RawLiterals(pos, 1, n);
            if (largest <= (n >> 7) + 4) return RawLiterals(pos, 0, n);

            HufCTable nt = Huffman.Build(count, maxSym);
            int newHdr = Huffman.WriteTable(nt, _hdrTmp, 0);
            long newBits = 0, oldBits = 0;
            bool oldOk = _huf != null;
            for (int s = 0; s <= maxSym; s++)
            {
                if (count[s] == 0) continue;
                newBits += (long)count[s] * nt.Lens[s];
                if (oldOk)
                {
                    if (_huf.Lens[s] == 0) oldOk = false;
                    else oldBits += (long)count[s] * _huf.Lens[s];
                }
            }
            long estNew = newHdr < 0 ? long.MaxValue : newHdr + ((newBits + 7) >> 3);
            long estOld = oldOk ? ((oldBits + 7) >> 3) : long.MaxValue;
            if (estNew == long.MaxValue && estOld == long.MaxValue) return RawLiterals(pos, 0, n);
            bool useOld = estOld <= estNew;
            HufCTable t = useOld ? _huf : nt;
            int hdrTree = useOld ? 0 : newHdr;
            bool single = n < 256;

            // exact compressed size
            int seg = (n + 3) / 4;
            int csize = hdrTree;
            Span<int> streamSize = stackalloc int[4];
            fixed (byte* l = _lit)
            {
                if (single)
                {
                    long bits = 0;
                    for (int i = 0; i < n; i++) bits += t.Lens[l[i]];
                    streamSize[0] = (int)((bits + 8) >> 3);
                    csize += streamSize[0];
                }
                else
                {
                    csize += 6;
                    for (int k = 0; k < 4; k++)
                    {
                        int from = k * seg, to = k == 3 ? n : from + seg;
                        long bits = 0;
                        for (int i = from; i < to; i++) bits += t.Lens[l[i]];
                        streamSize[k] = (int)((bits + 8) >> 3);
                        csize += streamSize[k];
                    }
                }
                if (csize >= n - ((n >> 6) + 2)) return RawLiterals(pos, 0, n);

                byte[] d = _scratch;
                int type = useOld ? 3 : 2;
                if (n < 1024)
                {
                    int v = type | ((single ? 0 : 1) << 2) | (n << 4) | (csize << 14);
                    d[pos++] = (byte)v; d[pos++] = (byte)(v >> 8); d[pos++] = (byte)(v >> 16);
                }
                else if (n < 16384)
                {
                    uint v = (uint)(type | (2 << 2) | (n << 4)) | ((uint)csize << 18);
                    Zc.Write32(d, pos, v);
                    pos += 4;
                }
                else
                {
                    ulong v = (ulong)(uint)(type | (3 << 2) | (n << 4)) | ((ulong)csize << 22);
                    Zc.Write32(d, pos, (uint)v);
                    d[pos + 4] = (byte)(v >> 32);
                    pos += 5;
                }
                if (!useOld)
                {
                    Buffer.BlockCopy(_hdrTmp, 0, d, pos, newHdr);
                    pos += newHdr;
                }
                if (single)
                {
                    pos = Huffman.EncodeStream(l, n, t, d, pos);
                }
                else
                {
                    d[pos] = (byte)streamSize[0]; d[pos + 1] = (byte)(streamSize[0] >> 8);
                    d[pos + 2] = (byte)streamSize[1]; d[pos + 3] = (byte)(streamSize[1] >> 8);
                    d[pos + 4] = (byte)streamSize[2]; d[pos + 5] = (byte)(streamSize[2] >> 8);
                    pos += 6;
                    for (int k = 0; k < 4; k++)
                    {
                        int from = k * seg, to = k == 3 ? n : from + seg;
                        int end = Huffman.EncodeStream(l + from, to - from, t, d, pos);
                        if (end - pos != streamSize[k]) throw new InvalidOperationException("zstd encoder: Huffman size mismatch");
                        pos = end;
                    }
                }
            }
            _hufPending = t;
            return pos;
        }

        /// <summary>Chooses the table mode for one symbol type, writes its description; returns the mode.</summary>
        int SelectTable(byte[] codes, int nSeq, int maxSymAllowed, FseTableState def, int maxLog,
                        FseTableState prev, ref FseTableState pending, ref int pos, out FseCTable ct)
        {
            Span<int> count = stackalloc int[maxSymAllowed + 1];
            count.Clear();
            for (int i = 0; i < nSeq; i++) count[codes[i]]++;
            int maxSym = maxSymAllowed;
            while (count[maxSym] == 0) maxSym--;
            byte[] d = _scratch;

            long costRle = count[maxSym] == nSeq ? 8 * 256 : long.MaxValue;
            long costDef = long.MaxValue;
            if (maxSym <= def.MaxSym)
            {
                costDef = 0;
                for (int s = 0; s <= maxSym; s++)
                    if (count[s] != 0) costDef += (long)count[s] * Fse.BitCost(def.Norm[s], def.Log);
            }
            long costRep = long.MaxValue;
            if (prev != null && maxSym <= prev.MaxSym)
            {
                costRep = 0;
                for (int s = 0; s <= maxSym; s++)
                {
                    if (count[s] == 0) continue;
                    if (prev.Norm[s] == 0) { costRep = long.MaxValue; break; }
                    costRep += (long)count[s] * Fse.BitCost(prev.Norm[s], prev.Log);
                }
            }
            long costFse = long.MaxValue;
            int log = 0, hdr = 0;
            if (costRle == long.MaxValue && nSeq >= 4)
            {
                log = Fse.OptimalTableLog(maxLog, nSeq, maxSym);
                Fse.Normalize(count, maxSym, nSeq, log, _normTmp);
                hdr = Fse.WriteNCount(_hdrTmp, 0, _normTmp, maxSym, log);
                costFse = (long)hdr * 8 * 256;
                for (int s = 0; s <= maxSym; s++)
                    if (count[s] != 0) costFse += (long)count[s] * Fse.BitCost(_normTmp[s], log);
            }

            long best = costDef;
            int mode = 0;
            if (costRep < best) { best = costRep; mode = 3; }
            if (costRle < best) { best = costRle; mode = 1; }
            if (costFse < best) { best = costFse; mode = 2; }
            if (best == long.MaxValue)
            {
                // no predefined table for these symbols and too few sequences for a cost estimate
                if (costRle != long.MaxValue) mode = 1;
                else
                {
                    log = Fse.OptimalTableLog(maxLog, nSeq, maxSym);
                    Fse.Normalize(count, maxSym, nSeq, log, _normTmp);
                    hdr = Fse.WriteNCount(_hdrTmp, 0, _normTmp, maxSym, log);
                    mode = 2;
                }
            }
            switch (mode)
            {
                case 0:
                    ct = def.CT;
                    pending = null;
                    break;
                case 1:
                    d[pos++] = (byte)maxSym;
                    ct = Fse.BuildRleCTable(maxSym);
                    pending = null;
                    break;
                case 2:
                {
                    Buffer.BlockCopy(_hdrTmp, 0, d, pos, hdr);
                    pos += hdr;
                    short[] norm = new short[maxSym + 1];
                    Array.Copy(_normTmp, norm, maxSym + 1);
                    pending = FseTableState.Create(norm, maxSym, log);
                    ct = pending.CT;
                    break;
                }
                default:
                    ct = prev.CT;
                    break;
            }
            return mode;
        }

        int EncodeSequences(int pos)
        {
            byte[] d = _scratch;
            int nSeq = _nSeq;
            if (nSeq == 0)
            {
                d[pos++] = 0;
                return pos;
            }
            if (nSeq < 128) d[pos++] = (byte)nSeq;
            else if (nSeq < 0x7F00) { d[pos++] = (byte)((nSeq >> 8) + 0x80); d[pos++] = (byte)nSeq; }
            else { d[pos++] = 0xFF; d[pos++] = (byte)(nSeq - 0x7F00); d[pos++] = (byte)((nSeq - 0x7F00) >> 8); }

            int[] sll = _seqLL, sml = _seqML, sof = _seqOff;
            byte[] llc = _llc, mlc = _mlc, ofc = _ofc;
            for (int i = 0; i < nSeq; i++)
            {
                llc[i] = (byte)LLCode(sll[i]);
                mlc[i] = (byte)MLCode(sml[i]);
                ofc[i] = (byte)Zc.HighBit((uint)sof[i]);
            }
            int modesPos = pos++;
            int llMode = SelectTable(llc, nSeq, Zc.MaxLL, DefLL, Zc.LLFseLog, _llPrev, ref _llPend, ref pos, out FseCTable ctLL);
            int ofMode = SelectTable(ofc, nSeq, Zc.MaxOff, DefOF, Zc.OffFseLog, _ofPrev, ref _ofPend, ref pos, out FseCTable ctOF);
            int mlMode = SelectTable(mlc, nSeq, Zc.MaxML, DefML, Zc.MLFseLog, _mlPrev, ref _mlPend, ref pos, out FseCTable ctML);
            d[modesPos] = (byte)((llMode << 6) | (ofMode << 4) | (mlMode << 2));

            if (pos + (long)nSeq * 12 + 16 > d.Length) throw new InvalidOperationException("zstd encoder: scratch too small");
            var bw = new BitWriter(d, pos);
            ushort[] stLLT = ctLL.StateTable, stOFT = ctOF.StateTable, stMLT = ctML.StateTable;
            int[] nbLL = ctLL.DeltaNbBits, nbOF = ctOF.DeltaNbBits, nbML = ctML.DeltaNbBits;
            int[] fsLL = ctLL.DeltaFindState, fsOF = ctOF.DeltaFindState, fsML = ctML.DeltaFindState;
            uint[] llBase = Zc.LLBase, mlBase = Zc.MLBase;
            byte[] llBits = Zc.LLBits, mlBits = Zc.MLBits;

            int n = nSeq - 1;
            int stML = ctML.Init(mlc[n]);
            int stOF = ctOF.Init(ofc[n]);
            int stLL = ctLL.Init(llc[n]);
            bw.Add((uint)sll[n] - llBase[llc[n]], llBits[llc[n]]);
            bw.Add((uint)sml[n] - mlBase[mlc[n]], mlBits[mlc[n]]);
            bw.Add((uint)sof[n] - (1u << ofc[n]), ofc[n]);
            for (n = nSeq - 2; n >= 0; n--)
            {
                int lc = llc[n], mc = mlc[n], oc = ofc[n];
                int nb = (stOF + nbOF[oc]) >> 16;
                bw.Add((uint)stOF, nb);
                stOF = stOFT[(stOF >> nb) + fsOF[oc]];
                nb = (stML + nbML[mc]) >> 16;
                bw.Add((uint)stML, nb);
                stML = stMLT[(stML >> nb) + fsML[mc]];
                nb = (stLL + nbLL[lc]) >> 16;
                bw.Add((uint)stLL, nb);
                stLL = stLLT[(stLL >> nb) + fsLL[lc]];
                bw.Add((uint)sll[n] - llBase[lc], llBits[lc]);
                bw.Add((uint)sml[n] - mlBase[mc], mlBits[mc]);
                bw.Add((uint)sof[n] - (1u << oc), oc);
            }
            bw.Add((uint)stML, ctML.Log);
            bw.Add((uint)stOF, ctOF.Log);
            bw.Add((uint)stLL, ctLL.Log);
            return bw.Close();
        }
    }
}
