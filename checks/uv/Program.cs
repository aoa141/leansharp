// Check program for the uv area (timers, TCP, UDP, DNS, signals, system info, pton/ntop).
// The Lean-exported IO.Error constructors are replaced by fakes that produce a string
// "<kind> errno=<n>: <details>".

using System.Diagnostics;
using System.Text;
using LeanSharp.Runtime;
using static LeanSharp.Runtime.LeanRt;

namespace UvCheck;

static unsafe class Fakes
{
    static Obj E(string kind, uint errno, Obj msg, Obj fname = null)
    {
        string f = fname != null ? $" file={lean_string_to_net(fname)}" : "";
        var r = lean_mk_string($"{kind} errno={errno}: {lean_string_to_net(msg)}{f}");
        lean_dec(msg);
        if (fname != null) lean_dec(fname);
        return r;
    }

    static Obj AlreadyExists(uint e, Obj m) => E("alreadyExists", e, m);
    static Obj AlreadyExistsFile(Obj f, uint e, Obj m) => E("alreadyExists", e, m, f);
    static Obj HardwareFault(uint e, Obj m) => E("hardwareFault", e, m);
    static Obj IllegalOperation(uint e, Obj m) => E("illegalOperation", e, m);
    static Obj InappropriateType(uint e, Obj m) => E("inappropriateType", e, m);
    static Obj InappropriateTypeFile(Obj f, uint e, Obj m) => E("inappropriateType", e, m, f);
    static Obj Interrupted(Obj f, uint e, Obj m) => E("interrupted", e, m, f);
    static Obj InvalidArgument(uint e, Obj m) => E("invalidArgument", e, m);
    static Obj InvalidArgumentFile(Obj f, uint e, Obj m) => E("invalidArgument", e, m, f);
    static Obj NoFileOrDirectory(Obj f, uint e, Obj m) => E("noFileOrDirectory", e, m, f);
    static Obj NoSuchThing(uint e, Obj m) => E("noSuchThing", e, m);
    static Obj NoSuchThingFile(Obj f, uint e, Obj m) => E("noSuchThing", e, m, f);
    static Obj OtherError(uint e, Obj m) => E("otherError", e, m);
    static Obj PermissionDenied(uint e, Obj m) => E("permissionDenied", e, m);
    static Obj PermissionDeniedFile(Obj f, uint e, Obj m) => E("permissionDenied", e, m, f);
    static Obj ProtocolError(uint e, Obj m) => E("protocolError", e, m);
    static Obj ResourceBusy(uint e, Obj m) => E("resourceBusy", e, m);
    static Obj ResourceExhausted(uint e, Obj m) => E("resourceExhausted", e, m);
    static Obj ResourceExhaustedFile(Obj f, uint e, Obj m) => E("resourceExhausted", e, m, f);
    static Obj ResourceVanished(uint e, Obj m) => E("resourceVanished", e, m);
    static Obj TimeExpired(uint e, Obj m) => E("timeExpired", e, m);
    static Obj UnsatisfiedConstraints(uint e, Obj m) => E("unsatisfiedConstraints", e, m);
    static Obj UnsupportedOperation(uint e, Obj m) => E("unsupportedOperation", e, m);

    public static void Register()
    {
        void R1(string n, delegate*<uint, Obj, Obj> f) => LeanExports.Register(n, (nint)f);
        void R2(string n, delegate*<Obj, uint, Obj, Obj> f) => LeanExports.Register(n, (nint)f);
        R1("lean_mk_io_error_already_exists", &AlreadyExists);
        R2("lean_mk_io_error_already_exists_file", &AlreadyExistsFile);
        R1("lean_mk_io_error_hardware_fault", &HardwareFault);
        R1("lean_mk_io_error_illegal_operation", &IllegalOperation);
        R1("lean_mk_io_error_inappropriate_type", &InappropriateType);
        R2("lean_mk_io_error_inappropriate_type_file", &InappropriateTypeFile);
        R2("lean_mk_io_error_interrupted", &Interrupted);
        R1("lean_mk_io_error_invalid_argument", &InvalidArgument);
        R2("lean_mk_io_error_invalid_argument_file", &InvalidArgumentFile);
        R2("lean_mk_io_error_no_file_or_directory", &NoFileOrDirectory);
        R1("lean_mk_io_error_no_such_thing", &NoSuchThing);
        R2("lean_mk_io_error_no_such_thing_file", &NoSuchThingFile);
        R1("lean_mk_io_error_other_error", &OtherError);
        R1("lean_mk_io_error_permission_denied", &PermissionDenied);
        R2("lean_mk_io_error_permission_denied_file", &PermissionDeniedFile);
        R1("lean_mk_io_error_protocol_error", &ProtocolError);
        R1("lean_mk_io_error_resource_busy", &ResourceBusy);
        R1("lean_mk_io_error_resource_exhausted", &ResourceExhausted);
        R2("lean_mk_io_error_resource_exhausted_file", &ResourceExhaustedFile);
        R1("lean_mk_io_error_resource_vanished", &ResourceVanished);
        R1("lean_mk_io_error_time_expired", &TimeExpired);
        R1("lean_mk_io_error_unsatisfied_constraints", &UnsatisfiedConstraints);
        R1("lean_mk_io_error_unsupported_operation", &UnsupportedOperation);
    }
}

static class Program
{
    static int s_fail, s_ok;

    static void Check(bool cond, string what)
    {
        if (cond) { s_ok++; Console.WriteLine("ok   " + what); }
        else { s_fail++; Console.WriteLine("FAIL " + what); }
    }

    static Obj Ok(Obj ioRes, string what)
    {
        if (!lean_io_result_is_ok(ioRes))
        {
            Check(false, $"{what}: IO error {Str(lean_io_result_get_error(ioRes))}");
            throw new Exception("aborting: " + what);
        }
        return lean_io_result_take_value(ioRes);
    }

    static string Str(Obj o) => lean_is_string(o) ? lean_string_to_net(o) : $"<obj tag {o.m_tag}>";

    /// <summary>Wait until a promise is resolved; returns the value (or null on timeout / dropped).</summary>
    static Obj Wait(Obj promise, int ms = 5000)
    {
        var t = ((PromiseObj)promise).m_result;
        var sw = Stopwatch.StartNew();
        lock (t)
        {
            while (t.m_value == null && sw.ElapsedMilliseconds < ms)
                Monitor.Wait(t, 2);
        }
        var v = t.m_value;
        if (v == null || lean_is_scalar(v)) return null;
        return lean_ctor_get(v, 0);
    }

    static bool IsResolved(Obj promise) => ((PromiseObj)promise).m_result.m_value != null;

    /// <summary>`Except IO.Error α` → value, or null (and prints the error).</summary>
    static Obj ExceptOk(Obj e, string what)
    {
        if (e == null) { Check(false, what + ": promise not resolved"); return null; }
        if (e.m_tag == 1) return lean_ctor_get(e, 0);
        Check(false, $"{what}: error {Str(lean_ctor_get(e, 0))}");
        return null;
    }

    static string ExceptErr(Obj e) => e != null && e.m_tag == 0 ? Str(lean_ctor_get(e, 0)) : null;

    static Obj MkIPv4(byte a, byte b, byte c, byte d) =>
        MkArray(new[] { lean_box(a), lean_box(b), lean_box(c), lean_box(d) });

    static Obj MkSockAddrV4(byte a, byte b, byte c, byte d, ushort port)
    {
        var inner = lean_alloc_ctor(0, 1, 2);
        lean_ctor_set(inner, 0, MkIPv4(a, b, c, d));
        lean_ctor_set_uint16_s(inner, 0, port);
        var r = lean_alloc_ctor(0, 1, 0);
        lean_ctor_set(r, 0, inner);
        return r;
    }

    static ushort PortOf(Obj sa) => lean_ctor_get_uint16_s(lean_ctor_get(sa, 0), 0);

    static string AddrStr(Obj sa)
    {
        var inner = lean_ctor_get(sa, 0);
        var ip = lean_ctor_get(inner, 0);
        string a = sa.m_tag == 0 ? Str(lean_uv_ntop_v4(ip)) : "[" + Str(lean_uv_ntop_v6(ip)) + "]";
        return $"{a}:{PortOf(sa)}";
    }

    static string IPAddrStr(Obj ip) => ip.m_tag == 0 ? Str(lean_uv_ntop_v4(lean_ctor_get(ip, 0))) : Str(lean_uv_ntop_v6(lean_ctor_get(ip, 0)));

    static Obj Bytes(string s) => MkByteArray(Encoding.UTF8.GetBytes(s));
    static string BytesStr(Obj ba) => Encoding.UTF8.GetString(ByteArraySpan(ba));

    static int Main()
    {
        Fakes.Register();
        Console.WriteLine($"promise implementation: {(typeof(LeanRt).GetMethod("lean_io_promise_new") != null ? "task manager" : "uv fallback")}");
        Run("pton/ntop", TestAddr);
        Run("timers", TestTimers);
        Run("tcp", TestTcp);
        Run("tcp errors", TestTcpErrors);
        Run("udp", TestUdp);
        Run("dns", TestDns);
        Run("system", TestSystem);
        Run("signals", TestSignals);
        Console.WriteLine($"\n{s_ok} ok, {s_fail} failed");
        return s_fail == 0 ? 0 : 1;
    }

    static void Run(string name, Action f)
    {
        Console.WriteLine($"--- {name}");
        try { f(); }
        catch (Exception ex) { Check(false, $"{name}: exception {ex}"); }
    }

    // ------------------------------------------------------------------

    static void TestAddr()
    {
        string P4(string s)
        {
            var r = lean_uv_pton_v4(lean_mk_string(s));
            return lean_is_scalar(r) ? "none" : Str(lean_uv_ntop_v4(lean_ctor_get(r, 0)));
        }
        string P6(string s)
        {
            var r = lean_uv_pton_v6(lean_mk_string(s));
            return lean_is_scalar(r) ? "none" : Str(lean_uv_ntop_v6(lean_ctor_get(r, 0)));
        }
        Check(P4("127.0.0.1") == "127.0.0.1", "pton4 127.0.0.1");
        Check(P4("255.255.255.255") == "255.255.255.255", "pton4 255.255.255.255");
        Check(P4("01.2.3.4") == "none", "pton4 rejects leading zero");
        Check(P4("1.2.3") == "none", "pton4 rejects 3 octets");
        Check(P4("1.2.3.256") == "none", "pton4 rejects 256");
        Check(P4("1.2.3.4\0") == "none", "pton4 rejects NUL");
        var v = lean_uv_pton_v4(lean_mk_string("10.20.30.40"));
        var arr = lean_ctor_get(v, 0);
        Check(lean_unbox(lean_array_get_core(arr, 0)) == 10 && lean_unbox(lean_array_get_core(arr, 3)) == 40, "pton4 octet order");
        Check(P6("::1") == "::1", "pton6 ::1");
        Check(P6("::") == "::", "pton6 ::");
        Check(P6("2001:DB8::1") == "2001:db8::1", "pton6 2001:db8::1");
        Check(P6("1:2:3:4:5:6:7:8") == "1:2:3:4:5:6:7:8", "pton6 full");
        Check(P6("1:0:0:4:0:0:0:8") == "1:0:0:4::8", "ntop6 longest zero run");
        Check(P6("::ffff:1.2.3.4") == "::ffff:1.2.3.4", "pton6 v4-mapped");
        Check(P6("fe80::1%en0") == "fe80::1", "pton6 zone id ignored");
        Check(P6("1::2::3") == "none", "pton6 rejects double ::");
        Check(P6("12345::") == "none", "pton6 rejects 5 hex digits");
        var v6 = lean_uv_pton_v6(lean_mk_string("2001:db8::ff"));
        var a6 = lean_ctor_get(v6, 0);
        Check(lean_unbox(lean_array_get_core(a6, 0)) == 0x2001 && lean_unbox(lean_array_get_core(a6, 7)) == 0xff, "pton6 segment order");
    }

    static void TestTimers()
    {
        var timer = Ok(lean_uv_timer_mk(100, 0), "timer mk");
        var sw = Stopwatch.StartNew();
        var p = Ok(lean_uv_timer_next(timer), "timer next");
        Check(!IsResolved(p), "one-shot timer not resolved immediately");
        var p2 = Ok(lean_uv_timer_next(timer), "timer next again");
        Check(ReferenceEquals(p, p2), "one-shot timer next returns same promise");
        var v = Wait(p, 3000);
        long el = sw.ElapsedMilliseconds;
        Check(v != null && lean_is_scalar(v), $"one-shot timer fired after {el} ms");
        Check(el >= 90, "one-shot timer waited at least ~100ms");

        // reset delays the timer
        var t2 = Ok(lean_uv_timer_mk(150, 0), "timer2 mk");
        sw.Restart();
        var q = Ok(lean_uv_timer_next(t2), "timer2 next");
        Thread.Sleep(100);
        Ok(lean_uv_timer_reset(t2), "timer2 reset");
        Wait(q, 3000);
        el = sw.ElapsedMilliseconds;
        Check(IsResolved(q) && el >= 230, $"reset timer fired after {el} ms (>= 250 expected)");

        // repeating
        var rt = Ok(lean_uv_timer_mk(50, 1), "repeating mk");
        sw.Restart();
        var r0 = Ok(lean_uv_timer_next(rt), "repeating next 0");
        Wait(r0, 1000);
        Check(IsResolved(r0) && sw.ElapsedMilliseconds < 40, $"repeating timer 0th tick immediate ({sw.ElapsedMilliseconds} ms)");
        int ticks = 0;
        for (int i = 0; i < 3; i++)
        {
            var ri = Ok(lean_uv_timer_next(rt), "repeating next");
            if (Wait(ri, 1000) != null) ticks++;
        }
        el = sw.ElapsedMilliseconds;
        Check(ticks == 3 && el >= 120, $"repeating timer ticked 3 times in {el} ms");
        Ok(lean_uv_timer_stop(rt), "repeating stop");
        var rs = Ok(lean_uv_timer_next(rt), "next after stop");
        Thread.Sleep(120);
        Check(!IsResolved(rs), "stopped timer does not tick");

        // a running timer only referenced by the loop survives a GC
        var gp = UnreferencedTimerPromise();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(Wait(gp, 3000) != null, "timer unreachable from managed code still fires after GC");

        // cancel
        var ct = Ok(lean_uv_timer_mk(50, 0), "cancel mk");
        var cp = Ok(lean_uv_timer_next(ct), "cancel next");
        Ok(lean_uv_timer_cancel(ct), "cancel");
        Thread.Sleep(120);
        Check(!IsResolved(cp), "cancelled timer does not resolve");
        var cp2 = Ok(lean_uv_timer_next(ct), "next after cancel");
        Check(!ReferenceEquals(cp, cp2), "cancel returns timer to initial state");
        Check(Wait(cp2, 2000) != null, "restarted timer fires");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static Obj UnreferencedTimerPromise()
    {
        var t = Ok(lean_uv_timer_mk(200, 0), "gc timer mk");
        var p = Ok(lean_uv_timer_next(t), "gc timer next");
        lean_dec(t);
        return p;
    }

    static void TestTcp()
    {
        var server = Ok(lean_uv_tcp_new(), "tcp new server");
        Ok(lean_uv_tcp_bind(server, MkSockAddrV4(127, 0, 0, 1, 0)), "tcp bind");
        Ok(lean_uv_tcp_listen(server, 16), "tcp listen");
        var saddr = Ok(lean_uv_tcp_getsockname(server), "getsockname");
        ushort port = PortOf(saddr);
        Check(port != 0 && AddrStr(saddr).StartsWith("127.0.0.1:"), $"server bound at {AddrStr(saddr)}");

        var ta = Ok(lean_uv_tcp_try_accept(server), "tryAccept");
        Check(ta.m_tag == 1 && lean_is_scalar(lean_ctor_get(ta, 0)), "tryAccept without connection is none");

        var acceptP = Ok(lean_uv_tcp_accept(server), "accept");
        var client = Ok(lean_uv_tcp_new(), "tcp new client");
        lean_uv_tcp_nodelay(client);
        var connP = Ok(lean_uv_tcp_connect(client, MkSockAddrV4(127, 0, 0, 1, port)), "connect");
        Check(ExceptOk(Wait(connP), "connect result") != null, "connect succeeded");
        var conn = ExceptOk(Wait(acceptP), "accept result");
        Check(conn != null && lean_is_external(conn), "accepted a connection");
        lean_inc(conn);

        var peer = Ok(lean_uv_tcp_getpeername(client), "getpeername");
        Check(PortOf(peer) == port, $"client peer is {AddrStr(peer)}");
        var local = Ok(lean_uv_tcp_getsockname(client), "client getsockname");
        var connPeer = Ok(lean_uv_tcp_getpeername(conn), "conn getpeername");
        Check(PortOf(connPeer) == PortOf(local), "server side sees client port");
        Ok(lean_uv_tcp_keepalive(client, 1, 60), "keepalive");
        var kaErr = lean_uv_tcp_keepalive(client, 1, 0);
        Check(lean_io_result_is_error(kaErr) && Str(lean_io_result_get_error(kaErr)).StartsWith("invalidArgument"), "keepalive delay 0 rejected");

        // client -> server
        var sendP = Ok(lean_uv_tcp_send(client, MkArray(new[] { Bytes("hel"), Bytes("lo") })), "send");
        Check(ExceptOk(Wait(sendP), "send result") != null, "send completed");
        var wr = Ok(lean_uv_tcp_wait_readable(conn), "waitReadable");
        var wv = ExceptOk(Wait(wr), "waitReadable result");
        Check(wv != null && lean_unbox(wv) == 1, "waitReadable true");
        var got = new StringBuilder();
        while (got.Length < 5)
        {
            var rp = Ok(lean_uv_tcp_recv(conn, 1024), "recv");
            var rv = ExceptOk(Wait(rp), "recv result");
            if (rv == null || lean_is_scalar(rv)) break;
            got.Append(BytesStr(lean_ctor_get(rv, 0)));
        }
        Check(got.ToString() == "hello", $"server received '{got}'");

        // echo back
        var echoP = Ok(lean_uv_tcp_send(conn, MkArray(new[] { Bytes(got.ToString()) })), "echo send");
        ExceptOk(Wait(echoP), "echo send result");
        var rp2 = Ok(lean_uv_tcp_recv(client, 1024), "client recv");
        var rv2 = ExceptOk(Wait(rp2), "client recv result");
        Check(rv2 != null && !lean_is_scalar(rv2) && BytesStr(lean_ctor_get(rv2, 0)) == "hello", "client received echo");

        // parallel recv is rejected
        var pr1 = Ok(lean_uv_tcp_recv(client, 16), "pending recv");
        var pr2 = lean_uv_tcp_recv(client, 16);
        Check(lean_io_result_is_error(pr2), "second parallel recv rejected: " + (lean_io_result_is_error(pr2) ? Str(lean_io_result_get_error(pr2)) : ""));
        Ok(lean_uv_tcp_cancel_recv(client), "cancel recv");
        Check(!IsResolved(pr1), "cancelled recv is not resolved (fallback promise: no deactivation)");
        // data sent after the cancel is received by the next recv
        var s3 = Ok(lean_uv_tcp_send(conn, MkArray(new[] { Bytes("again") })), "send after cancel");
        ExceptOk(Wait(s3), "send after cancel result");
        var rp3 = Ok(lean_uv_tcp_recv(client, 1024), "recv after cancel");
        var rv3 = ExceptOk(Wait(rp3), "recv after cancel result");
        Check(rv3 != null && !lean_is_scalar(rv3) && BytesStr(lean_ctor_get(rv3, 0)) == "again", "recv after cancel gets data");

        // zero size recv
        var z = lean_uv_tcp_recv(client, 0);
        Check(lean_io_result_is_error(z) && Str(lean_io_result_get_error(z)).Contains("must be positive"), "recv size 0 rejected");

        // shutdown -> EOF on the other side
        var shP = Ok(lean_uv_tcp_shutdown(client), "shutdown");
        Check(ExceptOk(Wait(shP), "shutdown result") != null, "shutdown completed");
        var sh2 = lean_uv_tcp_shutdown(client);
        Check(lean_io_result_is_error(sh2) && Str(lean_io_result_get_error(sh2)).Contains("already requested"), "second shutdown rejected");
        var eofP = Ok(lean_uv_tcp_recv(conn, 1024), "recv eof");
        var eofV = ExceptOk(Wait(eofP), "recv eof result");
        Check(eofV != null && lean_is_scalar(eofV), "server sees EOF (none)");
        var sendAfter = lean_uv_tcp_send(client, MkArray(new[] { Bytes("x") }));
        Check(lean_io_result_is_error(sendAfter), "send after shutdown fails: " + (lean_io_result_is_error(sendAfter) ? Str(lean_io_result_get_error(sendAfter)) : ""));

        // waitAcceptable + tryAccept
        var wa = Ok(lean_uv_tcp_wait_acceptable(server), "waitAcceptable");
        var client2 = Ok(lean_uv_tcp_new(), "client2");
        var c2 = Ok(lean_uv_tcp_connect(client2, MkSockAddrV4(127, 0, 0, 1, port)), "connect 2");
        ExceptOk(Wait(c2), "connect 2 result");
        Check(ExceptOk(Wait(wa), "waitAcceptable result") != null, "waitAcceptable resolved");
        var ta2 = Ok(lean_uv_tcp_try_accept(server), "tryAccept 2");
        Check(ta2.m_tag == 1 && !lean_is_scalar(lean_ctor_get(ta2, 0)), "tryAccept returns the connection");

        // accept on a non-listening socket
        var nl = lean_uv_tcp_accept(client2);
        Check(lean_io_result_is_error(nl) && Str(lean_io_result_get_error(nl)).Contains("not listening"), "accept on non-listening socket rejected");

        // cancel accept
        var ap = Ok(lean_uv_tcp_accept(server), "accept to cancel");
        Ok(lean_uv_tcp_cancel_accept(server), "cancel accept");
        Check(!IsResolved(ap), "cancelled accept not resolved");

        // releasing the sockets finalizes them
        lean_dec(conn);
        lean_dec(client);
        lean_dec(client2);
        lean_dec(server);
    }

    static void TestTcpErrors()
    {
        // find a free port, then close it
        var s = Ok(lean_uv_tcp_new(), "tmp");
        Ok(lean_uv_tcp_bind(s, MkSockAddrV4(127, 0, 0, 1, 0)), "tmp bind");
        Ok(lean_uv_tcp_listen(s, 1), "tmp listen");
        ushort port = PortOf(Ok(lean_uv_tcp_getsockname(s), "tmp name"));
        lean_dec(s);
        Thread.Sleep(50);
        var c = Ok(lean_uv_tcp_new(), "err client");
        var cp = Ok(lean_uv_tcp_connect(c, MkSockAddrV4(127, 0, 0, 1, port)), "connect refused");
        string err = ExceptErr(Wait(cp));
        Check(err != null && err.StartsWith("noSuchThing") && err.Contains("connection refused"), $"connection refused error: {err}");

        var u = Ok(lean_uv_tcp_new(), "unconnected");
        var r = lean_uv_tcp_recv(u, 10);
        Check(lean_io_result_is_error(r) && Str(lean_io_result_get_error(r)).StartsWith("invalidArgument"), "recv on unconnected socket: " + Str(lean_io_result_get_error(r)));
        var gp = lean_uv_tcp_getpeername(u);
        Check(lean_io_result_is_error(gp), "getpeername on fresh socket fails: " + Str(lean_io_result_get_error(gp)));

        // EADDRINUSE is reported by listen
        var a = Ok(lean_uv_tcp_new(), "a");
        Ok(lean_uv_tcp_bind(a, MkSockAddrV4(127, 0, 0, 1, 0)), "a bind");
        Ok(lean_uv_tcp_listen(a, 1), "a listen");
        ushort ap = PortOf(Ok(lean_uv_tcp_getsockname(a), "a name"));
        var b = Ok(lean_uv_tcp_new(), "b");
        var bb = lean_uv_tcp_bind(b, MkSockAddrV4(127, 0, 0, 1, ap));
        var bl = lean_uv_tcp_listen(b, 1);
        Check(lean_io_result_is_ok(bb) && lean_io_result_is_error(bl) && Str(lean_io_result_get_error(bl)).StartsWith("resourceBusy"),
            "address in use reported by listen: " + (lean_io_result_is_error(bl) ? Str(lean_io_result_get_error(bl)) : "ok"));
        lean_dec(a); lean_dec(b); lean_dec(c); lean_dec(u);
    }

    static void TestUdp()
    {
        var a = Ok(lean_uv_udp_new(), "udp a");
        var b = Ok(lean_uv_udp_new(), "udp b");
        Ok(lean_uv_udp_bind(a, MkSockAddrV4(127, 0, 0, 1, 0)), "udp bind a");
        Ok(lean_uv_udp_bind(b, MkSockAddrV4(127, 0, 0, 1, 0)), "udp bind b");
        var aAddr = Ok(lean_uv_udp_getsockname(a), "a name");
        var bAddr = Ok(lean_uv_udp_getsockname(b), "b name");
        Check(PortOf(aAddr) != 0 && PortOf(bAddr) != 0, $"udp bound at {AddrStr(aAddr)} and {AddrStr(bAddr)}");
        Ok(lean_uv_udp_set_broadcast(a, 1), "set broadcast");
        Ok(lean_uv_udp_set_ttl(a, 64), "set ttl");
        Ok(lean_uv_udp_set_multicast_ttl(a, 2), "set multicast ttl");
        Ok(lean_uv_udp_set_multicast_loop(a, 1), "set multicast loop");

        var recvP = Ok(lean_uv_udp_recv(b, 1024), "udp recv");
        var some = lean_mk_option_some(MkSockAddrV4(127, 0, 0, 1, PortOf(bAddr)));
        var sendP = Ok(lean_uv_udp_send(a, MkArray(new[] { Bytes("da"), Bytes("tagram") }), some), "udp send");
        Check(ExceptOk(Wait(sendP), "udp send result") != null, "udp send completed");
        var rv = ExceptOk(Wait(recvP), "udp recv result");
        if (rv != null)
        {
            var data = lean_ctor_get(rv, 0);
            var from = lean_ctor_get(rv, 1);
            Check(BytesStr(data) == "datagram", $"udp received '{BytesStr(data)}'");
            Check(!lean_is_scalar(from) && PortOf(lean_ctor_get(from, 0)) == PortOf(aAddr), "udp sender address " + (lean_is_scalar(from) ? "none" : AddrStr(lean_ctor_get(from, 0))));
        }

        // truncation → resourceExhausted
        var small = Ok(lean_uv_udp_recv(b, 3), "udp small recv");
        ExceptOk(Wait(Ok(lean_uv_udp_send(a, MkArray(new[] { Bytes("toolong") }), some), "udp send 2")), "udp send 2 result");
        string err = ExceptErr(Wait(small));
        Check(err != null && err.StartsWith("resourceExhausted"), $"oversized datagram rejected: {err}");

        // connected UDP + waitReadable
        Ok(lean_uv_udp_connect(a, MkSockAddrV4(127, 0, 0, 1, PortOf(bAddr))), "udp connect");
        var peer = Ok(lean_uv_udp_getpeername(a), "udp getpeername");
        Check(PortOf(peer) == PortOf(bAddr), "udp peer " + AddrStr(peer));
        var sendNoAddr = lean_uv_udp_send(b, MkArray(new[] { Bytes("x") }), lean_box(0));
        Check(lean_io_result_is_error(sendNoAddr), "send without address on unconnected socket fails: " + Str(lean_io_result_get_error(sendNoAddr)));
        var wr = Ok(lean_uv_udp_wait_readable(b), "udp waitReadable");
        Thread.Sleep(50);
        Check(!IsResolved(wr), "udp waitReadable pending");
        ExceptOk(Wait(Ok(lean_uv_udp_send(a, MkArray(new[] { Bytes("ping") }), lean_box(0)), "udp connected send")), "udp connected send result");
        Check(ExceptOk(Wait(wr), "udp waitReadable result") != null, "udp waitReadable resolved");
        var r2 = ExceptOk(Wait(Ok(lean_uv_udp_recv(b, 100), "udp recv 2")), "udp recv 2 result");
        Check(r2 != null && BytesStr(lean_ctor_get(r2, 0)) == "ping", "datagram still available after waitReadable");

        // cancel
        var cp = Ok(lean_uv_udp_recv(b, 100), "udp recv to cancel");
        Ok(lean_uv_udp_cancel_recv(b), "udp cancel recv");
        Check(!IsResolved(cp), "cancelled udp recv not resolved");

        // multicast membership (may be unsupported in sandboxes)
        var grp = lean_alloc_ctor(0, 1, 0);
        lean_ctor_set(grp, 0, MkIPv4(239, 1, 2, 3));
        var mem = lean_uv_udp_set_membership(b, grp, lean_box(0), 1);
        Console.WriteLine("     (join multicast group: " + (lean_io_result_is_ok(mem) ? "ok" : Str(lean_io_result_get_error(mem))) + ")");
        lean_dec(a); lean_dec(b);
    }

    static void TestDns()
    {
        var p = Ok(lean_uv_dns_get_info(lean_mk_string("localhost"), lean_mk_string(""), 0), "getAddrInfo localhost");
        var arr = ExceptOk(Wait(p, 10000), "getAddrInfo result");
        if (arr != null)
        {
            var addrs = new List<string>();
            for (ulong i = 0; i < lean_array_size(arr); i++) addrs.Add(IPAddrStr(lean_array_get_core(arr, i)));
            Check(addrs.Contains("127.0.0.1") || addrs.Contains("::1"), "localhost resolves to " + string.Join(", ", addrs));
        }
        var p4 = Ok(lean_uv_dns_get_info(lean_mk_string("localhost"), lean_mk_string("80"), 1), "getAddrInfo v4");
        var arr4 = ExceptOk(Wait(p4, 10000), "getAddrInfo v4 result");
        Check(arr4 != null && lean_array_size(arr4) > 0 && lean_array_get_core(arr4, 0).m_tag == 0, "family filter ipv4");
        var bad = lean_uv_dns_get_info(lean_mk_string("héllo"), lean_mk_string(""), 0);
        Check(lean_io_result_is_error(bad) && Str(lean_io_result_get_error(bad)).Contains("not ASCII"), "non-ASCII name rejected");
        var nx = Ok(lean_uv_dns_get_info(lean_mk_string("does-not-exist.invalid"), lean_mk_string(""), 0), "getAddrInfo invalid");
        string e = ExceptErr(Wait(nx, 15000));
        Check(e != null, "unknown host fails: " + e);
        var ni = Ok(lean_uv_dns_get_name(MkSockAddrV4(127, 0, 0, 1, 80)), "getNameInfo");
        var pair = ExceptOk(Wait(ni, 10000), "getNameInfo result");
        if (pair != null)
            Check(Str(lean_ctor_get(pair, 1)) == "http", $"getNameInfo 127.0.0.1:80 = ({Str(lean_ctor_get(pair, 0))}, {Str(lean_ctor_get(pair, 1))})");
    }

    static string U64(Obj o) => lean_ctor_get_uint64_s(o, 0).ToString();

    static void TestSystem()
    {
        var host = Str(Ok(lean_uv_os_gethostname(), "hostname"));
        Check(host.Length > 0, "hostname = " + host);
        var un = Ok(lean_uv_os_uname(), "uname");
        Check(Str(lean_ctor_get(un, 0)).Length > 0, $"uname = {Str(lean_ctor_get(un, 0))} | {Str(lean_ctor_get(un, 1))} | {Str(lean_ctor_get(un, 2))} | {Str(lean_ctor_get(un, 3))}");
        var pid = lean_ctor_get_uint64_s(Ok(lean_uv_os_getpid(), "pid"), 0);
        Check(pid == (ulong)Environment.ProcessId, "pid = " + pid);
        var ppid = lean_uv_os_getppid();
        Check(lean_io_result_is_ok(ppid) && lean_ctor_get_uint64_s(lean_io_result_get_value(ppid), 0) > 0, "ppid = " + (lean_io_result_is_ok(ppid) ? U64(lean_io_result_get_value(ppid)) : Str(lean_io_result_get_error(ppid))));
        Check(Str(Ok(lean_uv_cwd(), "cwd")) == Directory.GetCurrentDirectory(), "cwd");
        var cwd0 = Directory.GetCurrentDirectory();
        var tmp = Str(Ok(lean_uv_os_tmpdir(), "tmpdir"));
        Check(!tmp.EndsWith("/") && Directory.Exists(tmp), "tmpdir = " + tmp);
        Ok(lean_uv_chdir(lean_mk_string(tmp)), "chdir tmp");
        Check(Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd('/') == Path.GetFullPath(tmp).TrimEnd('/') || Directory.GetCurrentDirectory().EndsWith(Path.GetFileName(tmp)), "chdir works");
        Directory.SetCurrentDirectory(cwd0);
        var cd = lean_uv_chdir(lean_mk_string("/does/not/exist"));
        Check(lean_io_result_is_error(cd) && Str(lean_io_result_get_error(cd)).StartsWith("noFileOrDirectory") && Str(lean_io_result_get_error(cd)).Contains("file=/does/not/exist"), "chdir error: " + Str(lean_io_result_get_error(cd)));
        Check(Str(Ok(lean_uv_os_homedir(), "homedir")).Length > 0, "homedir");
        var pw = Ok(lean_uv_os_get_passwd(), "passwd");
        var uid = lean_ctor_get(pw, 1);
        var gid = lean_ctor_get(pw, 2);
        Check(Str(lean_ctor_get(pw, 0)).Length > 0, $"passwd user={Str(lean_ctor_get(pw, 0))} uid={(lean_is_scalar(uid) ? "none" : U64(lean_ctor_get(uid, 0)))} gid={(lean_is_scalar(gid) ? "none" : U64(lean_ctor_get(gid, 0)))} shell={(lean_is_scalar(lean_ctor_get(pw, 3)) ? "none" : Str(lean_ctor_get(lean_ctor_get(pw, 3), 0)))}");
        if (!lean_is_scalar(gid))
        {
            var g = Ok(lean_uv_os_get_group(lean_ctor_get_uint64_s(lean_ctor_get(gid, 0), 0)), "group");
            Console.WriteLine("     group: " + (lean_is_scalar(g) ? "none" : Str(lean_ctor_get(lean_ctor_get(g, 0), 0))));
        }
        if (OperatingSystem.IsWindows())
            // as in libuv: `uv_os_get_group` is not supported on Windows
            Check(lean_io_result_is_error(lean_uv_os_get_group(0)), "group 0 is not supported");
        else
        {
            var g0 = Ok(lean_uv_os_get_group(0), "group 0");
            Check(!lean_is_scalar(g0), "group 0 = " + (lean_is_scalar(g0) ? "none" : Str(lean_ctor_get(lean_ctor_get(g0, 0), 0))));
        }

        Ok(lean_uv_os_setenv(lean_mk_string("LEANSHARP_UV_TEST"), lean_mk_string("v1")), "setenv");
        var ge = Ok(lean_uv_os_getenv(lean_mk_string("LEANSHARP_UV_TEST")), "getenv");
        Check(!lean_is_scalar(ge) && Str(lean_ctor_get(ge, 0)) == "v1", "getenv after setenv");
        var env = Ok(lean_uv_os_environ(), "environ");
        bool found = false;
        for (ulong i = 0; i < lean_array_size(env); i++)
        {
            var pr = lean_array_get_core(env, i);
            if (Str(lean_ctor_get(pr, 0)) == "LEANSHARP_UV_TEST" && Str(lean_ctor_get(pr, 1)) == "v1") found = true;
        }
        Check(found, "environ contains the variable");
        Ok(lean_uv_os_unsetenv(lean_mk_string("LEANSHARP_UV_TEST")), "unsetenv");
        Check(lean_is_scalar(Ok(lean_uv_os_getenv(lean_mk_string("LEANSHARP_UV_TEST")), "getenv 2")), "getenv after unsetenv");
        var bad = lean_uv_os_setenv(lean_mk_string("A=B"), lean_mk_string("x"));
        Check(lean_io_result_is_error(bad), "setenv with '=' fails");
        var nul = lean_uv_os_setenv(lean_mk_string("A\0B"), lean_mk_string("x"));
        Check(lean_io_result_is_error(nul) && Str(lean_io_result_get_error(nul)).Contains("NUL"), "setenv with NUL fails");

        var cpus = Ok(lean_uv_cpu_info(), "cpu info");
        Check(lean_array_size(cpus) > 0, $"{lean_array_size(cpus)} cpus, model '{Str(lean_ctor_get(lean_array_get_core(cpus, 0), 0))}' speed {lean_ctor_get_uint64_s(lean_array_get_core(cpus, 0), 0)}");
        ulong total = lean_ctor_get_uint64_s(Ok(lean_uv_get_total_memory(), "total mem"), 0);
        ulong free = lean_ctor_get_uint64_s(Ok(lean_uv_get_free_memory(), "free mem"), 0);
        ulong avail = lean_ctor_get_uint64_s(Ok(lean_uv_get_available_memory(), "avail mem"), 0);
        ulong constr = lean_ctor_get_uint64_s(Ok(lean_uv_get_constrained_memory(), "constrained mem"), 0);
        Check(total > 0 && free <= total, $"memory total={total} free={free} available={avail} constrained={constr}");
        ulong h1 = lean_ctor_get_uint64_s(Ok(lean_uv_hrtime(), "hrtime"), 0);
        Thread.Sleep(10);
        ulong h2 = lean_ctor_get_uint64_s(Ok(lean_uv_hrtime(), "hrtime"), 0);
        Check(h2 > h1 && h2 - h1 >= 9_000_000, $"hrtime advanced {h2 - h1} ns");
        Check(lean_ctor_get_uint64_s(Ok(lean_uv_uptime(), "uptime"), 0) > 0, "uptime = " + U64(Ok(lean_uv_uptime(), "uptime")));
        var rnd = ExceptOk(Wait(Ok(lean_uv_random(32), "random")), "random result");
        Check(rnd != null && lean_sarray_size(rnd) == 32, "random 32 bytes");
        var ru = Ok(lean_uv_getrusage(), "getrusage");
        Check(lean_ctor_get_uint64_s(ru, 16) > 0, $"rusage user={lean_ctor_get_uint64_s(ru, 0)}ms maxrss={lean_ctor_get_uint64_s(ru, 16)}KB");
        var exe = Str(Ok(lean_uv_exepath(), "exepath"));
        Check(File.Exists(exe), "exepath = " + exe);
        var title = Str(Ok(lean_uv_get_process_title(), "title"));
        Ok(lean_uv_set_process_title(lean_mk_string("uv-check")), "set title");
        Check(Str(Ok(lean_uv_get_process_title(), "title 2")) == "uv-check", $"process title (was '{title}')");
        var pr0 = Ok(lean_uv_os_getpriority(0), "getpriority");
        Check(true, "priority = " + (long)lean_ctor_get_uint64_s(pr0, 0));
        var ifs = Ok(lean_uv_interface_addresses(), "interfaces");
        var names = new List<string>();
        for (ulong i = 0; i < lean_array_size(ifs); i++)
        {
            var it = lean_array_get_core(ifs, i);
            names.Add($"{Str(lean_ctor_get(it, 0))}={IPAddrStr(lean_ctor_get(it, 2))}/{IPAddrStr(lean_ctor_get(it, 3))}{(lean_ctor_get_uint8_s(it, 0) != 0 ? "(lo)" : "")}");
        }
        Check(names.Any(n => n.Contains("=127.0.0.1/255.0.0.0(lo)")), "interfaces: " + string.Join(" ", names));
        Check(lean_uv_event_loop_alive() == 1, "loop alive");
        var opts = lean_alloc_ctor(0, 0, 2);
        Check(lean_is_scalar(lean_uv_event_loop_configure(opts)), "configure");
    }

    static void TestSignals()
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("     (skipped on Windows)"); return; }
        var sig = Ok(lean_uv_signal_mk(10, 0), "signal mk SIGUSR1");
        var p = Ok(lean_uv_signal_next(sig), "signal next");
        Check(!IsResolved(p), "signal promise pending");
        var psi = new ProcessStartInfo("kill") { UseShellExecute = false };
        psi.ArgumentList.Add("-USR1");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        using (var k = Process.Start(psi)) k.WaitForExit();
        var v = Wait(p, 5000);
        int expected = OperatingSystem.IsMacOS() ? 30 : 10;
        Check(v != null && lean_is_scalar(v) && (int)lean_unbox(v) == expected, $"SIGUSR1 received (value {(v == null ? "none" : lean_unbox(v).ToString())})");
        // repeating
        var rs = Ok(lean_uv_signal_mk(12, 1), "signal mk SIGUSR2 repeating");
        int n = 0;
        for (int i = 0; i < 2; i++)
        {
            var rp = Ok(lean_uv_signal_next(rs), "repeating signal next");
            var psi2 = new ProcessStartInfo("kill") { UseShellExecute = false };
            psi2.ArgumentList.Add("-USR2");
            psi2.ArgumentList.Add(Environment.ProcessId.ToString());
            using (var k = Process.Start(psi2)) k.WaitForExit();
            if (Wait(rp, 5000) != null) n++;
        }
        Check(n == 2, "repeating signal received twice");
        Ok(lean_uv_signal_stop(rs), "signal stop");
        var bad = Ok(lean_uv_signal_mk(99, 0), "unknown signal mk");
        var bn = lean_uv_signal_next(bad);
        Check(lean_io_result_is_error(bn) && Str(lean_io_result_get_error(bn)).StartsWith("invalidArgument"), "unknown signal rejected on next");
    }
}
