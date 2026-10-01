// Cross-validation of LeanSharp.Zstd against the reference libzstd (through Python's
// standard-library module `compression.zstd`, Python >= 3.14).
//
//   dotnet build checks/zstd -c Release && dotnet checks/zstd/bin/Release/net10.0/Check.dll [--quick] [--keep]
//
// (a) reference-compressed vectors (several levels, with/without dictionary, checksums, streaming
//     frames without content size, multiple + skippable frames, long-distance matching, ...) must
//     decode to the original with ZstdDecoder;
// (b) ZstdEncoder output must decode to the original with the reference decoder (and with ours);
// (c) truncated / bit-flipped / garbage inputs must fail cleanly with ZstdException.
using System.Diagnostics;
using System.Text;
using LeanSharp.Zstd;

static class Program
{
    static int _pass, _fail;
    static readonly List<string> Failures = new();

    static void Ok() => Interlocked.Increment(ref _pass);
    static void Fail(string what)
    {
        Interlocked.Increment(ref _fail);
        lock (Failures) { if (Failures.Count < 60) Failures.Add(what); }
        Console.WriteLine("FAIL: " + what);
    }

    sealed class Input
    {
        public string Name;
        public byte[] Data;
        public bool Big;     // restrict the (slow) variants
        public bool Tiny;    // size-sweep input: only a few variants
    }

    sealed class RefJob
    {
        public int Id;
        public Input In;
        public string Kind;   // plain, chk, noid, stream, multi, ldm
        public int Level;
        public char Dict;     // n = none, d = v1 dictionary, r = raw-content dictionary
        public string Path;
        public override string ToString() => $"{In.Name} [{Kind} L{Level} dict={Dict}]";
    }

    static string FindRepoRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(d.FullName, "checks")))
                    return d.FullName;
        }
        throw new Exception("repository root not found");
    }

    static byte[] ConcatFiles(IEnumerable<string> files, int maxBytes)
    {
        var ms = new MemoryStream();
        foreach (string f in files)
        {
            if (ms.Length >= maxBytes) break;
            byte[] b = File.ReadAllBytes(f);
            ms.Write(b, 0, (int)Math.Min(b.Length, maxBytes - ms.Length));
        }
        return ms.ToArray();
    }

    static string RunPy(string script, params string[] args)
    {
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(script);
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        var err = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception($"python3 {Path.GetFileName(script)} failed ({p.ExitCode}): {err.Result}");
        return stdout;
    }

    const string PyScript = """
import sys, os
from multiprocessing import Pool
from compression import zstd
from compression.zstd import CompressionParameter as CP

work = sys.argv[1]
mode = sys.argv[2]
_dicts = {}

def get_dict(kind):
    if kind == 'n':
        return None
    if kind not in _dicts:
        if kind == 'd':
            _dicts[kind] = zstd.ZstdDict(open(os.path.join(work, 'dict_v1.bin'), 'rb').read())
        else:
            _dicts[kind] = zstd.ZstdDict(open(os.path.join(work, 'dict_raw.bin'), 'rb').read(), is_raw=True)
    return _dicts[kind]

def skippable(payload, n=0):
    return (0x184D2A50 + n).to_bytes(4, 'little') + len(payload).to_bytes(4, 'little') + payload

def gen(line):
    jid, inp, kind, level, dk = line.split('\t')
    level = int(level)
    data = open(os.path.join(work, 'in', inp), 'rb').read()
    zd = get_dict(dk)
    if kind == 'plain':
        out = zstd.compress(data, level=level, zstd_dict=zd)
    elif kind == 'chk':
        out = zstd.compress(data, options={CP.compression_level: level, CP.checksum_flag: 1}, zstd_dict=zd)
    elif kind == 'noid':
        out = zstd.compress(data, options={CP.compression_level: level, CP.dict_id_flag: 0}, zstd_dict=zd)
    elif kind == 'ldm':
        out = zstd.compress(data, options={CP.compression_level: level, CP.enable_long_distance_matching: 1, CP.window_log: 27}, zstd_dict=zd)
    elif kind == 'stream':
        c = zstd.ZstdCompressor(options={CP.compression_level: level, CP.checksum_flag: 1}, zstd_dict=zd)
        step = max(700, len(data) // 400)
        parts = []
        for i in range(0, len(data), step):
            parts.append(c.compress(data[i:i + step], mode=c.FLUSH_BLOCK))
        parts.append(c.flush())
        out = b''.join(parts)
    elif kind == 'multi':
        h = len(data) // 2
        out = (skippable(b'hello') + zstd.compress(data[:h], level=level, zstd_dict=zd) + skippable(b'', 7)
               + zstd.compress(data[h:], options={CP.compression_level: level, CP.checksum_flag: 1}, zstd_dict=zd)
               + skippable(b'x' * 300, 15))
    elif kind == 'xdict':
        # frame made without a dictionary, decoded by the reference with the v1 dictionary loaded
        out = zstd.compress(data, level=level)
        try:
            exp = b'O' + zstd.decompress(out, zstd_dict=get_dict('d'))
        except zstd.ZstdError:
            exp = b'E'
        open(os.path.join(work, 'ref', jid + '.expect'), 'wb').write(exp)
        open(os.path.join(work, 'ref', jid + '.zst'), 'wb').write(out)
        return jid
    else:
        raise Exception('unknown kind ' + kind)
    assert zstd.decompress(out, zstd_dict=zd) == data
    open(os.path.join(work, 'ref', jid + '.zst'), 'wb').write(out)
    return jid

def verify(line):
    jid, inp, dk = line.split('\t')
    try:
        data = open(os.path.join(work, 'in', inp), 'rb').read()
        comp = open(os.path.join(work, 'mine', jid + '.zst'), 'rb').read()
        if zstd.get_frame_size(comp) != len(comp):
            return 'FAIL\t%s\tframe size %d != %d' % (jid, zstd.get_frame_size(comp), len(comp))
        info = zstd.get_frame_info(comp)
        if info.decompressed_size != len(data):
            return 'FAIL\t%s\tframe content size %r != %d' % (jid, info.decompressed_size, len(data))
        out = zstd.decompress(comp, zstd_dict=get_dict(dk))
        if out != data:
            return 'FAIL\t%s\tcontent mismatch' % jid
        # streaming decoder (enforces the window size limit)
        dz = zstd.ZstdDecompressor(zstd_dict=get_dict(dk))
        out2 = dz.decompress(comp)
        if out2 != data or not dz.eof:
            return 'FAIL\t%s\tstreaming content mismatch' % jid
        return 'OK\t%s\t%d' % (jid, info.dictionary_id)
    except Exception as e:
        return 'FAIL\t%s\t%s: %s' % (jid, type(e).__name__, e)

if __name__ == '__main__':
    lines = [l for l in open(os.path.join(work, mode + '.jobs')).read().split('\n') if l]
    with Pool(int(sys.argv[3])) as pool:
        for r in pool.imap_unordered(gen if mode == 'gen' else verify, lines, chunksize=1):
            print(r, flush=True)
""";

    static int Main(string[] args)
    {
        bool quick = args.Contains("--quick");
        bool keep = args.Contains("--keep");
        var total = Stopwatch.StartNew();
        string root = FindRepoRoot();
        string dictPath = Path.Combine(root, "artifacts", "leangz", "dict", "v1.dict");
        string initDir = Path.Combine(root, "artifacts", "selfhost", "lib", "lean", "Init");
        string leanDir = Path.Combine(root, "artifacts", "selfhost", "lib", "lean", "Lean");
        string work = Path.Combine(Path.GetTempPath(), "leansharp-zstd-check-" + Environment.ProcessId);
        Directory.CreateDirectory(Path.Combine(work, "in"));
        Directory.CreateDirectory(Path.Combine(work, "ref"));
        Directory.CreateDirectory(Path.Combine(work, "mine"));
        try
        {
            return Run(root, dictPath, initDir, leanDir, work, quick, total);
        }
        finally
        {
            if (!keep) try { Directory.Delete(work, true); } catch { }
        }
    }

    static int Run(string root, string dictPath, string initDir, string leanDir, string work, bool quick, Stopwatch total)
    {
        byte[] dictBytes = File.ReadAllBytes(dictPath);
        var dictV1 = new ZstdDictionary(dictBytes);
        File.WriteAllBytes(Path.Combine(work, "dict_v1.bin"), dictBytes);

        // ---------------------------------------------------------------- inputs
        var inputs = new List<Input>();
        void Add(string name, byte[] data, bool big = false, bool tiny = false)
        {
            var inp = new Input { Name = name, Data = data, Big = big, Tiny = tiny };
            inputs.Add(inp);
            File.WriteAllBytes(Path.Combine(work, "in", name), data);
        }
        var rng = new Random(12345);
        byte[] Rand(int n) { var b = new byte[n]; rng.NextBytes(b); return b; }

        string[] oleans = Directory.GetFiles(initDir, "*.olean").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        byte[] prelude = File.ReadAllBytes(Path.Combine(initDir, "Prelude.olean"));
        byte[] core = File.ReadAllBytes(Path.Combine(initDir, "Core.olean"));
        string[] csFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/") && new FileInfo(f).Length is > 4000 and < 400000)
            .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        byte[] text = ConcatFiles(csFiles, 3_000_000);
        byte[] rawDict = core.AsSpan(4096, 100_000).ToArray();
        File.WriteAllBytes(Path.Combine(work, "dict_raw.bin"), rawDict);
        var dictRaw = new ZstdDictionary(rawDict);

        Add("empty", Array.Empty<byte>());
        Add("one-byte", new byte[] { 0x42 });
        Add("short-text", Encoding.UTF8.GetBytes("hello hello hello zstd"));
        Add("zeros-300", new byte[300]);
        Add("zeros-1M", new byte[1 << 20]);
        {
            var b = new byte[700_000];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)("abcdefghijklmnopqrstuvwxyz0123456"[i % 33] + (i / 100_000));
            Add("pattern-700K", b);
        }
        Add("random-100", Rand(100));
        Add("random-1M", Rand(1 << 20));
        {
            // low-entropy noise: no matches, Huffman only
            var b = new byte[2_000_000];
            for (int i = 0; i < b.Length; i++) { int r = rng.Next(256); b[i] = (byte)(r < 128 ? 'a' + (r & 3) : r < 200 ? 'e' + (r & 7) : r < 250 ? r & 31 : r); }
            Add("skewed-2M", b);
        }
        {
            // structured binary records with repeats at many distances
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            for (int i = 0; i < 120_000; i++)
            {
                w.Write((uint)(i * 2654435761u >> 20));
                w.Write((ushort)(i % 977));
                w.Write((long)(i / 13) << 3);
                if (i % 17 == 0) w.Write(Encoding.ASCII.GetBytes("record-" + (i % 4001)));
                if (i % 1000 == 3) w.Write(Rand(rng.Next(1, 200)));
            }
            Add("records-2M", ms.ToArray());
        }
        Add("text-cs", text);
        Add("text-cs-5K", text.AsSpan(1000, 5000).ToArray());
        Add("small-olean", File.ReadAllBytes(Path.Combine(initDir, "BinderNameHint.olean")));
        Add("Core.olean", core);
        Add("Prelude.olean", prelude);
        string ilean = Path.Combine(initDir, "Core.ilean");
        if (File.Exists(ilean)) Add("Core.ilean", File.ReadAllBytes(ilean));
        {
            // block boundary and header-format boundary sizes
            int[] sizes = { 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 255, 256, 257, 511, 1023, 1024, 1025,
                            4095, 4096, 16383, 16384, 16385, 65535, 65536, 65537, 65791, 65792, 131071, 131072, 131073, 262144, 262145 };
            foreach (int n in sizes)
            {
                Add("olean-" + n, core.AsSpan(8192, n).ToArray(), tiny: true);
                if (n <= 4096 || n == 131073) Add("text-" + n, text.AsSpan(0, n).ToArray(), tiny: true);
                if (n is 127 or 257 or 1025 or 131073) Add("rand-" + n, Rand(n), tiny: true);
            }
        }
        if (!quick)
        {
            Add("zeros-20M", new byte[20_000_000], big: true);
            var allOleans = oleans.Concat(Directory.Exists(leanDir)
                ? Directory.GetFiles(leanDir, "*.olean", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>());
            Add("oleans-40M", ConcatFiles(allOleans, 40_000_000), big: true);
            {
                // far repeats: beyond the encoder window and beyond level 19's window
                var b = new byte[30_000_000];
                byte[] r = Rand(3_000_000);
                r.CopyTo(b, 0);
                text.AsSpan(0, Math.Min(text.Length, 2_000_000)).CopyTo(b.AsSpan(3_000_000));
                prelude.CopyTo(b, 6_000_000);
                for (int i = 10_000_000; i < 24_000_000; i++) b[i] = (byte)(rng.Next(256) < 200 ? 'a' + (i & 7) : rng.Next(256));
                r.CopyTo(b, 24_000_000);
                prelude.AsSpan(0, 3_000_000).CopyTo(b.AsSpan(27_000_000));
                Add("far-repeats-30M", b, big: true);
            }
        }
        Console.WriteLine($"inputs: {inputs.Count} ({inputs.Sum(i => (long)i.Data.Length) / 1e6:F1} MB) in {work}");

        string script = Path.Combine(work, "zstd_ref.py");
        File.WriteAllText(script, PyScript);

        // ---------------------------------------------------------------- (a) reference -> our decoder
        var jobs = new List<RefJob>();
        void Job(Input inp, string kind, int level, char dict)
        {
            var j = new RefJob { Id = jobs.Count, In = inp, Kind = kind, Level = level, Dict = dict };
            j.Path = Path.Combine(work, "ref", j.Id + ".zst");
            jobs.Add(j);
        }
        foreach (Input inp in inputs)
        {
            if (inp.Tiny)
            {
                Job(inp, "plain", 3, 'n'); Job(inp, "plain", 19, 'n'); Job(inp, "plain", 19, 'd'); Job(inp, "stream", 6, 'd');
            }
            else if (inp.Big)
            {
                foreach (int l in new[] { 1, 3, 19 }) Job(inp, "plain", l, 'n');
                Job(inp, "plain", 22, 'n');
                Job(inp, "plain", 3, 'd');
                Job(inp, "plain", 19, 'd');
                Job(inp, "ldm", 3, 'n');
                Job(inp, "chk", 9, 'r');
                Job(inp, "multi", 1, 'd');
            }
            else
            {
                foreach (int l in new[] { -5, 1, 3, 9, 19, 22 })
                {
                    Job(inp, "plain", l, 'n');
                    Job(inp, "plain", l, 'd');
                }
                foreach (int l in new[] { 1, 5, 13, 19 }) Job(inp, "plain", l, 'r');
                Job(inp, "chk", 3, 'n'); Job(inp, "chk", 19, 'd');
                Job(inp, "noid", 3, 'd'); Job(inp, "noid", 19, 'd');
                Job(inp, "stream", 3, 'n'); Job(inp, "stream", 19, 'd'); Job(inp, "stream", 1, 'r'); Job(inp, "stream", 12, 'n');
                Job(inp, "multi", 3, 'n'); Job(inp, "multi", 19, 'd');
                Job(inp, "ldm", 19, 'n');
                Job(inp, "xdict", 3, 'n'); Job(inp, "xdict", 19, 'n');
            }
        }
        File.WriteAllText(Path.Combine(work, "gen.jobs"),
            string.Join("\n", jobs.Select(j => $"{j.Id}\t{j.In.Name}\t{j.Kind}\t{j.Level}\t{j.Dict}")));
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"(a) generating {jobs.Count} reference vectors with python3 compression.zstd ...");
        RunPy(script, work, "gen", "8");
        Console.WriteLine($"    done in {sw.Elapsed.TotalSeconds:F1}s");

        ZstdDictionary DictOf(char k) => k == 'd' ? dictV1 : k == 'r' ? dictRaw : null;
        sw.Restart();
        long decBytes = 0;
        int aPass0 = _pass;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 6 }, j =>
        {
            try
            {
                if (!File.Exists(j.Path)) { Fail($"(a) {j}: reference vector missing"); return; }
                byte[] comp = File.ReadAllBytes(j.Path);
                if (j.Kind == "xdict")
                {
                    // must behave like the reference decoder with a dictionary loaded and a frame without dictionary ID
                    byte[] expect = File.ReadAllBytes(Path.ChangeExtension(j.Path, ".expect"));
                    byte[] got;
                    try { got = ZstdDecoder.Decompress(comp, dictV1); }
                    catch (ZstdException) { got = null; }
                    bool same = expect[0] == 'E' ? got == null : got != null && got.AsSpan().SequenceEqual(expect.AsSpan(1));
                    if (same) Ok(); else Fail($"(a) {j}: differs from the reference decoder (reference {(expect[0] == 'E' ? "fails" : "decodes")}, we {(got == null ? "fail" : "decode")})");
                    return;
                }
                byte[] dec = ZstdDecoder.Decompress(comp, DictOf(j.Dict));
                Interlocked.Add(ref decBytes, dec.Length);
                if (dec.AsSpan().SequenceEqual(j.In.Data)) Ok();
                else Fail($"(a) {j}: decoded content differs");
            }
            catch (Exception e) { Fail($"(a) {j}: {e.GetType().Name}: {e.Message}"); }
        });
        Console.WriteLine($"(a) decoded {_pass - aPass0}/{jobs.Count} reference vectors correctly ({decBytes / 1e6:F0} MB) in {sw.Elapsed.TotalSeconds:F1}s");

        // dictionary semantics of the reference decoder
        {
            Input t = inputs.First(i => i.Name == "text-cs-5K");
            byte[] withDict = File.ReadAllBytes(jobs.First(j => j.In == t && j.Kind == "plain" && j.Level == 3 && j.Dict == 'd').Path);
            ExpectThrow("(a) frame with dictionary ID decoded without dictionary", () => ZstdDecoder.Decompress(withDict));
            ExpectThrow("(a) frame with dictionary ID decoded with a raw dictionary", () => ZstdDecoder.Decompress(withDict, dictRaw));
            Expect("(a) empty input decodes to empty output", () => ZstdDecoder.Decompress(ReadOnlySpan<byte>.Empty).Length == 0);
            Expect("(a) dictionary id", () => dictV1.Id == 1598502866u && dictRaw.Id == 0);
        }

        // ---------------------------------------------------------------- (b) our encoder -> reference decoder
        sw.Restart();
        var encJobs = new List<(int id, Input inp, int level, char dict)>();
        foreach (Input inp in inputs)
        {
            if (inp.Tiny)
            {
                encJobs.Add((encJobs.Count, inp, 19, 'n')); encJobs.Add((encJobs.Count, inp, 19, 'd')); encJobs.Add((encJobs.Count, inp, 2, 'r'));
            }
            else if (inp.Big)
            {
                encJobs.Add((encJobs.Count, inp, 3, 'n')); encJobs.Add((encJobs.Count, inp, 19, 'n'));
                encJobs.Add((encJobs.Count, inp, 19, 'd')); encJobs.Add((encJobs.Count, inp, 22, 'r'));
            }
            else
            {
                foreach (int l in new[] { 1, 3, 9, 19, 22 })
                    foreach (char d in "ndr")
                        encJobs.Add((encJobs.Count, inp, l, d));
            }
        }
        var encSizes = new long[encJobs.Count];
        int bPass0 = _pass;
        Parallel.ForEach(encJobs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, j =>
        {
            string what = $"(b) {j.inp.Name} [L{j.level} dict={j.dict}]";
            try
            {
                ZstdDictionary d = DictOf(j.dict);
                byte[] comp = j.dict == 'n' && j.level == 19 ? ZstdEncoder.Compress(j.inp.Data) : ZstdEncoder.Compress(j.inp.Data, d, j.level);
                encSizes[j.id] = comp.Length;
                File.WriteAllBytes(Path.Combine(work, "mine", j.id + ".zst"), comp);
                byte[] back = ZstdDecoder.Decompress(comp, d);
                if (!back.AsSpan().SequenceEqual(j.inp.Data)) { Fail(what + ": own decoder round trip differs"); return; }
                if (!j.inp.Big || j.level == 3)
                {
                    byte[] again = ZstdEncoder.Compress(j.inp.Data, d, j.level);
                    if (!again.AsSpan().SequenceEqual(comp)) { Fail(what + ": encoder is not deterministic"); return; }
                }
                if (comp.Length > j.inp.Data.Length + 3 * (j.inp.Data.Length / 131072 + 1) + 18) { Fail(what + ": output exceeds the stored-block bound"); return; }
                Ok();
            }
            catch (Exception e) { Fail($"{what}: {e.GetType().Name}: {e.Message}"); }
        });
        Console.WriteLine($"(b) compressed {encJobs.Count} inputs with ZstdEncoder (own round trip + determinism: {_pass - bPass0} ok) in {sw.Elapsed.TotalSeconds:F1}s");
        sw.Restart();
        File.WriteAllText(Path.Combine(work, "verify.jobs"), string.Join("\n", encJobs.Select(j => $"{j.id}\t{j.inp.Name}\t{j.dict}")));
        string vout = RunPy(script, work, "verify", "8");
        var seen = new HashSet<int>();
        int bRef = 0;
        foreach (string line in vout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = line.Split('\t');
            int id = int.Parse(f[1]);
            seen.Add(id);
            var j = encJobs[id];
            string what = $"(b) {j.inp.Name} [L{j.level} dict={j.dict}] reference decoder";
            if (f[0] != "OK") { Fail($"{what}: {f[2]}"); continue; }
            uint did = uint.Parse(f[2]);
            uint want = j.dict == 'd' ? dictV1.Id : 0;
            if (did != want) { Fail($"{what}: frame dictionary id {did}, expected {want}"); continue; }
            Ok(); bRef++;
        }
        foreach (var j in encJobs) if (!seen.Contains(j.id)) Fail($"(b) {j.inp.Name} [L{j.level} dict={j.dict}]: no verdict from the reference decoder");
        Console.WriteLine($"(b) reference decoder accepted {bRef}/{encJobs.Count} ZstdEncoder frames (one-shot + streaming, dictionary id checked) in {sw.Elapsed.TotalSeconds:F1}s");

        // ---------------------------------------------------------------- (c) corrupt input
        sw.Restart();
        int cPass0 = _pass;
        var corruptTask = Task.Run(() => CorruptionTests(jobs, inputs, dictV1, dictRaw));
        if (!corruptTask.Wait(TimeSpan.FromMinutes(10))) Fail("(c) corruption tests did not finish (decoder hang?)");
        else Console.WriteLine($"(c) corrupt input: {corruptTask.Result} in {sw.Elapsed.TotalSeconds:F1}s");

        // ---------------------------------------------------------------- ratios and throughput
        Console.WriteLine();
        Console.WriteLine("compressed sizes (bytes); 'ours' = ZstdEncoder, 'ref' = libzstd; ratio = ours / ref");
        Console.WriteLine($"{"input",-18}{"dict",5}{"size",11}{"ours L3",11}{"ours L19",11}{"ref L3",11}{"ref L19",11}{"L3/refL3",10}{"L19/refL3",10}{"L19/refL19",11}");
        string[] ratioInputs = { "text-cs-5K", "small-olean", "olean-65536", "Core.olean", "Prelude.olean", "text-cs", "Core.ilean", "records-2M", "skewed-2M", "oleans-40M", "far-repeats-30M" };
        foreach (string name in ratioInputs)
        {
            Input inp = inputs.FirstOrDefault(i => i.Name == name);
            if (inp == null) continue;
            foreach (char d in "nd")
            {
                long RefSize(int level)
                {
                    var j = jobs.FirstOrDefault(x => x.In == inp && x.Kind == "plain" && x.Level == level && x.Dict == d);
                    return j != null && File.Exists(j.Path) ? new FileInfo(j.Path).Length : -1;
                }
                long r3 = RefSize(3), r19 = RefSize(19);
                if (r3 < 0 || r19 < 0) continue;
                long o3 = ZstdEncoder.Compress(inp.Data, DictOf(d), 3).Length;
                long o19 = ZstdEncoder.Compress(inp.Data, DictOf(d), 19).Length;
                Console.WriteLine($"{name,-18}{(d == 'd' ? "v1" : "-"),5}{inp.Data.Length,11}{o3,11}{o19,11}{r3,11}{r19,11}{(double)o3 / r3,10:F3}{(double)o19 / r3,10:F3}{(double)o19 / r19,11:F3}");
                if (inp.Data.Length >= 5000 && o19 > r3 * 1.3) Fail($"ratio: {name} dict={d}: L19 output {o19} is more than 1.3x reference level 3 ({r3})");
                else Ok();
            }
        }

        Console.WriteLine();
        Console.WriteLine("throughput (single thread, best of 3)");
        Console.WriteLine($"{"input",-18}{"dict",5}{"enc L3 MB/s",13}{"enc L19 MB/s",14}{"dec MB/s",10}");
        foreach (string name in new[] { "Core.olean", "text-cs", "small-olean", "random-1M", "zeros-20M", "oleans-40M" })
        {
            Input inp = inputs.FirstOrDefault(i => i.Name == name);
            if (inp == null) continue;
            foreach (char d in inp.Big ? "n" : "nd")
            {
                ZstdDictionary dict = DictOf(d);
                int reps = inp.Big ? 1 : 3;
                int inner = Math.Max(1, 2_000_000 / Math.Max(inp.Data.Length, 1));
                double Best(Action a)
                {
                    double best = double.MaxValue;
                    for (int r = 0; r < reps; r++)
                    {
                        var s = Stopwatch.StartNew();
                        for (int k = 0; k < inner; k++) a();
                        best = Math.Min(best, s.Elapsed.TotalSeconds / inner);
                    }
                    return inp.Data.Length / 1e6 / best;
                }
                byte[] c19 = ZstdEncoder.Compress(inp.Data, dict, 19);
                double e3 = Best(() => ZstdEncoder.Compress(inp.Data, dict, 3));
                double e19 = Best(() => ZstdEncoder.Compress(inp.Data, dict, 19));
                double dd = Best(() => ZstdDecoder.Decompress(c19, dict));
                Console.WriteLine($"{name,-18}{(d == 'd' ? "v1" : "-"),5}{e3,13:F1}{e19,14:F1}{dd,10:F0}");
            }
        }

        Console.WriteLine();
        if (_fail > 0)
        {
            Console.WriteLine("failures:");
            foreach (string f in Failures) Console.WriteLine("  " + f);
        }
        Console.WriteLine($"zstd check: {_pass} passed, {_fail} failed ({total.Elapsed.TotalSeconds:F0}s, libzstd {pyVersionOf(script, work)})");
        return _fail == 0 ? 0 : 1;
    }

    static string pyVersionOf(string script, string work)
    {
        try
        {
            var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("from compression import zstd; print(zstd.zstd_version)");
            using var p = Process.Start(psi);
            string s = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return s;
        }
        catch { return "?"; }
    }

    static void Expect(string what, Func<bool> f)
    {
        try { if (f()) Ok(); else Fail(what); }
        catch (Exception e) { Fail($"{what}: {e.GetType().Name}: {e.Message}"); }
    }

    static void ExpectThrow(string what, Action a)
    {
        try { a(); Fail(what + ": expected ZstdException"); }
        catch (ZstdException) { Ok(); }
        catch (Exception e) { Fail($"{what}: {e.GetType().Name} instead of ZstdException: {e.Message}"); }
    }

    /// <summary>Truncations must throw ZstdException; bit flips and garbage must either throw ZstdException or decode.</summary>
    static string CorruptionTests(List<RefJob> jobs, List<Input> inputs, ZstdDictionary dictV1, ZstdDictionary dictRaw)
    {
        var rng = new Random(777);
        int trunc = 0, flips = 0, flipsDetected = 0, garbage = 0, bad = 0;
        ZstdDictionary DictOf(char k) => k == 'd' ? dictV1 : k == 'r' ? dictRaw : null;

        bool Try(byte[] data, ZstdDictionary d, string what, bool mustThrow)
        {
            try
            {
                ZstdDecoder.Decompress(data, d);
                if (mustThrow) { bad++; Fail($"(c) {what}: decoded without error"); }
                return false;
            }
            catch (ZstdException) { return true; }
            catch (Exception e) { bad++; Fail($"(c) {what}: {e.GetType().Name}: {e.Message}"); return true; }
        }

        var picks = new List<RefJob>();
        void Pick(string input, string kind, int level, char dict)
        {
            var j = jobs.FirstOrDefault(x => x.In.Name == input && x.Kind == kind && x.Level == level && x.Dict == dict);
            if (j != null && File.Exists(j.Path)) picks.Add(j);
        }
        Pick("text-cs-5K", "plain", 3, 'n'); Pick("text-cs-5K", "plain", 19, 'd'); Pick("text-cs-5K", "chk", 3, 'n');
        Pick("text-cs-5K", "stream", 3, 'n'); Pick("text-cs-5K", "stream", 19, 'd'); Pick("text-cs-5K", "plain", -5, 'n');
        Pick("small-olean", "plain", 19, 'n'); Pick("small-olean", "plain", 3, 'd'); Pick("small-olean", "chk", 19, 'd');
        Pick("short-text", "plain", 3, 'n'); Pick("one-byte", "plain", 3, 'n'); Pick("empty", "chk", 3, 'n');
        Pick("zeros-1M", "plain", 3, 'n'); Pick("random-100", "plain", 19, 'd'); Pick("pattern-700K", "stream", 12, 'n');
        Pick("Core.olean", "plain", 19, 'n'); Pick("Core.olean", "chk", 19, 'd'); Pick("text-cs", "stream", 3, 'n');
        Pick("records-2M", "plain", 22, 'd'); Pick("skewed-2M", "plain", 3, 'n'); Pick("olean-131073", "plain", 19, 'd');

        foreach (RefJob j in picks)
        {
            byte[] comp = File.ReadAllBytes(j.Path);
            ZstdDictionary d = DictOf(j.Dict);
            bool multi = j.Kind == "multi";
            // truncation: every prefix for small vectors, a sample for larger ones
            int nTrunc = comp.Length <= 6000 ? comp.Length - 1 : 300;
            for (int k = 0; k < nTrunc; k++)
            {
                int len = comp.Length <= 6000 ? k + 1 : 1 + rng.Next(comp.Length - 1);
                if (comp.Length > 6000 && k < 40) len = comp.Length - 1 - k;
                Try(comp.AsSpan(0, len).ToArray(), d, $"{j} truncated to {len}/{comp.Length}", mustThrow: true);
                trunc++;
            }
            // single bit flips
            int nFlip = comp.Length <= 6000 ? Math.Min(comp.Length * 8, 6000) : 400;
            for (int k = 0; k < nFlip; k++)
            {
                byte[] c = (byte[])comp.Clone();
                int bit = comp.Length * 8 <= 6000 ? k : rng.Next(comp.Length * 8);
                c[bit >> 3] ^= (byte)(1 << (bit & 7));
                if (Try(c, d, $"{j} bit {bit} flipped", mustThrow: false)) flipsDetected++;
                flips++;
            }
            // random byte / range damage
            for (int k = 0; k < 300; k++)
            {
                byte[] c = (byte[])comp.Clone();
                int n = 1 + rng.Next(4);
                for (int i = 0; i < n; i++) c[rng.Next(c.Length)] = (byte)rng.Next(256);
                if (Try(c, d, $"{j} random bytes overwritten", mustThrow: false)) flipsDetected++;
                flips++;
            }
        }
        // garbage after a valid magic number / frame header
        for (int k = 0; k < 20000; k++)
        {
            byte[] g = new byte[4 + rng.Next(200)];
            rng.NextBytes(g);
            g[0] = 0x28; g[1] = 0xB5; g[2] = 0x2F; g[3] = 0xFD;
            if ((k & 1) == 0 && g.Length > 8) { g[4] = (byte)(k % 3 == 0 ? 0x20 : 0x00); g[5] = (byte)(k % 5 == 0 ? 40 : 0x58); g[6] = (byte)(4 + 8 * rng.Next(30)); g[7] = 0; g[8] = 0; }
            Try(g, (k & 2) == 0 ? null : dictV1, "garbage frame", mustThrow: false);
            garbage++;
        }
        Try(new byte[] { 1, 2, 3 }, null, "3 bytes of garbage", mustThrow: true);
        Try(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, null, "bad magic", mustThrow: true);
        if (bad == 0) Ok();
        return $"{trunc} truncations all rejected, {flipsDetected}/{flips} damaged inputs rejected (rest decoded without crashing), {garbage} garbage frames, {bad} unclean failures over {picks.Count} vectors";
    }
}
