// Port of `runtime/uv/tcp.cpp` on top of System.Net.Sockets.Socket.
//
// libuv concepts emulated here:
//  * the OS socket is created lazily (by bind/connect/listen) with the address family needed;
//  * `delayed_error`: EADDRINUSE from bind is reported by listen/connect;
//  * a listening socket accepts one connection at a time and keeps it queued until `uv_accept`
//    takes it (`AcceptedPending`), calling the listen callback once per connection;
//  * `uv_read_stop` never loses data: if a cancelled .NET receive still completes with data, the
//    data is kept (`Leftover`) and returned by the next receive.

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using static LeanSharp.Runtime.LeanRt;

namespace LeanSharp.Runtime;

internal sealed class UvTcp
{
    public Obj Self;
    public Socket Sock;
    public Obj PromiseAccept, PromiseRead, PromiseShutdown, Client, ByteArray;
    public bool ShutdownRequested, Listening;
    public uint PendingConnections;
    public int DelayedError;
    public bool Readable, Writable, Connecting, Closed;

    // options set before the OS socket exists
    public bool OptNoDelay;
    public int OptKeepAlive = -1; // -1: not set, 0: off, 1: on
    public uint OptKeepAliveDelay;

    // listening
    public Socket AcceptedPending;
    public TaskCompletionSource AcceptedTaken;
    public CancellationTokenSource ListenCts;

    // reading
    public long ReadGen;
    public CancellationTokenSource ReadCts;
    public byte[] Leftover;
    public int LeftoverOff, LeftoverLen;
    /// <summary>
    /// The peer has closed its side: every read reports EOF. As in libuv, EOF does not make the
    /// stream "not readable" (`uv_read_start` still succeeds and the read callback gets `UV_EOF`
    /// again); e.g. `recvSelector` first waits for readability (which reports EOF) and then
    /// calls `recv?`, which must yield `none` rather than fail with ENOTCONN.
    /// </summary>
    public bool EofPending;
    public int ErrorPending;

    // writing (sequential chain of writes and shutdown)
    public Task WriteChain = Task.CompletedTask;

    public static readonly ExternalClass Class = new ExternalClass(Finalize, Foreach);

    static void Finalize(object data)
    {
        var t = (UvTcp)data;
        lock (UvLoop.Lock)
        {
            t.Close();
        }
    }

    static void Foreach(object data, Obj f)
    {
        var t = (UvTcp)data;
        UvUtil.ForeachChild(f, t.PromiseAccept);
        UvUtil.ForeachChild(f, t.PromiseShutdown);
        UvUtil.ForeachChild(f, t.PromiseRead);
        UvUtil.ForeachChild(f, t.ByteArray);
        UvUtil.ForeachChild(f, t.Client);
    }

    /// <summary>`uv_close`. Must hold the loop lock.</summary>
    public void Close()
    {
        if (Closed) return;
        Closed = true;
        Readable = Writable = Listening = false;
        try { ListenCts?.Cancel(); } catch { }
        try { ReadCts?.Cancel(); } catch { }
        try { Sock?.Dispose(); } catch { }
        try { AcceptedPending?.Dispose(); } catch { }
        AcceptedPending = null;
        AcceptedTaken?.TrySetResult();
    }

    /// <summary>`maybe_new_socket`: create the OS socket for `family` if there is none yet. Returns a libuv code.</summary>
    public int EnsureSocket(AddressFamily family)
    {
        if (Closed) return UvErr.EBADF;
        if (Sock != null) return 0;
        try
        {
            var s = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            if (family == AddressFamily.InterNetworkV6) s.DualMode = true;
            Sock = s;
            ApplyOptions();
            return 0;
        }
        catch (Exception ex)
        {
            return UvErr.FromException(ex);
        }
    }

    public void ApplyOptions()
    {
        if (Sock == null) return;
        try
        {
            if (OptNoDelay) Sock.NoDelay = true;
            if (OptKeepAlive >= 0) SetKeepAlive(Sock, OptKeepAlive != 0, OptKeepAliveDelay);
        }
        catch { }
    }

    /// <summary>`setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, 1)` on Unix (no-op on Windows).</summary>
    public static void SetReuseAddr(Socket s)
    {
        if (OperatingSystem.IsWindows()) return;
        bool bsd = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsFreeBSD()
            || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst();
        int level = bsd ? 0xffff : 1, name = bsd ? 4 : 2;
        try { s.SetRawSocketOption(level, name, BitConverter.GetBytes(1)); } catch { }
    }

    public static void SetKeepAlive(Socket s, bool on, uint delay)
    {
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, on);
        if (on)
        {
            try { s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, (int)Math.Min(delay, int.MaxValue)); } catch { }
        }
    }

    /// <summary>`uv_accept(server, client)`. Must hold the loop lock.</summary>
    public int Accept(UvTcp client)
    {
        var s = AcceptedPending;
        if (s == null) return UvErr.EAGAIN;
        AcceptedPending = null;
        var taken = AcceptedTaken;
        taken?.TrySetResult();
        if (client.Sock != null || client.Closed)
        {
            try { s.Dispose(); } catch { }
            return UvErr.EBUSY;
        }
        client.Sock = s;
        client.Readable = client.Writable = true;
        client.ApplyOptions();
        return 0;
    }

    /// <summary>The listen callback (`uv_listen`'s `cb`). Must hold the loop lock.</summary>
    void OnConnection(int status, List<Obj> toRelease)
    {
        Obj socket = Self;
        Obj promise = PromiseAccept;
        Obj client = Client;
        if (status >= 0 && client == null) PendingConnections++;
        if (promise == null) return;
        int result = status;
        if (status >= 0 && client != null)
            result = Accept(UvUtil.ExternalData<UvTcp>(client));
        PromiseAccept = null;
        Client = null;
        toRelease.Add(socket);
        if (result < 0)
        {
            if (client != null) toRelease.Add(client);
            UvPromise.ResolveWithCode(result, promise);
        }
        else
        {
            UvPromise.Resolve(UvUtil.ExceptOk(client ?? lean_box(0)), promise);
        }
        toRelease.Add(promise);
    }

    public void StartAcceptLoop()
    {
        ListenCts = new CancellationTokenSource();
        var listenSock = Sock;
        var ct = ListenCts.Token;
        Task.Run(() => AcceptLoop(listenSock, ct));
    }

    async Task AcceptLoop(Socket listenSock, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket s = null;
            int status;
            try
            {
                s = await listenSock.AcceptAsync(ct).ConfigureAwait(false);
                status = 0;
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                status = UvErr.FromException(ex);
            }
            Task wait = null;
            var toRelease = new List<Obj>();
            lock (UvLoop.Lock)
            {
                if (Closed || !ReferenceEquals(Sock, listenSock))
                {
                    try { s?.Dispose(); } catch { }
                    return;
                }
                if (s != null)
                {
                    AcceptedPending = s;
                    AcceptedTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = AcceptedTaken.Task;
                }
                OnConnection(status, toRelease);
            }
            foreach (var o in toRelease) lean_dec(o);
            if (wait != null) await wait.ConfigureAwait(false);
            else await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public static Obj FitReadBuffer(Obj byteArray, int nread)
    {
        if ((ulong)nread * 2 >= lean_sarray_capacity(byteArray))
        {
            lean_sarray_set_size(byteArray, (ulong)nread);
            return byteArray;
        }
        var fitted = lean_alloc_sarray(1, (ulong)nread, (ulong)nread);
        Array.Copy(lean_sarray_cptr(byteArray), lean_sarray_cptr(fitted), nread);
        lean_dec(byteArray);
        return fitted;
    }

    /// <summary>Keep data of a receive that completed after it was cancelled.</summary>
    public void StashData(byte[] buf, int n)
    {
        if (n <= 0) return;
        int have = Leftover == null ? 0 : LeftoverLen;
        var nb = new byte[have + n];
        if (have > 0) Array.Copy(Leftover, LeftoverOff, nb, 0, have);
        Array.Copy(buf, 0, nb, have, n);
        Leftover = nb; LeftoverOff = 0; LeftoverLen = nb.Length;
    }

    public bool HasPendingInput => (Leftover != null && LeftoverLen > 0) || EofPending || ErrorPending != 0;
}

internal static class UvTcpExterns
{
    static UvTcp UvTcpOf(Obj o) => UvUtil.ExternalData<UvTcp>(o);

    internal static Obj UvRecvSizeError(ulong size)
    {
        if (size != 0) return null;
        return lean_io_result_mk_error(UvErr.MkInvalidArgument(UvErr.PosixErrno(UvErr.EINVAL), "receive buffer size must be positive"));
    }

    /* Std.Internal.UV.TCP.Socket.new : IO Socket */
    public static Obj lean_uv_tcp_new()
    {
        var t = new UvTcp();
        Obj obj = UvUtil.AllocExternal(UvTcp.Class, t);
        lean_mark_mt(obj);
        t.Self = obj;
        return lean_io_result_mk_ok(obj);
    }

    /* Std.Internal.UV.TCP.Socket.connect (socket : @& Socket) (addr : @& SocketAddress) : IO (IO.Promise (Except IO.Error Unit)) */
    public static Obj lean_uv_tcp_connect(Obj socket, Obj addr)
    {
        var t = UvTcpOf(socket);
        IPEndPoint ep = UvNet.SocketAddressToNet(addr);
        lock (UvLoop.Lock)
        {
            if (t.Connecting) return UvErr.IoError(UvErr.EALREADY);
            if (t.Readable || t.Writable || t.ShutdownRequested) return UvErr.IoError(UvErr.EISCONN);
            if (t.Listening) return UvErr.IoError(UvErr.EINVAL);
            int r = t.EnsureSocket(ep.AddressFamily);
            if (r < 0) return UvErr.IoError(r);
            if (t.Sock.AddressFamily != ep.AddressFamily)
            {
                if (t.Sock.AddressFamily == AddressFamily.InterNetworkV6 && t.Sock.DualMode)
                    ep = new IPEndPoint(ep.Address.MapToIPv6(), ep.Port);
                else
                    return UvErr.IoError(UvErr.EINVAL);
            }

            Obj promise = UvPromise.New();
            // The event loop owns the socket.
            lean_inc(socket);
            lean_inc(promise);
            t.Connecting = true;

            if (t.DelayedError != 0)
            {
                int delayed = t.DelayedError;
                Task.Run(() => CompleteTcpConnect(t, socket, promise, delayed));
                return lean_io_result_mk_ok(promise);
            }

            Task task;
            try
            {
                task = t.Sock.ConnectAsync(ep);
            }
            catch (Exception ex)
            {
                t.Connecting = false;
                lean_dec(promise);
                lean_dec(promise);
                lean_dec(socket);
                return UvErr.IoError(UvErr.FromException(ex));
            }
            task.ContinueWith(tk =>
            {
                int status = tk.IsCompletedSuccessfully ? 0 : UvErr.FromException(tk.Exception ?? (Exception)new OperationCanceledException());
                CompleteTcpConnect(t, socket, promise, status);
            }, TaskScheduler.Default);
            return lean_io_result_mk_ok(promise);
        }
    }

    static void CompleteTcpConnect(UvTcp t, Obj socket, Obj promise, int status)
    {
        lock (UvLoop.Lock)
        {
            t.Connecting = false;
            if (t.Closed && status == 0) status = UvErr.ECANCELED;
            if (status == 0)
            {
                t.Readable = t.Writable = true;
                t.ApplyOptions();
            }
            UvPromise.ResolveWithCode(status, promise);
        }
        // The event loop does not own the objects anymore.
        lean_dec(socket);
        lean_dec(promise);
    }

    /* Std.Internal.UV.TCP.Socket.send (socket : @& Socket) (data : Array ByteArray) : IO (IO.Promise (Except IO.Error Unit)) */
    public static Obj lean_uv_tcp_send(Obj socket, Obj data_array)
    {
        var t = UvTcpOf(socket);
        ulong n = lean_array_size(data_array);
        if (n == 0)
        {
            lean_dec(data_array);
            Obj p0 = UvPromise.New();
            UvPromise.ResolveWithCode(0, p0);
            return lean_io_result_mk_ok(p0);
        }
        var segs = new List<ArraySegment<byte>>();
        for (ulong i = 0; i < n; i++)
        {
            Obj ba = lean_array_get_core(data_array, i);
            int sz = (int)lean_sarray_size(ba);
            if (sz > 0) segs.Add(new ArraySegment<byte>(lean_sarray_cptr(ba), 0, sz));
        }
        lock (UvLoop.Lock)
        {
            if (t.Sock == null || t.Closed)
            {
                lean_dec(data_array);
                return UvErr.IoError(UvErr.EBADF);
            }
            if (!t.Writable)
            {
                lean_dec(data_array);
                return UvErr.IoError(UvErr.EPIPE);
            }
            Obj promise = UvPromise.New();
            lean_mark_mt(data_array);
            // These objects are going to enter the loop and be owned by it.
            lean_inc(promise);
            lean_inc(socket);
            Socket sock = t.Sock;
            t.WriteChain = TcpWriteAsync(t.WriteChain, sock, segs, t, socket, promise, data_array);
            return lean_io_result_mk_ok(promise);
        }
    }

    static async Task TcpWriteAsync(Task prev, Socket sock, List<ArraySegment<byte>> segs, UvTcp t, Obj socket, Obj promise, Obj data)
    {
        try { await prev.ConfigureAwait(false); } catch { }
        await Task.Yield();
        int status = 0;
        try
        {
            foreach (var seg in segs)
            {
                int off = 0;
                while (off < seg.Count)
                {
                    int sent = await sock.SendAsync(seg.Slice(off), SocketFlags.None).ConfigureAwait(false);
                    if (sent <= 0) throw new SocketException((int)SocketError.ConnectionReset);
                    off += sent;
                }
            }
        }
        catch (Exception ex)
        {
            status = UvErr.FromException(ex);
            if (status == UvErr.EBADF) status = UvErr.ECANCELED;
        }
        lock (UvLoop.Lock)
        {
            UvPromise.ResolveWithCode(status, promise);
        }
        lean_dec(promise);
        lean_dec(data);
        lean_dec(socket);
    }

    /* Std.Internal.UV.TCP.Socket.recv? (socket : @& Socket) (size : UInt64) : IO (IO.Promise (Except IO.Error (Option ByteArray))) */
    public static Obj lean_uv_tcp_recv(Obj socket, ulong buffer_size)
    {
        var t = UvTcpOf(socket);
        lock (UvLoop.Lock)
        {
            if (t.PromiseRead != null) return UvErr.IoError(UvErr.EALREADY);
            var sizeError = UvRecvSizeError(buffer_size);
            if (sizeError != null) return sizeError;
            if (buffer_size > (ulong)Array.MaxLength) return UvErr.IoError(UvErr.ENOMEM);
            if (t.Closed) return UvErr.IoError(UvErr.EINVAL);
            if (!t.HasPendingInput && (t.Sock == null || !t.Readable)) return UvErr.IoError(UvErr.ENOTCONN);

            Obj byteArray = lean_alloc_sarray(1, 0, buffer_size);
            Obj promise = UvPromise.New();
            t.ByteArray = byteArray;
            t.PromiseRead = promise;
            // The event loop owns the socket.
            lean_inc(socket);
            lean_inc(promise);

            long gen = ++t.ReadGen;
            if (t.HasPendingInput)
            {
                if (t.Leftover != null && t.LeftoverLen > 0)
                {
                    int m = (int)Math.Min((ulong)t.LeftoverLen, buffer_size);
                    Array.Copy(t.Leftover, t.LeftoverOff, lean_sarray_cptr(byteArray), 0, m);
                    t.LeftoverOff += m; t.LeftoverLen -= m;
                    if (t.LeftoverLen == 0) t.Leftover = null;
                    CompleteTcpRead(t, gen, m, 0);
                }
                else if (t.ErrorPending != 0)
                {
                    int e = t.ErrorPending; t.ErrorPending = 0;
                    CompleteTcpRead(t, gen, 0, e);
                }
                else
                {
                    CompleteTcpRead(t, gen, 0, UvErr.EOF);
                }
                return lean_io_result_mk_ok(promise);
            }

            var cts = new CancellationTokenSource();
            t.ReadCts = cts;
            byte[] buf = lean_sarray_cptr(byteArray);
            int cap = (int)buffer_size;
            Socket sock = t.Sock;
            Task.Run(async () =>
            {
                int nread = 0, err = 0;
                try
                {
                    nread = await sock.ReceiveAsync(new Memory<byte>(buf, 0, cap), SocketFlags.None, cts.Token).ConfigureAwait(false);
                    if (nread == 0) err = UvErr.EOF;
                }
                catch (Exception ex)
                {
                    err = UvErr.FromException(ex);
                }
                lock (UvLoop.Lock)
                {
                    if (gen != t.ReadGen || !ReferenceEquals(t.PromiseRead, promise))
                    {
                        // The read was cancelled: keep what arrived for the next read.
                        if (err == 0) t.StashData(buf, nread);
                        else if (err == UvErr.EOF) t.EofPending = true;
                        else if (err != UvErr.ECANCELED && err != UvErr.EBADF) t.ErrorPending = err;
                        return;
                    }
                    CompleteTcpRead(t, gen, nread, err);
                }
            });
            return lean_io_result_mk_ok(promise);
        }
    }

    /// <summary>The read callback of `lean_uv_tcp_recv`. Must hold the loop lock.</summary>
    static void CompleteTcpRead(UvTcp t, long gen, int nread, int err)
    {
        Obj promise = t.PromiseRead;
        Obj byteArray = t.ByteArray;
        t.PromiseRead = null;
        t.ByteArray = null;
        t.ReadCts = null;
        if (err == 0)
        {
            byteArray = UvTcp.FitReadBuffer(byteArray, nread);
            UvPromise.Resolve(UvUtil.ExceptOk(lean_mk_option_some(byteArray)), promise);
        }
        else if (err == UvErr.EOF)
        {
            t.EofPending = true;
            lean_dec(byteArray);
            UvPromise.Resolve(UvUtil.ExceptOk(lean_box(0)), promise);
        }
        else
        {
            lean_dec(byteArray);
            UvPromise.Resolve(UvUtil.ExceptErr(UvErr.Decode(err)), promise);
        }
        lean_dec(promise);
        // The event loop does not own the object anymore.
        lean_dec(t.Self);
    }

    /* Std.Internal.UV.TCP.Socket.waitReadable (socket : @& Socket) : IO (IO.Promise (Except IO.Error Bool)) */
    public static Obj lean_uv_tcp_wait_readable(Obj socket)
    {
        var t = UvTcpOf(socket);
        lock (UvLoop.Lock)
        {
            if (t.PromiseRead != null) return UvErr.IoError(UvErr.EALREADY);
            if (t.Closed) return UvErr.IoError(UvErr.EINVAL);
            if (!t.HasPendingInput && (t.Sock == null || !t.Readable)) return UvErr.IoError(UvErr.ENOTCONN);

            Obj promise = UvPromise.New();
            t.PromiseRead = promise;
            lean_inc(socket);
            lean_inc(promise);
            long gen = ++t.ReadGen;

            if (t.HasPendingInput)
            {
                int res = (t.Leftover != null && t.LeftoverLen > 0) ? 1 : t.ErrorPending != 0 ? t.ErrorPending : UvErr.EOF;
                if (res < 0 && res != UvErr.EOF) t.ErrorPending = 0;
                CompleteTcpWaitReadable(t, res);
                return lean_io_result_mk_ok(promise);
            }

            var cts = new CancellationTokenSource();
            t.ReadCts = cts;
            Socket sock = t.Sock;
            Task.Run(async () =>
            {
                int res;
                try
                {
                    var one = new byte[1];
                    int n = await sock.ReceiveAsync(new Memory<byte>(one), SocketFlags.Peek, cts.Token).ConfigureAwait(false);
                    res = n > 0 ? 1 : UvErr.EOF;
                }
                catch (Exception ex)
                {
                    res = UvErr.FromException(ex);
                    if (res == UvErr.ENOBUFS || res == UvErr.EMSGSIZE) res = 1;
                }
                lock (UvLoop.Lock)
                {
                    if (gen != t.ReadGen || !ReferenceEquals(t.PromiseRead, promise)) return;
                    CompleteTcpWaitReadable(t, res);
                }
            });
            return lean_io_result_mk_ok(promise);
        }
    }

    /// <summary>`res`: 1 = readable, EOF, or an error code. Must hold the loop lock.</summary>
    static void CompleteTcpWaitReadable(UvTcp t, int res)
    {
        Obj promise = t.PromiseRead;
        t.PromiseRead = null;
        t.ReadCts = null;
        if (res == UvErr.EOF)
        {
            t.EofPending = true;
            UvPromise.Resolve(UvUtil.ExceptOk(lean_box(0)), promise);
        }
        else if (res < 0)
            UvPromise.Resolve(UvUtil.ExceptErr(UvErr.Decode(res)), promise);
        else
            UvPromise.Resolve(UvUtil.ExceptOk(lean_box(1)), promise);
        lean_dec(promise);
        lean_dec(t.Self);
    }

    /* Std.Internal.UV.TCP.Socket.cancelRecv (socket : @& Socket) : IO Unit */
    public static Obj lean_uv_tcp_cancel_recv(Obj socket)
    {
        var t = UvTcpOf(socket);
        Obj promise, byteArray;
        lock (UvLoop.Lock)
        {
            if (t.PromiseRead == null) return lean_io_result_mk_ok(lean_box(0));
            t.ReadGen++;
            try { t.ReadCts?.Cancel(); } catch { }
            t.ReadCts = null;
            promise = t.PromiseRead;
            byteArray = t.ByteArray;
            t.PromiseRead = null;
            t.ByteArray = null;
        }
        lean_dec(promise);
        if (byteArray != null) lean_dec(byteArray);
        lean_dec(socket);
        return lean_io_result_mk_ok(lean_box(0));
    }

    /* Std.Internal.UV.TCP.Socket.bind (socket : @& Socket) (addr : @& SocketAddress) : IO Unit */
    public static Obj lean_uv_tcp_bind(Obj socket, Obj addr)
    {
        var t = UvTcpOf(socket);
        IPEndPoint ep = UvNet.SocketAddressToNet(addr);
        lock (UvLoop.Lock)
        {
            int r = t.EnsureSocket(ep.AddressFamily);
            if (r < 0) return UvErr.IoError(r);
            if (t.Sock.AddressFamily != ep.AddressFamily) return UvErr.IoError(UvErr.EINVAL);
            try
            {
                // libuv sets SO_REUSEADDR (only) on Unix. .NET's ReuseAddress option also sets
                // SO_REUSEPORT on some platforms, which would allow two listeners on one port.
                UvTcp.SetReuseAddr(t.Sock);
                t.Sock.Bind(ep);
            }
            catch (Exception ex)
            {
                int code = UvErr.FromException(ex);
                if (code == UvErr.EADDRINUSE) { t.DelayedError = code; return UvUtil.IoOkUnit(); }
                return UvErr.IoError(code);
            }
            return UvUtil.IoOkUnit();
        }
    }

    /* Std.Internal.UV.TCP.Socket.listen (socket : @& Socket) (backlog : UInt32) : IO Unit */
    public static Obj lean_uv_tcp_listen(Obj socket, uint backlog)
    {
        var t = UvTcpOf(socket);
        lock (UvLoop.Lock)
        {
            if (t.DelayedError != 0) return UvErr.IoError(t.DelayedError);
            if (t.Readable || t.Writable || t.Connecting) return UvErr.IoError(UvErr.EINVAL);
            bool fresh = t.Sock == null;
            int r = t.EnsureSocket(AddressFamily.InterNetwork);
            if (r < 0) return UvErr.IoError(r);
            try
            {
                if (fresh || t.Sock.LocalEndPoint == null)
                    t.Sock.Bind(new IPEndPoint(t.Sock.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
                t.Sock.Listen((int)Math.Min(backlog, (uint)int.MaxValue));
            }
            catch (Exception ex)
            {
                return UvErr.IoError(UvErr.FromException(ex));
            }
            if (!t.Listening)
            {
                t.Listening = true;
                t.StartAcceptLoop();
            }
            return UvUtil.IoOkUnit();
        }
    }

    static Obj UvTcpNotListeningError() =>
        lean_io_result_mk_error(UvErr.MkInvalidArgument(UvErr.PosixErrno(UvErr.EINVAL), "socket is not listening"));

    static Obj UvTcpParallelAcceptError() =>
        lean_io_result_mk_error(UvErr.MkOtherError(UvErr.PosixErrno(UvErr.EALREADY),
            "parallel accept is not allowed! consider binding multiple sockets to the same address and accepting on them instead"));

    /* Std.Internal.UV.TCP.Socket.accept (socket : @& Socket) : IO (IO.Promise (Except IO.Error Socket)) */
    public static Obj lean_uv_tcp_accept(Obj socket)
    {
        var t = UvTcpOf(socket);
        lock (UvLoop.Lock)
        {
            if (t.PromiseAccept != null) return UvTcpParallelAcceptError();
            if (!t.Listening) return UvTcpNotListeningError();
            Obj client = lean_io_result_take_value(lean_uv_tcp_new());
            Obj promise = UvPromise.New();
            int result = t.Accept(UvTcpOf(client));
            if (result != UvErr.EAGAIN && t.PendingConnections > 0) t.PendingConnections--;
            if (result < 0 && result != UvErr.EAGAIN)
            {
                lean_dec(client);
                UvPromise.ResolveWithCode(result, promise);
            }
            else if (result >= 0)
            {
                UvPromise.Resolve(UvUtil.ExceptOk(client), promise);
            }
            else
            {
                // The event loop owns the objects. They are released in the listen callback.
                lean_inc(socket);
                lean_inc(promise);
                t.PromiseAccept = promise;
                t.Client = client;
            }
            return lean_io_result_mk_ok(promise);
        }
    }

    /* Std.Internal.UV.TCP.Socket.tryAccept (socket : @& Socket) : IO (Except IO.Error (Option Socket)) */
    public static Obj lean_uv_tcp_try_accept(Obj socket)
    {
        var t = UvTcpOf(socket);
        Obj client;
        lock (UvLoop.Lock)
        {
            if (t.PromiseAccept != null) return UvTcpParallelAcceptError();
            if (!t.Listening) return UvTcpNotListeningError();
            client = lean_io_result_take_value(lean_uv_tcp_new());
            int result = t.Accept(UvTcpOf(client));
            if (result != UvErr.EAGAIN && t.PendingConnections > 0) t.PendingConnections--;
            if (result >= 0)
                return lean_io_result_mk_ok(UvUtil.ExceptOk(lean_mk_option_some(client)));
            if (result != UvErr.EAGAIN)
            {
                lean_dec(client);
                return UvErr.IoError(result);
            }
        }
        lean_dec(client);
        return lean_io_result_mk_ok(UvUtil.ExceptOk(lean_box(0)));
    }

    /* Std.Internal.UV.TCP.Socket.waitAcceptable (socket : @& Socket) : IO (IO.Promise (Except IO.Error Unit)) */
    public static Obj lean_uv_tcp_wait_acceptable(Obj socket)
    {
        var t = UvTcpOf(socket);
        lock (UvLoop.Lock)
        {
            if (t.PromiseAccept != null) return UvErr.IoError(UvErr.EALREADY);
            if (!t.Listening) return UvTcpNotListeningError();
            Obj promise = UvPromise.New();
            if (t.PendingConnections > 0)
            {
                UvPromise.Resolve(UvUtil.ExceptOk(lean_box(0)), promise);
                return lean_io_result_mk_ok(promise);
            }
            // The event loop owns the objects. They are released in the listen callback.
            lean_inc(socket);
            lean_inc(promise);
            t.PromiseAccept = promise;
            return lean_io_result_mk_ok(promise);
        }
    }

    /* Std.Internal.UV.TCP.Socket.cancelAccept (socket : @& Socket) : IO Unit */
    public static Obj lean_uv_tcp_cancel_accept(Obj socket)
    {
        var t = UvTcpOf(socket);
        Obj promise, client;
        lock (UvLoop.Lock)
        {
            if (t.PromiseAccept == null) return lean_io_result_mk_ok(lean_box(0));
            promise = t.PromiseAccept;
            client = t.Client;
            t.PromiseAccept = null;
            t.Client = null;
        }
        lean_dec(promise);
        if (client != null) lean_dec(client);
        lean_dec(socket);
        return lean_io_result_mk_ok(lean_box(0));
    }

    /* Std.Internal.UV.TCP.Socket.shutdown (socket : @& Socket) : IO (IO.Promise (Except IO.Error Unit)) */
    public static Obj lean_uv_tcp_shutdown(Obj socket)
    {
        var t = UvTcpOf(socket);
        lock (UvLoop.Lock)
        {
            if (t.ShutdownRequested)
                return lean_io_result_mk_error(UvErr.MkOtherError(UvErr.PosixErrno(UvErr.EALREADY), "shutdown already requested"));
            if (t.Sock == null || !t.Writable || t.Closed) return UvErr.IoError(UvErr.ENOTCONN);
            Obj promise = UvPromise.New();
            t.PromiseShutdown = promise;
            lean_inc(promise);
            lean_inc(socket);
            t.ShutdownRequested = true;
            t.Writable = false;
            Socket sock = t.Sock;
            Task prev = t.WriteChain;
            t.WriteChain = Task.Run(async () =>
            {
                try { await prev.ConfigureAwait(false); } catch { }
                int status = 0;
                try { sock.Shutdown(SocketShutdown.Send); }
                catch (Exception ex) { status = UvErr.FromException(ex); }
                lock (UvLoop.Lock)
                {
                    Obj p = t.PromiseShutdown;
                    t.PromiseShutdown = null;
                    if (p != null)
                    {
                        UvPromise.ResolveWithCode(status, p);
                        lean_dec(p);
                    }
                }
                lean_dec(socket);
            });
            return lean_io_result_mk_ok(promise);
        }
    }

    /* Std.Internal.UV.TCP.Socket.getPeerName (socket : @& Socket) : IO SocketAddress */
    public static Obj lean_uv_tcp_getpeername(Obj socket)
    {
        var t = UvTcpOf(socket);
        lock (UvLoop.Lock)
        {
            if (t.DelayedError != 0) return UvErr.IoError(t.DelayedError);
            if (t.Sock == null || t.Closed) return UvErr.IoError(UvErr.EBADF);
            try
            {
                if (t.Sock.RemoteEndPoint is IPEndPoint ep) return lean_io_result_mk_ok(UvNet.NetToSocketAddress(ep));
                return UvErr.IoError(UvErr.ENOTCONN);
            }
            catch (Exception ex)
            {
                return UvErr.IoError(UvErr.FromException(ex));
            }
        }
    }

    /* Std.Internal.UV.TCP.Socket.getSockName (socket : @& Socket) : IO SocketAddress */
    public static Obj lean_uv_tcp_getsockname(Obj socket)
    {
        var t = UvTcpOf(socket);
        lock (UvLoop.Lock)
        {
            if (t.DelayedError != 0) return UvErr.IoError(t.DelayedError);
            if (t.Sock == null || t.Closed) return UvErr.IoError(UvErr.EBADF);
            try
            {
                if (t.Sock.LocalEndPoint is IPEndPoint ep) return lean_io_result_mk_ok(UvNet.NetToSocketAddress(ep));
                var any = t.Sock.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                return lean_io_result_mk_ok(UvNet.NetToSocketAddress(new IPEndPoint(any, 0)));
            }
            catch (Exception ex)
            {
                return UvErr.IoError(UvErr.FromException(ex));
            }
        }
    }

    /* Std.Internal.UV.TCP.Socket.noDelay (socket : @& Socket) : IO Unit */
    public static Obj lean_uv_tcp_nodelay(Obj socket)
    {
        var t = UvTcpOf(socket);
        lock (UvLoop.Lock)
        {
            t.OptNoDelay = true;
            if (t.Sock != null && !t.Closed)
            {
                try { t.Sock.NoDelay = true; }
                catch (Exception ex) { return UvErr.IoError(UvErr.FromException(ex)); }
            }
            return UvUtil.IoOkUnit();
        }
    }

    /* Std.Internal.UV.TCP.Socket.keepAlive (socket : @& Socket) (enable : Int8) (delay : UInt32) : IO Unit */
    public static Obj lean_uv_tcp_keepalive(Obj socket, byte enable, uint delay)
    {
        var t = UvTcpOf(socket);
        bool on = (sbyte)enable != 0;
        if (on && delay < 1) return UvErr.IoError(UvErr.EINVAL);
        lock (UvLoop.Lock)
        {
            t.OptKeepAlive = on ? 1 : 0;
            t.OptKeepAliveDelay = delay;
            if (t.Sock != null && !t.Closed)
            {
                try { UvTcp.SetKeepAlive(t.Sock, on, delay); }
                catch (Exception ex) { return UvErr.IoError(UvErr.FromException(ex)); }
            }
            return UvUtil.IoOkUnit();
        }
    }
}

/// <summary>Extern entry points (the implementation is in `UvTcpExterns`, outside the unsafe context, so that it can use async code).</summary>
public static unsafe partial class LeanRt
{
    public static Obj lean_uv_tcp_new() => UvTcpExterns.lean_uv_tcp_new();
    public static Obj lean_uv_tcp_connect(Obj socket, Obj addr) => UvTcpExterns.lean_uv_tcp_connect(socket, addr);
    public static Obj lean_uv_tcp_send(Obj socket, Obj data_array) => UvTcpExterns.lean_uv_tcp_send(socket, data_array);
    public static Obj lean_uv_tcp_recv(Obj socket, ulong buffer_size) => UvTcpExterns.lean_uv_tcp_recv(socket, buffer_size);
    public static Obj lean_uv_tcp_wait_readable(Obj socket) => UvTcpExterns.lean_uv_tcp_wait_readable(socket);
    public static Obj lean_uv_tcp_cancel_recv(Obj socket) => UvTcpExterns.lean_uv_tcp_cancel_recv(socket);
    public static Obj lean_uv_tcp_bind(Obj socket, Obj addr) => UvTcpExterns.lean_uv_tcp_bind(socket, addr);
    public static Obj lean_uv_tcp_listen(Obj socket, uint backlog) => UvTcpExterns.lean_uv_tcp_listen(socket, backlog);
    public static Obj lean_uv_tcp_accept(Obj socket) => UvTcpExterns.lean_uv_tcp_accept(socket);
    public static Obj lean_uv_tcp_try_accept(Obj socket) => UvTcpExterns.lean_uv_tcp_try_accept(socket);
    public static Obj lean_uv_tcp_wait_acceptable(Obj socket) => UvTcpExterns.lean_uv_tcp_wait_acceptable(socket);
    public static Obj lean_uv_tcp_cancel_accept(Obj socket) => UvTcpExterns.lean_uv_tcp_cancel_accept(socket);
    public static Obj lean_uv_tcp_shutdown(Obj socket) => UvTcpExterns.lean_uv_tcp_shutdown(socket);
    public static Obj lean_uv_tcp_getpeername(Obj socket) => UvTcpExterns.lean_uv_tcp_getpeername(socket);
    public static Obj lean_uv_tcp_getsockname(Obj socket) => UvTcpExterns.lean_uv_tcp_getsockname(socket);
    public static Obj lean_uv_tcp_nodelay(Obj socket) => UvTcpExterns.lean_uv_tcp_nodelay(socket);
    public static Obj lean_uv_tcp_keepalive(Obj socket, byte enable, uint delay) => UvTcpExterns.lean_uv_tcp_keepalive(socket, enable, delay);
}
