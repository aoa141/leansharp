// Optional parallel prefetching of imported modules (`OleanFile.PrefetchImports`).
//
// `importModules` reads the `.olean` files of the import closure one after the other. Natively that
// is cheap (the files are mapped), but LeanSharp converts every object into a managed object, which
// costs time proportional to the data size. When enabled, reading the main `.olean` part of a module
// schedules the modules it imports to be read on background threads: the files are located by
// guessing their search path root (the roots of previously found modules and the ancestors of the
// importing file). When Lean then asks for one of these files, the prefetched region is returned if
// the file is unchanged and the requested dependency regions are the ones used by the prefetch;
// otherwise the file is read synchronously, so a wrong guess only costs time and memory.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace LeanSharp.Runtime.Compact;

internal static class OleanPrefetcher
{
    internal static volatile bool Enabled;
    internal static int MaxWorkers = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);

    sealed class Module
    {
        public string Key;          // path without extension
        public int State;           // 0 = queued, 1 = running (worker), 2 = claimed by a synchronous read
    }

    sealed class FileEntry
    {
        public readonly TaskCompletionSource<CompactedRegionData> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Length;
        public DateTime MTime;
    }

    static readonly object s_lock = new();
    static readonly Dictionary<string, Module> s_modules = new(StringComparer.Ordinal);
    static readonly Dictionary<string, FileEntry> s_files = new(StringComparer.Ordinal);
    static readonly Queue<Module> s_queue = new();
    static readonly List<string> s_roots = new();
    static int s_workers;
    internal static int s_hits, s_misses;
    // IR parts are prefetched only once Lean has asked for IR in this process.
    static volatile bool s_irWanted;

    static string ModuleKey(string path)
    {
        if (path.EndsWith(".olean.server", StringComparison.Ordinal)) return path[..^".olean.server".Length];
        if (path.EndsWith(".olean.private", StringComparison.Ordinal)) return path[..^".olean.private".Length];
        if (path.EndsWith(".olean", StringComparison.Ordinal)) return path[..^".olean".Length];
        if (path.EndsWith(".ir.sig", StringComparison.Ordinal)) return path[..^".ir.sig".Length];
        if (path.EndsWith(".ir", StringComparison.Ordinal)) return path[..^".ir".Length];
        return null;
    }

    /// <summary>Read `path` against `deps`, using a prefetched result if possible.</summary>
    public static CompactedRegionData Read(string path, IReadOnlyList<CompactedRegionData> deps)
    {
        if (!Enabled) return RegionReader.ReadFile(path, deps);
        string full = Path.GetFullPath(path);
        if (!s_irWanted && (full.EndsWith(".ir", StringComparison.Ordinal) || full.EndsWith(".ir.sig", StringComparison.Ordinal)))
            s_irWanted = true;
        FileEntry fe = null;
        lock (s_lock)
        {
            if (s_files.Remove(full, out fe)) { }
            else
            {
                string key = ModuleKey(full);
                if (key != null && s_modules.TryGetValue(key, out var m) && m.State == 0) m.State = 2;
            }
        }
        CompactedRegionData r = null;
        if (fe != null)
        {
            try
            {
                var pre = fe.Result.Task.GetAwaiter().GetResult();
                var fi = new FileInfo(full);
                if (fi.Exists && fi.Length == fe.Length && fi.LastWriteTimeUtc == fe.MTime && SameDeps(pre.m_deps, deps))
                    r = pre;
            }
            catch (Exception) { /* read synchronously below to report the error */ }
        }
        if (r != null) Interlocked.Increment(ref s_hits); else Interlocked.Increment(ref s_misses);
        r ??= RegionReader.ReadFile(path, deps);
        if (full.EndsWith(".olean", StringComparison.Ordinal)) ScheduleImports(full, r);
        return r;
    }

    static bool SameDeps(CompactedRegionData[] used, IReadOnlyList<CompactedRegionData> given)
    {
        var g = new List<CompactedRegionData>();
        if (given != null) foreach (var d in given) if (d != null && !g.Contains(d)) g.Add(d);
        used ??= Array.Empty<CompactedRegionData>();
        if (used.Length != g.Count) return false;
        foreach (var d in g) if (Array.IndexOf(used, d) < 0) return false;
        return true;
    }

    /// <summary>Schedules the imports of the module whose main part `r` was read from `path`.</summary>
    static void ScheduleImports(string path, CompactedRegionData r)
    {
        Obj md = r.Root;
        // ModuleData: 5 object fields, `imports : Array Import` first; `Import.module : Name` first.
        if (md == null || md.m_tag != 0 || md.m_other != 5) return;
        Obj imps = LeanRt.lean_ctor_get(md, 0);
        if (imps.m_tag != LeanRt.LeanArray) return;
        var a = Unsafe.As<ArrayObj>(imps);
        var todo = new List<Module>();
        for (long i = 0; i < a.m_size; i++)
        {
            Obj imp = a.m_data[i];
            if (imp.m_tag != 0 || imp.m_other < 1) continue;
            string rel = NameToRelPath(LeanRt.lean_ctor_get(imp, 0));
            if (rel == null) continue;
            string key = Locate(path, rel);
            if (key == null) continue;
            lock (s_lock)
            {
                if (s_modules.ContainsKey(key)) continue;
                var m = new Module { Key = key };
                s_modules[key] = m;
                todo.Add(m);
            }
        }
        if (todo.Count == 0) return;
        lock (s_lock)
        {
            foreach (var m in todo) s_queue.Enqueue(m);
            while (s_workers < MaxWorkers && s_workers < s_queue.Count)
            {
                s_workers++;
                var t = new Thread(Worker) { IsBackground = true, Name = "olean-prefetch" };
                t.Start();
            }
        }
    }

    static string NameToRelPath(Obj n)
    {
        var parts = new List<string>();
        while (n.m_tag != LeanRt.LeanBoxTag)
        {
            if (n.m_tag != 1) return null;
            parts.Add(LeanRt.lean_string_to_net(LeanRt.lean_ctor_get(n, 1)));
            n = LeanRt.lean_ctor_get(n, 0);
        }
        if (parts.Count == 0) return null;
        parts.Reverse();
        return Path.Combine(parts.ToArray());
    }

    /// <summary>Guess the file (without extension) of module `rel` imported by `importer`.</summary>
    static string Locate(string importer, string rel)
    {
        string[] roots;
        lock (s_lock) roots = s_roots.ToArray();
        foreach (var root in roots)
        {
            string k = Path.Combine(root, rel);
            if (File.Exists(k + ".olean")) return k;
        }
        string dir = Path.GetDirectoryName(importer);
        for (int depth = 0; dir != null && depth < 8; depth++, dir = Path.GetDirectoryName(dir))
        {
            string k = Path.Combine(dir, rel);
            if (File.Exists(k + ".olean"))
            {
                lock (s_lock) if (!s_roots.Contains(dir)) s_roots.Insert(0, dir);
                return k;
            }
        }
        return null;
    }

    static readonly string[] s_allExts = { ".olean", ".olean.server", ".olean.private", ".ir.sig", ".ir" };
    static readonly string[] s_oleanExts = { ".olean", ".olean.server", ".olean.private" };

    static void Worker()
    {
        try
        {
            while (true)
            {
                Module m;
                var entries = new List<(string Path, FileEntry Entry)>();
                lock (s_lock)
                {
                    m = null;
                    while (s_queue.Count > 0)
                    {
                        var c = s_queue.Dequeue();
                        if (c.State == 0) { m = c; break; }
                    }
                    if (m == null) { s_workers--; return; }
                    m.State = 1;
                    foreach (var ext in s_irWanted ? s_allExts : s_oleanExts)
                    {
                        var fi = new FileInfo(m.Key + ext);
                        if (!fi.Exists) continue;
                        var fe = new FileEntry { Length = fi.Length, MTime = fi.LastWriteTimeUtc };
                        s_files[fi.FullName] = fe;
                        entries.Add((fi.FullName, fe));
                    }
                }
                // Same dependency structure as `readModuleDataPartsOfMod` / `readIRPartsOfMod`.
                var olean = new List<CompactedRegionData>();
                var ir = new List<CompactedRegionData>();
                bool oleanStop = false, irStop = false;
                foreach (var (p, fe) in entries)
                {
                    bool isIr = p.EndsWith(".ir", StringComparison.Ordinal) || p.EndsWith(".ir.sig", StringComparison.Ordinal);
                    var deps = isIr ? ir : olean;
                    if (!(isIr ? irStop : oleanStop))
                    {
                        bool ok = false, more = true;
                        try
                        {
                            var r = RegionReader.ReadFile(p, deps.ToArray());
                            deps.Add(r);
                            fe.Result.SetResult(r);
                            ok = true;
                            if (p.EndsWith(".olean", StringComparison.Ordinal))
                            {
                                ScheduleImports(p, r);
                                // Lean reads the other parts only for modules of the module system.
                                more = r.Root.m_tag == 0 && r.Root.m_other == 5 && LeanRt.lean_ctor_get_uint8_s(r.Root, 0) != 0;
                            }
                        }
                        catch (Exception ex)
                        {
                            fe.Result.TrySetException(ex);
                        }
                        if (!ok || !more) { if (isIr) irStop = true; else oleanStop = true; }
                        continue;
                    }
                    // Not prefetched: let the reader fall back to a synchronous read.
                    lock (s_lock) s_files.Remove(p);
                    fe.Result.TrySetException(new OleanFormatException("not prefetched"));
                }
            }
        }
        finally
        {
            RegionReader.ReleaseScratch();
        }
    }

    /// <summary>Drops all prefetched regions that have not been requested.</summary>
    public static void Discard()
    {
        lock (s_lock)
        {
            foreach (var m in s_queue) if (m.State == 0) m.State = 2;
            s_queue.Clear();
            s_files.Clear();
        }
    }
}
