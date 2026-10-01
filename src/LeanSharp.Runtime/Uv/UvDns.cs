// Port of `runtime/uv/dns.cpp` on top of System.Net.Dns.

using System.Net;
using System.Net.Sockets;
using static LeanSharp.Runtime.LeanRt;

namespace LeanSharp.Runtime;

internal static class UvDns
{
    public static bool IsSafeAsciiStr(ReadOnlySpan<byte> s)
    {
        foreach (byte c in s)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                  c == '-' || c == '_' || c == '.' || c == ':' || c == '/' || c == '+' ||
                  c == '~' || c == '@' || c == '=' || c == ',' || c == '%'))
                return false;
        }
        return true;
    }

    static Dictionary<string, int> s_servicesByName;
    static Dictionary<int, string> s_servicesByPort;
    static readonly object s_servicesLock = new();

    /// <summary>Parse `/etc/services` (TCP entries; UDP entries as fallback).</summary>
    static void LoadServices()
    {
        lock (s_servicesLock)
        {
            if (s_servicesByName != null) return;
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            var byPort = new Dictionary<int, string>();
            var byPortUdp = new Dictionary<int, string>();
            string text = OperatingSystem.IsWindows()
                ? UvUtil.TryReadFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "services"))
                : UvUtil.TryReadFile("/etc/services");
            if (text != null)
            {
                foreach (var rawLine in text.Split('\n'))
                {
                    var line = rawLine;
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line.Substring(0, hash);
                    var parts = line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    var pp = parts[1].Split('/');
                    if (pp.Length != 2 || !int.TryParse(pp[0], out int port)) continue;
                    bool tcp = pp[1] == "tcp";
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (i == 1) continue;
                        byName.TryAdd(parts[i], port);
                    }
                    if (tcp) byPort.TryAdd(port, parts[0]);
                    else byPortUdp.TryAdd(port, parts[0]);
                }
            }
            foreach (var kv in byPortUdp) byPort.TryAdd(kv.Key, kv.Value);
            s_servicesByPort = byPort;
            s_servicesByName = byName;
        }
    }

    /// <summary>Whether `getaddrinfo` would accept the service string.</summary>
    public static bool ServiceKnown(string service)
    {
        if (service.Length == 0) return true;
        if (service.All(char.IsAsciiDigit)) return true;
        LoadServices();
        return s_servicesByName.ContainsKey(service);
    }

    /// <summary>Service name for a port as `getnameinfo` (without NI_NUMERICSERV) reports it.</summary>
    public static string ServiceName(int port)
    {
        LoadServices();
        return s_servicesByPort.TryGetValue(port, out var n) ? n : port.ToString();
    }

    public static int DnsError(Exception ex)
    {
        while (ex is AggregateException ae && ae.InnerException != null) ex = ae.InnerException;
        if (ex is SocketException se)
        {
            switch (se.SocketErrorCode)
            {
                case SocketError.HostNotFound: return UvErr.EAI_NONAME;
                case SocketError.TryAgain: return UvErr.EAI_AGAIN;
                case SocketError.NoData: return UvErr.EAI_NODATA;
                case SocketError.NoRecovery: return UvErr.EAI_FAIL;
                case SocketError.AddressFamilyNotSupported: return UvErr.EAI_FAMILY;
                default: return UvErr.FromSocketError(se.SocketErrorCode);
            }
        }
        if (ex is ArgumentException) return UvErr.EAI_NONAME;
        return UvErr.FromException(ex);
    }
}

internal static class UvDnsExterns
{
    // Std.Internal.UV.DNS.getAddrInfo (host service : @& String) (family : UInt8) : IO (IO.Promise (Except IO.Error (Array IPAddr)))
    public static Obj lean_uv_dns_get_info(Obj name, Obj service, byte family)
    {
        if (!UvDns.IsSafeAsciiStr(lean_string_span(name)))
            return lean_io_result_mk_error(UvErr.MkInvalidArgument(UvErr.PosixErrno(UvErr.EINVAL), "name is not ASCII"));
        if (!UvDns.IsSafeAsciiStr(lean_string_span(service)))
            return lean_io_result_mk_error(UvErr.MkInvalidArgument(UvErr.PosixErrno(UvErr.EINVAL), "service is not ASCII"));

        string host = lean_string_to_net(name);
        string serv = lean_string_to_net(service);
        AddressFamily af = family switch
        {
            1 => AddressFamily.InterNetwork,
            2 => AddressFamily.InterNetworkV6,
            _ => AddressFamily.Unspecified,
        };
        Obj promise = UvPromise.New();
        lean_inc(promise);
        Task.Run(async () =>
        {
            Obj result;
            try
            {
                if (host.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                if (!UvDns.ServiceKnown(serv)) throw new SocketException((int)SocketError.TypeNotFound);
                IPAddress[] addrs = await Dns.GetHostAddressesAsync(host, af).ConfigureAwait(false);
                var list = new List<Obj>();
                foreach (var a in addrs)
                {
                    if (a.AddressFamily != AddressFamily.InterNetwork && a.AddressFamily != AddressFamily.InterNetworkV6) continue;
                    if (af != AddressFamily.Unspecified && a.AddressFamily != af) continue;
                    list.Add(UvNet.NetToIPAddr(a));
                }
                if (list.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
                result = UvUtil.ExceptOk(MkArray(list));
            }
            catch (Exception ex)
            {
                int code = UvDns.DnsError(ex);
                result = UvUtil.ExceptErr(UvErr.Decode(code));
            }
            lock (UvLoop.Lock) UvPromise.Resolve(result, promise);
            lean_dec(promise);
        });
        return lean_io_result_mk_ok(promise);
    }

    // Std.Internal.UV.DNS.getNameInfo (host : @& SocketAddress) : IO (IO.Promise (Except IO.Error (String × String)))
    public static Obj lean_uv_dns_get_name(Obj addr)
    {
        IPEndPoint ep = UvNet.SocketAddressToNet(addr);
        Obj promise = UvPromise.New();
        lean_inc(promise);
        Task.Run(async () =>
        {
            string host;
            try
            {
                var entry = await Dns.GetHostEntryAsync(ep.Address).ConfigureAwait(false);
                host = string.IsNullOrEmpty(entry.HostName) ? UvNet.NetNtop(ep.Address) : entry.HostName;
            }
            catch
            {
                // Without NI_NAMEREQD, getnameinfo falls back to the numeric form of the address.
                host = UvNet.NetNtop(ep.Address);
            }
            string serv = UvDns.ServiceName(ep.Port);
            var r = lean_alloc_ctor(0, 2, 0);
            lean_ctor_set(r, 0, lean_mk_string(host));
            lean_ctor_set(r, 1, lean_mk_string(serv));
            lock (UvLoop.Lock) UvPromise.Resolve(UvUtil.ExceptOk(r), promise);
            lean_dec(promise);
        });
        return lean_io_result_mk_ok(promise);
    }
}

/// <summary>Extern entry points (the implementation is in `UvDnsExterns`, outside the unsafe context, so that it can use async code).</summary>
public static unsafe partial class LeanRt
{
    public static Obj lean_uv_dns_get_info(Obj name, Obj service, byte family) => UvDnsExterns.lean_uv_dns_get_info(name, service, family);
    public static Obj lean_uv_dns_get_name(Obj addr) => UvDnsExterns.lean_uv_dns_get_name(addr);
}
