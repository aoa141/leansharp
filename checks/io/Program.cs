// Check program for the IO area. Lean-exported functions (IO.Error constructors,
// IO.FS.Stream.ofHandle, IO.eprintln, ...) are replaced by fakes.

using System.Text;
using LeanSharp.Runtime;
using static LeanSharp.Runtime.LeanRt;

namespace IOCheck;

static unsafe class Fakes
{
    // Fake IO.Error: ctor(0, [kind, fname | box 0, details], errno : UInt32)
    static Obj MkErr(string kind, Obj fname, uint errno, Obj details)
    {
        var o = lean_alloc_ctor(0, 3, 4);
        lean_ctor_set(o, 0, lean_mk_string(kind));
        lean_ctor_set(o, 1, fname ?? lean_box(0));
        lean_ctor_set(o, 2, details);
        lean_ctor_set_uint32(o, 3 * 8, errno);
        return o;
    }

    public static string Kind(Obj err) => lean_is_scalar(err) || err.m_tag != 0 ? "user" : lean_string_to_net(lean_ctor_get(err, 0));
    public static uint Errno(Obj err) => lean_ctor_get_uint32(err, 3 * 8);
    public static string FName(Obj err) { var f = lean_ctor_get(err, 1); return lean_is_scalar(f) ? null : lean_string_to_net(f); }

    static Obj UserError(Obj msg)
    {
        var o = lean_alloc_ctor(7, 1, 0);
        lean_ctor_set(o, 0, msg);
        return o;
    }

    static Obj ErrToString(Obj err)
    {
        string s;
        if (err.m_tag == 7) s = lean_string_to_net(lean_ctor_get(err, 0));
        else s = $"{Kind(err)} (error code: {Errno(err)}, {lean_string_to_net(lean_ctor_get(err, 2))})" + (FName(err) is string f ? $" file: {f}" : "");
        lean_dec(err);
        return lean_mk_string(s);
    }

    static Obj Eof(Obj unit) => UserError(lean_mk_string("EOF"));

    // Fake IO.FS.Stream: ctor(0, [handle])
    static Obj StreamOfHandle(Obj h)
    {
        var o = lean_alloc_ctor(0, 1, 0);
        lean_ctor_set(o, 0, h);
        return o;
    }

    public static readonly StringBuilder Eprinted = new();

    static Obj Eprintln(Obj s)
    {
        lock (Eprinted) Eprinted.Append(lean_string_to_net(s)).Append('\n');
        lean_dec(s);
        return lean_io_result_mk_ok(lean_box(0));
    }

    static Obj Eprint(Obj s)
    {
        lock (Eprinted) Eprinted.Append(lean_string_to_net(s));
        lean_dec(s);
        return lean_io_result_mk_ok(lean_box(0));
    }

    static byte GetProfiler(Obj opts) { lean_dec(opts); return 1; }
    static double GetProfilerThreshold(Obj opts) { lean_dec(opts); return 0.0; }

    static Obj E_already_exists(uint e, Obj d) => MkErr("already_exists", null, e, d);
    static Obj E_hardware_fault(uint e, Obj d) => MkErr("hardware_fault", null, e, d);
    static Obj E_illegal_operation(uint e, Obj d) => MkErr("illegal_operation", null, e, d);
    static Obj E_inappropriate_type(uint e, Obj d) => MkErr("inappropriate_type", null, e, d);
    static Obj E_invalid_argument(uint e, Obj d) => MkErr("invalid_argument", null, e, d);
    static Obj E_no_such_thing(uint e, Obj d) => MkErr("no_such_thing", null, e, d);
    static Obj E_other_error(uint e, Obj d) => MkErr("other_error", null, e, d);
    static Obj E_permission_denied(uint e, Obj d) => MkErr("permission_denied", null, e, d);
    static Obj E_protocol_error(uint e, Obj d) => MkErr("protocol_error", null, e, d);
    static Obj E_resource_busy(uint e, Obj d) => MkErr("resource_busy", null, e, d);
    static Obj E_resource_exhausted(uint e, Obj d) => MkErr("resource_exhausted", null, e, d);
    static Obj E_resource_vanished(uint e, Obj d) => MkErr("resource_vanished", null, e, d);
    static Obj E_time_expired(uint e, Obj d) => MkErr("time_expired", null, e, d);
    static Obj E_unsatisfied_constraints(uint e, Obj d) => MkErr("unsatisfied_constraints", null, e, d);
    static Obj E_unsupported_operation(uint e, Obj d) => MkErr("unsupported_operation", null, e, d);
    static Obj E_already_exists_file(Obj f, uint e, Obj d) => MkErr("already_exists_file", f, e, d);
    static Obj E_inappropriate_type_file(Obj f, uint e, Obj d) => MkErr("inappropriate_type_file", f, e, d);
    static Obj E_interrupted(Obj f, uint e, Obj d) => MkErr("interrupted", f, e, d);
    static Obj E_invalid_argument_file(Obj f, uint e, Obj d) => MkErr("invalid_argument_file", f, e, d);
    static Obj E_no_file_or_directory(Obj f, uint e, Obj d) => MkErr("no_file_or_directory", f, e, d);
    static Obj E_no_such_thing_file(Obj f, uint e, Obj d) => MkErr("no_such_thing_file", f, e, d);
    static Obj E_permission_denied_file(Obj f, uint e, Obj d) => MkErr("permission_denied_file", f, e, d);
    static Obj E_resource_exhausted_file(Obj f, uint e, Obj d) => MkErr("resource_exhausted_file", f, e, d);
    public static void RegisterErrors()
    {
        LeanExports.Register("lean_mk_io_error_already_exists", (nint)(delegate*<uint, Obj, Obj>)&E_already_exists);
        LeanExports.Register("lean_mk_io_error_hardware_fault", (nint)(delegate*<uint, Obj, Obj>)&E_hardware_fault);
        LeanExports.Register("lean_mk_io_error_illegal_operation", (nint)(delegate*<uint, Obj, Obj>)&E_illegal_operation);
        LeanExports.Register("lean_mk_io_error_inappropriate_type", (nint)(delegate*<uint, Obj, Obj>)&E_inappropriate_type);
        LeanExports.Register("lean_mk_io_error_invalid_argument", (nint)(delegate*<uint, Obj, Obj>)&E_invalid_argument);
        LeanExports.Register("lean_mk_io_error_no_such_thing", (nint)(delegate*<uint, Obj, Obj>)&E_no_such_thing);
        LeanExports.Register("lean_mk_io_error_other_error", (nint)(delegate*<uint, Obj, Obj>)&E_other_error);
        LeanExports.Register("lean_mk_io_error_permission_denied", (nint)(delegate*<uint, Obj, Obj>)&E_permission_denied);
        LeanExports.Register("lean_mk_io_error_protocol_error", (nint)(delegate*<uint, Obj, Obj>)&E_protocol_error);
        LeanExports.Register("lean_mk_io_error_resource_busy", (nint)(delegate*<uint, Obj, Obj>)&E_resource_busy);
        LeanExports.Register("lean_mk_io_error_resource_exhausted", (nint)(delegate*<uint, Obj, Obj>)&E_resource_exhausted);
        LeanExports.Register("lean_mk_io_error_resource_vanished", (nint)(delegate*<uint, Obj, Obj>)&E_resource_vanished);
        LeanExports.Register("lean_mk_io_error_time_expired", (nint)(delegate*<uint, Obj, Obj>)&E_time_expired);
        LeanExports.Register("lean_mk_io_error_unsatisfied_constraints", (nint)(delegate*<uint, Obj, Obj>)&E_unsatisfied_constraints);
        LeanExports.Register("lean_mk_io_error_unsupported_operation", (nint)(delegate*<uint, Obj, Obj>)&E_unsupported_operation);
        LeanExports.Register("lean_mk_io_error_already_exists_file", (nint)(delegate*<Obj, uint, Obj, Obj>)&E_already_exists_file);
        LeanExports.Register("lean_mk_io_error_inappropriate_type_file", (nint)(delegate*<Obj, uint, Obj, Obj>)&E_inappropriate_type_file);
        LeanExports.Register("lean_mk_io_error_interrupted", (nint)(delegate*<Obj, uint, Obj, Obj>)&E_interrupted);
        LeanExports.Register("lean_mk_io_error_invalid_argument_file", (nint)(delegate*<Obj, uint, Obj, Obj>)&E_invalid_argument_file);
        LeanExports.Register("lean_mk_io_error_no_file_or_directory", (nint)(delegate*<Obj, uint, Obj, Obj>)&E_no_file_or_directory);
        LeanExports.Register("lean_mk_io_error_no_such_thing_file", (nint)(delegate*<Obj, uint, Obj, Obj>)&E_no_such_thing_file);
        LeanExports.Register("lean_mk_io_error_permission_denied_file", (nint)(delegate*<Obj, uint, Obj, Obj>)&E_permission_denied_file);
        LeanExports.Register("lean_mk_io_error_resource_exhausted_file", (nint)(delegate*<Obj, uint, Obj, Obj>)&E_resource_exhausted_file);
    }

    public static void Register()
    {
        RegisterErrors();
        LeanExports.Register("lean_mk_io_user_error", (nint)(delegate*<Obj, Obj>)&UserError);
        LeanExports.Register("lean_mk_io_error_eof", (nint)(delegate*<Obj, Obj>)&Eof);
        LeanExports.Register("lean_io_error_to_string", (nint)(delegate*<Obj, Obj>)&ErrToString);
        LeanExports.Register("lean_stream_of_handle", (nint)(delegate*<Obj, Obj>)&StreamOfHandle);
        LeanExports.Register("lean_io_eprintln", (nint)(delegate*<Obj, Obj>)&Eprintln);
        LeanExports.Register("lean_io_eprint", (nint)(delegate*<Obj, Obj>)&Eprint);
        LeanExports.Register("lean_get_profiler", (nint)(delegate*<Obj, byte>)&GetProfiler);
        LeanExports.Register("lean_get_profiler_threshold", (nint)(delegate*<Obj, double>)&GetProfilerThreshold);
    }
}

static unsafe class Program
{
    static int s_failures, s_checks;

    static void Check(bool cond, string what)
    {
        s_checks++;
        if (!cond) { s_failures++; Console.WriteLine("FAIL: " + what); }
    }

    static void Eq<T>(T actual, T expected, string what)
    {
        s_checks++;
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            s_failures++;
            Console.WriteLine($"FAIL: {what}: expected <{expected}>, got <{actual}>");
        }
    }

    static Obj S(string s) => lean_mk_string(s);
    static string N(Obj s) => lean_string_to_net(s);

    static Obj Ok(Obj r, string what)
    {
        if (!lean_io_result_is_ok(r))
        {
            var e = lean_io_result_get_error(r);
            lean_inc(e);
            s_failures++; s_checks++;
            Console.WriteLine($"FAIL: {what}: IO error: {N(((delegate*<Obj, Obj>)LeanExports.Get("lean_io_error_to_string"))(e))}");
            return null;
        }
        s_checks++;
        return lean_io_result_take_value(r);
    }

    static Obj Err(Obj r, string what)
    {
        if (lean_io_result_is_ok(r)) { s_failures++; s_checks++; Console.WriteLine($"FAIL: {what}: expected an error"); return null; }
        s_checks++;
        var e = lean_io_result_get_error(r);
        lean_inc(e); lean_dec(r);
        return e;
    }

    static bool Mac => OperatingSystem.IsMacOS();

    static string ReadAll(Obj h)
    {
        var ms = new MemoryStream();
        while (true)
        {
            var b = Ok(lean_io_prim_handle_read(h, 4096), "read pipe");
            if (b == null) break;
            var sp = ByteArraySpan(b);
            if (sp.Length == 0) break;
            ms.Write(sp);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static Obj MkSpawnArgs(string cmd, string[] args, byte sin, byte sout, byte serr, string cwd = null,
                           (string, string)[] env = null, bool inheritEnv = true)
    {
        var cfg = lean_alloc_ctor(0, 0, 3);
        lean_ctor_set_uint8(cfg, 0, sin); lean_ctor_set_uint8(cfg, 1, sout); lean_ctor_set_uint8(cfg, 2, serr);
        var a = lean_alloc_ctor(0, 5, 2);
        lean_ctor_set(a, 0, cfg);
        lean_ctor_set(a, 1, S(cmd));
        lean_ctor_set(a, 2, MkArray(args.Select(S).ToList()));
        lean_ctor_set(a, 3, cwd == null ? lean_box(0) : lean_mk_option_some(S(cwd)));
        var envObjs = (env ?? Array.Empty<(string, string)>()).Select(kv => lean_mk_pair(S(kv.Item1), kv.Item2 == null ? lean_box(0) : lean_mk_option_some(S(kv.Item2)))).ToList();
        lean_ctor_set(a, 4, MkArray(envObjs));
        lean_ctor_set_uint8(a, 5 * 8, inheritEnv ? (byte)1 : (byte)0);
        lean_ctor_set_uint8(a, 5 * 8 + 1, 0);
        return a;
    }

    static void Main()
    {
        Fakes.Register();
        var cfgDummy = lean_box(0);
        string tmp = Path.Combine(Path.GetTempPath(), "leansharp-io-check-" + Environment.ProcessId);
        Directory.CreateDirectory(tmp);
        try
        {
            TestFiles(tmp);
            TestErrors(tmp);
            TestDirsAndMetadata(tmp);
            TestLocks(tmp);
            TestMisc();
            TestStdStreams();
            TestProcesses(tmp, cfgDummy);
            TestHook(cfgDummy);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception) { }
        }
        Console.WriteLine($"{s_checks - s_failures}/{s_checks} checks passed");
        Environment.Exit(s_failures == 0 ? 0 : 1);
    }

    static void TestFiles(string tmp)
    {
        var path = S(Path.Combine(tmp, "a.txt"));
        var h = Ok(lean_io_prim_handle_mk(path, 1), "open write");
        Ok(lean_io_prim_handle_put_str(h, S("hello\nwörld\n")), "putStr");
        Ok(lean_io_prim_handle_write(h, MkByteArray(new byte[] { (byte)'x', (byte)'y' })), "write");
        Ok(lean_io_prim_handle_flush(h), "flush");
        Eq(lean_io_prim_handle_is_tty(h), (byte)0, "file isTty");
        lean_dec(h); // finalizer closes the file
        Eq(File.ReadAllText(N(path)), "hello\nwörld\nxy", "file contents");

        h = Ok(lean_io_prim_handle_mk(path, 0), "open read");
        Eq(N(Ok(lean_io_prim_handle_get_line(h), "getLine 1")), "hello\n", "getLine 1");
        Eq(N(Ok(lean_io_prim_handle_get_line(h), "getLine 2")), "wörld\n", "getLine 2");
        Eq(N(Ok(lean_io_prim_handle_get_line(h), "getLine 3")), "xy", "getLine 3 (no newline)");
        Eq(N(Ok(lean_io_prim_handle_get_line(h), "getLine eof")), "", "getLine at EOF");
        Ok(lean_io_prim_handle_rewind(h), "rewind");
        var bytes = Ok(lean_io_prim_handle_read(h, 3), "read 3");
        Eq(Encoding.UTF8.GetString(ByteArraySpan(bytes)), "hel", "read 3");
        bytes = Ok(lean_io_prim_handle_read(h, 1000), "read rest");
        Eq(ByteArraySpan(bytes).Length, 12, "read rest length");
        bytes = Ok(lean_io_prim_handle_read(h, 10), "read eof");
        Eq(ByteArraySpan(bytes).Length, 0, "read at EOF");
        var e = Err(lean_io_prim_handle_put_str(h, S("x")), "write to read-only handle");
        if (e != null) Eq(Fakes.Kind(e), "invalid_argument", "write to read handle: EBADF kind");
        lean_dec(h);

        // append
        h = Ok(lean_io_prim_handle_mk(path, 4), "open append");
        Ok(lean_io_prim_handle_put_str(h, S("\nmore")), "append");
        lean_dec(h);
        Eq(File.ReadAllText(N(path)), "hello\nwörld\nxy\nmore", "append contents");

        // readWrite + truncate
        h = Ok(lean_io_prim_handle_mk(path, 3), "open readWrite");
        Eq(N(Ok(lean_io_prim_handle_get_line(h), "rw getLine")), "hello\n", "rw getLine");
        Ok(lean_io_prim_handle_truncate(h), "truncate");
        lean_dec(h);
        Eq(File.ReadAllText(N(path)), "hello\n", "truncate contents");

        // writeNew on existing file
        e = Err(lean_io_prim_handle_mk(path, 2), "writeNew existing");
        if (e != null) { Eq(Fakes.Kind(e), "already_exists_file", "writeNew kind"); Eq(Fakes.Errno(e), 17u, "EEXIST"); }

        // temp file / dir
        var tf = Ok(lean_io_create_tempfile(), "createTempFile");
        if (tf != null)
        {
            var th = lean_ctor_get(tf, 0); var tp = N(lean_ctor_get(tf, 1));
            Check(File.Exists(tp) && Path.GetFileName(tp).StartsWith("tmp."), "temp file exists: " + tp);
            Ok(lean_io_prim_handle_put_str(th, S("abc")), "temp put");
            Ok(lean_io_prim_handle_rewind(th), "temp rewind");
            Eq(N(Ok(lean_io_prim_handle_get_line(th), "temp getLine")), "abc", "temp readback");
            lean_dec(tf);
            File.Delete(tp);
        }
        var td = Ok(lean_io_create_tempdir(), "createTempDir");
        if (td != null) { Check(Directory.Exists(N(td)), "temp dir exists"); Directory.Delete(N(td)); }

        // rename / remove / hard link
        var p2 = S(Path.Combine(tmp, "b.txt"));
        Ok(lean_io_rename(path, p2), "rename");
        Check(!File.Exists(N(path)) && File.Exists(N(p2)), "rename moved file");
        var p3 = S(Path.Combine(tmp, "c.txt"));
        Ok(lean_io_hard_link(p2, p3), "hardLink");
        Eq(File.Exists(N(p3)) ? File.ReadAllText(N(p3)) : null, "hello\n", "hard link contents");
        e = Err(lean_io_hard_link(p2, p3), "hardLink existing");
        if (e != null) Eq(Fakes.Kind(e), "already_exists_file", "hardLink existing kind");
        Ok(lean_io_remove_file(p3), "removeFile");
        Check(!File.Exists(N(p3)), "file removed");
        e = Err(lean_io_remove_file(p3), "removeFile missing");
        if (e != null) { Eq(Fakes.Kind(e), "no_file_or_directory", "removeFile missing kind"); Eq(Fakes.FName(e), N(p3), "removeFile missing fname"); }
        // chmod
        if (!OperatingSystem.IsWindows())
        {
            Ok(lean_chmod(p2, 0x124 /* 0444 */), "chmod");
            Eq((int)File.GetUnixFileMode(N(p2)) & 0x1FF, 0x124, "chmod mode");
            Ok(lean_chmod(p2, 0x1A4 /* 0644 */), "chmod back");
        }
    }

    static void TestErrors(string tmp)
    {
        var missing = S(Path.Combine(tmp, "nope", "x.txt"));
        var e = Err(lean_io_prim_handle_mk(missing, 0), "open missing");
        if (e != null)
        {
            Eq(Fakes.Kind(e), "no_file_or_directory", "open missing kind");
            Eq(Fakes.Errno(e), 2u, "ENOENT");
            Eq(Fakes.FName(e), N(missing), "open missing fname");
            Eq(N(lean_ctor_get(e, 2)), "no such file or directory", "details");
        }
        e = Err(lean_io_prim_handle_mk(S(tmp), 0), "open dir");
        if (e != null) Eq(Fakes.Kind(e), "inappropriate_type_file", "open dir kind");
        e = Err(lean_io_prim_handle_mk(S("a\0b"), 0), "embedded NUL");
        if (e != null) Eq(N(lean_ctor_get(e, 2)), "string contains NUL bytes", "embedded NUL details");
        // show_error
        var r = lean_io_result_mk_error(LeanIOErrors.UserError("boom"));
        var saved = LeanIO.Stderr; var ms = new MemoryStream(); LeanIO.Stderr = ms;
        lean_io_result_show_error(r);
        LeanIO.Stderr = saved;
        Eq(Encoding.UTF8.GetString(ms.ToArray()), "uncaught exception: boom\n", "show_error");
    }

    static void TestDirsAndMetadata(string tmp)
    {
        var d = S(Path.Combine(tmp, "dir"));
        Ok(lean_io_create_dir(d), "createDir");
        var e = Err(lean_io_create_dir(d), "createDir existing");
        if (e != null) { Eq(Fakes.Kind(e), "already_exists_file", "createDir existing kind"); Eq(Fakes.Errno(e), 17u, "createDir EEXIST"); }
        e = Err(lean_io_create_dir(S(Path.Combine(tmp, "x", "y"))), "createDir no parent");
        if (e != null) Eq(Fakes.Kind(e), "no_file_or_directory", "createDir no parent kind");
        File.WriteAllText(Path.Combine(N(d), "f1"), "12345");
        Directory.CreateDirectory(Path.Combine(N(d), "sub"));
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(Path.Combine(N(d), "link"), "f1");

        var arr = Ok(lean_io_read_dir(d), "readDir");
        var names = new List<string>();
        for (ulong i = 0; i < lean_array_size(arr); i++)
        {
            var ent = lean_array_get_core(arr, i);
            Eq(N(lean_ctor_get(ent, 0)), N(d), "DirEntry.root");
            names.Add(N(lean_ctor_get(ent, 1)));
        }
        names.Sort(StringComparer.Ordinal);
        Eq(string.Join(",", names), OperatingSystem.IsWindows() ? "f1,sub" : "f1,link,sub", "readDir names");
        e = Err(lean_io_read_dir(S(Path.Combine(tmp, "nope"))), "readDir missing");
        if (e != null) Eq(Fakes.Kind(e), "no_file_or_directory", "readDir missing kind");

        var md = Ok(lean_io_metadata(S(Path.Combine(N(d), "f1"))), "metadata");
        Eq(lean_ctor_get_uint64(md, 16), 5UL, "metadata byteSize");
        Eq(lean_ctor_get_uint8(md, 32), (byte)1, "metadata type = file");
        var mod = lean_ctor_get(md, 1);
        long sec = (int)(uint)lean_unbox(lean_ctor_get(mod, 0));
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Check(Math.Abs(now - sec) < 100, $"metadata mtime {sec} ~ {now}");
        Check(lean_ctor_get_uint32(mod, 8) < 1_000_000_000u, "nsec range");
        md = Ok(lean_io_metadata(S(Path.Combine(N(d), "sub"))), "metadata dir");
        Eq(lean_ctor_get_uint8(md, 32), (byte)0, "metadata type = dir");
        if (!OperatingSystem.IsWindows())
        {
            md = Ok(lean_io_symlink_metadata(S(Path.Combine(N(d), "link"))), "symlinkMetadata");
            Eq(lean_ctor_get_uint8(md, 32), (byte)2, "symlinkMetadata type = symlink");
            md = Ok(lean_io_metadata(S(Path.Combine(N(d), "link"))), "metadata via link");
            Eq(lean_ctor_get_uint8(md, 32), (byte)1, "metadata via link type = file");
            Eq(lean_ctor_get_uint64(md, 16), 5UL, "metadata via link size");
            var rp = Ok(lean_io_realpath(S(Path.Combine(N(d), "sub", "..", "link"))), "realPath");
            string expected = Path.Combine(tmp, "dir", "f1");
            if (Mac && expected.StartsWith("/var/")) expected = "/private" + expected;
            Eq(N(rp), expected, "realPath");
        }
        e = Err(lean_io_realpath(S(Path.Combine(tmp, "nope"))), "realPath missing");
        if (e != null) Eq(Fakes.Kind(e), "no_file_or_directory", "realPath missing kind");

        e = Err(lean_io_remove_dir(d), "removeDir nonempty");
        if (e != null) { Eq(Fakes.Kind(e), "unsatisfied_constraints", "removeDir nonempty kind"); Eq(Fakes.Errno(e), Mac ? 66u : 39u, "ENOTEMPTY"); }
        Ok(lean_io_remove_dir(S(Path.Combine(N(d), "sub"))), "removeDir");
        Check(!Directory.Exists(Path.Combine(N(d), "sub")), "dir removed");
        e = Err(lean_io_remove_dir(S(Path.Combine(N(d), "f1"))), "removeDir file");
        if (e != null) Eq(Fakes.Kind(e), "inappropriate_type_file", "removeDir file kind (ENOTDIR)");

        var cwd = Ok(lean_io_current_dir(), "currentDir");
        Eq(N(cwd), Directory.GetCurrentDirectory(), "currentDir");
        Ok(lean_io_process_set_current_dir(d), "setCurrentDir");
        Eq(N(Ok(lean_io_process_get_current_dir(), "getCurrentDir")), new DirectoryInfo(N(d)).FullName.Replace("/var/", Mac ? "/private/var/" : "/var/"), "getCurrentDir after set");
        Ok(lean_io_process_set_current_dir(cwd), "restore cwd");
    }

    static void TestLocks(string tmp)
    {
        var p = S(Path.Combine(tmp, "lock"));
        var h1 = Ok(lean_io_prim_handle_mk(p, 1), "lock h1");
        var h2 = Ok(lean_io_prim_handle_mk(p, 0), "lock h2");
        Eq(lean_unbox(Ok(lean_io_prim_handle_try_lock(h1, 1), "tryLock h1 ex")), 1UL, "tryLock h1 exclusive");
        Eq(lean_unbox(Ok(lean_io_prim_handle_try_lock(h2, 0), "tryLock h2 sh")), 0UL, "tryLock h2 shared blocked");
        Ok(lean_io_prim_handle_unlock(h1), "unlock h1");
        Eq(lean_unbox(Ok(lean_io_prim_handle_try_lock(h2, 0), "tryLock h2 sh 2")), 1UL, "tryLock h2 shared");
        Eq(lean_unbox(Ok(lean_io_prim_handle_try_lock(h1, 0), "tryLock h1 sh")), 1UL, "tryLock h1 shared too");
        Eq(lean_unbox(Ok(lean_io_prim_handle_try_lock(h1, 1), "tryLock h1 ex 2")), 0UL, "exclusive blocked by shared");
        var t = Task.Run(() => { Ok(lean_io_prim_handle_lock(h1, 1), "blocking lock"); });
        Thread.Sleep(100);
        Check(!t.IsCompleted, "lock blocks while shared lock held");
        lean_dec(h2); // closing releases its lock
        Check(t.Wait(5000), "lock acquired after other handle closed");
        lean_dec(h1);
    }

    static void TestMisc()
    {
        var a = lean_unbox(lean_io_mono_ms_now());
        Thread.Sleep(20);
        var b = lean_unbox(lean_io_mono_ms_now());
        Check(b >= a + 15 && b < a + 5000, $"monoMsNow {a} -> {b}");
        var n1 = lean_nat_to_big(lean_io_mono_nanos_now());
        var n2 = lean_nat_to_big(lean_io_mono_nanos_now());
        Check(n2 >= n1, "monoNanosNow monotonic");
        var rb = Ok(lean_io_get_random_bytes(32), "getRandomBytes");
        Eq(ByteArraySpan(rb).Length, 32, "random bytes length");
        Check(ByteArraySpan(rb).ToArray().Any(x => x != 0), "random bytes nonzero");
        Eq(ByteArraySpan(Ok(lean_io_get_random_bytes(0), "random 0")).Length, 0, "random 0");

        Environment.SetEnvironmentVariable("LEANSHARP_IO_CHECK", "v a l");
        var ev = lean_io_getenv(S("LEANSHARP_IO_CHECK"));
        Check(!lean_is_scalar(ev) && N(lean_ctor_get(ev, 0)) == "v a l", "getEnv some");
        Check(lean_is_scalar(lean_io_getenv(S("LEANSHARP_IO_CHECK_UNSET"))), "getEnv none");

        lean_io_set_heartbeats(lean_box(41));
        LeanHeartbeats.Increment();
        Eq(lean_unbox(lean_io_get_num_heartbeats()), 42UL, "heartbeats");

        Eq(LeanProfiling.FormatG(12.345, 3), "12.3", "%.3g 12.345");
        Eq(LeanProfiling.FormatG(0.0001234, 3), "0.000123", "%.3g small");
        Eq(LeanProfiling.FormatG(1234.5, 3), "1.23e+03", "%.3g large");
        Eq(LeanProfiling.FormatG(1, 3), "1", "%.3g 1");
        Eq(LeanProfiling.FormatG(100, 3), "100", "%.3g 100");
        Eq(LeanProfiling.FormatG(0.5, 3), "0.5", "%.3g 0.5");

        Eq(lean_unbox(lean_closure_max_args(lean_box(0))), 16UL, "closureMaxArgs");
        Eq(lean_unbox(lean_get_usize_size(lean_box(0))), 8UL, "usize size");
        Eq(lean_unbox(lean_get_max_ctor_tag(lean_box(0))), 243UL, "max ctor tag");
        Eq(N(lean_get_githash(lean_box(0))), "67a8629274847c29155324086f6c0f49ba20ec8d", "githash");
        Eq(lean_unbox(lean_version_get_minor(lean_box(0))), 36UL, "version minor");
        Eq(lean_system_platform_osx(lean_box(0)), Mac ? (byte)1 : (byte)0, "platform osx");
        Console.WriteLine("platform target: " + N(lean_system_platform_target(lean_box(0))));
        Eq(exp2(10), 1024.0, "exp2");
        Eq(log2f(8f), 3f, "log2f");
        Eq(lean_io_initializing(), (byte)1, "initializing");
        lean_io_mark_end_initialization();
        Eq(lean_io_initializing(), (byte)0, "initialized");

        // timeit / dbg_trace / profileit through the fake eprintln
        Fakes.Eprinted.Clear();
        var fn = lean_alloc_closure((delegate*<Obj, Obj>)&ReturnOk42, 1, 0);
        var r = lean_io_timeit(S("took"), fn);
        Eq(lean_unbox(lean_io_result_get_value(r)), 42UL, "timeit result");
        Check(Fakes.Eprinted.ToString().StartsWith("took ") && Fakes.Eprinted.ToString().EndsWith("ms\n"), "timeit output: " + Fakes.Eprinted);
        Fakes.Eprinted.Clear();
        var v = lean_dbg_trace(S("trace msg"), lean_alloc_closure((delegate*<Obj, Obj>)&Return7, 1, 0));
        Eq(lean_unbox(v), 7UL, "dbgTrace result");
        Eq(Fakes.Eprinted.ToString(), "trace msg\n", "dbgTrace output");
        Fakes.Eprinted.Clear();
        var name = lean_alloc_ctor(1, 2, 8);
        lean_ctor_set(name, 0, lean_box(0)); lean_ctor_set(name, 1, S("Foo"));
        var name2 = lean_alloc_ctor(1, 2, 8);
        lean_ctor_set(name2, 0, name); lean_ctor_set(name2, 1, S("bar"));
        v = lean_profileit(S("elaboration"), lean_box(0), lean_alloc_closure((delegate*<Obj, Obj>)&Return7, 1, 0), name2);
        Eq(lean_unbox(v), 7UL, "profileit result");
        Check(Fakes.Eprinted.ToString().StartsWith("elaboration of Foo.bar took "), "profileit output: " + Fakes.Eprinted);
        var saved = LeanIO.Stderr; var ms = new MemoryStream(); LeanIO.Stderr = ms;
        lean_display_cumulative_profiling_times();
        LeanIO.Stderr = saved;
        Check(Encoding.UTF8.GetString(ms.ToArray()).StartsWith("cumulative profiling times:\n\telaboration "), "cumulative times");

        try { lean_io_exit(3); Check(false, "exit should throw"); }
        catch (LeanExitException ex) { Eq(ex.ExitCode, 3, "exit code"); }
        try { lean_sorry(0); Check(false, "sorry should throw"); }
        catch (LeanPanicException) { Check(true, "sorry panics"); }

        var tsr = Ok(lean_get_current_time(), "current time");
        long secs = (int)(uint)lean_unbox(lean_ctor_get(tsr, 0));
        Check(Math.Abs(secs - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) < 5, "current time secs");
        var ap = Ok(lean_io_app_path(), "appPath");
        Check(ap != null && File.Exists(N(ap)), "appPath exists: " + (ap == null ? "" : N(ap)));
        LeanPaths.AppPath = "/opt/lean/bin/lean";
        Eq(N(Ok(lean_io_app_path(), "appPath override")), "/opt/lean/bin/lean", "appPath override");
        LeanPaths.AppPath = null;
        var le = Err(lean_emit_llvm(lean_box(0), lean_box(0), S("x")), "emitLLVM");
        if (le != null) Eq(N(lean_ctor_get(le, 0)), "LLVM backend is not supported by LeanSharp", "emitLLVM error");
        var dl = Err(lean_dynlib_load(S("/x.so")), "dynlib load");
    }

    static Obj ReturnOk42(Obj w) => lean_io_result_mk_ok(lean_box(42));
    static Obj Return7(Obj u) => lean_box(7);

    static void TestStdStreams()
    {
        var savedOut = LeanIO.Stdout;
        var ms = new MemoryStream();
        LeanIO.Stdout = ms;
        try
        {
            var st = lean_get_stdout();
            var h = lean_ctor_get(st, 0);
            Ok(lean_io_prim_handle_put_str(h, S("to stdout\n")), "stdout putStr");
            Eq(Encoding.UTF8.GetString(ms.ToArray()), "to stdout\n", "stdout redirected via LeanIO.Stdout");
            // per-context redirection
            var ms2 = new MemoryStream();
            using (LeanStdStreams.Redirect(null, ms2, null))
                Ok(lean_io_prim_handle_put_str(h, S("ctx")), "stdout putStr ctx");
            Eq(Encoding.UTF8.GetString(ms2.ToArray()), "ctx", "stdout redirected via LeanStdStreams");
            // thread-local setStdout
            var fake = lean_alloc_ctor(0, 1, 0); lean_ctor_set(fake, 0, lean_box(123));
            var old = lean_get_set_stdout(fake);
            Check(ReferenceEquals(old, st), "setStdout returns previous stream");
            Check(ReferenceEquals(lean_get_stdout(), fake), "getStdout after setStdout");
            var other = Task.Factory.StartNew(() => lean_get_stdout(), TaskCreationOptions.LongRunning).Result;
            Check(ReferenceEquals(other, st), "other threads keep the global stdout");
            lean_get_set_stdout(old);
        }
        finally { LeanIO.Stdout = savedOut; }
        var sin = lean_get_stdin();
        Check(sin != null && !lean_is_scalar(sin), "getStdin");
    }

    static void TestProcesses(string tmp, Obj cfg)
    {
        if (OperatingSystem.IsWindows()) return;
        // echo hello, stdout piped
        var child = Ok(lean_io_process_spawn(MkSpawnArgs("echo", new[] { "hello" }, 1, 0, 1)), "spawn echo");
        if (child == null) return;
        Check(lean_is_scalar(lean_ctor_get(child, 0)) && lean_is_scalar(lean_ctor_get(child, 2)), "inherit/unused handles are ()");
        Eq(ReadAll(lean_ctor_get(child, 1)), "hello\n", "echo output");
        Eq(lean_unbox(Ok(lean_io_process_child_wait(cfg, child), "wait")), 0UL, "echo exit code");
        var tw = Ok(lean_io_process_child_try_wait(cfg, child), "tryWait");
        Check(!lean_is_scalar(tw) && lean_unbox(lean_ctor_get(tw, 0)) == 0, "tryWait after exit");
        lean_inc(child);
        Check(lean_io_process_child_pid(cfg, child) > 0, "child pid");
        lean_dec(child);

        // env, cwd, exit code, stderr
        var args = MkSpawnArgs("sh", new[] { "-c", "echo $FOO; pwd; echo err >&2; exit 3" }, 2, 0, 0, cwd: tmp,
            env: new[] { ("FOO", "bar"), ("HOME", null) });
        child = Ok(lean_io_process_spawn(args), "spawn sh");
        var outp = ReadAll(lean_ctor_get(child, 1));
        var real = LeanRt.IoRealPath(tmp);
        Eq(outp, "bar\n" + real + "\n", "sh output (env + cwd)");
        Eq(ReadAll(lean_ctor_get(child, 2)), "err\n", "sh stderr");
        Eq(lean_unbox(Ok(lean_io_process_child_wait(cfg, child), "wait sh")), 3UL, "sh exit code");
        lean_dec(child);

        // stdin piped: cat, takeStdin, close
        child = Ok(lean_io_process_spawn(MkSpawnArgs("cat", Array.Empty<string>(), 0, 0, 1)), "spawn cat");
        var stdinH = lean_ctor_get(child, 0);
        Ok(lean_io_prim_handle_put_str(stdinH, S("line1\nline2\n")), "write to cat");
        Ok(lean_io_prim_handle_flush(stdinH), "flush cat stdin");
        var pr = Ok(lean_io_process_child_take_stdin(cfg, child), "takeStdin");
        var child2 = lean_ctor_get(pr, 1); lean_inc(child2);
        var sh = lean_ctor_get(pr, 0); lean_inc(sh);
        lean_dec(pr);
        Check(lean_is_scalar(lean_ctor_get(child2, 0)), "takeStdin clears stdin");
        lean_dec(sh); // closes the pipe => EOF for cat
        var stdoutH = lean_ctor_get(child2, 1);
        Eq(N(Ok(lean_io_prim_handle_get_line(stdoutH), "cat getLine")), "line1\n", "cat line 1");
        Eq(ReadAll(stdoutH), "line2\n", "cat rest");
        Eq(lean_unbox(Ok(lean_io_process_child_wait(cfg, child2), "wait cat")), 0UL, "cat exit");

        // kill
        child = Ok(lean_io_process_spawn(MkSpawnArgs("sleep", new[] { "30" }, 2, 2, 2)), "spawn sleep");
        Check(lean_is_scalar(lean_io_result_take_value(lean_io_process_child_try_wait(cfg, child))), "sleep running");
        Ok(lean_io_process_child_kill(cfg, child), "kill");
        Eq(lean_unbox(Ok(lean_io_process_child_wait(cfg, child), "wait killed")), 137UL, "killed exit code");

        // missing command
        var e = Err(lean_io_process_spawn(MkSpawnArgs("leansharp-no-such-command", Array.Empty<string>(), 2, 2, 2)), "spawn missing");
        if (e != null) Eq(Fakes.Kind(e), "no_file_or_directory", "spawn missing kind");

        // inherit with redirected LeanIO.Stdout: output is pumped into it
        var savedOut = LeanIO.Stdout; var ms = new MemoryStream(); LeanIO.Stdout = ms;
        try
        {
            child = Ok(lean_io_process_spawn(MkSpawnArgs("echo", new[] { "pumped" }, 1, 1, 1)), "spawn echo inherit");
            Ok(lean_io_process_child_wait(cfg, child), "wait inherit");
        }
        finally { LeanIO.Stdout = savedOut; }
        Eq(Encoding.UTF8.GetString(ms.ToArray()), "pumped\n", "inherited stdout pumped to LeanIO.Stdout");
    }

    static void TestHook(Obj cfg)
    {
        LeanProcess.ProcessSpawnHook = req =>
        {
            if (req.CommandName != "fake-lean") return null;
            return new InProcessChild(req, ctx =>
            {
                // Lean-level stdout of the child resolves to the child's pipe.
                var st = lean_get_stdout();
                var h = lean_ctor_get(st, 0);
                lean_io_prim_handle_put_str(h, S("in-process: " + string.Join(" ", ctx.Request.Args) + "\n"));
                var line = lean_io_prim_handle_get_line(lean_ctor_get(lean_get_stdin(), 0));
                lean_io_prim_handle_put_str(h, S("got " + N(lean_io_result_get_value(line))));
                lean_io_exit(5);
                return 0;
            });
        };
        try
        {
            var child = Ok(lean_io_process_spawn(MkSpawnArgs("/some/dir/fake-lean", new[] { "--run", "x.lean" }, 0, 0, 1)), "spawn hooked");
            Check(lean_io_process_child_pid(cfg, Inc(child)) >= 0x40000000u, "fake pid");
            Ok(lean_io_prim_handle_put_str(lean_ctor_get(child, 0), S("input line\n")), "write to in-process child");
            Eq(ReadAll(lean_ctor_get(child, 1)), "in-process: --run x.lean\ngot input line\n", "in-process child output");
            Eq(lean_unbox(Ok(lean_io_process_child_wait(cfg, child), "wait hooked")), 5UL, "in-process exit code");
            // other commands fall through
            child = Ok(lean_io_process_spawn(MkSpawnArgs("true", Array.Empty<string>(), 2, 2, 2)), "spawn true");
            Eq(lean_unbox(Ok(lean_io_process_child_wait(cfg, child), "wait true")), 0UL, "true exit code");
        }
        finally { LeanProcess.ProcessSpawnHook = null; }
    }

    static Obj Inc(Obj o) { lean_inc(o); return o; }
}
