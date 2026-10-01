// Externs of `Lean.CompactedRegion` (Lean/CompactedRegion.lean), ported from library/module.cpp.

using System.Runtime.CompilerServices;
using LeanSharp.Runtime.Compact;

namespace LeanSharp.Runtime;

public static unsafe partial class LeanRt
{
    // `IO.Error.userError` (the last of the 19 constructors of `IO.Error`).
    static Obj CompactIoUserError(string msg)
    {
        var e = lean_alloc_ctor(18, 1, 0);
        lean_ctor_set(e, 0, lean_mk_string(msg));
        return lean_io_result_mk_error(e);
    }

    static readonly ExternalClass s_compactorClass = new ExternalClass(_ => { }, (_, _) => { });

    // Layout of the Lean `CompactedRegion` structure: object fields 0 = `filePath`, 1 = `root`;
    // usize slots 2..4 = `size`, `baseAddr`, `bufferOffset`; uint8 `isMemoryMapped` at byte 8*5.
    static Obj MkCompactedRegion(Obj fname, CompactedRegionData r)
    {
        var o = lean_alloc_ctor(0, 2, 8 * 3 + 1);
        lean_inc(fname);
        lean_ctor_set(o, 0, fname);
        lean_ctor_set(o, 1, r.Root);
        lean_ctor_set_usize(o, 2, r.Size);
        lean_ctor_set_usize(o, 3, r.BaseAddr);
        // Logically, the file is mapped at `base_addr`, so `root - buffer = rootAddr - base_addr`.
        lean_ctor_set_usize(o, 4, r.RootAddr - r.BaseAddr);
        lean_ctor_set_uint8(o, 8 * 5, 0);
        return o;
    }

    static CompactedRegionData RegionOf(Obj region)
    {
        Obj root = lean_ctor_get(region, 1);
        ulong size = lean_ctor_get_usize(region, 2);
        ulong baseAddr = lean_ctor_get_usize(region, 3);
        var r = CompactedRegionData.Find(root, baseAddr, size);
        if (r == null)
            throw new OleanFormatException($"unknown or freed compacted region (base address 0x{baseAddr:x})");
        return r;
    }

    static List<CompactedRegionData> DepRegionsOf(Obj odeps)
    {
        var a = Unsafe.As<ArrayObj>(odeps);
        var l = new List<CompactedRegionData>((int)a.m_size);
        for (long i = 0; i < a.m_size; i++) l.Add(RegionOf(a.m_data[i]));
        return l;
    }

    // Decoded regions are immutable (all objects are persistent), so they can be shared by all
    // imports of the same file in this process (e.g. test runners and in-process `lean`
    // invocations started by Lake). Disable with LEANSHARP_OLEAN_CACHE=0.
    static readonly bool s_cacheRegions = Environment.GetEnvironmentVariable("LEANSHARP_OLEAN_CACHE") != "0";
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime time, long len, CompactedRegionData[] deps, CompactedRegionData region)> s_regionCache = new();

    static CompactedRegionData ReadCached(string fname, CompactedRegionData[] deps, out bool hit)
    {
        hit = false;
        if (!s_cacheRegions) return OleanFile.ReadForLean(fname, deps);
        string key = Path.GetFullPath(fname);
        var fi = new FileInfo(key);
        DateTime t = fi.LastWriteTimeUtc;
        long len = fi.Length;
        if (s_regionCache.TryGetValue(key, out var e) && e.time == t && e.len == len && SameDeps(e.deps, deps))
        {
            hit = true;
            return e.region;
        }
        var r = OleanFile.ReadForLean(fname, deps);
        s_regionCache[key] = (t, len, deps, r);
        return r;
    }

    static bool SameDeps(CompactedRegionData[] a, CompactedRegionData[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    /// <summary>`CompactedRegion.read (fname : @&amp; FilePath) (depRegions : @&amp; Array CompactedRegion) : IO (α × CompactedRegion)`</summary>
    public static Obj lean_compacted_region_read(Obj ofname, Obj odep_regions)
    {
        string fname = lean_string_to_net(ofname);
        CompactedRegionData r;
        bool hit;
        try
        {
            var deps = DepRegionsOf(odep_regions);
            string path = LeanContext.ResolvePath(fname);
            if (!File.Exists(path))
                return CompactIoUserError($"failed to open file '{fname}': No such file or directory");
            r = ReadCached(path, deps.ToArray(), out hit);
        }
        catch (OleanFormatException ex)
        {
            string m = ex.Message;
            if (m == "invalid header" || m == "incompatible header")
                return CompactIoUserError($"failed to read file '{fname}', {m}");
            return CompactIoUserError($"failed to read '{fname}': {m}");
        }
        catch (IOException ex)
        {
            return CompactIoUserError($"failed to read file '{fname}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return CompactIoUserError($"failed to open file '{fname}': {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CompactIoUserError($"failed to read '{fname}': {ex.Message}");
        }
        if (!hit) CompactedRegionData.Register(r);
        var pair = lean_alloc_ctor(0, 2, 0);
        lean_ctor_set(pair, 0, r.Root);
        lean_ctor_set(pair, 1, MkCompactedRegion(ofname, r));
        return lean_io_result_mk_ok(pair);
    }

    /// <summary>
    /// `CompactedRegion.save (fname : @&amp; FilePath) (key : @&amp; Name) (data : @&amp; α)
    /// (depRegions : @&amp; Array CompactedRegion) (prev : Option Compactor) (allowClosures : Bool) : IO Compactor`
    /// </summary>
    public static Obj lean_compacted_region_save(Obj ofname, Obj mod, Obj odata, Obj odep_regions, Obj oprev, byte allow_closures_u8)
    {
        string fname = lean_string_to_net(ofname);
        Obj csObj;
        ObjectCompactor compactor;
        try
        {
            if (!lean_is_scalar(oprev))
            {
                csObj = lean_ctor_get(oprev, 0);
                lean_inc(csObj);
                lean_dec(oprev);
            }
            else
            {
                var deps = DepRegionsOf(odep_regions);
                var c = new ObjectCompactor(OleanFile.BaseAddrForKey(mod), deps, allow_closures_u8 != 0, OleanFile.UseGmpLayout);
                csObj = new ExternalObj { m_tag = LeanExternal, m_class = s_compactorClass, m_data = c };
            }
            compactor = (ObjectCompactor)Unsafe.As<ExternalObj>(csObj).m_data;
        }
        catch (OleanFormatException ex)
        {
            return CompactIoUserError($"failed to write '{fname}': {ex.Message}");
        }

        byte[] image;
        try
        {
            // As in native Lean, the file format follows this call's `allowClosures`, while closures
            // are accepted iff the chain's first save allowed them.
            image = OleanFile.SavePart(compactor, odata, allow_closures_u8 != 0);
        }
        catch (Exception ex) when (ex is OleanFormatException || ex is ArgumentException || ex is InvalidOperationException)
        {
            return CompactIoUserError($"failed to write '{fname}': {ex.Message}");
        }
        string dest = LeanContext.ResolvePath(fname);
        // (the suffix must be unique among the threads of this process too: in-process children)
        string tmp = dest + ".tmp." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId;
        try
        {
            File.WriteAllBytes(tmp, image);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch { }
            return CompactIoUserError($"failed to write '{fname}': failed to create file");
        }
        try
        {
            File.Move(tmp, dest, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch { }
            return CompactIoUserError($"failed to write '{fname}': {ex.Message}");
        }
        return lean_io_result_mk_ok(csObj);
    }

    /// <summary>`CompactedRegion.free (region : CompactedRegion) : IO Unit`</summary>
    public static Obj lean_compacted_region_free(Obj region)
    {
        Obj root = lean_ctor_get(region, 1);
        ulong size = lean_ctor_get_usize(region, 2);
        ulong baseAddr = lean_ctor_get_usize(region, 3);
        var r = CompactedRegionData.Find(root, baseAddr, size);
        if (r != null && !s_cacheRegions)
        {
            CompactedRegionData.Unregister(r, root);
            r.Release();
        }
        // Overwrite `root` so that later uses of this value do not keep the region alive.
        lean_ctor_set(region, 1, lean_box(0));
        lean_dec_ref(region);
        return lean_io_result_mk_ok(lean_box(0));
    }
}
