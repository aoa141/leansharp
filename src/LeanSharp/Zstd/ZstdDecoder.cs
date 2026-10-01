// Zstandard frame decoder (RFC 8878).
using System.Runtime.CompilerServices;

namespace LeanSharp.Zstd;

public static class ZstdDecoder
{
    /// <summary>Decodes all concatenated frames (and skips skippable frames) in <paramref name="src"/>.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> src, ZstdDictionary dictionary = null)
    {
        try
        {
            return new Ctx(dictionary).Run(src);
        }
        catch (ZstdException) { throw; }
        catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException)
        {
            throw new ZstdException("corrupt zstd data", e);
        }
    }

    static readonly SeqTable DefLL = SeqTable.Build(Zc.LLDefaultNorm, Zc.MaxLL, Zc.LLDefaultLog, Zc.LLBase, Zc.LLBits);
    static readonly SeqTable DefML = SeqTable.Build(Zc.MLDefaultNorm, Zc.MaxML, Zc.MLDefaultLog, Zc.MLBase, Zc.MLBits);
    static readonly SeqTable DefOF = SeqTable.Build(Zc.OffDefaultNorm, 28, Zc.OffDefaultLog, null, null);

    /// <summary>Reads a bit stream backward (as written by the encoder's forward bit writer).</summary>
    unsafe struct BackReader
    {
        public byte* P;
        public int Len;
        public long BitPos;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Read(int n)
        {
            BitPos -= n;
            long bi = BitPos >> 3;
            if (BitPos >= 0 && bi + 8 <= Len)
                return (uint)((Zc.Read64(P + bi) >> (int)(BitPos & 7)) & ((1UL << n) - 1));
            return Slow(n);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        uint Slow(int n)
        {
            // bits [BitPos, BitPos+n); bits below zero read as zero (and are reported as corruption later)
            long s = BitPos;
            int up = 0;
            if (s < 0)
            {
                if (-s >= n) return 0;
                up = (int)-s;
                n -= up;
                s = 0;
            }
            if (n == 0) return 0;
            long bi = s >> 3;
            ulong v = 0;
            for (int k = 0; k < 8; k++)
                if (bi + k < Len) v |= (ulong)P[bi + k] << (8 * k);
            return (uint)(((v >> (int)(s & 7)) & ((1UL << n) - 1)) << up);
        }
    }

    sealed class Ctx
    {
        readonly ZstdDictionary _dictArg;
        byte[] _out = Array.Empty<byte>();
        int _op;
        int _frameStart;
        byte[] _dictContent;
        int _blockMax;
        byte[] _litBuf;

        HufDTable _huf;
        bool _hufValid;
        SeqTable _ll, _of, _ml;
        bool _fseValid;
        long _rep1, _rep2, _rep3;
        readonly short[] _norm = new short[64];

        public Ctx(ZstdDictionary dict) { _dictArg = dict; }

        public byte[] Run(ReadOnlySpan<byte> src)
        {
            int ip = 0;
            while (ip < src.Length)
            {
                uint magic = Zc.Read32(src, ip);
                if ((magic & 0xFFFFFFF0u) == Zc.SkippableMagic)
                {
                    uint size = Zc.Read32(src, ip + 4);
                    if (size > (uint)(src.Length - ip - 8)) throw new ZstdException("truncated skippable frame");
                    ip += 8 + (int)size;
                    continue;
                }
                if (magic != Zc.FrameMagic) throw new ZstdException("not a zstd frame (bad magic number)");
                ip = DecodeFrame(src, ip + 4);
            }
            if (_op == _out.Length) return _out;
            return _out.AsSpan(0, _op).ToArray();
        }

        void Ensure(long needed)
        {
            if (needed <= _out.Length) return;
            if (needed > Array.MaxLength) throw new ZstdException("decompressed data too large");
            long cap = _out.Length == 0 ? needed : Math.Max(needed, Math.Min((long)_out.Length * 2, Array.MaxLength));
            var n = new byte[cap];
            Buffer.BlockCopy(_out, 0, n, 0, _op);
            _out = n;
        }

        int DecodeFrame(ReadOnlySpan<byte> src, int ip)
        {
            if (ip >= src.Length) throw new ZstdException("truncated frame header");
            byte fhd = src[ip++];
            int fcsFlag = fhd >> 6;
            bool single = (fhd & 0x20) != 0;
            if ((fhd & 0x08) != 0) throw new ZstdException("unsupported frame (reserved bit set)");
            bool hasChecksum = (fhd & 4) != 0;
            int didFlag = fhd & 3;
            ulong windowSize = 0;
            if (!single)
            {
                if (ip >= src.Length) throw new ZstdException("truncated frame header");
                byte wd = src[ip++];
                int wl = 10 + (wd >> 3);
                if (wl > 31) throw new ZstdException("window size too large");
                windowSize = (1UL << wl) + ((1UL << wl) >> 3) * (ulong)(wd & 7);
            }
            int didSize = didFlag == 3 ? 4 : didFlag;
            int fcsSize = fcsFlag == 0 ? (single ? 1 : 0) : (1 << fcsFlag);
            if (src.Length - ip < didSize + fcsSize) throw new ZstdException("truncated frame header");
            uint dictId = 0;
            for (int i = 0; i < didSize; i++) dictId |= (uint)src[ip + i] << (8 * i);
            ip += didSize;
            long fcs = -1;
            if (fcsSize > 0)
            {
                ulong v = 0;
                for (int i = 0; i < fcsSize; i++) v |= (ulong)src[ip + i] << (8 * i);
                if (fcsSize == 2) v += 256;
                ip += fcsSize;
                if (v > (ulong)Array.MaxLength) throw new ZstdException("frame content size too large");
                fcs = (long)v;
                // every block needs at least 4 bytes of input for at most 128 KB of output
                if (fcs > ((long)(src.Length - ip) / 4 + 1) * Zc.BlockMax) throw new ZstdException("truncated frame");
            }
            if (single) windowSize = (ulong)fcs;
            _blockMax = (int)Math.Min(windowSize, (ulong)Zc.BlockMax);

            ZstdDictionary dict = _dictArg;
            if (dictId != 0 && (dict == null || dict.Id != dictId))
                throw new ZstdException(dict == null
                    ? $"frame requires dictionary {dictId}"
                    : $"dictionary mismatch (frame needs {dictId}, got {dict.Id})");

            _rep1 = 1; _rep2 = 4; _rep3 = 8;
            _hufValid = false;
            _fseValid = false;
            _huf = null;
            _ll = DefLL; _of = DefOF; _ml = DefML;
            _dictContent = null;
            if (dict != null)
            {
                _dictContent = dict.Content;
                if (dict.HasEntropy)
                {
                    _huf = dict.HufD; _hufValid = true;
                    _ll = dict.LLD; _of = dict.OFD; _ml = dict.MLD; _fseValid = true;
                    _rep1 = dict.Rep[0]; _rep2 = dict.Rep[1]; _rep3 = dict.Rep[2];
                }
            }

            _frameStart = _op;
            if (fcs >= 0) Ensure(_op + fcs);
            long frameEnd = fcs >= 0 ? _op + fcs : long.MaxValue;
            while (true)
            {
                if (src.Length - ip < 3) throw new ZstdException("truncated block header");
                int bh = src[ip] | (src[ip + 1] << 8) | (src[ip + 2] << 16);
                ip += 3;
                bool last = (bh & 1) != 0;
                int type = (bh >> 1) & 3;
                int size = bh >> 3;
                switch (type)
                {
                    case 0:
                        if (size > _blockMax) throw new ZstdException("raw block too large");
                        if (src.Length - ip < size) throw new ZstdException("truncated raw block");
                        if (_op + (long)size > frameEnd) throw new ZstdException("frame content larger than declared");
                        Ensure(_op + (long)size);
                        src.Slice(ip, size).CopyTo(_out.AsSpan(_op));
                        _op += size;
                        ip += size;
                        break;
                    case 1:
                        if (size > _blockMax) throw new ZstdException("RLE block too large");
                        if (src.Length - ip < 1) throw new ZstdException("truncated RLE block");
                        if (_op + (long)size > frameEnd) throw new ZstdException("frame content larger than declared");
                        Ensure(_op + (long)size);
                        _out.AsSpan(_op, size).Fill(src[ip]);
                        _op += size;
                        ip += 1;
                        break;
                    case 2:
                    {
                        if (size > _blockMax) throw new ZstdException("compressed block too large");
                        if (src.Length - ip < size) throw new ZstdException("truncated compressed block");
                        long limit = Math.Min(frameEnd, _op + (long)_blockMax);
                        Ensure(limit);
                        DecodeBlock(src.Slice(ip, size), (int)limit);
                        ip += size;
                        break;
                    }
                    default:
                        throw new ZstdException("reserved block type");
                }
                if (last) break;
            }
            if (fcs >= 0 && _op - _frameStart != fcs) throw new ZstdException("frame content size mismatch");
            if (hasChecksum)
            {
                uint stored = Zc.Read32(src, ip);
                ip += 4;
                uint actual = (uint)XxHash64.Hash(_out.AsSpan(_frameStart, _op - _frameStart));
                if (stored != actual) throw new ZstdException("content checksum mismatch");
            }
            return ip;
        }

        void DecodeBlock(ReadOnlySpan<byte> blk, int outLimit)
        {
            if (blk.Length < 1) throw new ZstdException("empty compressed block");
            byte b0 = blk[0];
            int type = b0 & 3, sf = (b0 >> 2) & 3;
            int ip;
            ReadOnlySpan<byte> lits;
            if (type < 2)
            {
                int regen, hs;
                if ((sf & 1) == 0) { regen = b0 >> 3; hs = 1; }
                else if (sf == 1)
                {
                    if (blk.Length < 2) throw new ZstdException("truncated literals header");
                    regen = (b0 >> 4) | (blk[1] << 4); hs = 2;
                }
                else
                {
                    if (blk.Length < 3) throw new ZstdException("truncated literals header");
                    regen = (b0 >> 4) | (blk[1] << 4) | (blk[2] << 12); hs = 3;
                }
                if (regen > _blockMax) throw new ZstdException("literals section too large");
                if (type == 0)
                {
                    if (blk.Length - hs < regen) throw new ZstdException("truncated raw literals");
                    lits = blk.Slice(hs, regen);
                    ip = hs + regen;
                }
                else
                {
                    if (blk.Length - hs < 1) throw new ZstdException("truncated RLE literals");
                    _litBuf ??= new byte[Zc.BlockMax];
                    _litBuf.AsSpan(0, regen).Fill(blk[hs]);
                    lits = _litBuf.AsSpan(0, regen);
                    ip = hs + 1;
                }
            }
            else
            {
                int regen, comp, hs;
                bool four = sf != 0;
                if (sf < 2)
                {
                    if (blk.Length < 3) throw new ZstdException("truncated literals header");
                    int v = b0 | (blk[1] << 8) | (blk[2] << 16);
                    regen = (v >> 4) & 0x3FF; comp = (v >> 14) & 0x3FF; hs = 3;
                }
                else if (sf == 2)
                {
                    if (blk.Length < 4) throw new ZstdException("truncated literals header");
                    uint v = (uint)(b0 | (blk[1] << 8) | (blk[2] << 16) | (blk[3] << 24));
                    regen = (int)((v >> 4) & 0x3FFF); comp = (int)(v >> 18); hs = 4;
                }
                else
                {
                    if (blk.Length < 5) throw new ZstdException("truncated literals header");
                    ulong v = (ulong)(uint)(b0 | (blk[1] << 8) | (blk[2] << 16) | (blk[3] << 24)) | ((ulong)blk[4] << 32);
                    regen = (int)((v >> 4) & 0x3FFFF); comp = (int)(v >> 22); hs = 5;
                }
                if (regen > _blockMax) throw new ZstdException("literals section too large");
                if (comp > blk.Length - hs) throw new ZstdException("truncated compressed literals");
                var cs = blk.Slice(hs, comp);
                if (type == 2)
                {
                    _huf = Huffman.ReadTable(cs, out int used, null, out _);
                    _hufValid = true;
                    cs = cs.Slice(used);
                }
                else if (!_hufValid) throw new ZstdException("treeless literals without a previous Huffman table");
                _litBuf ??= new byte[Zc.BlockMax];
                var dst = _litBuf.AsSpan(0, regen);
                if (four) Huffman.Decode4(cs, dst, _huf);
                else Huffman.DecodeStream(cs, dst, _huf);
                lits = dst;
                ip = hs + comp;
            }

            // sequences section
            if (ip >= blk.Length) throw new ZstdException("missing sequences section");
            int nSeq = blk[ip++];
            if (nSeq >= 128)
            {
                if (nSeq == 255)
                {
                    if (blk.Length - ip < 2) throw new ZstdException("truncated sequences header");
                    nSeq = blk[ip] + (blk[ip + 1] << 8) + 0x7F00;
                    ip += 2;
                }
                else
                {
                    if (blk.Length - ip < 1) throw new ZstdException("truncated sequences header");
                    nSeq = ((nSeq - 128) << 8) + blk[ip++];
                }
            }
            if (nSeq == 0)
            {
                if (ip != blk.Length) throw new ZstdException("extraneous data in sequences section");
                if (lits.Length > outLimit - _op) throw new ZstdException("block content too large");
                lits.CopyTo(_out.AsSpan(_op));
                _op += lits.Length;
                return;
            }
            if (ip >= blk.Length) throw new ZstdException("truncated sequences header");
            int modes = blk[ip++];
            if ((modes & 3) != 0) throw new ZstdException("corrupt sequences header (reserved bits)");
            SetTable(modes >> 6, ref _ll, DefLL, Zc.MaxLL, Zc.LLFseLog, Zc.LLBase, Zc.LLBits, blk, ref ip);
            SetTable((modes >> 4) & 3, ref _of, DefOF, Zc.MaxOff, Zc.OffFseLog, null, null, blk, ref ip);
            SetTable((modes >> 2) & 3, ref _ml, DefML, Zc.MaxML, Zc.MLFseLog, Zc.MLBase, Zc.MLBits, blk, ref ip);
            _fseValid = true;
            Execute(blk.Slice(ip), nSeq, lits, outLimit);
        }

        void SetTable(int mode, ref SeqTable cur, SeqTable def, int maxSym, int maxLog, uint[] bases, byte[] bits,
                      ReadOnlySpan<byte> blk, ref int ip)
        {
            switch (mode)
            {
                case 0:
                    cur = def;
                    break;
                case 1:
                {
                    if (ip >= blk.Length) throw new ZstdException("truncated sequences header");
                    int sym = blk[ip++];
                    if (sym > maxSym) throw new ZstdException("corrupt RLE sequence symbol");
                    cur = SeqTable.Rle(sym, bases, bits);
                    break;
                }
                case 2:
                {
                    int ms = maxSym;
                    ip += Fse.ReadNCount(blk.Slice(ip), _norm, ref ms, maxLog, out int log);
                    cur = SeqTable.Build(_norm, ms, log, bases, bits);
                    break;
                }
                default:
                    if (!_fseValid) throw new ZstdException("repeat mode without a previous table");
                    break;
            }
        }

        unsafe void Execute(ReadOnlySpan<byte> bs, int nSeq, ReadOnlySpan<byte> lits, int outLimit)
        {
            if (bs.Length == 0) throw new ZstdException("missing sequences bit stream");
            byte lastByte = bs[bs.Length - 1];
            if (lastByte == 0) throw new ZstdException("corrupt sequences bit stream (no end mark)");
            byte[] outBuf = _out;
            int op = _op;
            int frameStart = _frameStart;
            long rep1 = _rep1, rep2 = _rep2, rep3 = _rep3;
            int litPos = 0, litLen = lits.Length;
            byte[] dictContent = _dictContent;
            int dictLen = dictContent == null ? 0 : dictContent.Length;
            SeqEntry[] llT = _ll.E, ofT = _of.E, mlT = _ml.E;

            fixed (byte* p = bs)
            fixed (byte* o = outBuf)
            fixed (byte* lp = lits)
            {
                BackReader br;
                br.P = p;
                br.Len = bs.Length;
                br.BitPos = (long)(bs.Length - 1) * 8 + Zc.HighBit(lastByte);
                uint llState = br.Read(_ll.Log);
                uint ofState = br.Read(_of.Log);
                uint mlState = br.Read(_ml.Log);
                if (br.BitPos < 0) throw new ZstdException("truncated sequences bit stream");
                for (int i = 0; i < nSeq; i++)
                {
                    SeqEntry le = llT[llState], oe = ofT[ofState], me = mlT[mlState];
                    ulong offVal = (ulong)oe.Base + br.Read(oe.ExtraBits);
                    int ml = (int)(me.Base + br.Read(me.ExtraBits));
                    int ll = (int)(le.Base + br.Read(le.ExtraBits));
                    long offset;
                    if (offVal > 3)
                    {
                        offset = (long)offVal - 3;
                        rep3 = rep2; rep2 = rep1; rep1 = offset;
                    }
                    else
                    {
                        int idx = (int)offVal - 1 + (ll == 0 ? 1 : 0);
                        if (idx == 0) offset = rep1;
                        else
                        {
                            offset = idx == 1 ? rep2 : idx == 2 ? rep3 : rep1 - 1;
                            if (offset == 0) throw new ZstdException("corrupt sequence (zero offset)");
                            if (idx != 1) rep3 = rep2;
                            rep2 = rep1;
                            rep1 = offset;
                        }
                    }
                    if (i + 1 < nSeq)
                    {
                        llState = le.NewState + br.Read(le.NbBits);
                        mlState = me.NewState + br.Read(me.NbBits);
                        ofState = oe.NewState + br.Read(oe.NbBits);
                    }
                    if (br.BitPos < 0) throw new ZstdException("truncated sequences bit stream");

                    // execute
                    if (ll > litLen - litPos) throw new ZstdException("corrupt sequence (literals overrun)");
                    if ((long)ll + ml > outLimit - op) throw new ZstdException("block content too large");
                    if (ll > 0)
                    {
                        if (ll <= 16 && litPos + 16 <= litLen && op + 16 <= outLimit)
                        {
                            *(ulong*)(o + op) = *(ulong*)(lp + litPos);
                            *(ulong*)(o + op + 8) = *(ulong*)(lp + litPos + 8);
                        }
                        else Buffer.MemoryCopy(lp + litPos, o + op, ll, ll);
                        op += ll;
                        litPos += ll;
                    }
                    long produced = op - frameStart;
                    if (offset > produced)
                    {
                        long back = offset - produced;
                        if (back > dictLen) throw new ZstdException("corrupt sequence (offset too large)");
                        int n = (int)Math.Min(ml, back);
                        new ReadOnlySpan<byte>(dictContent, (int)(dictLen - back), n).CopyTo(new Span<byte>(o + op, n));
                        op += n;
                        ml -= n;
                        if (ml == 0) continue;
                    }
                    byte* m = o + op - offset;
                    byte* d = o + op;
                    if (offset >= 8 && op + ml + 8 <= outLimit)
                    {
                        // 8 bytes at a time; overlap beyond 8 bytes is harmless
                        byte* end = d + ml;
                        do
                        {
                            *(ulong*)d = *(ulong*)m;
                            d += 8; m += 8;
                        } while (d < end);
                    }
                    else if (offset >= ml)
                    {
                        Buffer.MemoryCopy(m, d, ml, ml);
                    }
                    else if (offset == 1)
                    {
                        new Span<byte>(d, ml).Fill(*m);
                    }
                    else
                    {
                        for (int k = 0; k < ml; k++) d[k] = m[k];
                    }
                    op += ml;
                }
                if (br.BitPos != 0) throw new ZstdException("corrupt sequences bit stream (not fully consumed)");
                int rest = litLen - litPos;
                if (rest > outLimit - op) throw new ZstdException("block content too large");
                if (rest > 0) Buffer.MemoryCopy(lp + litPos, o + op, rest, rest);
                op += rest;
            }
            _op = op;
            _rep1 = rep1; _rep2 = rep2; _rep3 = rep3;
        }
    }
}
