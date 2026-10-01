// Port of `ltar.rs` of leangz (https://github.com/digama0/leangz, v0.1.20, Apache-2.0): the `.ltar`
// archive format Lake uses for its artifact cache.
//
// An archive holds the build trace of a module followed by its build outputs:
//
//   magic "LTAR" | "LTR2" | "LTR3" | "LTR4"
//   v4: flag byte (0: no hash follows, 1: hash follows);  depHash : u64 (unless flag 0)
//   trace path (NUL-terminated)
//   v2+: the trace, as one entry body (see below)
//   entries:  v3+: base directory index (byte);  path (NUL-terminated);  entry body
//   comments: v3+: a 0 byte;  an empty path (just NUL);  the comment (NUL-terminated)
//
// An entry body is a compression byte followed by
//   0 zstd           u64 length, zstd frame (no dictionary)
//   1 lgz            u64 length, zstd frame (dictionary v1) of the lgz encoding of an `.olean`
//   5 lgz module     like 1, preceded by the paths of the `.olean.server` and `.olean.private`
//                    files (each: v3+ index byte, NUL-terminated path); the lgz stream has 3 parts
//   2 hash, plain    u64: the file is the number in decimal
//   3 hash, JSON     u64: the file is `{"depHash":"<decimal>"}`
//   4/6 trace of a Lean module: [u64 depHash (4 only)], u64 hashes of `.olean`, `.ilean`, `.c`,
//                    then tagged optional hashes, terminated by 0
//
// Unpacking writes every file to a temporary file next to its destination and renames them all
// at the end.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LeanSharp.Zstd;

namespace LeanSharp.Leantar;

/// <summary>Error while unpacking an `.ltar` file (`UnpackError` in the Rust code).</summary>
public class LtarException : Exception
{
    public LtarException(string message) : base(message) { }
}

/// <summary>The archive ended in the middle of an entry: the file is corrupted (truncated).</summary>
public sealed class LtarTruncatedException : LtarException
{
    public LtarTruncatedException() : base("failed to fill whole buffer") { }
}

public static class Ltar
{
    const int CompressionLevel = 19;

    const byte CompressionZstd = 0, CompressionLgz = 1, CompressionHashPlain = 2, CompressionHashJson = 3,
        CompressionHashOutput = 4, CompressionLgzModule = 5, CompressionHash0Output = 6;

    const byte OutputHashEnd = 0, OutputHashOlean = 1, OutputHashBc = 2, OutputHashIr = 3, OutputHashIrSig = 4,
        OutputHashLtar = 5, OutputHashModuleNo = 6, OutputHashModuleYes = 7;

    static readonly string[] OleanExts = { "olean", "olean.server", "olean.private" };
    const string IleanExt = "ilean", IrSigExt = "ir.sig", IrExt = "ir", CExt = "c", BcExt = "bc", LtarExt = "ltar";
    const string TraceSchemaV3 = "2025-09-10";

    static readonly Lazy<ZstdDictionary> s_dictV1 = new(() =>
    {
        using var s = typeof(Ltar).Assembly.GetManifestResourceStream("LeanSharp.Leantar.v1.dict")
            ?? throw new InvalidOperationException("the leantar dictionary is not embedded in this assembly");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new ZstdDictionary(ms.ToArray());
    });

    /// <summary>The zstd dictionary for lgz streams (`dict/v1.dict` of leangz). Settable for hosts that load it differently.</summary>
    public static ZstdDictionary DictionaryV1
    {
        get => s_dictOverride ?? s_dictV1.Value;
        set => s_dictOverride = value;
    }
    static ZstdDictionary s_dictOverride;

    // ------------------------------------------------------------------
    // Traces

    enum Level { Trace, Info, Warning, Error }

    sealed record Descr(ulong Hash, string Ext)
    {
        public override string ToString() => Hash.ToString("x16", CultureInfo.InvariantCulture) + "." + Ext;
    }

    sealed class ModuleOutputDescrs
    {
        public bool? IsModule;
        public List<Descr> Olean = new();
        public Descr Ilean, IrSig, Ir, C, Bc, Ltar;
    }

    sealed class BuildTraceV3
    {
        public List<(string message, Level level)> Log = new();
        public ulong? DepHash;
        /// <summary>null, a `ModuleOutputDescrs`, or a `JsonElement` (any other value).</summary>
        public object Outputs;

        public bool IsSimple
        {
            get
            {
                if (Log.Count != 0) return false;
                if (Outputs == null) return true;
                if (Outputs is not ModuleOutputDescrs m) return false;
                return m.Ilean.Ext == IleanExt && m.C.Ext == CExt
                    && (m.IrSig == null || m.IrSig.Ext == IrSigExt)
                    && (m.Ir == null || m.Ir.Ext == IrExt)
                    && (m.Bc == null || m.Bc.Ext == BcExt)
                    && (m.Ltar == null || m.Ltar.Ext == LtarExt)
                    && m.Olean.Count != 0 && m.Olean.Count <= OleanExts.Length
                    && m.Olean.Select((d, i) => d.Ext == OleanExts[i]).All(b => b);
            }
        }
    }

    enum TraceKind { V1, V2, V3, Bad, Missing }

    /// <summary>Rust's `u64::from_str_radix`: an optional `+` and digits, nothing else.</summary>
    static bool TryParseU64(string s, int radix, out ulong value)
    {
        value = 0;
        int i = 0;
        if (s.Length > 0 && s[0] == '+') i = 1;
        if (i == s.Length) return false;
        for (; i < s.Length; i++)
        {
            int c = s[i], d;
            if (c >= '0' && c <= '9') d = c - '0';
            else if (c >= 'a' && c <= 'z') d = c - 'a' + 10;
            else if (c >= 'A' && c <= 'Z') d = c - 'A' + 10;
            else return false;
            if (d >= radix) return false;
            ulong next = unchecked(value * (ulong)radix + (ulong)d);
            if (value > (ulong.MaxValue - (ulong)d) / (ulong)radix) return false;
            value = next;
        }
        return true;
    }

    static bool TryParseDescr(JsonElement e, out Descr d)
    {
        d = null;
        if (e.ValueKind != JsonValueKind.String) return false;
        string s = e.GetString();
        // `s.as_bytes().get(16) == Some(&b'.')` and the first 16 bytes are the hash
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= 16 || bytes[16] != (byte)'.') return false;
        for (int i = 0; i < 16; i++) if (bytes[i] >= 0x80) return false;
        if (!TryParseU64(Encoding.ASCII.GetString(bytes, 0, 16), 16, out ulong hash)) return false;
        d = new Descr(hash, Encoding.UTF8.GetString(bytes, 17, bytes.Length - 17));
        return true;
    }

    static ModuleOutputDescrs TryParseModuleOutputs(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var m = new ModuleOutputDescrs();
        bool hasO = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject())
        {
            if (!seen.Add(p.Name)) continue; // serde_json keeps the last duplicate; duplicates do not occur in practice
            switch (p.Name)
            {
                case "m":
                    if (p.Value.ValueKind != JsonValueKind.True && p.Value.ValueKind != JsonValueKind.False) return null;
                    m.IsModule = p.Value.GetBoolean();
                    break;
                case "o":
                    if (p.Value.ValueKind != JsonValueKind.Array) return null;
                    foreach (var e in p.Value.EnumerateArray())
                    {
                        if (!TryParseDescr(e, out var d)) return null;
                        m.Olean.Add(d);
                    }
                    hasO = true;
                    break;
                case "i": if (!TryParseDescr(p.Value, out m.Ilean)) return null; break;
                case "rs": if (!TryParseDescr(p.Value, out m.IrSig)) return null; break;
                case "r": if (!TryParseDescr(p.Value, out m.Ir)) return null; break;
                case "c": if (!TryParseDescr(p.Value, out m.C)) return null; break;
                case "b": if (!TryParseDescr(p.Value, out m.Bc)) return null; break;
                case "l": if (!TryParseDescr(p.Value, out m.Ltar)) return null; break;
                default: return null;
            }
        }
        if (!hasO || m.Olean.Count == 0 || m.Olean.Count > OleanExts.Length) return null;
        if (m.Ilean == null || m.C == null) return null;
        return m;
    }

    /// <summary>Deserializes a schema-v3 trace object; null if it does not have the expected shape.</summary>
    static BuildTraceV3 TryParseTraceV3(JsonElement val)
    {
        var b = new BuildTraceV3();
        foreach (var p in val.EnumerateObject())
        {
            switch (p.Name)
            {
                case "log":
                    if (p.Value.ValueKind != JsonValueKind.Array) return null;
                    foreach (var e in p.Value.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.Object) return null;
                        if (!e.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.String) return null;
                        if (!e.TryGetProperty("level", out var lvl) || lvl.ValueKind != JsonValueKind.String) return null;
                        Level level;
                        switch (lvl.GetString())
                        {
                            case "trace": level = Level.Trace; break;
                            case "info": level = Level.Info; break;
                            case "warning": level = Level.Warning; break;
                            case "error": level = Level.Error; break;
                            default: return null;
                        }
                        b.Log.Add((msg.GetString(), level));
                    }
                    break;
                case "depHash":
                    if (p.Value.ValueKind == JsonValueKind.Null) break;
                    if (p.Value.ValueKind != JsonValueKind.String || !TryParseU64(p.Value.GetString(), 16, out ulong h)) return null;
                    b.DepHash = h;
                    break;
                case "outputs":
                    if (p.Value.ValueKind == JsonValueKind.Null) break;
                    b.Outputs = (object)TryParseModuleOutputs(p.Value) ?? p.Value.Clone();
                    break;
                case "synthetic":
                    if (p.Value.ValueKind != JsonValueKind.True && p.Value.ValueKind != JsonValueKind.False) return null;
                    break;
            }
        }
        return b;
    }

    static (TraceKind kind, ulong hash, BuildTraceV3 v3) ReadTraceFile(string tracePath)
    {
        string res;
        try { res = File.ReadAllText(tracePath, new UTF8Encoding(false, true)); }
        catch (FileNotFoundException) { return (TraceKind.Missing, 0, null); }
        catch (DirectoryNotFoundException) { return (TraceKind.Missing, 0, null); }
        if (TryParseU64(res, 10, out ulong n)) return (TraceKind.V1, n, null);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(res); }
        catch (JsonException e) { throw new IOException(e.Message); }
        using (doc)
        {
            var val = doc.RootElement;
            if (val.ValueKind == JsonValueKind.Object && val.TryGetProperty("schemaVersion", out var sv))
            {
                if (sv.ValueKind == JsonValueKind.String && sv.GetString() == TraceSchemaV3)
                {
                    var b = TryParseTraceV3(val);
                    return b != null ? (TraceKind.V3, 0UL, b) : (TraceKind.Bad, 0UL, null);
                }
                return (TraceKind.Bad, 0, null);
            }
            // schema v2: `{"depHash": "<decimal>"}`
            if (val.ValueKind == JsonValueKind.Object && val.TryGetProperty("depHash", out var dh)
                && dh.ValueKind == JsonValueKind.String && TryParseU64(dh.GetString(), 10, out ulong h2))
                return (TraceKind.V2, h2, null);
            return (TraceKind.Bad, 0, null);
        }
    }

    static ulong? TraceHash((TraceKind kind, ulong hash, BuildTraceV3 v3) t) => t.kind switch
    {
        TraceKind.V1 or TraceKind.V2 => t.hash,
        TraceKind.V3 => t.v3.DepHash,
        _ => null,
    };

    // ------------------------------------------------------------------
    // JSON output (compact, like `serde_json::to_vec`; objects of arbitrary values have sorted keys)

    static void WriteJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    static void WriteJsonValue(StringBuilder sb, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                bool first = true;
                // `serde_json::Value` keeps object keys in a sorted map; a later duplicate wins
                var props = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject()) props[p.Name] = p.Value;
                foreach (var p in props)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteJsonString(sb, p.Key);
                    sb.Append(':');
                    WriteJsonValue(sb, p.Value);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                bool f2 = true;
                foreach (var x in e.EnumerateArray())
                {
                    if (!f2) sb.Append(',');
                    f2 = false;
                    WriteJsonValue(sb, x);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String: WriteJsonString(sb, e.GetString()); break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.Null: sb.Append("null"); break;
            default: sb.Append(e.GetRawText()); break;
        }
    }

    static void WriteModuleOutputs(StringBuilder sb, ModuleOutputDescrs m)
    {
        sb.Append('{');
        bool first = true;
        void Key(string k)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(k).Append("\":");
        }
        if (m.IsModule != null) { Key("m"); sb.Append(m.IsModule.Value ? "true" : "false"); }
        Key("o");
        sb.Append('[');
        for (int i = 0; i < m.Olean.Count; i++)
        {
            if (i > 0) sb.Append(',');
            WriteJsonString(sb, m.Olean[i].ToString());
        }
        sb.Append(']');
        Key("i"); WriteJsonString(sb, m.Ilean.ToString());
        if (m.IrSig != null) { Key("rs"); WriteJsonString(sb, m.IrSig.ToString()); }
        if (m.Ir != null) { Key("r"); WriteJsonString(sb, m.Ir.ToString()); }
        Key("c"); WriteJsonString(sb, m.C.ToString());
        if (m.Bc != null) { Key("b"); WriteJsonString(sb, m.Bc.ToString()); }
        if (m.Ltar != null) { Key("l"); WriteJsonString(sb, m.Ltar.ToString()); }
        sb.Append('}');
    }

    static byte[] SerializeTraceV3(BuildTraceV3 b)
    {
        var sb = new StringBuilder();
        sb.Append("{\"schemaVersion\":");
        WriteJsonString(sb, TraceSchemaV3);
        if (b.Log.Count != 0)
        {
            sb.Append(",\"log\":[");
            for (int i = 0; i < b.Log.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"message\":");
                WriteJsonString(sb, b.Log[i].message);
                sb.Append(",\"level\":\"").Append(b.Log[i].level switch
                {
                    Level.Trace => "trace", Level.Info => "info", Level.Warning => "warning", _ => "error",
                }).Append("\"}");
            }
            sb.Append(']');
        }
        if (b.DepHash != null)
            sb.Append(",\"depHash\":\"").Append(b.DepHash.Value.ToString("x16", CultureInfo.InvariantCulture)).Append('"');
        if (b.Outputs != null)
        {
            sb.Append(",\"outputs\":");
            if (b.Outputs is ModuleOutputDescrs m) WriteModuleOutputs(sb, m);
            else WriteJsonValue(sb, (JsonElement)b.Outputs);
        }
        sb.Append('}');
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------
    // Reading the archive

    sealed class Reader
    {
        readonly byte[] m_data;
        public int Pos;
        public Reader(byte[] data) { m_data = data; }

        public bool AtEnd => Pos >= m_data.Length;

        public byte U8()
        {
            if (Pos >= m_data.Length) throw new LtarTruncatedException();
            return m_data[Pos++];
        }

        public ulong U64()
        {
            if (Pos + 8 > m_data.Length) throw new LtarTruncatedException();
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(m_data.AsSpan(Pos));
            Pos += 8;
            return v;
        }

        public ReadOnlySpan<byte> Bytes(ulong n)
        {
            if (n > (ulong)(m_data.Length - Pos)) throw new LtarTruncatedException();
            var s = m_data.AsSpan(Pos, (int)n);
            Pos += (int)n;
            return s;
        }

        public void Skip(long n)
        {
            // `seek_relative` past the end succeeds; the next read fails
            Pos = (int)Math.Min((long)Pos + n, int.MaxValue);
        }

        /// <summary>
        /// `read_until(0)` followed by `pop()`: reads up to and including the next NUL. Returns
        /// false at the end of the data; the last byte read is dropped (the NUL, or the last
        /// data byte if the terminator is missing).
        /// </summary>
        public bool CStr(out byte[] s)
        {
            s = Array.Empty<byte>();
            if (Pos >= m_data.Length) return false;
            int i = Array.IndexOf(m_data, (byte)0, Pos);
            int end = i < 0 ? m_data.Length : i + 1;
            s = m_data.AsSpan(Pos, end - Pos - 1).ToArray();
            Pos = end;
            return true;
        }
    }

    static int GetVersion(Reader r)
    {
        var magic = r.Bytes(4);
        if (magic.SequenceEqual("LTAR"u8)) return 1;
        if (magic.SequenceEqual("LTR2"u8)) return 2;
        if (magic.SequenceEqual("LTR3"u8)) return 3;
        if (magic.SequenceEqual("LTR4"u8)) return 4;
        throw new LtarException("bad .ltar file");
    }

    static string Utf8(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (ArgumentException e) { throw new LtarException("invalid utf-8 sequence: " + e.Message); }
    }

    static int s_warnedOverride;

    /// <summary>A file being written: a temporary file next to its destination, renamed by `Save`.</summary>
    sealed class TempFile
    {
        public readonly string Path, Temp;
        public TempFile(string path, ReadOnlySpan<byte> content)
        {
            Path = path;
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
            Temp = System.IO.Path.Combine(dir, ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 12));
            using var f = new FileStream(Temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            f.Write(content);
        }
        public void Save() => File.Move(Temp, Path, overwrite: true);
        public void Discard() { try { File.Delete(Temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// Unpacks an archive below the base directories and returns the trace hash. If the trace
    /// file on disk already has that hash, nothing (or, with several base directories, only
    /// what is missing) is unpacked, unless `force`.
    /// </summary>
    public static ulong Unpack(IReadOnlyList<string> basedirs, byte[] tarfile, bool force, ulong? traceOverride, bool verbose,
        TextWriter stdout, TextWriter stderr)
    {
        var r = new Reader(tarfile);
        int version = GetVersion(r);
        var uncommitted = new List<TempFile>();
        try
        {
            ulong? fileHash;
            if (version >= 4)
            {
                fileHash = r.U8() switch
                {
                    0 => null,
                    1 => r.U64(),
                    _ => throw new LtarException("bad .ltar file"),
                };
            }
            else fileHash = r.U64();
            ulong trace;
            if (traceOverride != null && fileHash != null)
            {
                // Warn at most once across the whole run, even when unpacking many files in parallel.
                if (Interlocked.Exchange(ref s_warnedOverride, 1) == 0)
                    stderr.WriteLine("warning: overriding the depHash stored in the .ltar file(s)");
                trace = traceOverride.Value;
            }
            else if (traceOverride != null) trace = traceOverride.Value;
            else if (fileHash != null) trace = fileHash.Value;
            else throw new LtarException("this .ltar was packed with -s (no depHash); an override hash must be supplied to unpack it");

            // null: end of the archive; "": a comment follows; otherwise the path
            string ReadPath(bool pathidx)
            {
                byte idx = 0;
                if (pathidx && version >= 3)
                {
                    if (r.AtEnd) return null;
                    idx = r.U8();
                }
                if (!r.CStr(out var s)) return null;
                if (s.Length == 0) return "";
                if (idx >= basedirs.Count) throw new LtarException($"not enough base paths (expected > {idx})");
                return Path.Combine(basedirs[idx], Utf8(s));
            }
            List<string> ReadExtra(byte compression)
            {
                var extra = new List<string>();
                if (compression == CompressionLgzModule)
                    for (int i = 0; i < 2; i++)
                    {
                        string p = ReadPath(true);
                        if (string.IsNullOrEmpty(p)) throw new LtarException("bad .ltar file");
                        extra.Add(p);
                    }
                return extra;
            }

            string tracePath = ReadPath(false);
            if (string.IsNullOrEmpty(tracePath)) throw new LtarException("bad .ltar file");
            bool skip = false;
            if (!force)
            {
                var b = ReadTraceFile(tracePath);
                skip = TraceHash(b) == trace;
                if (skip && basedirs.Count == 1)
                {
                    if (verbose) stdout.WriteLine($"not unpacking because the trace matches\n{tracePath}");
                    return trace;
                }
            }
            string lastDir = null;
            void CreateDirFor(string path)
            {
                // Unpacked files tend to reuse the same folders, so remember the last one.
                string dir = Path.GetDirectoryName(path);
                if (dir == null) throw new LtarException("bad .ltar file");
                if (dir != lastDir)
                {
                    if (dir.Length > 0) Directory.CreateDirectory(dir);
                    lastDir = dir;
                }
            }

            byte compression0 = version < 2 ? CompressionHashPlain : r.U8();
            var extra0 = ReadExtra(compression0);
            if (skip && File.Exists(tracePath))
                SkipOne(r, verbose, tracePath, compression0, extra0, stdout);
            else
            {
                CreateDirFor(tracePath);
                if (version < 2)
                    uncommitted.Add(new TempFile(tracePath, Encoding.ASCII.GetBytes(trace.ToString(CultureInfo.InvariantCulture))));
                else
                    UnpackOne(r, traceOverride, verbose, tracePath, compression0, extra0, uncommitted, stdout);
            }
            while (true)
            {
                string path = ReadPath(true);
                if (path == null) break;
                if (path.Length != 0)
                {
                    byte compression = r.U8();
                    var extra = ReadExtra(compression);
                    if (skip && extra.Append(path).All(p => File.Exists(p) || Directory.Exists(p)))
                        SkipOne(r, verbose, path, compression, extra, stdout);
                    else
                    {
                        CreateDirFor(path);
                        UnpackOne(r, null, verbose, path, compression, extra, uncommitted, stdout);
                    }
                }
                else if (r.CStr(out var comment))
                {
                    if (verbose) stdout.WriteLine("comment: " + Utf8(comment));
                }
                else throw new LtarException("bad .ltar file");
            }

            var committed = new List<string>();
            try
            {
                foreach (var f in uncommitted)
                {
                    f.Save();
                    committed.Add(f.Path);
                }
            }
            catch (Exception)
            {
                foreach (var p in committed) try { File.Delete(p); } catch (IOException) { }
                throw;
            }
            uncommitted.Clear();
            return trace;
        }
        finally
        {
            foreach (var f in uncommitted) f.Discard();
        }
    }

    static byte[] ZstdDecode(ReadOnlySpan<byte> frame, ZstdDictionary dict)
    {
        try { return ZstdDecoder.Decompress(frame, dict); }
        catch (ZstdException e) { throw new LtarException("zstd: " + e.Message); }
    }

    static void UnpackOne(Reader r, ulong? traceOverride, bool verbose, string path, byte compression, List<string> extra,
        List<TempFile> uncommitted, TextWriter stdout)
    {
        if (verbose)
        {
            stdout.WriteLine($"copying {path}, compression = {compression}");
            foreach (var p in extra) stdout.WriteLine($"      + {p}");
        }
        switch (compression)
        {
            case CompressionZstd:
            {
                var data = ZstdDecode(r.Bytes(r.U64()), null);
                if (traceOverride is ulong hash)
                {
                    // The depHash was stripped from this opaque trace at pack time: splice it back
                    // in without needing to understand the rest of the schema.
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(data); }
                    catch (JsonException) { throw new LtarException("bad .ltar file"); }
                    using (doc)
                    {
                        var sb = new StringBuilder();
                        var root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("schemaVersion", out var sv)
                            && sv.ValueKind == JsonValueKind.String && sv.GetString() == TraceSchemaV3)
                        {
                            var props = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                            foreach (var p in root.EnumerateObject()) props[p.Name] = p.Value;
                            sb.Append('{');
                            bool first = true, wrote = false;
                            void DepHash()
                            {
                                if (!first) sb.Append(',');
                                first = false; wrote = true;
                                sb.Append("\"depHash\":\"").Append(hash.ToString("x16", CultureInfo.InvariantCulture)).Append('"');
                            }
                            foreach (var p in props)
                            {
                                if (p.Key == "depHash") { DepHash(); continue; }
                                if (!wrote && string.CompareOrdinal(p.Key, "depHash") > 0) DepHash();
                                if (!first) sb.Append(',');
                                first = false;
                                WriteJsonString(sb, p.Key);
                                sb.Append(':');
                                WriteJsonValue(sb, p.Value);
                            }
                            if (!wrote) DepHash();
                            sb.Append('}');
                        }
                        else WriteJsonValue(sb, root);
                        data = new UTF8Encoding(false).GetBytes(sb.ToString());
                    }
                }
                uncommitted.Add(new TempFile(path, data));
                break;
            }
            case CompressionLgz:
            case CompressionLgzModule:
            {
                var lgz = ZstdDecode(r.Bytes(r.U64()), DictionaryV1);
                byte[] buf; (int start, int end)[] ranges;
                try { (buf, ranges) = Lgz.Decompress(lgz, extra.Count + 1); }
                catch (LgzException e) { throw new LtarException("lgz: " + e.Message); }
                var paths = new List<string> { path };
                paths.AddRange(extra);
                for (int i = 0; i < ranges.Length; i++)
                    uncommitted.Add(new TempFile(paths[i], buf.AsSpan(ranges[i].start, ranges[i].end - ranges[i].start)));
                break;
            }
            case CompressionHashPlain:
            {
                ulong stored = r.U64();
                ulong t = traceOverride ?? stored;
                uncommitted.Add(new TempFile(path, Encoding.ASCII.GetBytes(t.ToString(CultureInfo.InvariantCulture))));
                break;
            }
            case CompressionHashJson:
            {
                ulong stored = r.U64();
                ulong t = traceOverride ?? stored;
                uncommitted.Add(new TempFile(path, Encoding.ASCII.GetBytes("{\"depHash\":\"" + t.ToString(CultureInfo.InvariantCulture) + "\"}")));
                break;
            }
            case CompressionHashOutput:
            case CompressionHash0Output:
            {
                ulong? hash = compression == CompressionHashOutput ? r.U64() : null;
                hash = traceOverride ?? hash;
                var m = new ModuleOutputDescrs();
                m.Olean.Add(new Descr(r.U64(), OleanExts[0]));
                m.Ilean = new Descr(r.U64(), IleanExt);
                m.C = new Descr(r.U64(), CExt);
                int nextOlean = 1;
                void Once(ref Descr field, string ext)
                {
                    var d = new Descr(r.U64(), ext);
                    if (field != null) throw new LtarException("bad .ltar file");
                    field = d;
                }
                while (true)
                {
                    byte t = r.U8();
                    if (t == OutputHashEnd) break;
                    switch (t)
                    {
                        case OutputHashOlean:
                        {
                            ulong h = r.U64();
                            if (nextOlean >= OleanExts.Length) throw new LtarException("bad .ltar file");
                            m.Olean.Add(new Descr(h, OleanExts[nextOlean++]));
                            break;
                        }
                        case OutputHashIrSig: Once(ref m.IrSig, IrSigExt); break;
                        case OutputHashIr: Once(ref m.Ir, IrExt); break;
                        case OutputHashBc: Once(ref m.Bc, BcExt); break;
                        case OutputHashLtar: Once(ref m.Ltar, LtarExt); break;
                        case OutputHashModuleYes:
                            if (m.IsModule != null) throw new LtarException("bad .ltar file");
                            m.IsModule = true;
                            break;
                        case OutputHashModuleNo:
                            if (m.IsModule != null) throw new LtarException("bad .ltar file");
                            m.IsModule = false;
                            break;
                        default: throw new LtarException("bad .ltar file");
                    }
                }
                var b = new BuildTraceV3 { DepHash = hash, Outputs = m };
                uncommitted.Add(new TempFile(path, SerializeTraceV3(b)));
                break;
            }
            default:
                throw new LtarException($"unsupported compression {compression}");
        }
    }

    static void SkipOne(Reader r, bool verbose, string path, byte compression, List<string> extra, TextWriter stdout)
    {
        if (verbose && path != null)
        {
            stdout.WriteLine($"skipping {path}, compression = {compression}");
            foreach (var p in extra) stdout.WriteLine($"       + {p}");
        }
        long len;
        switch (compression)
        {
            case CompressionZstd:
            case CompressionLgz:
            case CompressionLgzModule:
                len = checked((long)r.U64());
                break;
            case CompressionHashPlain:
            case CompressionHashJson:
                len = 8;
                break;
            case CompressionHashOutput:
            case CompressionHash0Output:
                r.Skip(compression == CompressionHashOutput ? 32 : 24);
                while (true)
                {
                    byte t = r.U8();
                    if (t == OutputHashEnd) return;
                    if (t is OutputHashOlean or OutputHashIr or OutputHashIrSig or OutputHashBc or OutputHashLtar) r.U64();
                    else if (t is OutputHashModuleYes or OutputHashModuleNo) { }
                    else throw new LtarException("bad .ltar file");
                }
            default:
                throw new LtarException($"unsupported compression {compression}");
        }
        r.Skip(len);
    }

    // ------------------------------------------------------------------
    // Writing an archive

    /// <summary>
    /// Packs the trace file and the given files (paths relative to the base directories;
    /// `-i N FILE` selects base directory N, `-c COMMENT` adds a comment) into an archive.
    /// </summary>
    public static byte[] Pack(IReadOnlyList<string> basedirs, string tracePath, IReadOnlyList<string> args, bool includeHash,
        bool verbose, TextWriter stdout, TextWriter stderr)
    {
        var t = ReadTraceFile(Path.Combine(basedirs[0], tracePath));
        int version;
        BuildTraceV3 trace;
        switch (t.kind)
        {
            case TraceKind.Missing: throw new InvalidOperationException("expected .trace file");
            case TraceKind.Bad: throw new InvalidOperationException("bad .trace file");
            case TraceKind.V1: version = 1; trace = new BuildTraceV3 { DepHash = t.hash }; break;
            case TraceKind.V2: version = 2; trace = new BuildTraceV3 { DepHash = t.hash }; break;
            default:
                version = 3; trace = t.v3;
                trace.Log.RemoveAll(m => m.level < Level.Info);
                break;
        }
        if (!includeHash) trace.DepHash = null;
        if (trace.DepHash == null && version < 4) version = 4;

        var o = new MemoryStream();
        void U8(byte b) => o.WriteByte(b);
        void U64(ulong v)
        {
            Span<byte> s = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(s, v);
            o.Write(s);
        }
        void Str(string s)
        {
            o.Write(new UTF8Encoding(false).GetBytes(s));
            U8(0);
        }
        void PackZstd(ReadOnlySpan<byte> data)
        {
            U8(CompressionZstd);
            var z = ZstdEncoder.Compress(data, null, CompressionLevel);
            U64((ulong)z.Length);
            o.Write(z);
        }

        o.Write(version switch { 1 => "LTAR"u8, 2 => "LTR2"u8, 3 => "LTR3"u8, _ => "LTR4"u8 });
        if (trace.DepHash is ulong dh)
        {
            if (version >= 4) U8(1);
            U64(dh);
        }
        else U8(0);
        Str(tracePath);
        if (version >= 2)
        {
            if (trace.DepHash != null && trace.Outputs == null && trace.IsSimple)
            {
                U8(CompressionHashJson);
                U64(trace.DepHash.Value);
            }
            else if (trace.Outputs is ModuleOutputDescrs m && trace.IsSimple)
            {
                if (trace.DepHash != null)
                {
                    U8(CompressionHashOutput);
                    U64(trace.DepHash.Value);
                }
                else U8(CompressionHash0Output);
                U64(m.Olean[0].Hash);
                U64(m.Ilean.Hash);
                U64(m.C.Hash);
                for (int i = 1; i < m.Olean.Count; i++) { U8(OutputHashOlean); U64(m.Olean[i].Hash); }
                if (m.IrSig != null) { U8(OutputHashIrSig); U64(m.IrSig.Hash); }
                if (m.Ir != null) { U8(OutputHashIr); U64(m.Ir.Hash); }
                if (m.Bc != null) { U8(OutputHashBc); U64(m.Bc.Hash); }
                if (m.Ltar != null) { U8(OutputHashLtar); U64(m.Ltar.Hash); }
                if (m.IsModule == true) U8(OutputHashModuleYes);
                else if (m.IsModule == false) U8(OutputHashModuleNo);
                U8(OutputHashEnd);
            }
            else PackZstd(SerializeTraceV3(trace));
        }

        // (comment) or (file, and for a module the `.olean.server` and `.olean.private` files)
        var items = new List<(string comment, (byte idx, string file) file, (byte idx, string file)[] module)>();
        for (int i = 0; i < args.Count; i++)
        {
            string file = args[i];
            if (file == "-c")
            {
                if (++i >= args.Count) throw new InvalidOperationException("expected comment argument");
                items.Add((args[i], default, null));
                continue;
            }
            byte idx = 0;
            if (file == "-i")
            {
                if (i + 2 >= args.Count) throw new InvalidOperationException("expected index argument and file");
                if (!byte.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out idx))
                    throw new InvalidOperationException("expected number");
                file = args[i + 2];
                i += 2;
            }
            bool module = file.EndsWith(".olean.private", StringComparison.Ordinal) && items.Count >= 2
                && items[^1].comment == null && items[^1].module == null
                && items[^2].comment == null && items[^2].module == null
                && items[^2].file.file.EndsWith(".olean", StringComparison.Ordinal)
                && items[^1].file.file.EndsWith(".olean.server", StringComparison.Ordinal);
            if (module)
            {
                var server = items[^1].file;
                var olean = items[^2].file;
                items.RemoveRange(items.Count - 2, 2);
                items.Add((null, olean, new[] { server, (idx, file) }));
            }
            else items.Add((null, (idx, file), null));
        }

        foreach (var item in items)
        {
            if (item.comment != null)
            {
                if (version >= 3) U8(0);
                U8(0);
                Str(item.comment);
                continue;
            }
            string PathOf((byte idx, string file) f)
            {
                if (f.idx >= basedirs.Count) throw new InvalidOperationException("not enough parent paths");
                return Path.Combine(basedirs[f.idx], f.file);
            }
            void Entry((byte idx, string file) f)
            {
                if (version >= 3) U8(f.idx);
                else if (f.idx != 0) throw new InvalidOperationException("base directory indexes need a version 3 trace");
                Str(f.file);
            }
            Entry(item.file);
            string path = PathOf(item.file);
            if (verbose) stdout.WriteLine($"compressing {path}");
            byte[] data = File.ReadAllBytes(path);
            bool isOlean = Path.GetExtension(path) == ".olean";
            if (isOlean && data.AsSpan().StartsWith("olean"u8))
            {
                U8(item.module != null ? CompressionLgzModule : CompressionLgz);
                var parts = new List<byte[]> { data };
                if (item.module != null)
                    foreach (var f in item.module)
                    {
                        Entry(f);
                        string p = PathOf(f);
                        if (verbose) stdout.WriteLine($"          + {p}");
                        parts.Add(File.ReadAllBytes(p));
                    }
                var z = ZstdEncoder.Compress(Lgz.Compress(parts), DictionaryV1, CompressionLevel);
                U64((ulong)z.Length);
                o.Write(z);
            }
            else if (data.Length <= 20 && IsAscii(data) && TryParseU64(Encoding.ASCII.GetString(data), 10, out ulong n))
            {
                U8(CompressionHashPlain);
                U64(n);
            }
            else PackZstd(data);
        }
        return o.ToArray();
    }

    static bool IsAscii(byte[] data)
    {
        foreach (byte b in data) if (b >= 0x80) return false;
        return true;
    }

    // ------------------------------------------------------------------

    /// <summary>The comments stored in an archive (`leantar -k`).</summary>
    public static List<string> Comments(byte[] tarfile)
    {
        var r = new Reader(tarfile);
        int version = GetVersion(r);
        r.U64();
        bool ReadCStr(bool pathidx, out byte[] s)
        {
            s = Array.Empty<byte>();
            if (pathidx)
            {
                if (r.AtEnd) return false;
                r.U8();
            }
            return r.CStr(out s);
        }
        if (!ReadCStr(false, out _)) throw new LtarException("bad .ltar file");
        if (version >= 2) SkipOne(r, false, null, r.U8(), new List<string>(), TextWriter.Null);
        var comments = new List<string>();
        while (ReadCStr(version >= 3, out var buf))
        {
            if (buf.Length == 0)
            {
                if (!ReadCStr(false, out var c)) throw new LtarException("bad .ltar file");
                comments.Add(Utf8(c));
            }
            else
            {
                byte compression = r.U8();
                if (compression == CompressionLgzModule)
                    for (int i = 0; i < 2; i++) ReadCStr(version >= 3, out _);
                SkipOne(r, false, null, compression, new List<string>(), TextWriter.Null);
            }
        }
        return comments;
    }
}
