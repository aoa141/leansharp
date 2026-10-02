// Checks for the compact area: reading native .olean files, validating their structure, round
// tripping them through the managed compactor, and measuring read performance.
//
// Usage: dotnet run -c Release [-- <lib/lean dir>] [--all] [--roundtrip-all]

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using LeanSharp.Runtime;
using LeanSharp.Runtime.Compact;

static unsafe class Program
{
    static int s_fail;

    static void Check(bool ok, string what)
    {
        if (!ok) { s_fail++; Console.WriteLine("FAIL: " + what); }
    }

    static int Main(string[] args)
    {
        string lib = Path.Combine(Environment.GetEnvironmentVariable("HOME")!, "Repos/lean4/build/release/stage1/lib/lean");
        bool all = false, rtAll = false, perfOnly = false;
        foreach (var a in args)
        {
            if (a == "--all") all = true;
            else if (a == "--perf-only") perfOnly = true;
            else if (a == "--micro") { }
            else if (a == "--import" || (Array.IndexOf(args, a) > 0 && args[Array.IndexOf(args, a) - 1] == "--import")) { }
            else if (a == "--roundtrip-all") rtAll = true;
            else lib = a;
        }

        int ii = Array.IndexOf(args, "--import");
        if (ii >= 0) { for (int k = 0; k < 1; k++) ImportPerf(lib, args[ii + 1]); return 0; }
        if (args.Contains("--micro"))
        {
            string mf = ModFile(lib, "Lean.Meta.Basic");
            var main = OleanFile.Read(mf); var srv = OleanFile.Read(mf + ".server", main);
            string which = Environment.GetEnvironmentVariable("MICRO") ?? ".private";
            var img = File.ReadAllBytes(mf + (which == "main" ? "" : which));
            for (int it = 0; it < 5; it++)
            {
                var cpuS = Process.GetCurrentProcess().TotalProcessorTime;
                var swm = Stopwatch.StartNew();
                long no = 0;
                for (int k = 0; k < 50; k++) no += (which == "main" ? OleanFile.Parse("p", img) : OleanFile.Parse("p", img, main, srv)).ObjectCount;
                var cpuE = Process.GetCurrentProcess().TotalProcessorTime - cpuS;
                Console.WriteLine($"micro: {no / 1e6:F2} M objects, {img.Length * 50 / 1e6:F0} MB in {swm.Elapsed.TotalMilliseconds:F0} ms (cpu {cpuE.TotalMilliseconds:F0} ms) = {swm.Elapsed.TotalMilliseconds * 1e6 / no:F1} ns/obj");
            }
            return 0;
        }
        if (perfOnly) { ReadPerf(lib, "Init"); if (all) ReadPerf(lib, null); return 0; }
        TestLazy(lib);
        TestPrelude(lib);
        TestErrors(lib);
        TestExterns(lib);
        TestSynthetic();
        TestPrefetch(lib, "Init.Data.List.Lemmas");
        foreach (var m in new[] { "Init.Prelude", "Init", "Init.Core", "Lean.Elab.Term", "Lean.Meta.Basic", "Lake" })
            RoundTrip(lib, m);
        if (rtAll)
        {
            int n = 0, identical = 0;
            foreach (var f in Directory.EnumerateFiles(lib, "*.olean", SearchOption.AllDirectories))
            {
                string mod = Path.GetRelativePath(lib, f)[..^".olean".Length].Replace('/', '.');
                n++;
                if (RoundTrip(lib, mod, quiet: true)) identical++;
            }
            Console.WriteLine($"round trip: {identical}/{n} modules byte-identical (all parts incl. IR)");
        }
        ReadPerf(lib, "Init");
        if (all) ReadPerf(lib, null);

        Console.WriteLine(s_fail == 0 ? "ALL CHECKS PASSED" : $"{s_fail} CHECK(S) FAILED");
        return s_fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    // Names

    static Obj MkName(string dotted)
    {
        Obj n = LeanRt.lean_box(0);
        foreach (var part in dotted.Split('.'))
        {
            var s = LeanRt.lean_mk_string(part);
            var r = LeanRt.lean_alloc_ctor(1, 2, 8);
            LeanRt.lean_ctor_set(r, 0, n);
            LeanRt.lean_ctor_set(r, 1, s);
            ulong ph = OleanFile.NameHash(n);
            LeanRt.lean_ctor_set_uint64_s(r, 0, LeanHash.Mix(ph, LeanHash.HashStr(LeanRt.lean_string_span(s), 11)));
            n = r;
        }
        return n;
    }

    static string NameToString(Obj n)
    {
        if (LeanRt.lean_is_scalar(n)) return "[anonymous]";
        var parts = new List<string>();
        while (!LeanRt.lean_is_scalar(n))
        {
            var f = LeanRt.lean_ctor_get(n, 1);
            parts.Add(n.m_tag == 1 ? LeanRt.lean_string_to_net(f) : LeanRt.lean_nat_to_big(f).ToString());
            n = LeanRt.lean_ctor_get(n, 0);
        }
        parts.Reverse();
        return string.Join(".", parts);
    }

    static bool IsName(Obj n, HashSet<Obj> verified, ref int mismatches)
    {
        if (LeanRt.lean_is_scalar(n)) return LeanRt.lean_unbox(n) == 0;
        if (verified.Contains(n)) return true;
        if ((n.m_tag != 1 && n.m_tag != 2) || n.m_other != 2 || n.m_cs_sz != 8) return false;
        var f1 = LeanRt.lean_ctor_get(n, 1);
        if (n.m_tag == 1 && f1.m_tag != LeanRt.LeanString) return false;
        if (n.m_tag == 2 && f1.m_tag != LeanRt.LeanBoxTag && f1.m_tag != LeanRt.LeanMPZ) return false;
        if (!IsName(LeanRt.lean_ctor_get(n, 0), verified, ref mismatches)) return false;
        if (OleanFile.ComputeNameHash(n) != OleanFile.NameHash(n)) mismatches++;
        verified.Add(n);
        return true;
    }

    // ------------------------------------------------------------------

    static string ModFile(string lib, string mod) => Path.Combine(lib, mod.Replace('.', '/') + ".olean");

    /// <summary>
    /// Lazily decoded regions (LazyRegion.cs). Must run first: a file can only be read lazily
    /// while its address range is free.
    /// </summary>
    static void TestLazy(string lib)
    {
        Console.WriteLine("== lazy decoding");
        // 1. Open, look at a few things, then compact the lazy graph again: must give the files.
        foreach (var mod in new[] { "Lean.Meta.WHNF", "Init.Data.Nat.Basic" })
        {
            string f = ModFile(lib, mod);
            foreach (var group in PartGroups(f))
            {
                var before = OleanFile.LazyStatistics;
                var regions = new List<CompactedRegionData>();
                foreach (var p in group) regions.Add(OleanFile.ReadLazy(p, regions.ToArray()));
                var opened = OleanFile.LazyStatistics;
                Check(opened.Regions == before.Regions + group.Count, $"{mod}: {group.Count} parts opened lazily");
                Check(regions.All(r => r.IdsAreAddresses), $"{mod}: objects are identified by address");
                long total = regions.Sum(r => (long)r.ObjectCount);
                Check(opened.Objects - before.Objects < total / 10 + 16, $"{mod}: opening creates few objects ({opened.Objects - before.Objects} of {total})");
                if (group[0].EndsWith(".olean"))
                {
                    Obj md = regions[0].Root;
                    Check(md.m_tag == 0 && md.m_other == 5, $"{mod}: ModuleData shape");
                    var names = Unsafe.As<ArrayObj>(LeanRt.lean_ctor_get(md, 1));
                    var consts = Unsafe.As<ArrayObj>(LeanRt.lean_ctor_get(md, 2));
                    Check(ReferenceEquals(LeanRt.lean_ctor_get(md, 1), names), $"{mod}: a field read twice gives the same object");
                    int mism = 0;
                    for (long i = 0; i < names.m_size; i++)
                    {
                        Obj cv = LeanRt.lean_ctor_get(LeanRt.lean_ctor_get(consts.m_data[i], 0), 0);
                        if (!ReferenceEquals(LeanRt.lean_ctor_get(cv, 0), names.m_data[i])) mism++;
                    }
                    Check(names.m_size > 0 && mism == 0, $"{mod}: ConstantInfo names are shared with constNames ({mism} mismatches of {names.m_size})");
                    var partial = OleanFile.LazyStatistics;
                    Check(partial.Objects - before.Objects < total, $"{mod}: looking at the names creates part of the objects ({partial.Objects - before.Objects} of {total})");
                }
                var h = regions[0].Header;
                string savedGit = OleanFile.GitHash, savedVer = OleanFile.LeanVersion;
                OleanFile.GitHash = h.GitHash; OleanFile.LeanVersion = h.LeanVersion;
                Obj key = MkName(group[0].EndsWith(".ir.sig") ? mod + ".ir" : mod);
                var c = new ObjectCompactor(OleanFile.BaseAddrForKey(key), null, false, h.Gmp);
                bool same = true;
                foreach (var (r, path) in regions.Zip(group))
                    same &= OleanFile.SavePart(c, r.Root).AsSpan().SequenceEqual(File.ReadAllBytes(path));
                OleanFile.GitHash = savedGit; OleanFile.LeanVersion = savedVer;
                Check(same, $"{mod}: compacting the lazily decoded graph reproduces {group.Count} file(s)");
                var after = OleanFile.LazyStatistics;
                Check(after.Objects - before.Objects == total, $"{mod}: every object created exactly once ({after.Objects - before.Objects} of {total})");
                // the eager reader gives an equal graph (without address ids: the range is taken)
                var eager = new List<CompactedRegionData>();
                foreach (var p in group) eager.Add(OleanFile.Read(p, eager.ToArray()));
                Check(!eager[0].IdsAreAddresses && regions.Zip(eager).All(x => DeepEq(x.First.Root, x.Second.Root)), $"{mod}: equal to the eagerly decoded graph");
                // pointers from an eagerly read part into a lazily read one, and the index
                if (group.Count > 1)
                {
                    var mixed = OleanFile.Read(group[1], regions[0]);
                    Check(DeepEq(mixed.Root, regions[1].Root), $"{mod}: eager part on top of a lazy part");
                }
                Check(regions[0].EnsureIndex().Length == regions[0].ObjectCount, $"{mod}: index of a lazy region");
            }
        }
        // 2. Many threads walking the same fresh graph must agree on every object.
        {
            string mod = "Lean.Elab.Do.Legacy";
            string f = ModFile(lib, mod);
            if (!File.Exists(f)) { mod = "Lean.Meta.Tactic.Simp.Rewrite"; f = ModFile(lib, mod); }
            var before = OleanFile.LazyStatistics;
            var main = OleanFile.ReadLazy(f);
            var roots = new Obj[8][];
            var threads = new Thread[8];
            Exception err = null;
            for (int t = 0; t < threads.Length; t++)
            {
                int tt = t;
                threads[t] = new Thread(() =>
                {
                    try
                    {
                        // depth first, children in a thread-specific order
                        var seen = new HashSet<Obj>(ReferenceEqualityComparer.Instance);
                        var st = new Stack<Obj>();
                        st.Push(main.Root);
                        while (st.Count > 0)
                        {
                            var o = st.Pop();
                            if (o.m_tag == LeanRt.LeanBoxTag || !seen.Add(o)) continue;
                            if (o.m_tag <= LeanRt.LeanMaxCtorTag)
                                for (uint i = 0; i < o.m_other; i++) st.Push(LeanRt.lean_ctor_get(o, (tt & 1) == 0 ? i : (uint)(o.m_other - 1 - i)));
                            else if (o.m_tag == LeanRt.LeanArray)
                            {
                                var a = Unsafe.As<ArrayObj>(o);
                                for (long i = 0; i < a.m_size; i++) st.Push(a.m_data[(i + tt) % a.m_size]);
                            }
                            else if (o.m_tag == LeanRt.LeanThunk) st.Push(Unsafe.As<ThunkObj>(o).m_value);
                        }
                        roots[tt] = seen.OrderBy(o => o.m_id).ToArray();
                    }
                    catch (Exception e) { err = e; }
                }, 256 << 20);
                threads[t].Start();
            }
            foreach (var t in threads) t.Join();
            Check(err == null, $"{mod}: concurrent traversal ({err?.Message})");
            bool agree = roots.All(r => r != null && r.Length == roots[0].Length);
            for (int t = 1; agree && t < roots.Length; t++)
                for (int i = 0; agree && i < roots[0].Length; i++) agree = ReferenceEquals(roots[0][i], roots[t][i]);
            Check(agree, $"{mod}: all threads see the same {roots[0]?.Length} objects");
            if (!agree || roots[0].Length != main.ObjectCount)
            {
                Console.WriteLine("  lengths: " + string.Join(" ", roots.Select(r => r?.Length)));
                var have = new HashSet<Obj>(roots[0], ReferenceEqualityComparer.Instance);
                Console.WriteLine("  missing tags: " + string.Join(" ", main.EnsureIndex().Where(o => !have.Contains(o)).GroupBy(o => o.m_tag).Select(g => $"{g.Key}x{g.Count()}")));
            }
            var after = OleanFile.LazyStatistics;
            Check(after.Objects - before.Objects == main.ObjectCount && roots[0].Length == main.ObjectCount, $"{mod}: every object created exactly once ({after.Objects - before.Objects} of {main.ObjectCount})");
        }
        // 3. Errors: a pointer outside of the file is found when it is followed.
        {
            string f = ModFile(lib, "Lean.Meta.DiscrTree");
            if (File.Exists(f + ".server"))
            {
                bool thrown = false;
                try
                {
                    var orphan = OleanFile.ReadLazy(f + ".server");
                    var st = new Stack<Obj>();
                    var seen = new HashSet<Obj>(ReferenceEqualityComparer.Instance);
                    st.Push(orphan.Root);
                    while (st.Count > 0)
                    {
                        var o = st.Pop();
                        if (o.m_tag == LeanRt.LeanBoxTag || !seen.Add(o)) continue;
                        if (o.m_tag <= LeanRt.LeanMaxCtorTag) for (uint i = 0; i < o.m_other; i++) st.Push(LeanRt.lean_ctor_get(o, i));
                        else if (o.m_tag == LeanRt.LeanArray) foreach (var x in Unsafe.As<ArrayObj>(o).m_data) st.Push(x);
                    }
                }
                catch (OleanFormatException) { thrown = true; }
                Check(thrown, "pointer into a missing dependency region is reported when followed");
            }
        }
    }

    static void TestPrelude(string lib)
    {
        Console.WriteLine("== Init.Prelude structure");
        string f = ModFile(lib, "Init.Prelude");
        var sw = Stopwatch.StartNew();
        var main = OleanFile.Read(f);
        var server = OleanFile.Read(f + ".server", main);
        var priv = OleanFile.Read(f + ".private", main, server);
        sw.Stop();
        var h = main.Header;
        Console.WriteLine($"header: v{h.Version} flags={h.Flags} version='{h.LeanVersion}' githash='{h.GitHash}' base=0x{h.BaseAddr:x}");
        Console.WriteLine($"objects: main={main.ObjectCount} server={server.ObjectCount} private={priv.ObjectCount}; read in {sw.Elapsed.TotalMilliseconds:F1} ms");
        Check(h.Version == 2, "version 2");
        Check(OleanFile.BaseAddrForKey(MkName("Init.Prelude")) == h.BaseAddr, "base address derived from module name hash");
        Check(server.BaseAddr > main.BaseAddr && server.BaseAddr % 65536 == 0, "server part base address");

        foreach (var (label, r) in new[] { ("main", main), ("server", server), ("private", priv) })
        {
            var md = r.Root;
            Check(md.m_tag == 0 && md.m_other == 5 && md.m_cs_sz == 8, $"{label}: ModuleData shape (tag {md.m_tag}, {md.m_other} fields, scalar size {md.m_cs_sz})");
            byte isModule = LeanRt.lean_ctor_get_uint8_s(md, 0);
            var imports = Unsafe.As<ArrayObj>(LeanRt.lean_ctor_get(md, 0));
            var constNames = Unsafe.As<ArrayObj>(LeanRt.lean_ctor_get(md, 1));
            var constants = Unsafe.As<ArrayObj>(LeanRt.lean_ctor_get(md, 2));
            var extra = Unsafe.As<ArrayObj>(LeanRt.lean_ctor_get(md, 3));
            var entries = Unsafe.As<ArrayObj>(LeanRt.lean_ctor_get(md, 4));
            Console.WriteLine($"{label}: isModule={isModule} imports={imports.m_size} constNames={constNames.m_size} constants={constants.m_size} extraConstNames={extra.m_size} entries={entries.m_size}");
            Check(constNames.m_size == constants.m_size, $"{label}: #constNames = #constants");
            var verified = new HashSet<Obj>(ReferenceEqualityComparer.Instance);
            int mism = 0, bad = 0;
            for (long i = 0; i < constNames.m_size; i++)
                if (!IsName(constNames.m_data[i], verified, ref mism)) bad++;
            Check(bad == 0, $"{label}: all constNames are Names ({bad} bad)");
            Check(mism == 0, $"{label}: Name hashes match ({mism} mismatches)");
            if (constNames.m_size > 0)
                Console.WriteLine($"{label}: first names: " + string.Join(", ", Enumerable.Range(0, (int)Math.Min(5, constNames.m_size)).Select(i => NameToString(constNames.m_data[i]))));
            // ConstantInfo.name must agree with constNames (ConstantInfo -> XxxVal -> ConstantVal -> name)
            int nameMismatch = 0;
            for (long i = 0; i < constants.m_size; i++)
            {
                var ci = constants.m_data[i];
                var val = LeanRt.lean_ctor_get(ci, 0);
                var cval = LeanRt.lean_ctor_get(val, 0);
                var nm = LeanRt.lean_ctor_get(cval, 0);
                if (!ReferenceEquals(nm, constNames.m_data[i])) nameMismatch++;
            }
            Check(nameMismatch == 0, $"{label}: ConstantInfo names are shared with constNames ({nameMismatch} mismatches)");
            // Scan all Name-shaped objects of the region and verify their hashes.
            int nNames = 0; mism = 0;
            foreach (var (_, o) in r.Objects())
            {
                if (o.m_tag == 1 && o.m_other == 2 && o.m_cs_sz == 8)
                {
                    int m0 = mism;
                    if (IsName(o, verified, ref mism)) nNames++;
                }
            }
            Console.WriteLine($"{label}: {nNames} Name objects verified by hash");
            Check(mism == 0, $"{label}: all Name hashes match ({mism} mismatches)");
        }
        // Dependency lookups without the recent-regions cache (on-demand index of the main part).
        OleanFile.ReleaseScratchBuffers();
        var main2 = OleanFile.Read(f);
        var server2a = OleanFile.Read(f + ".server", main2);
        OleanFile.ReleaseScratchBuffers();
        var server2b = OleanFile.Read(f + ".server", main2);
        var priv2 = OleanFile.Read(f + ".private", main2, server2b);
        Check(DeepEq(server2a.Root, server2b.Root) && DeepEq(server2b.Root, server.Root), "server part read via on-demand index");
        Check(DeepEq(priv2.Root, priv.Root), "private part read via on-demand index");
        Check(main2.m_objs != null && main2.m_objs.Length == main2.ObjectCount, "on-demand index built");
        // Objects in the server part refer into the main part.
        var smd = server.Root;
        Check(ReferenceEquals(smd, server.Root), "root");
        // Imports of Init.Prelude: none (it is the root of the library).
        var imps = Unsafe.As<ArrayObj>(LeanRt.lean_ctor_get(main.Root, 0));
        Check(imps.m_size == 0, "Init.Prelude has no imports");
        // Look up something by name.
        var cn = Unsafe.As<ArrayObj>(LeanRt.lean_ctor_get(main.Root, 1));
        var names = new HashSet<string>();
        for (long i = 0; i < cn.m_size; i++) names.Add(NameToString(cn.m_data[i]));
        Check(names.Contains("Nat.add") && names.Contains("Lean.Name") && names.Contains("Bool"), "well-known constants present");
    }

    static void TestErrors(string lib)
    {
        Console.WriteLine("== error handling");
        string f = ModFile(lib, "Init.Prelude");
        var img = File.ReadAllBytes(f);
        var bad = (byte[])img.Clone(); bad[0] = (byte)'x';
        try { OleanFile.Parse("bad", bad); Check(false, "bad magic rejected"); } catch (OleanFormatException e) { Check(e.Message == "invalid header", "bad magic message"); }
        var bad2 = (byte[])img.Clone(); bad2[5] = 7;
        try { OleanFile.Parse("bad", bad2); Check(false, "bad version rejected"); } catch (OleanFormatException e) { Check(e.Message == "incompatible header", "bad version message"); }
        // server part without its dependency
        try { OleanFile.Read(f + ".server"); Check(false, "missing dep region detected"); } catch (OleanFormatException) { }
        var trunc = img.AsSpan(0, img.Length / 2).ToArray();
        try { OleanFile.Parse("trunc", trunc); Check(false, "truncated file rejected"); } catch (OleanFormatException) { }
        // Fuzzing: corrupted files must be rejected with OleanFormatException (or parse), never crash.
        var rnd = new Random(1234);
        var small = File.ReadAllBytes(ModFile(lib, "Init.Core") + ".server");
        int ok = 0, rejected = 0, other = 0;
        for (int it = 0; it < 3000; it++)
        {
            var b = (byte[])small.Clone();
            int nflip = 1 + rnd.Next(8);
            for (int k = 0; k < nflip; k++) b[OleanHeader.Size + rnd.Next(b.Length - OleanHeader.Size)] = (byte)rnd.Next(256);
            if (it % 10 == 0) b = b.AsSpan(0, OleanHeader.Size + rnd.Next(b.Length - OleanHeader.Size)).ToArray();
            try { OleanFile.Parse("fuzz", b, OleanFile.Read(ModFile(lib, "Init.Core"))); ok++; }
            catch (OleanFormatException) { rejected++; }
            catch (Exception e) { if (other++ < 3) Console.WriteLine("fuzz: unexpected " + e); }
        }
        Console.WriteLine($"fuzz: {ok} parsed, {rejected} rejected, {other} unexpected exceptions");
        Check(other == 0, "fuzzing raises only OleanFormatException");
    }

    static void TestExterns(string lib)
    {
        Console.WriteLine("== externs");
        string f = ModFile(lib, "Init.Core");
        var empty = LeanRt.lean_alloc_array(0, 0);
        var r1 = LeanRt.lean_compacted_region_read(LeanRt.lean_mk_string(f), empty);
        Check(LeanRt.lean_io_result_is_ok(r1), "read ok");
        var pair = LeanRt.lean_io_result_get_value(r1);
        var md = LeanRt.lean_ctor_get(pair, 0);
        var region = LeanRt.lean_ctor_get(pair, 1);
        Check(md.m_tag == 0 && md.m_other == 5, "read returns ModuleData");
        Check(region.m_other == 2 && region.m_cs_sz == 25, "CompactedRegion layout");
        Check(LeanRt.lean_ctor_get_usize(region, 2) == (ulong)new FileInfo(f).Length, "region size = file size");
        Check(LeanRt.lean_ctor_get_usize(region, 3) == OleanFile.ReadHeader(f).BaseAddr, "region baseAddr");
        Check(LeanRt.lean_ctor_get_usize(region, 4) + 56 == (ulong)new FileInfo(f).Length, "region bufferOffset (ModuleData is the last object)");
        Check(ReferenceEquals(LeanRt.lean_ctor_get(region, 1), md), "region root");
        // read server part with the main region as dependency
        var deps = LeanRt.lean_alloc_array(1, 1);
        LeanRt.lean_array_set_core(deps, 0, region);
        var r2 = LeanRt.lean_compacted_region_read(LeanRt.lean_mk_string(f + ".server"), deps);
        Check(LeanRt.lean_io_result_is_ok(r2), "read server part with dep region");
        var r3 = LeanRt.lean_compacted_region_read(LeanRt.lean_mk_string(f + ".server"), empty);
        Check(LeanRt.lean_io_result_is_error(r3), "server part without dep region fails");
        if (LeanRt.lean_io_result_is_error(r3))
            Console.WriteLine("  error: " + LeanRt.lean_string_to_net(LeanRt.lean_ctor_get(LeanRt.lean_io_result_get_error(r3), 0)));
        var r4 = LeanRt.lean_compacted_region_read(LeanRt.lean_mk_string(f + ".nonexistent"), empty);
        Check(LeanRt.lean_io_result_is_error(r4) && LeanRt.lean_io_result_get_error(r4).m_tag == 18, "missing file -> userError");

        // save: chained parts (as saveModuleDataParts), then read back with the Lean-level protocol
        string tmpDir = Path.Combine(Path.GetTempPath(), "leansharp-compact-" + Environment.ProcessId);
        Directory.CreateDirectory(tmpDir);
        string o1 = Path.Combine(tmpDir, "Core.olean"), o2 = o1 + ".server";
        var serverMd = LeanRt.lean_ctor_get(LeanRt.lean_io_result_get_value(r2), 0);
        string savedGit = OleanFile.GitHash;
        OleanFile.GitHash = OleanFile.ReadHeader(f).GitHash;
        var key = MkName("Init.Core");
        var s1 = LeanRt.lean_compacted_region_save(LeanRt.lean_mk_string(o1), key, md, empty, LeanRt.lean_box(0), 0);
        Check(LeanRt.lean_io_result_is_ok(s1), "save part 1");
        var cs = LeanRt.lean_io_result_get_value(s1);
        var s2 = LeanRt.lean_compacted_region_save(LeanRt.lean_mk_string(o2), key, serverMd, empty, LeanRt.lean_mk_option_some(cs), 0);
        Check(LeanRt.lean_io_result_is_ok(s2), "save part 2 (chained)");
        OleanFile.GitHash = savedGit;
        Check(File.ReadAllBytes(o1).AsSpan().SequenceEqual(File.ReadAllBytes(f)), "extern save: part 1 byte-identical");
        Check(File.ReadAllBytes(o2).AsSpan().SequenceEqual(File.ReadAllBytes(f + ".server")), "extern save: part 2 byte-identical");
        var rr1 = LeanRt.lean_compacted_region_read(LeanRt.lean_mk_string(o1), empty);
        var reg1 = LeanRt.lean_ctor_get(LeanRt.lean_io_result_get_value(rr1), 1);
        var d1 = LeanRt.lean_alloc_array(1, 1); LeanRt.lean_array_set_core(d1, 0, reg1);
        var rr2 = LeanRt.lean_compacted_region_read(LeanRt.lean_mk_string(o2), d1);
        Check(LeanRt.lean_io_result_is_ok(rr2), "re-read saved chain");
        Check(DeepEq(LeanRt.lean_ctor_get(LeanRt.lean_io_result_get_value(rr2), 0), serverMd), "re-read server part equals original");
        // closures are rejected unless allowed
        var clo = LeanRt.lean_alloc_closure((delegate*<Obj, Obj>)&Id, 1, 0);
        var s3 = LeanRt.lean_compacted_region_save(LeanRt.lean_mk_string(Path.Combine(tmpDir, "clo.olean")), key, LeanRt.lean_mk_pair(clo, LeanRt.lean_box(3)), empty, LeanRt.lean_box(0), 0);
        Check(LeanRt.lean_io_result_is_error(s3), "closure rejected");
        // v3 with closures, same process
        string o3 = Path.Combine(tmpDir, "clo3.olean");
        var cl2 = LeanRt.lean_alloc_closure((delegate*<Obj, Obj, Obj>)&Fst, 2, 1);
        LeanRt.lean_closure_set(cl2, 0, LeanRt.lean_mk_string("captured"));
        var md1 = LeanRt.lean_ctor_get(LeanRt.lean_io_result_get_value(rr1), 0);
        var s4 = LeanRt.lean_compacted_region_save(LeanRt.lean_mk_string(o3), key, LeanRt.lean_mk_pair(cl2, md1), d1, LeanRt.lean_box(0), 1);
        Check(LeanRt.lean_io_result_is_ok(s4), "v3 save with closures and dep region");
        Check(new FileInfo(o3).Length < 4096, "dep region objects are referenced, not copied");
        var rr3 = LeanRt.lean_compacted_region_read(LeanRt.lean_mk_string(o3), d1);
        Check(LeanRt.lean_io_result_is_ok(rr3), "v3 read");
        if (LeanRt.lean_io_result_is_ok(rr3))
        {
            var p3 = LeanRt.lean_ctor_get(LeanRt.lean_io_result_get_value(rr3), 0);
            var c3 = LeanRt.lean_ctor_get(p3, 0);
            Check(ReferenceEquals(LeanRt.lean_ctor_get(p3, 1), LeanRt.lean_ctor_get(LeanRt.lean_io_result_get_value(rr1), 0)), "dep pointer resolved to dep region object");
            var res = LeanRt.lean_apply_1(c3, LeanRt.lean_box(5));
            Check(res.m_tag == LeanRt.LeanString && LeanRt.lean_string_to_net(res) == "captured", "re-read closure works");
        }
        // free
        var fr = LeanRt.lean_compacted_region_free(reg1);
        Check(LeanRt.lean_io_result_is_ok(fr), "free");
        try { Directory.Delete(tmpDir, true); } catch { }
    }

    /// <summary>Reads the import closure of `root` (Lean's order) and returns the regions by path.</summary>
    static Dictionary<string, CompactedRegionData> ImportClosure(string lib, string root)
    {
        var res = new Dictionary<string, CompactedRegionData>();
        var visited = new HashSet<string>();
        void Visit(string mod)
        {
            if (!visited.Add(mod)) return;
            string f = ModFile(lib, mod);
            var parts = new List<CompactedRegionData> { OleanFile.ReadForLean(f) };
            res[f] = parts[0];
            if (LeanRt.lean_ctor_get_uint8_s(parts[0].Root, 0) != 0)
            {
                parts.Add(res[f + ".server"] = OleanFile.ReadForLean(f + ".server", parts.ToArray()));
                parts.Add(res[f + ".private"] = OleanFile.ReadForLean(f + ".private", parts.ToArray()));
            }
            string irSig = Path.ChangeExtension(f, ".ir.sig"), ir = Path.ChangeExtension(f, ".ir");
            if (File.Exists(irSig))
            {
                var s = res[irSig] = OleanFile.ReadForLean(irSig);
                if (File.Exists(ir)) res[ir] = OleanFile.ReadForLean(ir, s);
            }
            var imps = Unsafe.As<ArrayObj>(LeanRt.lean_ctor_get(parts[0].Root, 0));
            for (long i = 0; i < imps.m_size; i++) Visit(NameToString(LeanRt.lean_ctor_get(imps.m_data[i], 0)));
        }
        Visit(root);
        return res;
    }

    static void TestPrefetch(string lib, string root)
    {
        Console.WriteLine("== prefetch");
        var seq = ImportClosure(lib, root);
        int h0 = OleanPrefetcher.s_hits;
        OleanFile.PrefetchImports = true;
        var pre = ImportClosure(lib, root);
        OleanFile.PrefetchImports = false;
        int hits = OleanPrefetcher.s_hits - h0;
        Console.WriteLine($"{root}: {seq.Count} files, {hits} served by prefetch");
        Check(hits > seq.Count / 2, "most files served by prefetch");
        Check(seq.Count == pre.Count, "same files");
        int bad = 0;
        foreach (var (p, r) in seq)
        {
            var q = pre[p];
            if (!DeepEq(r.Root, q.Root)) bad++;
            // pointers into dependency regions resolve into the regions Lean passed as deps
            if (p.EndsWith(".server") && !(q.m_deps.Length == 1 && ReferenceEquals(q.m_deps[0], pre[p[..^".server".Length]]))) bad++;
        }
        Check(bad == 0, $"prefetched regions equal synchronously read ones ({bad} differ)");
    }

    static Obj Id(Obj x) => x;
    static Obj Fst(Obj a, Obj b) => a;

    static void TestSynthetic()
    {
        Console.WriteLine("== synthetic objects");
        // big numbers, scalar arrays, floats in scalar areas, thunks, refs, a long list, sharing
        var big = LeanRt.lean_big_to_nat(BigInteger.Pow(2, 200) + 12345);
        var negBig = LeanRt.lean_alloc_mpz(-(BigInteger.Pow(3, 100)));
        var ba = LeanRt.MkByteArray(new byte[] { 1, 2, 3, 4, 5 });
        var fa = LeanRt.lean_alloc_sarray(8, 2, 2);
        BitConverter.TryWriteBytes(Unsafe.As<SArrayObj>(fa).m_data.AsSpan(0), 3.25);
        BitConverter.TryWriteBytes(Unsafe.As<SArrayObj>(fa).m_data.AsSpan(8), -1.5);
        var sc = LeanRt.lean_alloc_ctor(3, 1, 13);
        LeanRt.lean_ctor_set(sc, 0, LeanRt.lean_mk_string("héllo"));
        LeanRt.lean_ctor_set_float_s(sc, 0, Math.PI);
        LeanRt.lean_ctor_set_uint32_s(sc, 8, 0xdeadbeef);
        LeanRt.lean_ctor_set_uint8_s(sc, 12, 7);
        var thunk = new ThunkObj { m_tag = LeanRt.LeanThunk, m_value = LeanRt.lean_box(42) };
        var rf = LeanRt.lean_st_mk_ref(LeanRt.lean_mk_string("ref"));
        var xs = new List<Obj>();
        for (int i = 0; i < 200000; i++) xs.Add(LeanRt.lean_box((ulong)i));
        var list = LeanRt.MkList(xs);
        var s1 = LeanRt.lean_mk_string("shared"); var s2 = LeanRt.lean_mk_string("shared");
        var bigBox = LeanRt.lean_box(1UL << 62);
        var many = LeanRt.lean_alloc_ctor(0, 12, 0);
        Obj[] fields = { big, negBig, ba, fa, sc, thunk, rf, list, s1, s2, bigBox, LeanRt.lean_mk_empty_array() };
        for (int i = 0; i < fields.Length; i++) LeanRt.lean_ctor_set(many, (uint)i, fields[i]);

        var c = new ObjectCompactor(OleanFile.BaseAddrForKey(MkName("Test")));
        var img = OleanFile.SavePart(c, many);
        var r = OleanFile.Parse("synthetic", img);
        var o = r.Root;
        Check(o.m_tag == 0 && o.m_other == 12, "synthetic root shape");
        Check(DeepEq(o, many), "synthetic round trip equal");
        Check(ReferenceEquals(LeanRt.lean_ctor_get(o, 8), LeanRt.lean_ctor_get(o, 9)), "equal strings are max-shared");
        Check(LeanRt.lean_ctor_get(o, 4).m_cs_sz == 16, "ctor scalar area rounded to 16 bytes");
        Check(LeanRt.lean_ctor_get_float_s(LeanRt.lean_ctor_get(o, 4), 0) == Math.PI, "float field");
        Check(LeanRt.lean_ctor_get_uint32_s(LeanRt.lean_ctor_get(o, 4), 8) == 0xdeadbeef, "uint32 field");
        Check(LeanRt.lean_nat_to_big(LeanRt.lean_ctor_get(o, 0)) == BigInteger.Pow(2, 200) + 12345, "big nat");
        Check(Unsafe.As<MpzObj>(LeanRt.lean_ctor_get(o, 1)).m_value == -(BigInteger.Pow(3, 100)), "negative big int");
        Check(LeanRt.lean_unbox(LeanRt.lean_ctor_get(o, 10)) == 1UL << 62, "large scalar");
        Check(o.m_rc == 0 && LeanRt.lean_ctor_get(o, 7).m_rc == 0, "objects are persistent");
        // compacting the re-read graph again gives the same bytes
        var c2 = new ObjectCompactor(OleanFile.BaseAddrForKey(MkName("Test")));
        Check(OleanFile.SavePart(c2, o).AsSpan().SequenceEqual(img), "re-compaction is byte-identical");
        // non-GMP layout
        var c3 = new ObjectCompactor(OleanFile.BaseAddrForKey(MkName("Test")), gmp: false);
        var img3 = OleanFile.SavePart(c3, many);
        Check(img3[6] == 0, "non-GMP flag");
        Check(DeepEq(OleanFile.Parse("nongmp", img3).Root, many), "non-GMP round trip");
    }

    // Structural equality of object graphs (with memo on visited pairs).
    static bool DeepEq(Obj a, Obj b)
    {
        var seen = new HashSet<(Obj, Obj)>(new PairCmp());
        var todo = new Stack<(Obj, Obj)>();
        todo.Push((a, b));
        while (todo.Count > 0)
        {
            var (x, y) = todo.Pop();
            if (ReferenceEquals(x, y)) continue;
            if (x == null || y == null) return false;
            if (!seen.Add((x, y))) continue;
            if (x.m_tag != y.m_tag) return false;
            int tag = x.m_tag;
            if (tag == LeanRt.LeanBoxTag) { if (LeanRt.lean_unbox(x) != LeanRt.lean_unbox(y)) return false; continue; }
            if (tag <= LeanRt.LeanMaxCtorTag)
            {
                if (x.m_other != y.m_other) return false;
                var bx = LeanRt.CtorScalarBytes(x); var by = LeanRt.CtorScalarBytes(y);
                int n = Math.Max(bx.Length, by.Length);
                for (int i = 0; i < n; i++) if ((i < bx.Length ? bx[i] : 0) != (i < by.Length ? by[i] : 0)) return false;
                for (uint i = 0; i < x.m_other; i++) todo.Push((LeanRt.lean_ctor_get(x, i), LeanRt.lean_ctor_get(y, i)));
                continue;
            }
            switch (tag)
            {
                case LeanRt.LeanArray:
                {
                    var ax = Unsafe.As<ArrayObj>(x); var ay = Unsafe.As<ArrayObj>(y);
                    if (ax.m_size != ay.m_size) return false;
                    for (long i = 0; i < ax.m_size; i++) todo.Push((ax.m_data[i], ay.m_data[i]));
                    break;
                }
                case LeanRt.LeanScalarArray:
                    if (!LeanRt.ByteArraySpan(x).SequenceEqual(LeanRt.ByteArraySpan(y)) && !Unsafe.As<SArrayObj>(x).m_data.AsSpan().SequenceEqual(Unsafe.As<SArrayObj>(y).m_data)) return false;
                    break;
                case LeanRt.LeanString:
                    if (!LeanRt.lean_string_span(x).SequenceEqual(LeanRt.lean_string_span(y)) || Unsafe.As<StrObj>(x).m_length != Unsafe.As<StrObj>(y).m_length) return false;
                    break;
                case LeanRt.LeanMPZ:
                    if (Unsafe.As<MpzObj>(x).m_value != Unsafe.As<MpzObj>(y).m_value) return false;
                    break;
                case LeanRt.LeanThunk: todo.Push((Unsafe.As<ThunkObj>(x).m_value, Unsafe.As<ThunkObj>(y).m_value)); break;
                case LeanRt.LeanRef: todo.Push((Unsafe.As<RefObj>(x).m_value, Unsafe.As<RefObj>(y).m_value)); break;
                case LeanRt.LeanTask: todo.Push((Unsafe.As<TaskObj>(x).m_value, Unsafe.As<TaskObj>(y).m_value)); break;
                default: return false;
            }
        }
        return true;
    }

    sealed class PairCmp : IEqualityComparer<(Obj, Obj)>
    {
        public bool Equals((Obj, Obj) a, (Obj, Obj) b) => ReferenceEquals(a.Item1, b.Item1) && ReferenceEquals(a.Item2, b.Item2);
        public int GetHashCode((Obj, Obj) p) => RuntimeHelpers.GetHashCode(p.Item1) * 31 + RuntimeHelpers.GetHashCode(p.Item2);
    }

    // ------------------------------------------------------------------

    static List<List<string>> PartGroups(string mainFile)
    {
        var groups = new List<List<string>>();
        var g = new List<string> { mainFile };
        foreach (var ext in new[] { ".server", ".private" })
            if (File.Exists(mainFile + ext)) g.Add(mainFile + ext);
        groups.Add(g);
        string irSig = Path.ChangeExtension(mainFile, ".ir.sig"), ir = Path.ChangeExtension(mainFile, ".ir");
        if (File.Exists(irSig))
        {
            var g2 = new List<string> { irSig };
            if (File.Exists(ir)) g2.Add(ir);
            groups.Add(g2);
        }
        return groups;
    }

    /// <summary>Read all parts of a module, re-compact them (chained, as saveModuleDataParts) and compare bytes.</summary>
    static bool RoundTrip(string lib, string mod, bool quiet = false)
    {
        string f = ModFile(lib, mod);
        if (!File.Exists(f)) { if (!quiet) Console.WriteLine($"(skip {mod}: no file)"); return true; }
        bool allSame = true;
        foreach (var group in PartGroups(f))
        {
            var regions = new List<CompactedRegionData>();
            var sw = Stopwatch.StartNew();
            RegionReader.EagerIndex = true;
            try { foreach (var p in group) regions.Add(OleanFile.Read(p, regions.ToArray())); }
            finally { RegionReader.EagerIndex = false; }
            double readMs = sw.Elapsed.TotalMilliseconds;
            // The on-demand index (DFS from the root) must reproduce the file order.
            foreach (var r in regions)
            {
                var eager = r.m_objs;
                var dfs = r.TraverseFileOrder();
                bool same = eager.Length == dfs.Length;
                for (int i = 0; same && i < eager.Length; i++) same = ReferenceEquals(eager[i], dfs[i]);
                Check(same, $"{r.FilePath}: traversal order = file order ({dfs.Length} vs {eager.Length} objects)");
            }
            var h = regions[0].Header;
            string savedGit = OleanFile.GitHash, savedVer = OleanFile.LeanVersion;
            OleanFile.GitHash = h.GitHash; OleanFile.LeanVersion = h.LeanVersion;
            Obj key = MkName(group[0].EndsWith(".ir.sig") ? mod + ".ir" : mod);
            var c = new ObjectCompactor(OleanFile.BaseAddrForKey(key), null, false, h.Gmp);
            sw.Restart();
            var images = new List<byte[]>();
            foreach (var r in regions) images.Add(OleanFile.SavePart(c, r.Root));
            double writeMs = sw.Elapsed.TotalMilliseconds;
            OleanFile.GitHash = savedGit; OleanFile.LeanVersion = savedVer;
            for (int i = 0; i < group.Count; i++)
            {
                var orig = File.ReadAllBytes(group[i]);
                bool same = orig.AsSpan().SequenceEqual(images[i]);
                if (!same)
                {
                    allSame = false;
                    int d = 0; while (d < Math.Min(orig.Length, images[i].Length) && orig[d] == images[i][d]) d++;
                    Console.WriteLine($"DIFF {Path.GetFileName(group[i])}: sizes {orig.Length} vs {images[i].Length}, first difference at {d}");
                }
                if (!quiet)
                    Console.WriteLine($"{Path.GetRelativePath(lib, group[i]),-45} {orig.Length,10} bytes {regions[i].ObjectCount,9} objs  {(same ? "byte-identical" : "DIFFERENT")}");
            }
            // re-read our images and compare the object graphs
            var re = new List<CompactedRegionData>();
            for (int i = 0; i < images.Count; i++)
            {
                re.Add(OleanFile.Parse(group[i], images[i], re.ToArray()));
                Check(DeepEq(re[i].Root, regions[i].Root), $"{group[i]}: re-read graph equal");
            }
            if (!quiet) Console.WriteLine($"   read {readMs:F1} ms, write {writeMs:F1} ms");
        }
        Check(allSame || quiet, $"{mod}: round trip byte-identical");
        return allSame;
    }

    /// <summary>Reads all parts of the import closure of `root`, as `importModules` does.</summary>
    static void ImportPerf(string lib, string root)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long mem0 = GC.GetTotalMemory(true);
        long alloc0 = GC.GetTotalAllocatedBytes(true);
        var pause0 = GC.GetTotalPauseDuration();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var lm = Environment.GetEnvironmentVariable("CHECK_LATENCY");
        var oldLm = System.Runtime.GCSettings.LatencyMode;
        if (lm != null) System.Runtime.GCSettings.LatencyMode = Enum.Parse<System.Runtime.GCLatencyMode>(lm);
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        var visited = new HashSet<string>();
        var keep = new List<CompactedRegionData>();
        long bytes = 0, objs = 0; int mods = 0;
        bool prefetch = Environment.GetEnvironmentVariable("CHECK_PREFETCH") == "1";
        OleanFile.PrefetchImports = prefetch;
        // Same order and dependency structure as `importModulesCore` (loadData, loadIR, then imports).
        void Visit(string mod)
        {
            if (!visited.Add(mod)) return;
            string f = ModFile(lib, mod);
            var parts = new List<CompactedRegionData>();
            parts.Add(OleanFile.ReadForLean(f));
            if (LeanRt.lean_ctor_get_uint8_s(parts[0].Root, 0) != 0)
            {
                parts.Add(OleanFile.ReadForLean(f + ".server", parts.ToArray()));
                parts.Add(OleanFile.ReadForLean(f + ".private", parts.ToArray()));
            }
            string irSig = Path.ChangeExtension(f, ".ir.sig"), ir = Path.ChangeExtension(f, ".ir");
            if (File.Exists(irSig))
            {
                var irs = new List<CompactedRegionData> { OleanFile.ReadForLean(irSig) };
                if (File.Exists(ir)) irs.Add(OleanFile.ReadForLean(ir, irs.ToArray()));
                parts.AddRange(irs);
            }
            foreach (var r in parts) { keep.Add(r); bytes += (long)r.Size; objs += r.ObjectCount; }
            mods++;
            var imps = Unsafe.As<ArrayObj>(LeanRt.lean_ctor_get(parts[0].Root, 0));
            for (long i = 0; i < imps.m_size; i++) Visit(NameToString(LeanRt.lean_ctor_get(imps.m_data[i], 0)));
        }
        if (Environment.GetEnvironmentVariable("CHECK_PARALLEL") == "1")
        {
            // Experiment: collect the closure first (main parts only), then read all modules in parallel.
            var order = new List<string>();
            var seen = new HashSet<string>();
            void Collect(string mod)
            {
                if (!seen.Add(mod)) return;
                var r0 = OleanFile.Read(ModFile(lib, mod));
                var imps0 = Unsafe.As<ArrayObj>(LeanRt.lean_ctor_get(r0.Root, 0));
                for (long i = 0; i < imps0.m_size; i++) Collect(NameToString(LeanRt.lean_ctor_get(imps0.m_data[i], 0)));
                order.Add(mod);
            }
            Collect(root);
            Console.WriteLine($"closure computed in {sw.Elapsed.TotalSeconds:F2} s");
            sw.Restart();
            var bag = new System.Collections.Concurrent.ConcurrentBag<CompactedRegionData>();
            Parallel.ForEach(order, mod =>
            {
                foreach (var group in PartGroups(ModFile(lib, mod)))
                {
                    var regs = new List<CompactedRegionData>();
                    foreach (var p in group) { var r = OleanFile.Read(p, regs.ToArray()); regs.Add(r); bag.Add(r); }
                }
            });
            foreach (var r in bag) { keep.Add(r); bytes += (long)r.Size; objs += r.ObjectCount; }
            mods = order.Count;
        }
        else Visit(root);
        sw.Stop();
        System.Runtime.GCSettings.LatencyMode = oldLm;
        Console.WriteLine($"GCs: gen0={GC.CollectionCount(0) - g0} gen1={GC.CollectionCount(1) - g1} gen2={GC.CollectionCount(2) - g2}");
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu0;
        long alloc = GC.GetTotalAllocatedBytes(true) - alloc0;
        var pause = GC.GetTotalPauseDuration() - pause0;
        long mem = GC.GetTotalMemory(true) - mem0;
        OleanFile.PrefetchImports = false;
        if (prefetch) Console.WriteLine($"prefetch: {OleanPrefetcher.s_hits} hits, {OleanPrefetcher.s_misses} misses");
        Console.WriteLine($"import {root}{(prefetch ? " (prefetch)" : "")}: {mods} modules, {keep.Count} files, {bytes / 1e6:F0} MB, {objs / 1e6:F2} M objects in {sw.Elapsed.TotalSeconds:F2} s (cpu {cpu.TotalSeconds:F2} s, GC pause {pause.TotalSeconds:F2} s); allocated {alloc / 1e6:F0} MB, live {mem / 1e6:F0} MB");
        GC.KeepAlive(keep);
    }

    static void ReadPerf(string lib, string prefix)
    {
        Console.WriteLine($"== read performance: {(prefix ?? "all modules")}");
        var mains = Directory.EnumerateFiles(lib, "*.olean", SearchOption.AllDirectories)
            .Where(f => prefix == null || Path.GetRelativePath(lib, f).StartsWith(prefix + "/") || Path.GetRelativePath(lib, f) == prefix + ".olean")
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        for (int iter = 0; iter < 2; iter++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long mem0 = GC.GetTotalMemory(true);
            long alloc0 = GC.GetTotalAllocatedBytes(true);
            int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
            var pause0 = GC.GetTotalPauseDuration();
            var keep = new List<CompactedRegionData>();
            long bytes = 0, objs = 0; int files = 0;
            bool nogc = Environment.GetEnvironmentVariable("CHECK_NOGC") == "1" && GC.TryStartNoGCRegion(3L << 30);
            var lm = Environment.GetEnvironmentVariable("CHECK_LATENCY");
            var oldLm = System.Runtime.GCSettings.LatencyMode;
            if (lm != null) System.Runtime.GCSettings.LatencyMode = Enum.Parse<System.Runtime.GCLatencyMode>(lm);
            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            foreach (var f in mains)
                foreach (var group in PartGroups(f))
                {
                    var regs = new List<CompactedRegionData>();
                    foreach (var p in group)
                    {
                        var r = OleanFile.Read(p, regs.ToArray());
                        regs.Add(r); keep.Add(r);
                        bytes += (long)r.Size; objs += r.ObjectCount; files++;
                    }
                }
            sw.Stop();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpu0;
            System.Runtime.GCSettings.LatencyMode = oldLm;
            if (nogc) { Console.WriteLine("(no GC region)"); GC.EndNoGCRegion(); }
            long alloc = GC.GetTotalAllocatedBytes(true) - alloc0;
            long mem = GC.GetTotalMemory(true) - mem0;
            Console.WriteLine($"iter {iter}: {files} files, {bytes / 1e6:F1} MB, {objs / 1e6:F2} M objects in {sw.Elapsed.TotalSeconds:F2} s (cpu {cpu.TotalSeconds:F2} s) " +
                              $"({bytes / 1e6 / sw.Elapsed.TotalSeconds:F0} MB/s); allocated {alloc / 1e6:F0} MB, live {mem / 1e6:F0} MB " +
                              $"({(double)mem / Math.Max(1, objs):F1} B/obj); GCs gen0={GC.CollectionCount(0) - gc0} gen2={GC.CollectionCount(2) - gc2}, GC pause {(GC.GetTotalPauseDuration() - pause0).TotalSeconds:F2} s");
            GC.KeepAlive(keep);
        }
    }
}
