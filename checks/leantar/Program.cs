// Checks of the lgz encoding (src/LeanSharp/Leantar/Lgz.cs).
//   Check roundtrip <dir>            every .olean under <dir>: Decompress(Compress(x)) == x,
//                                    and module triples (.olean, .olean.server, .olean.private) together
//   Check compress <out> <olean>...  write the lgz stream of the given parts
//   Check decompress <in> <n> <outprefix>
//   Check leantar <args>             the leantar command line (see cross_ltar.sh)
using LeanSharp.Leantar;

if (args.Length >= 1 && args[0] == "leantar")
    return LeantarCli.Main(args[1..], Console.In, Console.Out, Console.Error);
if (args.Length >= 2 && args[0] == "roundtrip")
{
    int files = 0, bad = 0; long inBytes = 0, outBytes = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    foreach (var f in Directory.EnumerateFiles(args[1], "*.olean", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
    {
        var parts = new List<byte[]> { File.ReadAllBytes(f) };
        if (File.Exists(f + ".server") && File.Exists(f + ".private"))
        {
            parts.Add(File.ReadAllBytes(f + ".server"));
            parts.Add(File.ReadAllBytes(f + ".private"));
        }
        try
        {
            var lgz = Lgz.Compress(parts);
            var (buf, ranges) = Lgz.Decompress(lgz, parts.Count);
            bool ok = ranges.Length == parts.Count;
            for (int i = 0; ok && i < parts.Count; i++)
                ok = buf.AsSpan(ranges[i].start, ranges[i].end - ranges[i].start).SequenceEqual(parts[i]);
            // single files too
            var lgz1 = Lgz.Compress(new[] { parts[0] });
            var (buf1, r1) = Lgz.Decompress(lgz1, 1);
            ok = ok && buf1.AsSpan(r1[0].start, r1[0].end - r1[0].start).SequenceEqual(parts[0]);
            files++; inBytes += parts.Sum(p => (long)p.Length); outBytes += lgz.Length;
            if (!ok) { bad++; Console.WriteLine("MISMATCH " + f); }
        }
        catch (Exception e) { bad++; Console.WriteLine($"ERROR {f}: {e.Message}"); }
    }
    Console.WriteLine($"{files} modules, {bad} failures, {inBytes / 1e6:F1} MB -> {outBytes / 1e6:F1} MB lgz in {sw.Elapsed.TotalSeconds:F1} s");
    return bad == 0 ? 0 : 1;
}
if (args.Length >= 3 && args[0] == "compress")
{
    File.WriteAllBytes(args[1], Lgz.Compress(args.Skip(2).Select(File.ReadAllBytes).ToList()));
    return 0;
}
if (args.Length == 4 && args[0] == "decompress")
{
    var (buf, ranges) = Lgz.Decompress(File.ReadAllBytes(args[1]), int.Parse(args[2]));
    for (int i = 0; i < ranges.Length; i++)
        File.WriteAllBytes(args[3] + "." + i, buf.AsSpan(ranges[i].start, ranges[i].end - ranges[i].start).ToArray());
    return 0;
}
Console.Error.WriteLine("usage: Check roundtrip <dir> | compress <out> <olean>... | decompress <in> <n> <outprefix>");
return 2;
