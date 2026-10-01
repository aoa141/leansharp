// Port of `lgz.rs` of leangz (https://github.com/digama0/leangz, v0.1.20, Apache-2.0): the
// `.olean`-specific encoding used inside `.ltar` archives.
//
// An `.olean` file is a compacted region: objects with 8-byte headers that refer to each other by
// address. The encoding walks the object graph from the root and writes a prefix code: one opcode
// per object followed by its children, with back references for shared objects. Hash and data
// fields of `Name`, `Level` and `Expr` objects ("exprish" mode) are not stored: the decoder
// recomputes them. The decoder rebuilds a byte-identical `.olean` because Lean's compactor writes
// children before parents in the same order as this walk.
//
// The format is deterministic: `Compress` produces the same bytes as the Rust implementation.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LeanSharp.Leantar;

/// <summary>Error in an `.olean` or lgz stream (the Rust code panics in these cases).</summary>
public sealed class LgzException : Exception
{
    public LgzException(string message) : base(message) { }
}

public static class Lgz
{
    // ------------------------------------------------------------------
    // Lean object tags

    const byte TagPromise = 244, TagClosure = 245, TagArray = 246, TagStructArray = 247, TagScalarArray = 248,
        TagString = 249, TagMpz = 250, TagThunk = 251, TagTask = 252, TagRef = 253, TagExternal = 254, TagReserved = 255;

    public const ulong NameAnonHash = 1723;

    static ReadOnlySpan<byte> Magic0 => "LGZ!"u8;
    static ReadOnlySpan<byte> Magic1 => "LGZ1"u8;
    static ReadOnlySpan<byte> Magic2 => "LGZ2"u8;

    // opcodes valid in normal and exprish mode
    const byte UInt0 = 0xd0, UInt0End = 0xef, Backref0 = 0xf0, Backref0End = 0xf7,
        Int1 = 0xf8, Int2 = 0xf9, Int4 = 0xfa, Int8 = 0xfb, Backref1 = 0xfc, Backref2 = 0xfd, Backref4 = 0xfe, Save = 0xff;

    // only valid in normal mode
    const byte OpArray = 0x00, OpBigCtor = 0x10, OpScalarArray = 0x20, OpString = 0x30, OpMpz = 0x40,
        OpThunk = 0x50, OpTask = 0x60, OpRef = 0x70, OpExprish = 0x80, OpPromise = 0x90;

    // only valid in exprish mode
    const byte XNormal = 0x20, XNameStr = 0x00, XNameNum = 0x01, XLevelSucc = 0x02, XLevelMax = 0x03, XLevelIMax = 0x04,
        XLevelParam = 0x05, XLevelMVar = 0x06, XExprBVar = 0x07, XExprFVar = 0x08, XExprMVar = 0x09, XExprSort = 0x0a,
        XExprLit = 0x0b, XExprMData = 0x0c, XExprProj = 0x0d, XExprLet = 0x0e, XExprLetEnd = 0x0f,
        XExprLambda = 0x18, XExprForallEnd = 0x1f, XExprApp = 0x20, XExprAppEnd = 0x3f,
        XExprConstApp = 0x40, XExprConstAppEnd = 0x5f;

    // ------------------------------------------------------------------
    // Hashes and cached data of Name/Level/Expr (must match Lean's)

    public static ulong MixHash(ulong h, ulong k)
    {
        const ulong m = 0xc6a4a7935bd1e995;
        k *= m;
        k ^= k >> 47;
        k ^= m;
        h ^= k;
        h *= m;
        return h;
    }

    public static ulong StrHash(ReadOnlySpan<byte> bytes)
    {
        const ulong M = 0xc6a4a7935bd1e995;
        const int R = 47;
        ulong h = 11 ^ ((ulong)bytes.Length * M);
        int n = bytes.Length / 8;
        for (int i = 0; i < n; i++)
        {
            ulong k = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(8 * i));
            k *= M; k ^= k >> R; k *= M;
            h ^= k; h *= M;
        }
        var rest = bytes.Slice(8 * n);
        if (!rest.IsEmpty)
        {
            Span<byte> buf = stackalloc byte[8];
            buf.Clear();
            rest.CopyTo(buf);
            h ^= BinaryPrimitives.ReadUInt64LittleEndian(buf);
            h *= M;
        }
        h ^= h >> R;
        h *= M;
        h ^= h >> R;
        return h;
    }

    static ulong PackLevelData(uint h, uint depth, byte bits) => h | ((ulong)bits << 32) | ((ulong)depth << 40);
    static (uint h, uint depth, byte bits) UnpackLevelData(ulong d) => ((uint)d, (uint)(d >> 40), (byte)(d >> 32));

    static readonly ulong LevelZeroData = PackLevelData(2221, 0, 0);

    static ulong LevelSuccData(ulong data)
    {
        var (h, depth, bits) = UnpackLevelData(data);
        return PackLevelData((uint)MixHash(2243, h), depth + 1, bits);
    }

    static ulong LevelDataBinary(ulong salt, ulong d1, ulong d2)
    {
        var (h1, depth1, bits1) = UnpackLevelData(d1);
        var (h2, depth2, bits2) = UnpackLevelData(d2);
        uint h = (uint)MixHash(salt, MixHash(h1, h2));
        return PackLevelData(h, Math.Max(depth1, depth2) + 1, (byte)(bits1 | bits2));
    }

    static ulong LevelMaxData(ulong d1, ulong d2) => LevelDataBinary(2251, d1, d2);
    static ulong LevelIMaxData(ulong d1, ulong d2) => LevelDataBinary(2267, d1, d2);
    static ulong LevelParamData(ulong hash) => PackLevelData((uint)MixHash(2239, hash), 0, 2);
    static ulong LevelMVarData(ulong hash) => PackLevelData((uint)MixHash(2237, hash), 0, 1);

    static ulong PackExprData(ulong h, uint bvars, byte depth, byte bits) =>
        (uint)h | ((ulong)depth << 32) | ((ulong)bits << 40) | ((ulong)bvars << 44);

    static (ulong h, uint bvars, byte depth, byte bits) UnpackExprData(ulong d) =>
        ((uint)d, (uint)(d >> 44), (byte)(d >> 32), (byte)((d >> 40) & 15));

    static byte SatInc(byte d) => d == 255 ? (byte)255 : (byte)(d + 1);
    static uint SatDec(uint b) => b == 0 ? 0 : b - 1;

    static ulong ExprConstData(ulong hash1, ulong hash2, byte bits2) =>
        PackExprData(MixHash(5, MixHash(hash1, hash2)), 0, 0, (byte)(bits2 << 2));
    static ulong ExprBVarData(ulong n) => PackExprData(MixHash(7, n), (uint)n + 1, 0, 0);
    static ulong ExprSortData(ulong data)
    {
        var (h, _, bits) = UnpackLevelData(data);
        return PackExprData(MixHash(11, h), 0, 0, (byte)(bits << 2));
    }
    static ulong ExprFVarData(ulong hash) => PackExprData(MixHash(13, MixHash(0, hash)), 0, 0, 1);
    static ulong ExprMVarData(ulong hash) => PackExprData(MixHash(17, MixHash(0, hash)), 0, 0, 2);
    static ulong ExprMDataData(ulong data)
    {
        var (h, bvars, depth, bits) = UnpackExprData(data);
        h = MixHash((ulong)depth + 1, h);
        return PackExprData(h, bvars, SatInc(depth), bits);
    }
    static ulong ExprProjData(ulong hash1, ulong num2, ulong data3)
    {
        var (h, bvars, depth, bits) = UnpackExprData(data3);
        h = MixHash((ulong)depth + 1, MixHash(hash1, MixHash(num2, h)));
        return PackExprData(h, bvars, SatInc(depth), bits);
    }
    static ulong ExprAppData(ulong d1, ulong d2)
    {
        var (_, bvars1, depth1, bits1) = UnpackExprData(d1);
        var (_, bvars2, depth2, bits2) = UnpackExprData(d2);
        byte depth = SatInc(Math.Max(depth1, depth2));
        return PackExprData(MixHash(d1, d2), Math.Max(bvars1, bvars2), depth, (byte)(bits1 | bits2));
    }
    static ulong ExprBinderData(ulong d1, ulong d2)
    {
        var (h1, bvars1, depth1, bits1) = UnpackExprData(d1);
        var (h2, bvars2, depth2, bits2) = UnpackExprData(d2);
        byte depth = Math.Max(depth1, depth2);
        ulong h = MixHash((ulong)depth + 1, MixHash(h1, h2));
        uint bvars = Math.Max(bvars1, SatDec(bvars2));
        return PackExprData(h, bvars, SatInc(depth), (byte)(bits1 | bits2));
    }
    static ulong ExprLetData(ulong d1, ulong d2, ulong d3)
    {
        var (h1, bvars1, depth1, bits1) = UnpackExprData(d1);
        var (h2, bvars2, depth2, bits2) = UnpackExprData(d2);
        var (h3, bvars3, depth3, bits3) = UnpackExprData(d3);
        byte depth = Math.Max(Math.Max(depth1, depth2), depth3);
        ulong h = MixHash((ulong)depth + 1, MixHash(h1, MixHash(h2, h3)));
        uint bvars = Math.Max(Math.Max(bvars1, bvars2), SatDec(bvars3));
        return PackExprData(h, bvars, SatInc(depth), (byte)(bits1 | bits2 | bits3));
    }
    static ulong ExprLitData(ulong hash) => PackExprData(MixHash(3, hash), 0, 0, 0);

    /// <summary>One-byte opcode of a small constructor (works for all constructors in the Lean library), or -1.</summary>
    static int PackCtor(byte ctor, byte numFields, ushort sfields)
    {
        if (numFields == 0 && sfields == 0) return -1; // unused anyway because these are enum-like
        if (ctor <= 12 && numFields <= 7 && sfields <= 1) return (ctor << 4) | (numFields << 1) | sfields;
        return -1;
    }

    static (byte numFields, ushort sfields) UnpackCtor(byte tag)
    {
        int n = tag & 15;
        return ((byte)(n >> 1), (ushort)(n & 1));
    }

    // ------------------------------------------------------------------
    // `.olean` headers

    enum OLeanVersion { V0, V1, V2 }

    /// <summary>Whether big numbers use the GMP layout on this platform (`use_gmp` in the Rust code).</summary>
    static bool UseGmp(bool macV2)
    {
        if (OperatingSystem.IsWindows()) return false;
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        if (arch == System.Runtime.InteropServices.Architecture.X64) return true; // unix x86_64
        if (OperatingSystem.IsMacOS() && arch == System.Runtime.InteropServices.Architecture.Arm64) return !macV2;
        return false;
    }

    static readonly string[] s_macV1GmpHashes =
    {
        "be6c4894e0a6c542d56a6f4bb1238087267d21a0", "7ed9b73f4d4c994b603cd369758c79eacdafc62f",
        "141856d6e6d808a85b9147a530294fee8e48e15f", "8f9843a4a5fe1b0c2f24c74097f296e2818771ee",
        "1b78cb4836cf626007bd38872956a6fab8910993", "3b58e0649156610ce3aeed4f7b5c652340c668d4",
        "702c31b8071269f0052fd1e0fb3891a079a655bd", "c375e19f6b656fcd594cdca3a38b8578634df8cd",
        "daa22187642d4cf6954c39a23eab20d8a8675416", "0edf1bac392f7e2fe0266b28b51c498306363a84",
        "c122849f0759797734971b6ff4dfa82f01c653c6", "ec3042d94bd11a42430f9e14d39e26b1f880f99b",
        "e9e858a4484905a0bfe97c4f05c3924ead02eed8", "dc2533473114eb8656439ff2b9335209784aa640",
        "4cb90dddfcb8ceda2b89711d567d593e1fd07090", "b1b73a444f9b13c003ad9dd05881c44e9861a827",
        "01d414ac36dc28f3e424dabd36d818873fea655c", "480d7314a2c499f670609b2c2623a79d36cea760",
        "6d22e0e5cc5a4392466e3d6dd8522486d1fd038b",
    };

    static bool MacV1UseGmp(ReadOnlySpan<byte> githash)
    {
        if (!(OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64))
            return false;
        string s = System.Text.Encoding.ASCII.GetString(githash);
        return Array.IndexOf(s_macV1GmpHashes, s) >= 0;
    }

    sealed class Header
    {
        public bool UseGmp;
        public byte[] LeanVersion = new byte[33]; // raw field (all zero: unknown)
        public byte[] Githash = new byte[40];
        public ulong Base, Root, Offset;
        public int HeaderSize;
    }

    static void Check(bool cond, string msg)
    {
        if (!cond) throw new LgzException(msg);
    }

    static OLeanVersion SniffVersion(ReadOnlySpan<byte> olean)
    {
        if (olean.Length >= 32 && olean.Slice(0, 16).SequenceEqual("oleanfile!!!!!!!"u8)) return OLeanVersion.V0;
        Check(olean.Length >= 64, "bad header");
        return olean[5] switch
        {
            1 => OLeanVersion.V1,
            2 => OLeanVersion.V2,
            var v => throw new LgzException($"unexpected olean version: {v}"),
        };
    }

    static Header ParseHeader(OLeanVersion version, ReadOnlySpan<byte> olean)
    {
        var h = new Header();
        switch (version)
        {
            case OLeanVersion.V0:
                Check(olean.Length >= 32 && olean.Slice(0, 16).SequenceEqual("oleanfile!!!!!!!"u8), "bad header");
                h.Base = BinaryPrimitives.ReadUInt64LittleEndian(olean.Slice(16));
                h.Root = BinaryPrimitives.ReadUInt64LittleEndian(olean.Slice(24));
                h.HeaderSize = 32;
                h.UseGmp = UseGmp(false);
                break;
            case OLeanVersion.V1:
                Check(olean.Length >= 64 && olean.Slice(0, 5).SequenceEqual("olean"u8), "bad header");
                Check(olean[5] == 1 && olean[46] == 0 && olean[47] == 0, "bad header");
                olean.Slice(6, 40).CopyTo(h.Githash);
                h.Base = BinaryPrimitives.ReadUInt64LittleEndian(olean.Slice(48));
                h.Root = BinaryPrimitives.ReadUInt64LittleEndian(olean.Slice(56));
                h.HeaderSize = 64;
                h.UseGmp = UseGmp(MacV1UseGmp(h.Githash));
                break;
            default:
                Check(olean.Length >= 96 && olean.Slice(0, 5).SequenceEqual("olean"u8), "bad header");
                Check(olean[5] == 2 && (olean[6] & ~1) == 0, "bad header");
                h.UseGmp = olean[6] != 0;
                olean.Slice(7, 33).CopyTo(h.LeanVersion);
                olean.Slice(40, 40).CopyTo(h.Githash);
                h.Base = BinaryPrimitives.ReadUInt64LittleEndian(olean.Slice(80));
                h.Root = BinaryPrimitives.ReadUInt64LittleEndian(olean.Slice(88));
                h.HeaderSize = 96;
                break;
        }
        h.Offset = h.Base + (ulong)h.HeaderSize;
        return h;
    }

    /// <summary>`LeanVersion::parse`: (major, minor, patch, rc_m1) of a `4.x.y[-rcN]` version string, or null.</summary>
    static byte[] ParseRegularVersion(byte[] v)
    {
        if (v[0] != (byte)'4' || v[1] != (byte)'.') return null;
        var s = v.AsSpan(2);
        int end = s.IndexOf((byte)'.');
        if (end < 0 || !TryParseU8(s.Slice(0, end), out byte minor)) return null;
        s = s.Slice(end + 1);
        end = s.IndexOf((byte)0);
        if (end >= 0) s = s.Slice(0, end);
        end = s.IndexOf((byte)'-');
        if (end < 0) end = s.Length;
        if (!TryParseU8(s.Slice(0, end), out byte patch)) return null;
        var rest = s.Slice(end);
        byte rc;
        if (rest.StartsWith("-rc"u8))
        {
            if (!TryParseU8(rest.Slice(3), out rc)) return null;
        }
        else if (rest.IsEmpty) rc = 0;
        else return null;
        return new byte[] { 4, minor, patch, unchecked((byte)(rc - 1)) };
    }

    /// <summary>Rust's `str::parse::&lt;u8&gt;`: optional `+`, then digits, no overflow.</summary>
    static bool TryParseU8(ReadOnlySpan<byte> s, out byte value)
    {
        value = 0;
        if (!s.IsEmpty && s[0] == (byte)'+') s = s.Slice(1);
        if (s.IsEmpty) return false;
        int v = 0;
        foreach (byte c in s)
        {
            if (c < '0' || c > '9') return false;
            v = v * 10 + (c - '0');
            if (v > 255) return false;
        }
        value = (byte)v;
        return true;
    }

    // ------------------------------------------------------------------
    // Reading objects

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong U64(byte[] buf, long pos) => BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(checked((int)pos), 8));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint U32(byte[] buf, long pos) => BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(checked((int)pos), 4));

    /// <summary>Object header at `pos`: m_rc (4), m_cs_sz (2), num_fields (1), tag (1).</summary>
    readonly struct ObjHeader
    {
        public readonly uint Rc; public readonly ushort CsSz; public readonly byte NumFields, Tag;
        public ObjHeader(byte[] buf, long pos)
        {
            var s = buf.AsSpan(checked((int)pos), 8);
            Rc = BinaryPrimitives.ReadUInt32LittleEndian(s);
            CsSz = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(4));
            NumFields = s[6]; Tag = s[7];
        }
        /// <summary>`(cs_sz >> 3).saturating_sub(num_fields + 1)`</summary>
        public ushort SFields => (ushort)Math.Max(0, (CsSz >> 3) - (NumFields + 1));
    }

    static long PadTo8(long pos) => (pos + 7) & ~7L;

    /// <summary>Calls `f` for every object pointer in the object at `pos0`; returns the position after the object (unpadded).</summary>
    static long OnSubobjs(bool useGmp, byte[] buf, long pos0, Action<ulong> f)
    {
        var header = new ObjHeader(buf, pos0);
        long pos = pos0 + 8;
        Check(header.Rc == 0, "object with non-zero reference count");
        switch (header.Tag)
        {
            case TagArray:
            {
                ulong size = U64(buf, pos), capacity = U64(buf, pos + 8);
                Check(size == capacity && header.CsSz == 1 && header.NumFields == 0, "bad array");
                pos += 16;
                for (ulong i = 0; i < size; i++, pos += 8)
                {
                    ulong ptr = U64(buf, pos);
                    if ((ptr & 1) == 0) f(ptr);
                }
                return pos;
            }
            case TagScalarArray:
            {
                ulong size = U64(buf, pos), capacity = U64(buf, pos + 8);
                Check(header.CsSz == 1 && size == capacity, "bad scalar array");
                return pos + 16 + (long)capacity;
            }
            case TagString:
            {
                ulong size = U64(buf, pos), capacity = U64(buf, pos + 8);
                Check(header.CsSz == 1 && size == capacity, "bad string");
                return pos + 24 + (long)capacity;
            }
            case TagMpz:
                if (useGmp)
                {
                    uint capacity = U32(buf, pos);
                    int size = (int)U32(buf, pos + 4);
                    Check(header.CsSz == (ushort)((capacity + 3) << 3) && (uint)Math.Abs((long)size) == capacity && capacity != 0, "bad mpz");
                    return pos + 16 + 8L * capacity;
                }
                else
                {
                    ulong sign = U64(buf, pos), size = U64(buf, pos + 8);
                    Check(header.CsSz == (ushort)((size + 8) << 2) && sign < 2 && size != 0, "bad mpz");
                    return pos + 24 + 4L * (long)size;
                }
            case TagThunk:
            case TagTask:
            {
                ulong value = U64(buf, pos), imp = U64(buf, pos + 8);
                Check(header.CsSz == 3 << 3 && imp == 0, "bad thunk/task");
                if ((value & 1) == 0) f(value);
                return pos + 16;
            }
            case TagRef:
            case TagPromise:
            {
                ulong value = U64(buf, pos);
                Check(header.CsSz == 2 << 3, "bad ref/promise");
                if ((value & 1) == 0) f(value);
                return pos + 8;
            }
            case TagClosure: throw new LgzException("closure");
            case TagStructArray: throw new LgzException("struct array");
            case TagExternal: throw new LgzException("external");
            case TagReserved: throw new LgzException("reserved");
            default:
            {
                int lenExceptSFields = 8 + 8 * header.NumFields;
                Check(lenExceptSFields <= header.CsSz && (header.CsSz & 7) == 0, "bad constructor");
                for (int i = 0; i < header.NumFields; i++)
                {
                    ulong ptr = U64(buf, pos + 8 * i);
                    if ((ptr & 1) == 0) f(ptr);
                }
                return pos0 + header.CsSz;
            }
        }
    }

    // Lookups of the cached data of objects, by pointer (`None` in Rust = false here).

    static bool GetNumHash(bool useGmp, byte[] buf, ulong offset, ulong ptr, out ulong hash)
    {
        hash = 0;
        if ((ptr & 1) == 1) { hash = ptr >> 1; return true; }
        long pos = (long)(ptr - offset);
        var header = new ObjHeader(buf, pos);
        if (header.Tag != TagMpz) return false;
        pos += 8;
        if (useGmp)
        {
            int signSize = (int)U32(buf, pos + 4);
            ulong lo = U64(buf, pos + 16);
            hash = signSize < 0 ? unchecked(0 - lo) : lo;
        }
        else
        {
            ulong sign = U64(buf, pos);
            ulong lo = U64(buf, pos + 24);
            hash = sign != 0 ? unchecked(0 - lo) : lo;
        }
        return true;
    }

    static bool GetNameHash(byte[] buf, ulong offset, ulong ptr, out ulong hash)
    {
        hash = 0;
        if ((ptr & 1) == 1)
        {
            if ((ptr >> 1) == 0) { hash = NameAnonHash; return true; } // Name.anonymous
            return false;
        }
        long pos = (long)(ptr - offset);
        var header = new ObjHeader(buf, pos);
        if (header.SFields < 1) return false;
        hash = U64(buf, pos + 8 + 8 * header.NumFields);
        return true;
    }

    static bool GetStrHash(byte[] buf, ulong offset, ulong ptr, out ulong hash)
    {
        hash = 0;
        if ((ptr & 1) == 1) return false;
        long pos = (long)(ptr - offset);
        var header = new ObjHeader(buf, pos);
        if (header.Tag != TagString) return false;
        ulong size = U64(buf, pos + 8);
        hash = StrHash(buf.AsSpan(checked((int)(pos + 32)), checked((int)(size - 1))));
        return true;
    }

    static bool GetLevelData(byte[] buf, ulong offset, ulong ptr, out ulong data)
    {
        data = 0;
        if ((ptr & 1) == 1)
        {
            if ((ptr >> 1) == 0) { data = LevelZeroData; return true; } // Level.zero
            return false;
        }
        long pos = (long)(ptr - offset);
        var header = new ObjHeader(buf, pos);
        if (header.SFields < 1) return false;
        data = U64(buf, pos + 8 + 8 * header.NumFields);
        return true;
    }

    static bool GetExprData(byte[] buf, ulong offset, ulong ptr, out ulong data)
    {
        data = 0;
        if ((ptr & 1) != 0) return false;
        long pos = (long)(ptr - offset);
        var header = new ObjHeader(buf, pos);
        if (header.SFields < 1) return false;
        data = U64(buf, pos + 8 + 8 * header.NumFields);
        return true;
    }

    static bool GetLitHash(bool useGmp, byte[] buf, ulong offset, ulong ptr, out ulong hash)
    {
        hash = 0;
        if ((ptr & 1) != 0) return false;
        long pos = (long)(ptr - offset);
        var header = new ObjHeader(buf, pos);
        return header.Tag switch
        {
            0 => GetNumHash(useGmp, buf, offset, U64(buf, pos + 8), out hash),
            1 => GetStrHash(buf, offset, U64(buf, pos + 8), out hash),
            _ => false,
        };
    }

    static (ulong hash, byte bits) GetListLevelData(byte[] buf, ulong offset, ulong ptr)
    {
        ulong hash = 7; byte bits = 0;
        while ((ptr & 1) == 0)
        {
            long pos = (long)(ptr - offset);
            var header = new ObjHeader(buf, pos);
            if (header.Tag != 1) break;
            if (header.NumFields >= 2)
            {
                ulong ptr1 = U64(buf, pos + 8), ptr2 = U64(buf, pos + 16);
                if (!GetLevelData(buf, offset, ptr1, out ulong data)) break;
                var (h1, _, bits1) = UnpackLevelData(data);
                hash = MixHash(hash, h1);
                bits |= bits1;
                ptr = ptr2;
            }
            // (the Rust code loops forever on a `tag == 1` object with fewer than 2 fields; so do we not)
            else break;
        }
        return (hash, bits);
    }

    // ------------------------------------------------------------------
    // Growable output buffer

    sealed class Out
    {
        public byte[] Buf;
        public int Len;
        public Out(int capacity) { Buf = new byte[Math.Max(capacity, 256)]; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Ensure(int n)
        {
            if (Len + n > Buf.Length) Grow(n);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow(int n)
        {
            long cap = Math.Max((long)Buf.Length * 2, (long)Len + n);
            if (cap > Array.MaxLength) cap = Array.MaxLength;
            if (Len + (long)n > cap) throw new LgzException("output too large");
            Array.Resize(ref Buf, (int)cap);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Byte(byte b) { Ensure(1); Buf[Len++] = b; }
        public void Bytes(ReadOnlySpan<byte> b) { Ensure(b.Length); b.CopyTo(Buf.AsSpan(Len)); Len += b.Length; }
        public void Zeros(int n) { Ensure(n); Buf.AsSpan(Len, n).Clear(); Len += n; }
        public void U16(ushort v) { Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(Buf.AsSpan(Len), v); Len += 2; }
        public void U32(uint v) { Ensure(4); BinaryPrimitives.WriteUInt32LittleEndian(Buf.AsSpan(Len), v); Len += 4; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void U64(ulong v) { Ensure(8); BinaryPrimitives.WriteUInt64LittleEndian(Buf.AsSpan(Len), v); Len += 8; }
        public byte[] ToArray() => Buf.AsSpan(0, Len).ToArray();
    }

    // ------------------------------------------------------------------
    // Compression

    enum Mode { Normal, Exprish }

    /// <summary>
    /// Encodes one `.olean` file, or the three parts of a module (`.olean`, `.olean.server`,
    /// `.olean.private`, whose address ranges follow each other at 64 KiB boundaries), as one lgz
    /// stream.
    /// </summary>
    public static byte[] Compress(IReadOnlyList<byte[]> oleans)
    {
        byte[] result = null;
        RunDeep(() => result = CompressCore(oleans));
        return result;
    }

    static byte[] CompressCore(IReadOnlyList<byte[]> oleans)
    {
        Check(oleans.Count > 0, "no input");
        byte[] olean = oleans[0];
        var version = SniffVersion(olean);
        Check(oleans.Count == 1 || version >= OLeanVersion.V2, "multi-part modules need olean version 2");
        var h = ParseHeader(version, olean);
        Check((h.Base & ~((ulong)uint.MaxValue << 16)) == 0, "unsupported base address");
        ulong end = h.Base + (ulong)olean.Length;
        // (root, offset of the part's data in the combined buffer, the part's file)
        var parts = new List<(ulong root, int offset, byte[] file, int hdr)> { (h.Root, 0, olean, h.HeaderSize) };
        for (int i = 1; i < oleans.Count; i++)
        {
            byte[] o2 = oleans[i];
            Check(version == SniffVersion(o2), "mixed olean versions");
            var h2 = ParseHeader(version, o2);
            Check(h.UseGmp == h2.UseGmp, "mixed configurations");
            Check(h.LeanVersion.AsSpan().SequenceEqual(h2.LeanVersion) && h.Githash.AsSpan().SequenceEqual(h2.Githash), "mixed headers");
            end = (end + 0xFFFF) & ~0xFFFFUL;
            Check(end == h2.Base, "module parts are not contiguous");
            end += (ulong)o2.Length;
            parts.Add((h2.Root, checked((int)(h2.Offset - h.Offset)), o2, h2.HeaderSize));
        }
        var last = parts[^1];
        int len = last.file.Length - last.hdr + last.offset;

        // the object data of all parts at their offsets (addresses are `h.Offset + index`)
        byte[] buf;
        if (parts.Count == 1)
            buf = olean.AsSpan(h.HeaderSize).ToArray();
        else
        {
            buf = new byte[len];
            foreach (var p in parts) p.file.AsSpan(p.hdr).CopyTo(buf.AsSpan(p.offset));
        }

        // reference counts (saturating at 255): objects referenced more than once get a back reference
        var refs = new byte[len >> 3];
        ulong offset = h.Offset;
        Action<ulong> count = ptr =>
        {
            ref byte b = ref refs[checked((int)((ptr - offset) >> 3))];
            if (b != 255) b++;
        };
        foreach (var p in parts)
        {
            long pos = p.offset, partEnd = p.offset + (p.file.Length - p.hdr);
            while (pos < partEnd)
            {
                pos = OnSubobjs(h.UseGmp, buf, pos, count);
                pos = PadTo8(pos);
            }
            count(p.root);
        }

        var w = new Writer { UseGmp = h.UseGmp, Buf = buf, Offset = offset, Refs = refs, File = new Out(len / 4 + 256) };
        switch (version)
        {
            case OLeanVersion.V0:
                w.File.Bytes(Magic0);
                w.File.U32((uint)(h.Base >> 16));
                w.File.U64((ulong)olean.Length);
                break;
            case OLeanVersion.V1:
                w.File.Bytes(Magic1);
                w.File.U32((uint)(h.Base >> 16));
                w.File.U64((ulong)olean.Length);
                w.File.Bytes(h.Githash);
                break;
            default:
                w.File.Bytes(Magic2);
                w.File.U32((uint)(h.Base >> 16));
                w.File.U64(end - h.Base);
                w.File.Bytes(h.Githash);
                w.File.Byte(h.UseGmp ? (byte)1 : (byte)0);
                var regular = ParseRegularVersion(h.LeanVersion);
                if (regular != null) w.File.Bytes(regular);
                else { w.File.Byte(0); w.File.Bytes(h.LeanVersion); }
                break;
        }
        foreach (var p in parts) w.WriteObj(p.root, Mode.Normal);
        return w.File.ToArray();
    }

    sealed class Writer
    {
        public bool UseGmp;
        public byte[] Buf;
        public Out File;
        public ulong Offset;
        public byte[] Refs;
        readonly Dictionary<ulong, uint> m_backrefs = new();

        void WriteI64(Mode mode, long num)
        {
            if (num >= 0 && num < 32) WriteOp(mode, mode, (byte)(UInt0 + num));
            else if (num == (sbyte)num) { WriteOp(mode, mode, Int1); File.Byte((byte)(sbyte)num); }
            else if (num == (short)num) { WriteOp(mode, mode, Int2); File.U16((ushort)(short)num); }
            else if (num == (int)num) { WriteOp(mode, mode, Int4); File.U32((uint)(int)num); }
            else { WriteOp(mode, mode, Int8); File.U64((ulong)num); }
        }

        void WriteBackref(Mode mode, uint num)
        {
            if (num < 8) WriteOp(mode, mode, (byte)(Backref0 + num));
            else if (num <= byte.MaxValue) { WriteOp(mode, mode, Backref1); File.Byte((byte)num); }
            else if (num <= ushort.MaxValue) { WriteOp(mode, mode, Backref2); File.U16((ushort)num); }
            else { WriteOp(mode, mode, Backref4); File.U32(num); }
        }

        bool IsReused(ulong ptr) => Refs[checked((int)((ptr - Offset) >> 3))] > 1;

        void WriteOp(Mode curMode, Mode mode, byte op)
        {
            if (curMode == Mode.Normal && mode == Mode.Exprish) File.Byte(OpExprish);
            else if (curMode == Mode.Exprish && mode == Mode.Normal) File.Byte(XNormal);
            File.Byte(op);
        }

        ulong P(long pos) => U64(Buf, pos);

        /// <summary>`Expr.const` / `Expr.app`: an application spine `f a1 ... an` (n ≤ 31) is written as one opcode.</summary>
        bool ParseNaryApp(long pos, Mode mode, byte ctor, byte numFields, ushort sfields)
        {
            var stack = new List<ulong>();
            ulong ptr = 0; bool hasPtr = false;
            while (true)
            {
                if (ctor == 4 && numFields == 2 && sfields == 1)
                {
                    // Expr.const
                    ulong ptr1 = P(pos), ptr2 = P(pos + 8);
                    if (!GetNameHash(Buf, Offset, ptr1, out ulong hash1)) break;
                    var (hash2, bits2) = GetListLevelData(Buf, Offset, ptr2);
                    if (ExprConstData(hash1, hash2, bits2) == P(pos + 16))
                    {
                        WriteOp(mode, Mode.Exprish, (byte)(XExprConstApp + stack.Count));
                        WriteObj(ptr1, Mode.Exprish);
                        WriteObj(ptr2, Mode.Normal);
                        for (int i = stack.Count - 1; i >= 0; i--) WriteObj(stack[i], Mode.Exprish);
                        return true;
                    }
                }
                else if (ctor == 5 && numFields == 2 && sfields == 1 && stack.Count < 0x1f)
                {
                    // Expr.app
                    ulong ptr1 = P(pos), ptr2 = P(pos + 8);
                    if (!GetExprData(Buf, Offset, ptr1, out ulong data1)) break;
                    if (!GetExprData(Buf, Offset, ptr2, out ulong data2)) break;
                    if (ExprAppData(data1, data2) == P(pos + 16))
                    {
                        ptr = ptr1; hasPtr = true;
                        stack.Add(ptr2);
                        if ((ptr1 & 1) == 0 && !IsReused(ptr1))
                        {
                            long p1 = (long)(ptr1 - Offset);
                            var header = new ObjHeader(Buf, p1);
                            pos = p1 + 8; ctor = header.Tag; numFields = header.NumFields; sfields = header.SFields;
                            continue;
                        }
                        break;
                    }
                }
                break;
            }
            if (!hasPtr) return false;
            WriteOp(mode, Mode.Exprish, (byte)(XExprApp + stack.Count));
            WriteObj(ptr, Mode.Exprish);
            for (int i = stack.Count - 1; i >= 0; i--) WriteObj(stack[i], Mode.Exprish);
            return true;
        }

        /// <summary>Writes a constructor object as a Name/Level/Expr node if its stored hash/data field is what the decoder would recompute.</summary>
        bool TryWriteExprishCtor(long pos, Mode mode, byte ctor, byte numFields, ushort sfields)
        {
            ulong h1, h2, d1, d2, d3;
            switch ((ctor, numFields, sfields))
            {
                // Name.anonymous, Level.zero not needed because they are scalars
                case (1, 2, 1):
                {
                    // Name.str
                    ulong ptr1 = P(pos), ptr2 = P(pos + 8);
                    if (!GetNameHash(Buf, Offset, ptr1, out h1)) return false;
                    if (!GetStrHash(Buf, Offset, ptr2, out h2)) return false;
                    if (MixHash(h1, h2) == P(pos + 16))
                    {
                        WriteOp(mode, Mode.Exprish, XNameStr);
                        WriteObj(ptr1, Mode.Exprish);
                        WriteObj(ptr2, Mode.Normal);
                        return true;
                    }
                    return false;
                }
                case (1, 1, 1):
                {
                    // Level.succ
                    ulong ptr = P(pos);
                    if (GetLevelData(Buf, Offset, ptr, out d1) && LevelSuccData(d1) == P(pos + 8))
                    {
                        WriteOp(mode, Mode.Exprish, XLevelSucc);
                        WriteObj(ptr, Mode.Exprish);
                        return true;
                    }
                    // Expr.fvar
                    if (GetNameHash(Buf, Offset, ptr, out h1) && ExprFVarData(h1) == P(pos + 8))
                    {
                        WriteOp(mode, Mode.Exprish, XExprFVar);
                        WriteObj(ptr, Mode.Exprish);
                        return true;
                    }
                    return false;
                }
                case (2, 2, 1):
                {
                    // Level.max
                    ulong ptr1 = P(pos), ptr2 = P(pos + 8);
                    if (GetLevelData(Buf, Offset, ptr1, out d1) && GetLevelData(Buf, Offset, ptr2, out d2)
                        && LevelMaxData(d1, d2) == P(pos + 16))
                    {
                        WriteOp(mode, Mode.Exprish, XLevelMax);
                        WriteObj(ptr1, Mode.Exprish);
                        WriteObj(ptr2, Mode.Exprish);
                        return true;
                    }
                    // Name.num
                    if (!GetNameHash(Buf, Offset, ptr1, out h1)) return false;
                    h2 = (ptr2 & 1) == 1 ? ptr2 >> 1 : 17;
                    if (MixHash(h1, h2) == P(pos + 16))
                    {
                        WriteOp(mode, Mode.Exprish, XNameNum);
                        WriteObj(ptr1, Mode.Exprish);
                        WriteObj(ptr2, Mode.Normal);
                        return true;
                    }
                    return false;
                }
                case (3, 2, 1):
                {
                    // Level.imax
                    ulong ptr1 = P(pos), ptr2 = P(pos + 8);
                    if (!GetLevelData(Buf, Offset, ptr1, out d1) || !GetLevelData(Buf, Offset, ptr2, out d2)) return false;
                    if (LevelIMaxData(d1, d2) == P(pos + 16))
                    {
                        WriteOp(mode, Mode.Exprish, XLevelIMax);
                        WriteObj(ptr1, Mode.Exprish);
                        WriteObj(ptr2, Mode.Exprish);
                        return true;
                    }
                    return false;
                }
                case (4, 1, 1):
                {
                    // Level.param
                    ulong ptr = P(pos);
                    if (!GetNameHash(Buf, Offset, ptr, out h1)) return false;
                    if (LevelParamData(h1) == P(pos + 8))
                    {
                        WriteOp(mode, Mode.Exprish, XLevelParam);
                        WriteObj(ptr, Mode.Exprish);
                        return true;
                    }
                    return false;
                }
                case (5, 1, 1):
                {
                    // Level.mvar
                    ulong ptr = P(pos);
                    if (!GetNameHash(Buf, Offset, ptr, out h1)) return false;
                    if (LevelMVarData(h1) == P(pos + 8))
                    {
                        WriteOp(mode, Mode.Exprish, XLevelMVar);
                        WriteObj(ptr, Mode.Exprish);
                        return true;
                    }
                    return false;
                }
                case (0, 1, 1):
                {
                    // Expr.bvar
                    ulong ptr = P(pos);
                    if (!GetNumHash(UseGmp, Buf, Offset, ptr, out h1)) return false;
                    if (ExprBVarData(h1) == P(pos + 8))
                    {
                        WriteOp(mode, Mode.Exprish, XExprBVar);
                        WriteObj(ptr, Mode.Normal);
                        return true;
                    }
                    return false;
                }
                case (2, 1, 1):
                {
                    // Expr.mvar
                    ulong ptr = P(pos);
                    if (!GetNameHash(Buf, Offset, ptr, out h1)) return false;
                    if (ExprMVarData(h1) == P(pos + 8))
                    {
                        WriteOp(mode, Mode.Exprish, XExprMVar);
                        WriteObj(ptr, Mode.Exprish);
                        return true;
                    }
                    return false;
                }
                case (3, 1, 1):
                {
                    // Expr.sort
                    ulong ptr = P(pos);
                    if (!GetLevelData(Buf, Offset, ptr, out d1)) return false;
                    if (ExprSortData(d1) == P(pos + 8))
                    {
                        WriteOp(mode, Mode.Exprish, XExprSort);
                        WriteObj(ptr, Mode.Exprish);
                        return true;
                    }
                    return false;
                }
                // Expr.const, Expr.app
                case (4, 2, 1):
                case (5, 2, 1):
                    return ParseNaryApp(pos, mode, ctor, numFields, sfields);
                case (6, 3, 2):
                case (7, 3, 2):
                {
                    // Expr.lam, Expr.forallE
                    ulong name = P(pos), ty = P(pos + 8), body = P(pos + 16), data = P(pos + 24), bi = P(pos + 32);
                    if (!GetExprData(Buf, Offset, ty, out d1) || !GetExprData(Buf, Offset, body, out d2)) return false;
                    if (ExprBinderData(d1, d2) == data && (bi & ~3UL) == 0)
                    {
                        WriteOp(mode, Mode.Exprish, (byte)((ctor << 2) + (byte)bi));
                        WriteObj(name, Mode.Exprish);
                        WriteObj(ty, Mode.Exprish);
                        WriteObj(body, Mode.Exprish);
                        return true;
                    }
                    return false;
                }
                case (8, 4, 2):
                {
                    // Expr.letE
                    ulong name = P(pos), ty = P(pos + 8), value = P(pos + 16), body = P(pos + 24), data = P(pos + 32), nonDep = P(pos + 40);
                    if (!GetExprData(Buf, Offset, ty, out d1) || !GetExprData(Buf, Offset, value, out d2)
                        || !GetExprData(Buf, Offset, body, out d3)) return false;
                    if (ExprLetData(d1, d2, d3) == data && (nonDep & ~1UL) == 0)
                    {
                        WriteOp(mode, Mode.Exprish, (byte)(XExprLet + (byte)nonDep));
                        WriteObj(name, Mode.Exprish);
                        WriteObj(ty, Mode.Exprish);
                        WriteObj(value, Mode.Exprish);
                        WriteObj(body, Mode.Exprish);
                        return true;
                    }
                    return false;
                }
                case (9, 1, 1):
                {
                    // Expr.lit
                    ulong ptr = P(pos);
                    if (!GetLitHash(UseGmp, Buf, Offset, ptr, out h1)) return false;
                    if (ExprLitData(h1) == P(pos + 8))
                    {
                        WriteOp(mode, Mode.Exprish, XExprLit);
                        WriteObj(ptr, Mode.Normal);
                        return true;
                    }
                    return false;
                }
                case (10, 2, 1):
                {
                    // Expr.mdata
                    ulong ptr1 = P(pos), ptr2 = P(pos + 8);
                    if (!GetExprData(Buf, Offset, ptr2, out d1)) return false;
                    if (ExprMDataData(d1) == P(pos + 16))
                    {
                        WriteOp(mode, Mode.Exprish, XExprMData);
                        WriteObj(ptr1, Mode.Normal);
                        WriteObj(ptr2, Mode.Exprish);
                        return true;
                    }
                    return false;
                }
                case (11, 3, 1):
                {
                    // Expr.proj
                    ulong ptr1 = P(pos), ptr2 = P(pos + 8), ptr3 = P(pos + 16);
                    if (!GetNameHash(Buf, Offset, ptr1, out h1) || !GetNumHash(UseGmp, Buf, Offset, ptr2, out h2)
                        || !GetExprData(Buf, Offset, ptr3, out d3)) return false;
                    if (ExprProjData(h1, h2, d3) == P(pos + 24))
                    {
                        WriteOp(mode, Mode.Exprish, XExprProj);
                        WriteObj(ptr1, Mode.Exprish);
                        WriteObj(ptr2, Mode.Normal);
                        WriteObj(ptr3, Mode.Exprish);
                        return true;
                    }
                    return false;
                }
                default:
                    return false;
            }
        }

        public void WriteObj(ulong ptr, Mode mode)
        {
            if ((ptr & 1) == 1)
            {
                WriteI64(mode, (long)ptr >> 1);
                return;
            }
            if (m_backrefs.TryGetValue(ptr, out uint idx))
            {
                WriteBackref(mode, idx);
                return;
            }
            bool save = IsReused(ptr);
            if (save) WriteOp(mode, mode, Save);
            long start = (long)(ptr - Offset);
            var header = new ObjHeader(Buf, start);
            long pos = start + 8;
            switch (header.Tag)
            {
                case TagArray:
                {
                    WriteOp(mode, Mode.Normal, OpArray);
                    ulong size = P(pos);
                    WriteI64(Mode.Normal, checked((long)size));
                    pos += 16;
                    for (ulong i = 0; i < size; i++, pos += 8) WriteObj(P(pos), Mode.Normal);
                    break;
                }
                case TagScalarArray:
                {
                    WriteOp(mode, Mode.Normal, OpScalarArray);
                    ulong size = P(pos);
                    WriteI64(Mode.Normal, checked((long)size));
                    File.Bytes(Buf.AsSpan(checked((int)(pos + 16)), checked((int)size)));
                    break;
                }
                case TagString:
                {
                    WriteOp(mode, Mode.Normal, OpString);
                    ulong size = P(pos);
                    var s = Buf.AsSpan(checked((int)(pos + 24)), checked((int)size));
                    // Internal nulls are not supported
                    Check(s.IndexOf((byte)0) == s.Length - 1, "string with an interior NUL");
                    File.Bytes(s);
                    break;
                }
                case TagMpz:
                {
                    int capacity; uint signSize;
                    if (UseGmp)
                    {
                        capacity = checked((int)U32(Buf, pos));
                        signSize = U32(Buf, pos + 4);
                        pos += 16;
                    }
                    else
                    {
                        ulong sign = P(pos), size = P(pos + 8);
                        pos += 24;
                        ulong cap = (size + 1) >> 1;
                        Check(cap <= int.MaxValue, "bad mpz");
                        capacity = (int)cap;
                        signSize = sign != 0 ? (uint)(-capacity) : (uint)capacity;
                        Check((size & 1) == 0 || U32(Buf, pos + 8L * capacity - 4) == 0, "bad mpz");
                    }
                    WriteOp(mode, Mode.Normal, OpMpz);
                    WriteI64(Mode.Normal, signSize); // `u32 -> i64`: zero-extended
                    File.Bytes(Buf.AsSpan(checked((int)pos), 8 * capacity));
                    break;
                }
                case TagThunk:
                case TagTask:
                case TagRef:
                case TagPromise:
                {
                    byte op = header.Tag switch { TagThunk => OpThunk, TagTask => OpTask, TagRef => OpRef, _ => OpPromise };
                    WriteOp(mode, Mode.Normal, op);
                    WriteObj(P(pos), Mode.Normal);
                    break;
                }
                case TagClosure:
                case TagStructArray:
                case TagExternal:
                case TagReserved:
                    throw new LgzException("unsupported object");
                default:
                {
                    byte ctor = header.Tag;
                    ushort sfields = (ushort)((header.CsSz >> 3) - 1 - header.NumFields);
                    if (!TryWriteExprishCtor(pos, mode, ctor, header.NumFields, sfields))
                    {
                        int packed = PackCtor(ctor, header.NumFields, sfields);
                        if (packed >= 0) WriteOp(mode, Mode.Normal, (byte)packed);
                        else
                        {
                            WriteOp(mode, Mode.Normal, OpBigCtor);
                            File.Byte(ctor); File.Byte(header.NumFields);
                            File.U16(sfields);
                        }
                        for (int i = 0; i < header.NumFields; i++, pos += 8) WriteObj(P(pos), Mode.Normal);
                        File.Bytes(Buf.AsSpan(checked((int)pos), 8 * sfields));
                    }
                    break;
                }
            }
            if (save) m_backrefs.Add(ptr, checked((uint)m_backrefs.Count));
        }
    }

    // ------------------------------------------------------------------
    // Decompression

    /// <summary>
    /// Decodes an lgz stream holding `n` parts. Returns the buffer with all parts (each padded to
    /// a 64 KiB boundary except the last) and the range of each `.olean` file in it.
    /// </summary>
    public static (byte[] buffer, (int start, int end)[] ranges) Decompress(ReadOnlySpan<byte> input, int n)
    {
        byte[] src = input.ToArray();
        (byte[], (int, int)[]) result = default;
        RunDeep(() => result = DecompressCore(src, n));
        return result;
    }

    static (byte[], (int, int)[]) DecompressCore(byte[] src, int n)
    {
        Check(src.Length >= 16 && n >= 1, "truncated lgz stream");
        int version;
        var magic = src.AsSpan(0, 4);
        if (magic.SequenceEqual(Magic2)) version = 2;
        else if (magic.SequenceEqual(Magic1)) version = 1;
        else if (magic.SequenceEqual(Magic0)) version = 0;
        else throw new LgzException("unrecognized LGZ version");
        ulong baseAddr = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(4)) << 16;
        ulong capacity = BinaryPrimitives.ReadUInt64LittleEndian(src.AsSpan(8));
        var d = new Decompressor
        {
            UseGmp = UseGmp(false),
            Src = src, Pos = 16,
            Buf = new Out(checked((int)Math.Min(capacity, (ulong)Array.MaxLength))),
            Offset = baseAddr,
        };
        switch (version)
        {
            case 0:
                d.Buf.Bytes("oleanfile!!!!!!!"u8);
                break;
            case 1:
            {
                d.Buf.Bytes("olean"u8);
                d.Buf.Byte(1);
                var githash = d.Take(40);
                if (MacV1UseGmp(githash)) d.UseGmp = UseGmp(true);
                d.Buf.Bytes(githash);
                d.Buf.Zeros(2);
                break;
            }
            default:
            {
                d.Buf.Bytes("olean"u8);
                d.Buf.Byte(2);
                var githash = d.Take(40).ToArray();
                byte flags = d.ReadU8();
                d.UseGmp = (flags & 1) != 0;
                d.Buf.Byte(d.UseGmp ? (byte)1 : (byte)0);
                var leanVersion = new byte[33];
                byte major = d.ReadU8();
                if (major == 0) d.Take(33).CopyTo(leanVersion);
                else
                {
                    byte minor = d.ReadU8(), patch = d.ReadU8(), rcM1 = d.ReadU8();
                    string v = rcM1 == byte.MaxValue ? $"{major}.{minor}.{patch}" : $"{major}.{minor}.{patch}-rc{rcM1 + 1}";
                    System.Text.Encoding.ASCII.GetBytes(v).CopyTo(leanVersion, 0);
                }
                d.Buf.Bytes(leanVersion);
                d.Buf.Bytes(githash);
                break;
            }
        }
        int basePos = d.Buf.Len;
        d.Buf.U64(baseAddr);
        int fixupPos = d.Buf.Len;
        d.Buf.U64(0); // fixed below
        int headerSize = d.Buf.Len;
        var ranges = new List<(int, int)>();
        int extra = 0;
        while (true)
        {
            ulong root = d.WriteObj();
            BinaryPrimitives.WriteUInt64LittleEndian(d.Buf.Buf.AsSpan(extra + fixupPos), root);
            ranges.Add((extra, d.Buf.Len));
            if (--n == 0) break;
            d.Buf.Zeros(((d.Buf.Len + 0xFFFF) & ~0xFFFF) - d.Buf.Len);
            extra = d.Buf.Len;
            d.Buf.Bytes(d.Buf.Buf.AsSpan(0, headerSize).ToArray());
            BinaryPrimitives.WriteUInt64LittleEndian(d.Buf.Buf.AsSpan(extra + basePos), baseAddr + (ulong)extra);
        }
        return (d.Buf.ToArray(), ranges.ToArray());
    }

    sealed class Decompressor
    {
        public bool UseGmp;
        public byte[] Src;
        public int Pos;
        public Out Buf;
        public ulong Offset;
        readonly List<ulong> m_backrefs = new();
        ulong[] m_stack = new ulong[1024];
        int m_stackLen;

        public ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Pos + (long)n > Src.Length) throw new LgzException("truncated lgz stream");
            var s = Src.AsSpan(Pos, n);
            Pos += n;
            return s;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadU8()
        {
            if (Pos >= Src.Length) throw new LgzException("truncated lgz stream");
            return Src[Pos++];
        }

        ulong Here => Offset + (ulong)Buf.Len;

        ulong WriteHeader(byte tag, ushort csSz, byte numFields)
        {
            ulong pos = Here;
            Buf.U32(0); Buf.U16(csSz); Buf.Byte(numFields); Buf.Byte(tag);
            return pos;
        }

        void Copy(int size, int sizePadded)
        {
            Buf.Bytes(Take(size));
            Buf.Zeros(sizePadded - size);
        }

        void Push(ulong v)
        {
            if (m_stackLen == m_stack.Length) Array.Resize(ref m_stack, m_stack.Length * 2);
            m_stack[m_stackLen++] = v;
        }

        void Pop(int start)
        {
            for (int i = start; i < m_stackLen; i++) Buf.U64(m_stack[i]);
            m_stackLen = start;
        }

        long ReadI64(byte tag)
        {
            if (tag >= UInt0 && tag <= UInt0End) return tag - UInt0;
            switch (tag)
            {
                case Int1: return (sbyte)ReadU8();
                case Int2: return BinaryPrimitives.ReadInt16LittleEndian(Take(2));
                case Int4: return BinaryPrimitives.ReadInt32LittleEndian(Take(4));
                case Int8: return BinaryPrimitives.ReadInt64LittleEndian(Take(8));
                default: throw new LgzException("unexpected int");
            }
        }

        uint ReadBackref(byte tag)
        {
            if (tag >= Backref0 && tag <= Backref0End) return (uint)(tag - Backref0);
            switch (tag)
            {
                case Backref1: return ReadU8();
                case Backref2: return BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
                case Backref4: return BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
                default: throw new LgzException("unexpected backref");
            }
        }

        ulong Backref(uint r)
        {
            if (r >= (uint)m_backrefs.Count) throw new LgzException("invalid back reference");
            return m_backrefs[(int)r];
        }

        ulong WriteCtorHeader(byte ctor, byte numFields, ushort sfields) =>
            WriteHeader(ctor, (ushort)((numFields + 1 + sfields) << 3), numFields);

        ulong WriteCtor(byte ctor, byte numFields, ushort sfields)
        {
            int start = m_stackLen;
            for (int i = 0; i < numFields; i++) Push(WriteObj());
            ulong pos = WriteCtorHeader(ctor, numFields, sfields);
            Pop(start);
            int size = sfields << 3;
            Copy(size, size);
            return pos;
        }

        ulong WriteStr()
        {
            int start = Pos;
            ulong len = 0;
            while (true)
            {
                byte c = ReadU8();
                if (c == 0) break;
                if ((c & 0xC0) != 0x80) len++;
            }
            int size = Pos - start; // includes the NUL
            ulong pos = WriteHeader(TagString, 1, 0);
            Buf.U64((ulong)size);
            Buf.U64((ulong)size);
            Buf.U64(len);
            Buf.Bytes(Src.AsSpan(start, size));
            Buf.Zeros((int)(PadTo8(size) - size));
            return pos;
        }

        // The decoder reads the data fields of objects it has just written from the output buffer.
        byte[] B => Buf.Buf;

        ulong NameHash(ulong ptr) => GetNameHash(B, Offset, ptr, out ulong h) ? h : throw new LgzException("expected a name");
        ulong StrHashOf(ulong ptr) => GetStrHash(B, Offset, ptr, out ulong h) ? h : throw new LgzException("expected a string");
        ulong LevelData(ulong ptr) => GetLevelData(B, Offset, ptr, out ulong d) ? d : throw new LgzException("expected a level");
        ulong ExprData(ulong ptr) => GetExprData(B, Offset, ptr, out ulong d) ? d : throw new LgzException("expected an expression");
        ulong NumHash(ulong ptr) => GetNumHash(UseGmp, B, Offset, ptr, out ulong h) ? h : throw new LgzException("expected a number");
        ulong LitHash(ulong ptr) => GetLitHash(UseGmp, B, Offset, ptr, out ulong h) ? h : throw new LgzException("expected a literal");

        ulong WriteExprish()
        {
            byte tag = ReadU8();
            ulong pos, ptr, ptr1, ptr2, ptr3;
            switch (tag)
            {
                case XNormal:
                    return WriteObj();
                case XNameStr:
                    ptr1 = WriteExprish(); ptr2 = WriteObj();
                    pos = WriteCtorHeader(1, 2, 1);
                    Buf.U64(ptr1); Buf.U64(ptr2);
                    Buf.U64(MixHash(NameHash(ptr1), StrHashOf(ptr2)));
                    return pos;
                case XNameNum:
                    ptr1 = WriteExprish(); ptr2 = WriteObj();
                    pos = WriteCtorHeader(2, 2, 1);
                    Buf.U64(ptr1); Buf.U64(ptr2);
                    Buf.U64(MixHash(NameHash(ptr1), (ptr2 & 1) == 1 ? ptr2 >> 1 : 17));
                    return pos;
                case XLevelSucc:
                    ptr = WriteExprish();
                    pos = WriteCtorHeader(1, 1, 1);
                    Buf.U64(ptr);
                    Buf.U64(LevelSuccData(LevelData(ptr)));
                    return pos;
                case XLevelMax:
                    ptr1 = WriteExprish(); ptr2 = WriteExprish();
                    pos = WriteCtorHeader(2, 2, 1);
                    Buf.U64(ptr1); Buf.U64(ptr2);
                    Buf.U64(LevelMaxData(LevelData(ptr1), LevelData(ptr2)));
                    return pos;
                case XLevelIMax:
                    ptr1 = WriteExprish(); ptr2 = WriteExprish();
                    pos = WriteCtorHeader(3, 2, 1);
                    Buf.U64(ptr1); Buf.U64(ptr2);
                    Buf.U64(LevelIMaxData(LevelData(ptr1), LevelData(ptr2)));
                    return pos;
                case XLevelParam:
                    ptr = WriteExprish();
                    pos = WriteCtorHeader(4, 1, 1);
                    Buf.U64(ptr);
                    Buf.U64(LevelParamData(NameHash(ptr)));
                    return pos;
                case XLevelMVar:
                    ptr = WriteExprish();
                    pos = WriteCtorHeader(5, 1, 1);
                    Buf.U64(ptr);
                    Buf.U64(LevelMVarData(NameHash(ptr)));
                    return pos;
                case XExprBVar:
                    ptr = WriteObj();
                    pos = WriteCtorHeader(0, 1, 1);
                    Buf.U64(ptr);
                    Buf.U64(ExprBVarData(NumHash(ptr)));
                    return pos;
                case XExprFVar:
                    ptr = WriteExprish();
                    pos = WriteCtorHeader(1, 1, 1);
                    Buf.U64(ptr);
                    Buf.U64(ExprFVarData(NameHash(ptr)));
                    return pos;
                case XExprMVar:
                    ptr = WriteExprish();
                    pos = WriteCtorHeader(2, 1, 1);
                    Buf.U64(ptr);
                    Buf.U64(ExprMVarData(NameHash(ptr)));
                    return pos;
                case XExprSort:
                    ptr = WriteExprish();
                    pos = WriteCtorHeader(3, 1, 1);
                    Buf.U64(ptr);
                    Buf.U64(ExprSortData(LevelData(ptr)));
                    return pos;
                case XExprLit:
                    ptr = WriteObj();
                    pos = WriteCtorHeader(9, 1, 1);
                    Buf.U64(ptr);
                    Buf.U64(ExprLitData(LitHash(ptr)));
                    return pos;
                case XExprMData:
                    ptr1 = WriteObj(); ptr2 = WriteExprish();
                    pos = WriteCtorHeader(10, 2, 1);
                    Buf.U64(ptr1); Buf.U64(ptr2);
                    Buf.U64(ExprMDataData(ExprData(ptr2)));
                    return pos;
                case XExprProj:
                    ptr1 = WriteExprish(); ptr2 = WriteObj(); ptr3 = WriteExprish();
                    pos = WriteCtorHeader(11, 3, 1);
                    Buf.U64(ptr1); Buf.U64(ptr2); Buf.U64(ptr3);
                    Buf.U64(ExprProjData(NameHash(ptr1), NumHash(ptr2), ExprData(ptr3)));
                    return pos;
                case Save:
                    pos = WriteExprish();
                    m_backrefs.Add(pos);
                    return pos;
            }
            if (tag >= XExprLet && tag <= XExprLetEnd)
            {
                ulong name = WriteExprish(), ty = WriteExprish(), value = WriteExprish(), body = WriteExprish();
                pos = WriteCtorHeader(8, 4, 2);
                Buf.U64(name); Buf.U64(ty); Buf.U64(value); Buf.U64(body);
                Buf.U64(ExprLetData(ExprData(ty), ExprData(value), ExprData(body)));
                Buf.U64((ulong)(tag & 1));
                return pos;
            }
            if (tag >= XExprLambda && tag <= XExprForallEnd)
            {
                ulong name = WriteExprish(), ty = WriteExprish(), body = WriteExprish();
                pos = WriteCtorHeader((byte)(tag >> 2), 3, 2);
                Buf.U64(name); Buf.U64(ty); Buf.U64(body);
                Buf.U64(ExprBinderData(ExprData(ty), ExprData(body)));
                Buf.U64((ulong)(tag & 3));
                return pos;
            }
            if (tag > XExprApp && tag <= XExprAppEnd)
            {
                pos = WriteExprish();
                ulong data = ExprData(pos);
                for (int i = 0; i < (tag & 0x1f); i++)
                {
                    ulong arg = WriteExprish();
                    ulong pos2 = WriteCtorHeader(5, 2, 1);
                    data = ExprAppData(data, ExprData(arg));
                    Buf.U64(pos); Buf.U64(arg); Buf.U64(data);
                    pos = pos2;
                }
                return pos;
            }
            if (tag >= XExprConstApp && tag <= XExprConstAppEnd)
            {
                ptr1 = WriteExprish();
                ulong hash1 = NameHash(ptr1);
                ptr2 = WriteObj();
                var (hash2, bits2) = GetListLevelData(B, Offset, ptr2);
                ulong data = ExprConstData(hash1, hash2, bits2);
                pos = WriteCtorHeader(4, 2, 1);
                Buf.U64(ptr1); Buf.U64(ptr2); Buf.U64(data);
                for (int i = 0; i < (tag & 0x1f); i++)
                {
                    ulong arg = WriteExprish();
                    ulong pos2 = WriteCtorHeader(5, 2, 1);
                    data = ExprAppData(data, ExprData(arg));
                    Buf.U64(pos); Buf.U64(arg); Buf.U64(data);
                    pos = pos2;
                }
                return pos;
            }
            if ((tag >= UInt0 && tag <= UInt0End) || (tag >= Int1 && tag <= Int8))
                return (ulong)((ReadI64(tag) << 1) | 1);
            if ((tag >= Backref0 && tag <= Backref0End) || (tag >= Backref1 && tag <= Backref4))
                return Backref(ReadBackref(tag));
            throw new LgzException($"unexpected exprish opcode 0x{tag:x2}");
        }

        public ulong WriteObj()
        {
            byte tag = ReadU8();
            ulong pos, value;
            switch (tag)
            {
                case OpBigCtor:
                {
                    byte ctor = ReadU8(), numFields = ReadU8();
                    ushort sfields = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
                    return WriteCtor(ctor, numFields, sfields);
                }
                case Save:
                    pos = WriteObj();
                    m_backrefs.Add(pos);
                    return pos;
                case OpArray:
                {
                    ulong size = (ulong)ReadI64(ReadU8());
                    if (size > (ulong)(Src.Length - Pos)) throw new LgzException("truncated lgz stream");
                    int start = m_stackLen;
                    for (ulong i = 0; i < size; i++) Push(WriteObj());
                    pos = WriteHeader(TagArray, 1, 0);
                    Buf.U64(size); Buf.U64(size);
                    Pop(start);
                    return pos;
                }
                case OpScalarArray:
                {
                    ulong size = (ulong)ReadI64(ReadU8());
                    if (size > (ulong)(Src.Length - Pos)) throw new LgzException("truncated lgz stream");
                    pos = WriteHeader(TagScalarArray, 1, 0);
                    Buf.U64(size); Buf.U64(size);
                    Copy((int)size, (int)PadTo8((long)size));
                    return pos;
                }
                case OpString:
                    return WriteStr();
                case OpMpz:
                {
                    int signSize = (int)ReadI64(ReadU8());
                    uint capacity = (uint)signSize & 0x7FFFFFFF;
                    if (capacity > (uint)(Src.Length - Pos) / 8) throw new LgzException("truncated lgz stream");
                    int size = (int)capacity << 3;
                    if (UseGmp)
                    {
                        pos = WriteHeader(TagMpz, (ushort)((capacity + 3) << 3), 0);
                        Buf.U32(capacity);
                        Buf.U32((uint)signSize);
                        Buf.U64(Here + 8);
                        Copy(size, size);
                    }
                    else
                    {
                        var limbs = Take(size);
                        int size2 = size >> 2;
                        if (size >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(limbs.Slice(size - 4)) == 0) size2--;
                        pos = WriteHeader(TagMpz, (ushort)((size2 + 8) << 2), 0);
                        Buf.U64(signSize < 0 ? 1UL : 0UL);
                        Buf.U64((ulong)size2);
                        Buf.U64(Here + 8);
                        Buf.Bytes(limbs);
                    }
                    return pos;
                }
                case OpThunk:
                    value = WriteObj();
                    pos = WriteHeader(TagThunk, 3 << 3, 0);
                    Buf.U64(value); Buf.U64(0);
                    return pos;
                case OpTask:
                    value = WriteObj();
                    pos = WriteHeader(TagTask, 3 << 3, 0);
                    Buf.U64(value); Buf.U64(0);
                    return pos;
                case OpRef:
                    value = WriteObj();
                    pos = WriteHeader(TagRef, 2 << 3, 0);
                    Buf.U64(value);
                    return pos;
                case OpPromise:
                    value = WriteObj();
                    pos = WriteHeader(TagPromise, 2 << 3, 0);
                    Buf.U64(value);
                    return pos;
                case OpExprish:
                    return WriteExprish();
            }
            if ((tag >= Backref0 && tag <= Backref0End) || (tag >= Backref1 && tag <= Backref4))
                return Backref(ReadBackref(tag));
            if (tag <= 0xcf)
            {
                var (numFields, sfields) = UnpackCtor(tag);
                return WriteCtor((byte)(tag >> 4), numFields, sfields);
            }
            return (ulong)((ReadI64(tag) << 1) | 1);
        }
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The encoder and decoder recurse along the object graph (a list of n elements nests n
    /// deep), so they run on a thread with a large stack.
    /// </summary>
    static void RunDeep(Action action)
    {
        Exception error = null;
        var th = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        }, 1024 * 1024 * 1024);
        th.Start();
        th.Join();
        if (error != null)
        {
            if (error is LgzException) throw new LgzException(error.Message);
            throw new LgzException("invalid data: " + error.Message);
        }
    }
}
