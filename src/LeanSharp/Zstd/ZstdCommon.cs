// Pure managed Zstandard (RFC 8878): shared constants, helpers and XXH64.
using System.Numerics;
using System.Runtime.CompilerServices;

namespace LeanSharp.Zstd;

public sealed class ZstdException : Exception
{
    public ZstdException(string message) : base(message) { }
    public ZstdException(string message, Exception inner) : base(message, inner) { }
}

internal static unsafe class Zc
{
    public const uint FrameMagic = 0xFD2FB528;
    public const uint DictMagic = 0xEC30A437;
    public const uint SkippableMagic = 0x184D2A50;
    public const int BlockMax = 1 << 17;

    public const int MaxLL = 35, MaxML = 52, MaxOff = 31;
    public const int LLFseLog = 9, MLFseLog = 9, OffFseLog = 8;
    public const int LLDefaultLog = 6, MLDefaultLog = 6, OffDefaultLog = 5;

    public static readonly uint[] LLBase =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096,
        8192, 16384, 32768, 65536,
    };
    public static readonly byte[] LLBits =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12,
        13, 14, 15, 16,
    };
    /// <summary>Match length baselines (actual lengths, minimum 3).</summary>
    public static readonly uint[] MLBase =
    {
        3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
        19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
        35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051,
        4099, 8195, 16387, 32771, 65539,
    };
    public static readonly byte[] MLBits =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11,
        12, 13, 14, 15, 16,
    };

    public static readonly short[] LLDefaultNorm =
    {
        4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1,
        -1, -1, -1, -1,
    };
    public static readonly short[] MLDefaultNorm =
    {
        1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1,
        -1, -1, -1, -1, -1,
    };
    public static readonly short[] OffDefaultNorm =
    {
        1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HighBit(uint v) => 31 - BitOperations.LeadingZeroCount(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Read32(byte* p)
    {
        uint v = Unsafe.ReadUnaligned<uint>(p);
        return BitConverter.IsLittleEndian ? v : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read64(byte* p)
    {
        ulong v = Unsafe.ReadUnaligned<ulong>(p);
        return BitConverter.IsLittleEndian ? v : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
    }

    public static uint Read32(ReadOnlySpan<byte> s, int pos)
    {
        if ((uint)pos > (uint)s.Length || s.Length - pos < 4) throw new ZstdException("truncated input");
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(pos, 4));
    }

    public static void Write32(byte[] d, int pos, uint v)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(pos, 4), v);
    }
}

/// <summary>XXH64 (used for the frame content checksum).</summary>
internal static class XxHash64
{
    const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL,
                P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Round(ulong acc, ulong input)
    {
        acc += input * P2;
        acc = BitOperations.RotateLeft(acc, 31);
        return acc * P1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong R64(ReadOnlySpan<byte> s, int p) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(p, 8));

    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        int len = data.Length, p = 0;
        ulong h;
        if (len >= 32)
        {
            ulong v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
            int limit = len - 32;
            do
            {
                v1 = Round(v1, R64(data, p));
                v2 = Round(v2, R64(data, p + 8));
                v3 = Round(v3, R64(data, p + 16));
                v4 = Round(v4, R64(data, p + 24));
                p += 32;
            } while (p <= limit);
            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) +
                BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            h = (h ^ Round(0, v1)) * P1 + P4;
            h = (h ^ Round(0, v2)) * P1 + P4;
            h = (h ^ Round(0, v3)) * P1 + P4;
            h = (h ^ Round(0, v4)) * P1 + P4;
        }
        else h = seed + P5;
        h += (ulong)len;
        while (p + 8 <= len)
        {
            h ^= Round(0, R64(data, p));
            h = BitOperations.RotateLeft(h, 27) * P1 + P4;
            p += 8;
        }
        if (p + 4 <= len)
        {
            h ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p, 4)) * P1;
            h = BitOperations.RotateLeft(h, 23) * P2 + P3;
            p += 4;
        }
        while (p < len)
        {
            h ^= data[p] * P5;
            h = BitOperations.RotateLeft(h, 11) * P1;
            p++;
        }
        h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
        return h;
    }
}
