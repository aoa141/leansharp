// Zstandard dictionaries (formatted, magic 0xEC30A437, or raw content).
namespace LeanSharp.Zstd;

public sealed class ZstdDictionary
{
    /// <summary>Dictionary ID (0 for a raw-content dictionary).</summary>
    public uint Id { get; }

    internal readonly byte[] Content;
    internal readonly int[] Rep = { 1, 4, 8 };
    internal readonly bool HasEntropy;

    // decoder side
    internal readonly HufDTable HufD;
    internal readonly SeqTable LLD, OFD, MLD;
    // encoder side
    internal readonly HufCTable HufC;
    internal readonly FseTableState LLC, OFC, MLC;

    readonly object _lock = new object();
    readonly Dictionary<int, (int[] head, int[] chain)> _prepared = new Dictionary<int, (int[], int[])>();

    /// <param name="dictionary">a zstd dictionary file (magic 0xEC30A437) or raw content</param>
    public ZstdDictionary(byte[] dictionary)
    {
        if (dictionary == null) throw new ArgumentNullException(nameof(dictionary));
        ReadOnlySpan<byte> d = dictionary;
        if (d.Length < 8 || Zc.Read32(d, 0) != Zc.DictMagic)
        {
            Id = 0;
            Content = (byte[])dictionary.Clone();
            return;
        }
        try
        {
            Id = Zc.Read32(d, 4);
            int pos = 8;
            HufC = new HufCTable();
            HufD = Huffman.ReadTable(d.Slice(pos), out int used, HufC.Lens, out int hufLog);
            pos += used;
            Huffman.AssignCodes(HufC);

            short[] ofNorm = new short[Zc.MaxOff + 1];
            int ofMax = Zc.MaxOff;
            pos += Fse.ReadNCount(d.Slice(pos), ofNorm, ref ofMax, Zc.OffFseLog, out int ofLog);
            short[] mlNorm = new short[Zc.MaxML + 1];
            int mlMax = Zc.MaxML;
            pos += Fse.ReadNCount(d.Slice(pos), mlNorm, ref mlMax, Zc.MLFseLog, out int mlLog);
            short[] llNorm = new short[Zc.MaxLL + 1];
            int llMax = Zc.MaxLL;
            pos += Fse.ReadNCount(d.Slice(pos), llNorm, ref llMax, Zc.LLFseLog, out int llLog);

            OFD = SeqTable.Build(ofNorm, ofMax, ofLog, null, null);
            MLD = SeqTable.Build(mlNorm, mlMax, mlLog, Zc.MLBase, Zc.MLBits);
            LLD = SeqTable.Build(llNorm, llMax, llLog, Zc.LLBase, Zc.LLBits);
            OFC = FseTableState.Create(ofNorm, ofMax, ofLog);
            MLC = FseTableState.Create(mlNorm, mlMax, mlLog);
            LLC = FseTableState.Create(llNorm, llMax, llLog);

            if (d.Length - pos < 12) throw new ZstdException("truncated dictionary");
            int contentLen = d.Length - pos - 12;
            for (int i = 0; i < 3; i++)
            {
                uint r = Zc.Read32(d, pos + 4 * i);
                if (r == 0 || r > (uint)contentLen) throw new ZstdException("corrupt dictionary (repeat offsets)");
                Rep[i] = (int)r;
            }
            pos += 12;
            Content = d.Slice(pos).ToArray();
            HasEntropy = true;
        }
        catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException)
        {
            throw new ZstdException("corrupt dictionary", e);
        }
    }

    /// <summary>
    /// Match finder tables (hash heads and chain links) for the dictionary content, built once per
    /// hash size and shared by all compressions with this dictionary.
    /// </summary>
    internal unsafe void GetPrepared(int hashLog, out int[] head, out int[] chain)
    {
        lock (_lock)
        {
            if (!_prepared.TryGetValue(hashLog, out var p))
            {
                int n = Content.Length;
                p.head = new int[1 << hashLog];
                p.chain = new int[n];
                fixed (byte* b = Content)
                {
                    for (int i = 1; i + 4 <= n; i++)
                    {
                        uint h = ZstdEncoder.Hash4(Zc.Read32(b + i), hashLog);
                        p.chain[i] = p.head[h];
                        p.head[h] = i;
                    }
                }
                _prepared[hashLog] = p;
            }
            head = p.head;
            chain = p.chain;
        }
    }
}
