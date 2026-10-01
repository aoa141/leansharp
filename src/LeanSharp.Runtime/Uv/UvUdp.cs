// Port of `runtime/uv/udp.cpp` on top of System.Net.Sockets.Socket (datagram sockets).

using System.Net;
using System.Net.Sockets;
using static LeanSharp.Runtime.LeanRt;

namespace LeanSharp.Runtime;

internal sealed class UvUdp
{
    public Obj Self;
    public Socket Sock;
    public Obj PromiseRead, ByteArray;
    public bool Connected, Closed;
    public long ReadGen;
    public CancellationTokenSource ReadCts;
    // a datagram that arrived for a cancelled receive (kept for the next one)
    public byte[] PendingData;
    public int PendingLen;          // -1: datagram too large for the cancelled buffer
    public IPEndPoint PendingFrom;
    public int ErrorPending;
    public Task SendChain = Task.CompletedTask;

    public static readonly ExternalClass Class = new ExternalClass(Finalize, Foreach);

    static void Finalize(object data)
    {
        var u = (UvUdp)data;
        lock (UvLoop.Lock) u.Close();
    }

    static void Foreach(object data, Obj f)
    {
        var u = (UvUdp)data;
        UvUtil.ForeachChild(f, u.PromiseRead);
        UvUtil.ForeachChild(f, u.ByteArray);
    }

    public void Close()
    {
        if (Closed) return;
        Closed = true;
        try { ReadCts?.Cancel(); } catch { }
        try { Sock?.Dispose(); } catch { }
    }

    /// <summary>Create the OS socket if needed. Returns a libuv code.</summary>
    public int EnsureSocket(AddressFamily family)
    {
        if (Closed) return UvErr.EBADF;
        if (Sock != null) return 0;
        try
        {
            var s = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            if (family == AddressFamily.InterNetworkV6) s.DualMode = true;
            Sock = s;
            return 0;
        }
        catch (Exception ex)
        {
            return UvErr.FromException(ex);
        }
    }

    /// <summary>`uv__udp_maybe_deferred_bind`: bind to the wildcard address if not bound yet.</summary>
    public int MaybeDeferredBind(AddressFamily family)
    {
        int r = EnsureSocket(family);
        if (r < 0) return r;
        try
        {
            if (Sock.LocalEndPoint == null)
            {
                if (!OperatingSystem.IsWindows())
                    Sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                Sock.Bind(new IPEndPoint(Sock.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            }
            return 0;
        }
        catch (Exception ex)
        {
            return UvErr.FromException(ex);
        }
    }

    public IPEndPoint AnyEndPoint() =>
        new IPEndPoint(Sock.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

    /// <summary>Result of a receive: (length or -1 if truncated, sender, error).</summary>
    public async Task<(int n, IPEndPoint from, int err)> ReceiveAsync(Socket sock, byte[] buf, int cap, CancellationToken ct)
    {
        try
        {
            // One byte more than requested to detect datagrams that do not fit (UV_UDP_PARTIAL).
            var r = await sock.ReceiveFromAsync(new Memory<byte>(buf, 0, cap + 1), SocketFlags.None, AnyEndPoint(), ct).ConfigureAwait(false);
            int n = r.ReceivedBytes;
            return (n > cap ? -1 : n, r.RemoteEndPoint as IPEndPoint, 0);
        }
        catch (Exception ex)
        {
            int code = UvErr.FromException(ex);
            if (code == UvErr.EMSGSIZE) return (-1, null, 0);
            return (0, null, code);
        }
    }
}

internal static class UvUdpExterns
{
    static UvUdp UvUdpOf(Obj o) => UvUtil.ExternalData<UvUdp>(o);

    /* Std.Internal.UV.UDP.Socket.new : IO Socket */
    public static Obj lean_uv_udp_new()
    {
        var u = new UvUdp();
        Obj obj = UvUtil.AllocExternal(UvUdp.Class, u);
        lean_mark_mt(obj);
        u.Self = obj;
        return lean_io_result_mk_ok(obj);
    }

    /* Std.Internal.UV.UDP.Socket.bind (socket : @& Socket) (addr : @& SocketAddress) : IO Unit */
    public static Obj lean_uv_udp_bind(Obj socket, Obj addr)
    {
        var u = UvUdpOf(socket);
        IPEndPoint ep = UvNet.SocketAddressToNet(addr);
        lock (UvLoop.Lock)
        {
            int r = u.EnsureSocket(ep.AddressFamily);
            if (r < 0) return UvErr.IoError(r);
            if (u.Sock.AddressFamily != ep.AddressFamily || u.Sock.LocalEndPoint != null) return UvErr.IoError(UvErr.EINVAL);
            try
            {
                // UV_UDP_REUSEADDR
                u.Sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                u.Sock.Bind(ep);
            }
            catch (Exception ex)
            {
                return UvErr.IoError(UvErr.FromException(ex));
            }
            return UvUtil.IoOkUnit();
        }
    }

    /* Std.Internal.UV.UDP.Socket.connect (socket : @& Socket) (addr : @& SocketAddress) : IO Unit */
    public static Obj lean_uv_udp_connect(Obj socket, Obj addr)
    {
        var u = UvUdpOf(socket);
        IPEndPoint ep = UvNet.SocketAddressToNet(addr);
        lock (UvLoop.Lock)
        {
            if (u.Connected) return UvErr.IoError(UvErr.EISCONN);
            int r = u.MaybeDeferredBind(ep.AddressFamily);
            if (r < 0) return UvErr.IoError(r);
            try
            {
                if (u.Sock.AddressFamily == AddressFamily.InterNetworkV6 && ep.AddressFamily == AddressFamily.InterNetwork)
                    ep = new IPEndPoint(ep.Address.MapToIPv6(), ep.Port);
                u.Sock.Connect(ep);
                u.Connected = true;
            }
            catch (Exception ex)
            {
                return UvErr.IoError(UvErr.FromException(ex));
            }
            return UvUtil.IoOkUnit();
        }
    }

    /* Std.Internal.UV.UDP.Socket.send (socket : @& Socket) (data : Array ByteArray) (addr : @& Option SocketAddress) : IO (IO.Promise (Except IO.Error Unit)) */
    public static Obj lean_uv_udp_send(Obj socket, Obj data_array, Obj opt_addr)
    {
        var u = UvUdpOf(socket);
        ulong n = lean_array_size(data_array);
        if (n == 0)
        {
            lean_dec(data_array);
            Obj p0 = UvPromise.New();
            UvPromise.ResolveWithCode(0, p0);
            return lean_io_result_mk_ok(p0);
        }
        // uv_udp_send sends all buffers as a single datagram.
        long total = 0;
        for (ulong i = 0; i < n; i++) total += (long)lean_sarray_size(lean_array_get_core(data_array, i));
        if (total > Array.MaxLength) { lean_dec(data_array); return UvErr.IoError(UvErr.EMSGSIZE); }
        var datagram = new byte[total];
        long off = 0;
        for (ulong i = 0; i < n; i++)
        {
            Obj ba = lean_array_get_core(data_array, i);
            int sz = (int)lean_sarray_size(ba);
            Array.Copy(lean_sarray_cptr(ba), 0, datagram, off, sz);
            off += sz;
        }
        IPEndPoint ep = lean_obj_tag(opt_addr) == 1 ? UvNet.SocketAddressToNet(lean_ctor_get(opt_addr, 0)) : null;

        lock (UvLoop.Lock)
        {
            int r;
            if (ep == null)
            {
                if (!u.Connected) { lean_dec(data_array); return UvErr.IoError(UvErr.EDESTADDRREQ); }
                r = u.Closed ? UvErr.EBADF : 0;
            }
            else
            {
                if (u.Connected) { lean_dec(data_array); return UvErr.IoError(UvErr.EISCONN); }
                r = u.MaybeDeferredBind(ep.AddressFamily);
                if (r == 0 && u.Sock.AddressFamily != ep.AddressFamily)
                {
                    if (u.Sock.AddressFamily == AddressFamily.InterNetworkV6 && u.Sock.DualMode)
                        ep = new IPEndPoint(ep.Address.MapToIPv6(), ep.Port);
                    else
                        r = UvErr.EINVAL;
                }
            }
            if (r < 0) { lean_dec(data_array); return UvErr.IoError(r); }

            Obj promise = UvPromise.New();
            lean_mark_mt(data_array);
            lean_inc(promise);
            lean_inc(socket);
            Socket sock = u.Sock;
            Task prev = u.SendChain;
            u.SendChain = Task.Run(async () =>
            {
                try { await prev.ConfigureAwait(false); } catch { }
                int status = 0;
                try
                {
                    if (ep == null) await sock.SendAsync(new ReadOnlyMemory<byte>(datagram), SocketFlags.None).ConfigureAwait(false);
                    else await sock.SendToAsync(new ReadOnlyMemory<byte>(datagram), SocketFlags.None, ep).ConfigureAwait(false);
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
                lean_dec(socket);
                lean_dec(data_array);
            });
            return lean_io_result_mk_ok(promise);
        }
    }

    /* Std.Internal.UV.UDP.Socket.recv (socket : @& Socket) (size : UInt64) : IO (IO.Promise (Except IO.Error (ByteArray × Option SocketAddress))) */
    public static Obj lean_uv_udp_recv(Obj socket, ulong buffer_size)
    {
        var u = UvUdpOf(socket);
        lock (UvLoop.Lock)
        {
            if (u.PromiseRead != null) return UvErr.IoError(UvErr.EALREADY);
            var sizeError = UvTcpExterns.UvRecvSizeError(buffer_size);
            if (sizeError != null) return sizeError;
            if (buffer_size >= (ulong)Array.MaxLength) return UvErr.IoError(UvErr.ENOMEM);
            if (u.Closed) return UvErr.IoError(UvErr.EINVAL);
            int r = u.MaybeDeferredBind(AddressFamily.InterNetwork);
            if (r < 0) return UvErr.IoError(r);

            Obj byteArray = lean_alloc_sarray(1, 0, buffer_size);
            Obj promise = UvPromise.New();
            u.ByteArray = byteArray;
            u.PromiseRead = promise;
            lean_inc(promise);
            lean_inc(socket);
            long gen = ++u.ReadGen;
            int cap = (int)buffer_size;

            if (u.PendingData != null || u.ErrorPending != 0)
            {
                if (u.ErrorPending != 0)
                {
                    int e = u.ErrorPending; u.ErrorPending = 0;
                    CompleteUdpRecv(u, 0, null, e);
                }
                else
                {
                    int len = u.PendingLen;
                    if (len > cap) len = -1;
                    if (len > 0) Array.Copy(u.PendingData, 0, lean_sarray_cptr(byteArray), 0, len);
                    var from = u.PendingFrom;
                    u.PendingData = null; u.PendingFrom = null; u.PendingLen = 0;
                    CompleteUdpRecv(u, len, from, 0);
                }
                return lean_io_result_mk_ok(promise);
            }

            var cts = new CancellationTokenSource();
            u.ReadCts = cts;
            // The receive buffer has one extra byte to detect truncation.
            var buf = new byte[cap + 1];
            Socket sock = u.Sock;
            Task.Run(async () =>
            {
                var (nread, from, err) = await u.ReceiveAsync(sock, buf, cap, cts.Token).ConfigureAwait(false);
                lock (UvLoop.Lock)
                {
                    if (gen != u.ReadGen || !ReferenceEquals(u.PromiseRead, promise))
                    {
                        if (err == 0)
                        {
                            u.PendingData = nread > 0 ? buf[..nread] : Array.Empty<byte>();
                            u.PendingLen = nread;
                            u.PendingFrom = from;
                        }
                        else if (err != UvErr.ECANCELED && err != UvErr.EBADF) u.ErrorPending = err;
                        return;
                    }
                    if (err == 0 && nread > 0) Array.Copy(buf, 0, lean_sarray_cptr(u.ByteArray), 0, nread);
                    CompleteUdpRecv(u, nread, from, err);
                }
            });
            return lean_io_result_mk_ok(promise);
        }
    }

    /// <summary>The receive callback. `nread == -1`: datagram did not fit. Must hold the loop lock.</summary>
    static void CompleteUdpRecv(UvUdp u, int nread, IPEndPoint from, int err)
    {
        Obj promise = u.PromiseRead;
        Obj byteArray = u.ByteArray;
        u.PromiseRead = null;
        u.ByteArray = null;
        u.ReadCts = null;
        if (err != 0)
        {
            lean_dec(byteArray);
            UvPromise.Resolve(UvUtil.ExceptErr(UvErr.Decode(err)), promise);
        }
        else if (nread < 0)
        {
            lean_dec(byteArray);
            UvPromise.Resolve(UvUtil.ExceptErr(UvErr.Decode(UvErr.EMSGSIZE)), promise);
        }
        else
        {
            byteArray = UvTcp.FitReadBuffer(byteArray, nread);
            Obj addrObj = from != null ? lean_mk_option_some(UvNet.NetToSocketAddress(from)) : lean_box(0);
            var prod = lean_alloc_ctor(0, 2, 0);
            lean_ctor_set(prod, 0, byteArray);
            lean_ctor_set(prod, 1, addrObj);
            UvPromise.Resolve(UvUtil.ExceptOk(prod), promise);
        }
        lean_dec(promise);
        lean_dec(u.Self);
    }

    /* Std.Internal.UV.UDP.Socket.waitReadable (socket : @& Socket) : IO (IO.Promise (Except IO.Error Unit)) */
    public static Obj lean_uv_udp_wait_readable(Obj socket)
    {
        var u = UvUdpOf(socket);
        lock (UvLoop.Lock)
        {
            if (u.PromiseRead != null) return UvErr.IoError(UvErr.EALREADY);
            if (u.Closed) return UvErr.IoError(UvErr.EINVAL);
            int r = u.MaybeDeferredBind(AddressFamily.InterNetwork);
            if (r < 0) return UvErr.IoError(r);
            Obj promise = UvPromise.New();
            u.PromiseRead = promise;
            lean_inc(promise);
            lean_inc(socket);
            long gen = ++u.ReadGen;
            if (u.PendingData != null || u.ErrorPending != 0)
            {
                int e = u.ErrorPending; u.ErrorPending = 0;
                CompleteUdpWaitReadable(u, e);
                return lean_io_result_mk_ok(promise);
            }
            var cts = new CancellationTokenSource();
            u.ReadCts = cts;
            Socket sock = u.Sock;
            Task.Run(async () =>
            {
                int err = 0;
                try
                {
                    var one = new byte[1];
                    await sock.ReceiveFromAsync(new Memory<byte>(one), SocketFlags.Peek, u.AnyEndPoint(), cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    err = UvErr.FromException(ex);
                    if (err == UvErr.EMSGSIZE || err == UvErr.ENOBUFS) err = 0;
                }
                lock (UvLoop.Lock)
                {
                    if (gen != u.ReadGen || !ReferenceEquals(u.PromiseRead, promise)) return;
                    CompleteUdpWaitReadable(u, err);
                }
            });
            return lean_io_result_mk_ok(promise);
        }
    }

    static void CompleteUdpWaitReadable(UvUdp u, int err)
    {
        Obj promise = u.PromiseRead;
        u.PromiseRead = null;
        u.ReadCts = null;
        if (err != 0) UvPromise.Resolve(UvUtil.ExceptErr(UvErr.Decode(err)), promise);
        else UvPromise.Resolve(UvUtil.ExceptOk(lean_box(0)), promise);
        lean_dec(promise);
        lean_dec(u.Self);
    }

    /* Std.Internal.UV.UDP.Socket.cancelRecv (socket : @& Socket) : IO Unit */
    public static Obj lean_uv_udp_cancel_recv(Obj socket)
    {
        var u = UvUdpOf(socket);
        Obj promise, byteArray;
        lock (UvLoop.Lock)
        {
            if (u.PromiseRead == null) return UvUtil.IoOkUnit();
            u.ReadGen++;
            try { u.ReadCts?.Cancel(); } catch { }
            u.ReadCts = null;
            promise = u.PromiseRead;
            byteArray = u.ByteArray;
            u.PromiseRead = null;
            u.ByteArray = null;
        }
        lean_dec(promise);
        if (byteArray != null) lean_dec(byteArray);
        lean_dec(socket);
        return UvUtil.IoOkUnit();
    }

    /* Std.Internal.UV.UDP.Socket.getPeerName (socket : @& Socket) : IO SocketAddress */
    public static Obj lean_uv_udp_getpeername(Obj socket)
    {
        var u = UvUdpOf(socket);
        lock (UvLoop.Lock)
        {
            if (u.Sock == null || u.Closed) return UvErr.IoError(UvErr.EBADF);
            if (!u.Connected) return UvErr.IoError(UvErr.ENOTCONN);
            try
            {
                if (u.Sock.RemoteEndPoint is IPEndPoint ep) return lean_io_result_mk_ok(UvNet.NetToSocketAddress(ep));
                return UvErr.IoError(UvErr.ENOTCONN);
            }
            catch (Exception ex) { return UvErr.IoError(UvErr.FromException(ex)); }
        }
    }

    /* Std.Internal.UV.UDP.Socket.getSockName (socket : @& Socket) : IO SocketAddress */
    public static Obj lean_uv_udp_getsockname(Obj socket)
    {
        var u = UvUdpOf(socket);
        lock (UvLoop.Lock)
        {
            if (u.Sock == null || u.Closed) return UvErr.IoError(UvErr.EBADF);
            try
            {
                if (u.Sock.LocalEndPoint is IPEndPoint ep) return lean_io_result_mk_ok(UvNet.NetToSocketAddress(ep));
                return lean_io_result_mk_ok(UvNet.NetToSocketAddress(u.AnyEndPoint()));
            }
            catch (Exception ex) { return UvErr.IoError(UvErr.FromException(ex)); }
        }
    }

    static Obj UvUdpSetOpt(Obj socket, Action<Socket> set)
    {
        var u = UvUdpOf(socket);
        lock (UvLoop.Lock)
        {
            if (u.Sock == null || u.Closed) return UvErr.IoError(UvErr.EBADF);
            try { set(u.Sock); }
            catch (Exception ex) { return UvErr.IoError(UvErr.FromException(ex)); }
            return UvUtil.IoOkUnit();
        }
    }

    static SocketOptionLevel UvIpLevel(Socket s) =>
        s.AddressFamily == AddressFamily.InterNetworkV6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP;

    /* Std.Internal.UV.UDP.Socket.setBroadcast (socket : @& Socket) (on : Bool) : IO Unit */
    public static Obj lean_uv_udp_set_broadcast(Obj socket, byte enable) =>
        UvUdpSetOpt(socket, s => s.EnableBroadcast = enable != 0);

    /* Std.Internal.UV.UDP.Socket.setMulticastLoop (socket : @& Socket) (on : Bool) : IO Unit */
    public static Obj lean_uv_udp_set_multicast_loop(Obj socket, byte enable) =>
        UvUdpSetOpt(socket, s => s.SetSocketOption(UvIpLevel(s), SocketOptionName.MulticastLoopback, enable != 0));

    /* Std.Internal.UV.UDP.Socket.setMulticastTTL (socket : @& Socket) (ttl : UInt32) : IO Unit */
    public static Obj lean_uv_udp_set_multicast_ttl(Obj socket, uint ttl)
    {
        if (ttl > 255) return UvErr.IoError(UvErr.EINVAL);
        return UvUdpSetOpt(socket, s =>
        {
            if (s.AddressFamily == AddressFamily.InterNetworkV6)
                s.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, (int)ttl);
            else
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, (int)ttl);
        });
    }

    /* Std.Internal.UV.UDP.Socket.setMembership (socket : @& Socket) (multicastAddr : @& IPAddr) (interfaceAddr : @& Option IPAddr) (membership : UInt8) : IO Unit */
    public static Obj lean_uv_udp_set_membership(Obj socket, Obj multicast_addr, Obj interface_addr, byte membership)
    {
        var u = UvUdpOf(socket);
        IPAddress group = UvNet.IPAddrToNet(multicast_addr);
        IPAddress iface = lean_is_scalar(interface_addr) ? null : UvNet.IPAddrToNet(lean_ctor_get(interface_addr, 0));
        if (membership > 1) return UvErr.IoError(UvErr.EINVAL);
        lock (UvLoop.Lock)
        {
            int r = u.MaybeDeferredBind(group.AddressFamily);
            if (r < 0) return UvErr.IoError(r);
            try
            {
                var name = membership == 1 ? SocketOptionName.AddMembership : SocketOptionName.DropMembership;
                if (group.AddressFamily == AddressFamily.InterNetwork)
                {
                    if (u.Sock.AddressFamily != AddressFamily.InterNetwork) return UvErr.IoError(UvErr.EINVAL);
                    var opt = iface != null ? new MulticastOption(group, iface) : new MulticastOption(group);
                    u.Sock.SetSocketOption(SocketOptionLevel.IP, name, opt);
                }
                else
                {
                    if (u.Sock.AddressFamily != AddressFamily.InterNetworkV6) return UvErr.IoError(UvErr.EINVAL);
                    long ifIndex = iface != null ? UvIPv6InterfaceIndex(iface) : 0;
                    u.Sock.SetSocketOption(SocketOptionLevel.IPv6, name, new IPv6MulticastOption(group, ifIndex));
                }
            }
            catch (Exception ex)
            {
                return UvErr.IoError(UvErr.FromException(ex));
            }
            return UvUtil.IoOkUnit();
        }
    }

    /// <summary>Index of the network interface that has the given IPv6 address (0 if unknown).</summary>
    static long UvIPv6InterfaceIndex(IPAddress addr)
    {
        if (addr.ScopeId != 0) return addr.ScopeId;
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = ni.GetIPProperties();
                foreach (var ua in props.UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                        new IPAddress(ua.Address.GetAddressBytes()).Equals(addr))
                        return props.GetIPv6Properties().Index;
            }
        }
        catch { }
        return 0;
    }

    /* Std.Internal.UV.UDP.Socket.setMulticastInterface (socket : @& Socket) (interfaceAddr : @& IPAddr) : IO Unit */
    public static Obj lean_uv_udp_set_multicast_interface(Obj socket, Obj interface_addr)
    {
        IPAddress iface = UvNet.IPAddrToNet(interface_addr);
        return UvUdpSetOpt(socket, s =>
        {
            if (iface.AddressFamily == AddressFamily.InterNetwork)
            {
                if (s.AddressFamily != AddressFamily.InterNetwork) throw new SocketException((int)SocketError.InvalidArgument);
                // IP_MULTICAST_IF takes the address in network byte order.
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, BitConverter.ToInt32(iface.GetAddressBytes(), 0));
            }
            else
            {
                if (s.AddressFamily != AddressFamily.InterNetworkV6) throw new SocketException((int)SocketError.InvalidArgument);
                s.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, (int)UvIPv6InterfaceIndex(iface));
            }
        });
    }

    /* Std.Internal.UV.UDP.Socket.setTTL (socket : @& Socket) (ttl : UInt32) : IO Unit */
    public static Obj lean_uv_udp_set_ttl(Obj socket, uint ttl)
    {
        if (ttl < 1 || ttl > 255) return UvErr.IoError(UvErr.EINVAL);
        return UvUdpSetOpt(socket, s => s.SetSocketOption(UvIpLevel(s), SocketOptionName.IpTimeToLive, (int)ttl));
    }
}

/// <summary>Extern entry points (the implementation is in `UvUdpExterns`, outside the unsafe context, so that it can use async code).</summary>
public static unsafe partial class LeanRt
{
    public static Obj lean_uv_udp_new() => UvUdpExterns.lean_uv_udp_new();
    public static Obj lean_uv_udp_bind(Obj socket, Obj addr) => UvUdpExterns.lean_uv_udp_bind(socket, addr);
    public static Obj lean_uv_udp_connect(Obj socket, Obj addr) => UvUdpExterns.lean_uv_udp_connect(socket, addr);
    public static Obj lean_uv_udp_send(Obj socket, Obj data_array, Obj opt_addr) => UvUdpExterns.lean_uv_udp_send(socket, data_array, opt_addr);
    public static Obj lean_uv_udp_recv(Obj socket, ulong buffer_size) => UvUdpExterns.lean_uv_udp_recv(socket, buffer_size);
    public static Obj lean_uv_udp_wait_readable(Obj socket) => UvUdpExterns.lean_uv_udp_wait_readable(socket);
    public static Obj lean_uv_udp_cancel_recv(Obj socket) => UvUdpExterns.lean_uv_udp_cancel_recv(socket);
    public static Obj lean_uv_udp_getpeername(Obj socket) => UvUdpExterns.lean_uv_udp_getpeername(socket);
    public static Obj lean_uv_udp_getsockname(Obj socket) => UvUdpExterns.lean_uv_udp_getsockname(socket);
    public static Obj lean_uv_udp_set_broadcast(Obj socket, byte enable) => UvUdpExterns.lean_uv_udp_set_broadcast(socket, enable);
    public static Obj lean_uv_udp_set_multicast_loop(Obj socket, byte enable) => UvUdpExterns.lean_uv_udp_set_multicast_loop(socket, enable);
    public static Obj lean_uv_udp_set_multicast_ttl(Obj socket, uint ttl) => UvUdpExterns.lean_uv_udp_set_multicast_ttl(socket, ttl);
    public static Obj lean_uv_udp_set_membership(Obj socket, Obj multicast_addr, Obj interface_addr, byte membership) => UvUdpExterns.lean_uv_udp_set_membership(socket, multicast_addr, interface_addr, membership);
    public static Obj lean_uv_udp_set_multicast_interface(Obj socket, Obj interface_addr) => UvUdpExterns.lean_uv_udp_set_multicast_interface(socket, interface_addr);
    public static Obj lean_uv_udp_set_ttl(Obj socket, uint ttl) => UvUdpExterns.lean_uv_udp_set_ttl(socket, ttl);
}
