// Managed API for reading and writing compacted region files (`.olean` & co.). The Lean externs in
// LeanRt.Compact.cs are thin wrappers around this.

using System.Runtime.CompilerServices;
using System.Text;

namespace LeanSharp.Runtime.Compact;

public static class OleanFile
{
    /// <summary>Version string written into new files (`get_short_version_string()`).</summary>
    public static string LeanVersion = "4.36.0-pre";
    /// <summary>Githash written into new files (`LEAN_GITHASH`).</summary>
    public static string GitHash = "77f336f7ae6a60419d3882e0d5ca7ac3a2155528";
    /// <summary>Write big numbers in the GMP layout (flags bit 0), as the native release build does.</summary>
    public static bool UseGmpLayout = true;

    /// <summary>
    /// Read imported modules in parallel ahead of time (see OleanPrefetcher.cs). Off by default;
    /// also enabled by the environment variable LEANSHARP_OLEAN_PREFETCH=1.
    /// </summary>
    public static bool PrefetchImports
    {
        get => OleanPrefetcher.Enabled;
        set { OleanPrefetcher.Enabled = value; if (!value) OleanPrefetcher.Discard(); }
    }

    /// <summary>Maximum number of prefetch threads.</summary>
    public static int PrefetchThreads { get => OleanPrefetcher.MaxWorkers; set => OleanPrefetcher.MaxWorkers = Math.Max(1, value); }

    /// <summary>Drops prefetched regions that were not requested (e.g. after importing).</summary>
    public static void DiscardPrefetched() => OleanPrefetcher.Discard();

    static OleanFile()
    {
        if (Environment.GetEnvironmentVariable("LEANSHARP_OLEAN_PREFETCH") == "1") OleanPrefetcher.Enabled = true;
    }

    /// <summary>
    /// Reads a compacted region as `CompactedRegion.read` does (using prefetched data when
    /// `PrefetchImports` is enabled).
    /// </summary>
    public static CompactedRegionData ReadForLean(string path, params CompactedRegionData[] deps) =>
        OleanPrefetcher.Read(path, deps);

    public static OleanHeader ReadHeader(string path)
    {
        var b = new byte[OleanHeader.Size];
        using var f = File.OpenRead(path);
        int n = f.ReadAtLeast(b, b.Length, throwOnEndOfStream: false);
        if (n != b.Length || !OleanHeader.TryParse(b, out var h))
            throw new OleanFormatException("invalid header");
        return h;
    }

    /// <summary>
    /// Reads a compacted region. Pointers outside of the file are resolved in `deps` (e.g. the
    /// `.olean` part when reading the `.olean.server` part). The objects are persistent.
    /// </summary>
    public static CompactedRegionData Read(string path, params CompactedRegionData[] deps) =>
        RegionReader.ReadFile(path, deps);

    /// <summary>
    /// Releases the per-thread scratch buffers of the reader (e.g. after importing). They are
    /// recreated on demand; pointer resolution into recently read regions becomes a bit slower.
    /// </summary>
    public static void ReleaseScratchBuffers() => RegionReader.ReleaseScratch();

    /// <summary>Reads a compacted region from an in-memory image of the file.</summary>
    public static CompactedRegionData Parse(string path, byte[] image, params CompactedRegionData[] deps) =>
        RegionReader.Parse(path, image, image.LongLength, deps);

    /// <summary>`lean_name_hash` of a `Name` object.</summary>
    public static ulong NameHash(Obj n) =>
        LeanRt.lean_is_scalar(n) ? 1723UL : LeanRt.lean_ctor_get_uint64_s(n, 0);

    /// <summary>Hash of a `Name` built from its components (for checks): Name.str hash = mixHash(p, String.hash).</summary>
    public static ulong ComputeNameHash(Obj n)
    {
        if (LeanRt.lean_is_scalar(n)) return 1723UL;
        ulong ph = ComputeNameHash(LeanRt.lean_ctor_get(n, 0));
        Obj f = LeanRt.lean_ctor_get(n, 1);
        if (n.m_tag == 1)
            return LeanHash.Mix(ph, LeanHash.HashStr(LeanRt.lean_string_span(f), 11));
        var v = LeanRt.lean_nat_to_big(f);
        return LeanHash.Mix(ph, v < ulong.MaxValue + (System.Numerics.BigInteger)1 ? (ulong)v : 17UL);
    }

    /// <summary>The deterministic base address native Lean derives from the module name.</summary>
    public static ulong BaseAddrForKey(Obj name)
    {
        ulong a = NameHash(name);
        a %= 0x7f0000000000UL;
        a &= ~(OleanLayout.PartAlign - 1);
        return a;
    }

    public static ObjectCompactor NewCompactor(Obj key, IEnumerable<CompactedRegionData> deps = null, bool allowClosures = false) =>
        new ObjectCompactor(BaseAddrForKey(key), deps, allowClosures, UseGmpLayout);

    /// <summary>
    /// Compacts `data` as the next part of `compactor` and returns the file image
    /// (port of the body of `lean_compacted_region_save`).
    /// </summary>
    /// <param name="v3">Write the `v3` format (closure tables); defaults to the compactor's `AllowClosures`.
    /// As in native Lean, closures are accepted iff the compactor was created with `allowClosures`.</param>
    public static byte[] SavePart(ObjectCompactor compactor, Obj data, bool? v3 = null)
    {
        bool isV3 = v3 ?? compactor.AllowClosures;
        const long ALIGN = (long)OleanLayout.PartAlign;
        if (compactor.Size % ALIGN != 0)
            compactor.Alloc(ALIGN - compactor.Size % ALIGN);
        long fileOffset = compactor.Size;
        compactor.Alloc(OleanHeader.Size);
        var header = new OleanHeader
        {
            Version = (byte)(isV3 ? 3 : 2),
            Flags = (byte)(compactor.Gmp ? 1 : 0),
            LeanVersion = LeanVersion,
            GitHash = GitHash,
            BaseAddr = compactor.BaseAddr + (ulong)fileOffset,
        };
        var ms = new MemoryStream();
        var hb = new byte[OleanHeader.Size];
        header.WriteTo(hb);
        ms.Write(hb);
        if (!isV3)
        {
            compactor.Compact(data);
            ms.Write(compactor.Data.Slice((int)(fileOffset + OleanHeader.Size)));
        }
        else
        {
            compactor.Alloc(8); // data_size slot
            compactor.Compact(data);
            long dataOffset = fileOffset + OleanHeader.Size + 8;
            long dataSize = compactor.Size - dataOffset;
            ms.Write(BitConverter.GetBytes((ulong)dataSize));
            ms.Write(compactor.Data.Slice((int)dataOffset, (int)dataSize));
            var offs = compactor.ClosureOffsets;
            var fileOffs = offs.Select(o => (ulong)(o - dataOffset)).ToList();
            offs.Clear();
            compactor.Alloc(4);
            ms.Write(BitConverter.GetBytes((uint)fileOffs.Count));
            if (fileOffs.Count > 0)
            {
                compactor.Alloc(8L * fileOffs.Count);
                foreach (var o in fileOffs) ms.Write(BitConverter.GetBytes(o));
            }
            // Relocation table: closures refer to code of this process only.
            var id = Encoding.UTF8.GetBytes(RegionReader.ProcessLibId);
            int nlibs = fileOffs.Count > 0 ? 1 : 0;
            compactor.Alloc(4 + nlibs * (8 + 4 + id.Length));
            ms.Write(BitConverter.GetBytes((uint)nlibs));
            if (nlibs > 0)
            {
                ms.Write(BitConverter.GetBytes(0UL));
                ms.Write(BitConverter.GetBytes((uint)id.Length));
                ms.Write(id);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Writes `image` to `path` atomically (temp file + rename), as native Lean does.</summary>
    public static void WriteAtomically(string path, byte[] image)
    {
        string tmp = path + ".tmp." + Environment.ProcessId;
        try
        {
            File.WriteAllBytes(tmp, image);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Saves `data` to `path` in a fresh compactor (`saveModuleData`).</summary>
    public static void Write(string path, Obj key, Obj data)
    {
        var c = NewCompactor(key);
        WriteAtomically(path, SavePart(c, data));
    }
}
