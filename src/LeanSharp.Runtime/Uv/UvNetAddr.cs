// Port of `runtime/uv/net_addr.cpp`: conversions between `Std.Net` address values and .NET
// addresses, `inet_pton`/`inet_ntop` (ported from libuv's `inet.c`), and interface addresses.
//
// Lean representations:
//   IPv4Addr        = Array UInt8 (4 boxed octets)      (single-field structure over a Vector)
//   IPv6Addr        = Array UInt16 (8 boxed segments)
//   IPAddr          = v4 (tag 0) / v6 (tag 1), one object field
//   SocketAddressV4/6 = ctor 0 { addr : obj; port : UInt16 (scalar) }
//   SocketAddress   = v4 (tag 0) / v6 (tag 1), one object field (SocketAddressV4/6)

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using static LeanSharp.Runtime.LeanRt;

namespace LeanSharp.Runtime;

internal static class UvNet
{
    // ------------------------------------------------------------------
    // Lean → bytes

    /// <summary>`lean_ipv4_addr_to_in_addr` (4 bytes, network order).</summary>
    public static byte[] IPv4ToBytes(Obj ipv4)
    {
        var r = new byte[4];
        for (uint i = 0; i < 4; i++) r[i] = (byte)lean_unbox(lean_array_get_core(ipv4, i));
        return r;
    }

    /// <summary>`lean_ipv6_addr_to_in6_addr` (16 bytes, network order).</summary>
    public static byte[] IPv6ToBytes(Obj ipv6)
    {
        var r = new byte[16];
        for (uint i = 0; i < 8; i++)
        {
            ushort seg = (ushort)lean_unbox(lean_array_get_core(ipv6, i));
            r[2 * i] = (byte)(seg >> 8);
            r[2 * i + 1] = (byte)seg;
        }
        return r;
    }

    /// <summary>`IPAddr` (borrowed) → .NET address.</summary>
    public static IPAddress IPAddrToNet(Obj ipAddr)
    {
        Obj a = lean_ctor_get(ipAddr, 0);
        return lean_ptr_tag(ipAddr) == 0 ? new IPAddress(IPv4ToBytes(a)) : new IPAddress(IPv6ToBytes(a));
    }

    /// <summary>`lean_socket_address_to_sockaddr_storage` (`SocketAddress` borrowed).</summary>
    public static IPEndPoint SocketAddressToNet(Obj sa)
    {
        Obj inner = lean_ctor_get(sa, 0);
        Obj ip = lean_ctor_get(inner, 0);
        ushort port = lean_ctor_get_uint16_s(inner, 0);
        IPAddress addr = lean_ptr_tag(sa) == 0 ? new IPAddress(IPv4ToBytes(ip)) : new IPAddress(IPv6ToBytes(ip));
        return new IPEndPoint(addr, port);
    }

    // ------------------------------------------------------------------
    // bytes → Lean

    /// <summary>`lean_in_addr_to_ipv4_addr`.</summary>
    public static Obj BytesToIPv4(ReadOnlySpan<byte> b)
    {
        var arr = lean_alloc_array(4, 4);
        for (uint i = 0; i < 4; i++) lean_array_set_core(arr, i, lean_box(b[(int)i]));
        return arr;
    }

    /// <summary>`lean_in6_addr_to_ipv6_addr`.</summary>
    public static Obj BytesToIPv6(ReadOnlySpan<byte> b)
    {
        var arr = lean_alloc_array(8, 8);
        for (uint i = 0; i < 8; i++) lean_array_set_core(arr, i, lean_box((ulong)((b[(int)(2 * i)] << 8) | b[(int)(2 * i + 1)])));
        return arr;
    }

    /// <summary>`lean_in_addr_storage_to_ip_addr`.</summary>
    public static Obj NetToIPAddr(IPAddress a)
    {
        bool v6 = a.AddressFamily == AddressFamily.InterNetworkV6;
        var bytes = a.GetAddressBytes();
        var r = lean_alloc_ctor(v6 ? 1u : 0u, 1, 0);
        lean_ctor_set(r, 0, v6 ? BytesToIPv6(bytes) : BytesToIPv4(bytes));
        return r;
    }

    /// <summary>`lean_sockaddr_to_socketaddress`.</summary>
    public static Obj NetToSocketAddress(IPEndPoint ep)
    {
        bool v6 = ep.AddressFamily == AddressFamily.InterNetworkV6;
        var bytes = ep.Address.GetAddressBytes();
        var inner = lean_alloc_ctor(0, 1, 2);
        lean_ctor_set(inner, 0, v6 ? BytesToIPv6(bytes) : BytesToIPv4(bytes));
        lean_ctor_set_uint16_s(inner, 0, (ushort)ep.Port);
        var r = lean_alloc_ctor(v6 ? 1u : 0u, 1, 0);
        lean_ctor_set(r, 0, inner);
        return r;
    }

    // ------------------------------------------------------------------
    // inet_pton / inet_ntop (libuv src/inet.c)

    static int Pton4(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        Span<byte> tmp = stackalloc byte[4];
        tmp.Clear();
        bool sawDigit = false;
        int octets = 0, tp = 0;
        foreach (byte ch in src)
        {
            if (ch >= (byte)'0' && ch <= (byte)'9')
            {
                uint nw = (uint)tmp[tp] * 10 + (uint)(ch - '0');
                if (sawDigit && tmp[tp] == 0) return UvErr.EINVAL;
                if (nw > 255) return UvErr.EINVAL;
                tmp[tp] = (byte)nw;
                if (!sawDigit)
                {
                    if (++octets > 4) return UvErr.EINVAL;
                    sawDigit = true;
                }
            }
            else if (ch == (byte)'.' && sawDigit)
            {
                if (octets == 4) return UvErr.EINVAL;
                tmp[++tp] = 0;
                sawDigit = false;
            }
            else return UvErr.EINVAL;
        }
        if (octets < 4) return UvErr.EINVAL;
        tmp.CopyTo(dst);
        return 0;
    }

    static int HexVal(byte ch)
    {
        if (ch >= '0' && ch <= '9') return ch - '0';
        if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
        if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
        return -1;
    }

    static byte At(ReadOnlySpan<byte> s, int i) => i < s.Length ? s[i] : (byte)0;

    static int Pton6(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        Span<byte> tmp = stackalloc byte[16];
        tmp.Clear();
        int tp = 0, endp = 16, colonp = -1;
        int pos = 0;
        if (At(src, pos) == ':')
            if (At(src, ++pos) != ':') return UvErr.EINVAL;
        int curtok = pos;
        int seenXdigits = 0;
        uint val = 0;
        while (true)
        {
            byte ch = At(src, pos++);
            if (ch == 0) break;
            int hv = HexVal(ch);
            if (hv >= 0)
            {
                val <<= 4;
                val |= (uint)hv;
                if (++seenXdigits > 4) return UvErr.EINVAL;
                continue;
            }
            if (ch == ':')
            {
                curtok = pos;
                if (seenXdigits == 0)
                {
                    if (colonp >= 0) return UvErr.EINVAL;
                    colonp = tp;
                    continue;
                }
                else if (At(src, pos) == 0)
                {
                    return UvErr.EINVAL;
                }
                if (tp + 2 > endp) return UvErr.EINVAL;
                tmp[tp++] = (byte)((val >> 8) & 0xff);
                tmp[tp++] = (byte)(val & 0xff);
                seenXdigits = 0;
                val = 0;
                continue;
            }
            if (ch == '.' && tp + 4 <= endp)
            {
                if (Pton4(src.Slice(curtok), tmp.Slice(tp, 4)) == 0)
                {
                    tp += 4;
                    seenXdigits = 0;
                    break;
                }
            }
            return UvErr.EINVAL;
        }
        if (seenXdigits != 0)
        {
            if (tp + 2 > endp) return UvErr.EINVAL;
            tmp[tp++] = (byte)((val >> 8) & 0xff);
            tmp[tp++] = (byte)(val & 0xff);
        }
        if (colonp >= 0)
        {
            int n = tp - colonp;
            if (tp == endp) return UvErr.EINVAL;
            for (int i = 1; i <= n; i++)
            {
                tmp[endp - i] = tmp[colonp + n - i];
                tmp[colonp + n - i] = 0;
            }
            tp = endp;
        }
        if (tp != endp) return UvErr.EINVAL;
        tmp.CopyTo(dst);
        return 0;
    }

    /// <summary>`uv_inet_pton(AF_INET, ...)`.</summary>
    public static bool TryPton4(ReadOnlySpan<byte> s, Span<byte> dst) => Pton4(s, dst) == 0;

    /// <summary>`uv_inet_pton(AF_INET6, ...)` (a `%zone` suffix is ignored).</summary>
    public static bool TryPton6(ReadOnlySpan<byte> s, Span<byte> dst)
    {
        int p = s.IndexOf((byte)'%');
        if (p >= 0)
        {
            if (p > 46 - 1) return false;
            s = s.Slice(0, p);
        }
        return Pton6(s, dst) == 0;
    }

    public static string Ntop4(ReadOnlySpan<byte> b) => $"{b[0]}.{b[1]}.{b[2]}.{b[3]}";

    public static string Ntop6(ReadOnlySpan<byte> src)
    {
        Span<uint> words = stackalloc uint[8];
        words.Clear();
        for (int i = 0; i < 16; i++) words[i / 2] |= (uint)src[i] << ((1 - (i % 2)) << 3);
        int bestBase = -1, bestLen = 0, curBase = -1, curLen = 0;
        for (int i = 0; i < 8; i++)
        {
            if (words[i] == 0)
            {
                if (curBase == -1) { curBase = i; curLen = 1; }
                else curLen++;
            }
            else if (curBase != -1)
            {
                if (bestBase == -1 || curLen > bestLen) { bestBase = curBase; bestLen = curLen; }
                curBase = -1;
            }
        }
        if (curBase != -1)
        {
            if (bestBase == -1 || curLen > bestLen) { bestBase = curBase; bestLen = curLen; }
        }
        if (bestBase != -1 && bestLen < 2) bestBase = -1;
        var sb = new StringBuilder();
        for (int i = 0; i < 8; i++)
        {
            if (bestBase != -1 && i >= bestBase && i < bestBase + bestLen)
            {
                if (i == bestBase) sb.Append(':');
                continue;
            }
            if (i != 0) sb.Append(':');
            if (i == 6 && bestBase == 0 && (bestLen == 6 || (bestLen == 7 && words[7] != 0x0001) || (bestLen == 5 && words[5] == 0xffff)))
            {
                sb.Append(Ntop4(src.Slice(12)));
                goto done;
            }
            sb.Append(words[i].ToString("x"));
        }
        if (bestBase != -1 && bestBase + bestLen == 8) sb.Append(':');
    done:
        return sb.ToString();
    }

    public static IPAddress PrefixMask(int prefix, int nbytes)
    {
        var b = new byte[nbytes];
        for (int i = 0; i < nbytes * 8 && i < prefix; i++) b[i / 8] |= (byte)(0x80 >> (i % 8));
        return new IPAddress(b);
    }

    /// <summary>`lean_ip_addr_ntop` (borrowed `IPAddr`).</summary>
    public static string IPAddrNtop(Obj ipAddr)
    {
        Obj a = lean_ctor_get(ipAddr, 0);
        return lean_ptr_tag(ipAddr) == 0 ? Ntop4(IPv4ToBytes(a)) : Ntop6(IPv6ToBytes(a));
    }

    /// <summary>Textual form of a .NET address as `uv_inet_ntop` prints it.</summary>
    public static string NetNtop(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetworkV6 ? Ntop6(a.GetAddressBytes()) : Ntop4(a.GetAddressBytes());
}

public static unsafe partial class LeanRt
{
    /* Std.Net.IPV4Addr.ofString (s : @&String) : Option IPV4Addr */
    public static Obj lean_uv_pton_v4(Obj str_obj)
    {
        var s = lean_string_span(str_obj);
        if (s.IndexOf((byte)0) >= 0) return lean_box(0);
        Span<byte> b = stackalloc byte[4];
        if (UvNet.TryPton4(s, b)) return lean_mk_option_some(UvNet.BytesToIPv4(b));
        return lean_box(0);
    }

    /* Std.Net.IPV4Addr.toString (addr : @&IPV4Addr) : String */
    public static Obj lean_uv_ntop_v4(Obj ipv4_addr) => lean_mk_string(UvNet.Ntop4(UvNet.IPv4ToBytes(ipv4_addr)));

    /* Std.Net.IPV6Addr.ofString (s : @&String) : Option IPV6Addr */
    public static Obj lean_uv_pton_v6(Obj str_obj)
    {
        var s = lean_string_span(str_obj);
        if (s.IndexOf((byte)0) >= 0) return lean_box(0);
        Span<byte> b = stackalloc byte[16];
        if (UvNet.TryPton6(s, b)) return lean_mk_option_some(UvNet.BytesToIPv6(b));
        return lean_box(0);
    }

    /* Std.Net.IPV6Addr.toString (addr : @&IPV6Addr) : String */
    public static Obj lean_uv_ntop_v6(Obj ipv6_addr) => lean_mk_string(UvNet.Ntop6(UvNet.IPv6ToBytes(ipv6_addr)));

    /* Std.Net.interfaceAddresses : IO (Array InterfaceAddress) */
    public static Obj lean_uv_interface_addresses()
    {
        NetworkInterface[] ifaces;
        try
        {
            ifaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return lean_io_result_mk_error(UvErr.MkInvalidArgument(UvErr.PosixErrno(UvErr.EINVAL), "failed to get interface addresses"));
        }
        var list = new List<Obj>();
        foreach (var ni in ifaces)
        {
            bool loopback = ni.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            // libuv only reports interfaces that are up and running.
            if (!loopback && ni.OperationalStatus != OperationalStatus.Up) continue;
            IPInterfaceProperties props;
            byte[] mac;
            try
            {
                props = ni.GetIPProperties();
                mac = ni.GetPhysicalAddress().GetAddressBytes();
            }
            catch { continue; }
            foreach (var ua in props.UnicastAddresses)
            {
                var addr = ua.Address;
                IPAddress mask;
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    try { mask = ua.IPv4Mask; } catch { mask = null; }
                    if (mask == null || mask.Equals(IPAddress.Any)) mask = UvNet.PrefixMask(ua.PrefixLength, 4);
                }
                else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    mask = UvNet.PrefixMask(ua.PrefixLength, 16);
                }
                else continue;
                var iface = lean_alloc_ctor(0, 4, 1);
                lean_ctor_set(iface, 0, lean_mk_string(ni.Name));
                var macArr = lean_alloc_array(6, 6);
                for (uint i = 0; i < 6; i++) lean_array_set_core(macArr, i, lean_box(i < mac.Length ? mac[i] : 0u));
                lean_ctor_set(iface, 1, macArr);
                lean_ctor_set_uint8_s(iface, 0, loopback ? (byte)1 : (byte)0);
                lean_ctor_set(iface, 2, UvNet.NetToIPAddr(addr));
                lean_ctor_set(iface, 3, UvNet.NetToIPAddr(mask));
                list.Add(iface);
            }
        }
        return lean_io_result_mk_ok(MkArray(list));
    }
}
