// Port of `tar.rs` of leangz (https://github.com/digama0/leangz, v0.1.20, Apache-2.0): the
// `leantar` command line.

using System.Globalization;
using System.Text.Json;

namespace LeanSharp.Leantar;

public static class LeantarCli
{
    public const string Version = "0.1.20";

    const string Help =
        "leantar " + Version + " lean (de)compression utility\n" +
        "\n" +
        "usage:\n" +
        "* leantar [OPTS] {-d,-x} [FILE.ltar ...]\n" +
        "  Decompress each file FILE.ltar into the current directory.\n" +
        "  * If one of the arguments is '-', then additional files are read from stdin,\n" +
        "    one filename per line.\n" +
        "  * With '-j -', stdin is instead read as JSON: a list of records of the form\n" +
        "      { file: string, base?: string | [string], hash?: string }\n" +
        "    where \"foo\" is interpreted like {\"file\": \"foo\"}.\n" +
        "    * file: the ltar file to decompress\n" +
        "    * base: the extraction base (overrides -C)\n" +
        "    * hash: a (hex) depHash to write into the unpacked .trace, replacing the\n" +
        "      one stored in the .ltar (required if the file was packed with -s)\n" +
        "\n" +
        "* leantar [OPTS] OUT.ltar FILE.trace [(FILE | -c COMMENT) ...]\n" +
        "  Compress files FILE.trace and FILE ... into OUT.ltar\n" +
        "  * -c COMMENT will add a comment to the file which can be recovered using -k\n" +
        "\n" +
        "* leantar [OPTS] -k FILE.ltar\n" +
        "  Unpack comments in FILE.ltar\n" +
        "\n" +
        "general options:\n" +
        "  --version   Prints the version and exits\n" +
        "  --help      Prints the help and exits\n" +
        "  -v          Show verbose information\n" +
        "  -C <DIR>    Use DIR instead of current dir as extraction base\n" +
        "              (can be overridden per-file with the JSON input)\n" +
        "\n" +
        "compress opts:\n" +
        "  -s          Strip depHash from .ltar output\n" +
        "\n" +
        "decompress opts:\n" +
        "  -f          Always unpack even if a matching trace file exists\n" +
        "  -j          Read the '-' stdin input as JSON (default: one filename per line)\n" +
        "  -r, --delete-corrupted\n" +
        "              Delete input FILE.ltar files if they fail parsing\n" +
        "  --jobs <N>  Unpack files with N threads (default: num CPUs)";

    sealed class UsageError : Exception
    {
        public UsageError(string message) : base(message) { }
    }

    /// <summary>
    /// Runs `leantar` with the given arguments. Relative paths are resolved against `cwd`
    /// (default: the current directory of the process). Returns the exit code.
    /// </summary>
    public static int Main(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdoutRaw, TextWriter stderrRaw, string cwd = null)
    {
        cwd ??= Directory.GetCurrentDirectory();
        // several threads write diagnostics
        var stdout = TextWriter.Synchronized(stdoutRaw);
        var stderr = TextWriter.Synchronized(stderrRaw);
        try
        {
            return Run(argv, stdin, stdout, stderr, cwd);
        }
        catch (UsageError e)
        {
            stderr.WriteLine("error: " + e.Message);
            stderr.WriteLine(Help);
            return 1;
        }
        catch (Exception e)
        {
            // the Rust program panics in these cases (exit code 101)
            stderr.WriteLine("leantar: " + e.Message);
            return 101;
        }
        finally
        {
            stdout.Flush();
            stderr.Flush();
        }
    }

    static int Run(IReadOnlyList<string> argv, TextReader stdin, TextWriter stdout, TextWriter stderr, string cwd)
    {
        bool doDecompress = false, doShowComments = false, verbose = false, force = false;
        bool jsonStdin = false, deleteCorrupted = false, includeHash = true;
        int jobs = Environment.ProcessorCount;
        var basedirs = new List<string>();
        string Abs(string p) => Path.GetFullPath(p, cwd);
        int i = 0;
        for (; i < argv.Count; i++)
        {
            string arg = argv[i];
            if (arg == "--version") { stdout.WriteLine("leantar " + Version); return 0; }
            if (arg == "--help") { stderr.WriteLine(Help); return 0; }
            if (arg == "-v") verbose = true;
            else if (arg == "-f") force = true;
            else if (arg == "-j") jsonStdin = true;
            else if (arg == "-r" || arg == "--delete-corrupted") deleteCorrupted = true;
            else if (arg == "--jobs")
            {
                if (++i >= argv.Count) throw new UsageError("--jobs missing argument");
                jobs = int.Parse(argv[i], NumberStyles.None, CultureInfo.InvariantCulture);
            }
            else if (arg == "-d" || arg == "-x") doDecompress = true;
            else if (arg == "-C")
            {
                if (++i >= argv.Count) throw new UsageError("-C missing argument");
                basedirs.Add(Abs(argv[i]));
            }
            else if (arg == "-k") doShowComments = true;
            else if (arg == "-s") includeHash = false;
            else break;
        }
        var rest = argv.Skip(i).ToList();
        if (basedirs.Count == 0) basedirs.Add(Abs("."));

        if (doShowComments)
        {
            if (rest.Count == 0) throw new UsageError("expected FILE.ltar");
            string file = rest[0];
            byte[] data;
            try { data = File.ReadAllBytes(Abs(file)); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                stderr.WriteLine($"{file} not found");
                return 1;
            }
            try
            {
                foreach (var c in Ltar.Comments(data)) stdout.WriteLine(c);
            }
            catch (LtarException e)
            {
                stderr.WriteLine(e.Message);
                return 1;
            }
            return 0;
        }

        if (doDecompress)
        {
            // (per-file base directories, file, hash override)
            var items = new List<(List<string> bases, string file, ulong? hash)>();
            bool fromStdin = false;
            foreach (var arg in rest)
            {
                if (arg != "-")
                {
                    items.Add((new List<string>(), arg, null));
                    continue;
                }
                if (fromStdin) throw new InvalidOperationException("two stdin inputs");
                fromStdin = true;
                if (!jsonStdin)
                {
                    string line;
                    while ((line = stdin.ReadLine()) != null) items.Add((new List<string>(), line, null));
                    continue;
                }
                using var doc = JsonDocument.Parse(stdin.ReadToEnd());
                if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("expected a JSON array");
                foreach (var j in doc.RootElement.EnumerateArray())
                {
                    if (j.ValueKind == JsonValueKind.String)
                    {
                        items.Add((new List<string>(), j.GetString(), null));
                        continue;
                    }
                    if (j.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("expected object");
                    if (!j.TryGetProperty("file", out var f) || f.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException("expected string");
                    var bases = new List<string>();
                    if (j.TryGetProperty("base", out var b))
                    {
                        if (b.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var v in b.EnumerateArray())
                            {
                                if (v.ValueKind == JsonValueKind.Null) bases.Add(null);
                                else if (v.ValueKind == JsonValueKind.String) bases.Add(v.GetString());
                                else throw new InvalidOperationException("expected string or null");
                            }
                        }
                        else if (b.ValueKind == JsonValueKind.String) bases.Add(b.GetString());
                        else throw new InvalidOperationException("expected string or array");
                    }
                    ulong? hash = null;
                    if (j.TryGetProperty("hash", out var h) && h.ValueKind != JsonValueKind.Null)
                    {
                        if (h.ValueKind != JsonValueKind.String
                            || !ulong.TryParse(h.GetString(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hv))
                            throw new InvalidOperationException("expected hex hash");
                        hash = hv;
                    }
                    items.Add((bases, f.GetString(), hash));
                }
            }

            int error = 0;
            Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, item =>
            {
                var (bases, file, hash) = item;
                if (verbose) stdout.WriteLine($"unpacking {file}");
                try
                {
                    var dirs = new List<string>(basedirs);
                    for (int k = 0; k < bases.Count; k++)
                    {
                        if (bases[k] == null) continue;
                        if (k < dirs.Count) dirs[k] = Abs(bases[k]);
                        else if (k == dirs.Count) dirs.Add(Abs(bases[k]));
                        else throw new InvalidOperationException($"{file}: missing basedir {dirs.Count}");
                    }
                    byte[] data;
                    try { data = File.ReadAllBytes(Abs(file)); }
                    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                    {
                        stderr.WriteLine($"{file} not found");
                        Volatile.Write(ref error, 1);
                        return;
                    }
                    try
                    {
                        Ltar.Unpack(dirs, data, force, hash, verbose, stdout, stderr);
                    }
                    catch (LtarTruncatedException)
                    {
                        if (deleteCorrupted)
                        {
                            stderr.WriteLine($"{file}: removing corrupted file");
                            try { File.Delete(Abs(file)); } catch (IOException) { }
                        }
                        else
                        {
                            stderr.WriteLine($"{file}: file is corrupted, try deleting or redownloading it");
                            Volatile.Write(ref error, 1);
                        }
                    }
                    catch (Exception e) when (e is LtarException or IOException or UnauthorizedAccessException)
                    {
                        stderr.WriteLine($"{file}: {e.Message}");
                        Volatile.Write(ref error, 1);
                    }
                }
                catch (Exception e)
                {
                    stderr.WriteLine($"{file}: {e.Message}");
                    Volatile.Write(ref error, 101);
                }
            });
            return error;
        }

        {
            if (rest.Count < 1) throw new UsageError("expected OUT.ltar");
            if (rest.Count < 2) throw new UsageError("expected FILE.trace");
            string tarfile = Abs(rest[0]);
            byte[] data = Ltar.Pack(basedirs, rest[1], rest.Skip(2).ToList(), includeHash, verbose, stdout, stderr);
            // write next to the destination and rename, so that readers never see a partial file
            string dir = Path.GetDirectoryName(tarfile) ?? ".";
            string tmp = Path.Combine(dir, ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 12));
            try
            {
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, tarfile, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (IOException) { }
                throw;
            }
            return 0;
        }
    }
}
