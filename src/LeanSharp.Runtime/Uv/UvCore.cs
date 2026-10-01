// Shared infrastructure of the LeanSharp port of `src/runtime/uv/*` (the libuv based parts of the
// Lean runtime): the "event loop" lock, the promise abstraction, libuv-style error codes and their
// translation to `IO.Error`, and small object helpers.
//
// There is no libuv here. Asynchronous operations are implemented with .NET primitives
// (System.Net.Sockets async APIs, System.Threading.Timer, PosixSignalRegistration, ...), whose
// callbacks run on thread pool threads. To keep the state machines of the C code intact, every
// operation and every completion callback runs under the single global lock `UvLoop.Lock` (the
// counterpart of `event_loop_lock(&global_ev)`; like the C mutex it is recursive).

using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using static LeanSharp.Runtime.LeanRt;

namespace LeanSharp.Runtime;

/// <summary>Counterpart of `global_ev` (event_loop.cpp).</summary>
internal static class UvLoop
{
    /// <summary>Protects all uv handle state (recursive, like `uv_mutex_init_recursive`).</summary>
    public static readonly object Lock = new();

    // Handles the loop references while they are active (a running .NET timer or signal
    // registration is otherwise only reachable from the handle itself and could be collected).
    static readonly HashSet<object> s_active = new(ReferenceEqualityComparer.Instance);

    /// <summary>Keep `h` reachable while it is active. Must hold `Lock`.</summary>
    public static void Root(object h) => s_active.Add(h);

    /// <summary>Must hold `Lock`.</summary>
    public static void Unroot(object h) => s_active.Remove(h);
}

/// <summary>Single place through which the uv code creates and resolves `IO.Promise`s.</summary>
internal static unsafe class UvPromise
{
    /// <summary>`lean_promise_new()` followed by `mark_mt` (the loop resolves it from another thread).</summary>
    public static Obj New()
    {
        Obj p = lean_io_promise_new();
        lean_mark_mt(p);
        return p;
    }

    /// <summary>`lean_promise_resolve(value, promise)`: takes `value`, borrows `promise`.</summary>
    public static void Resolve(Obj value, Obj promise) => lean_dec(lean_io_promise_resolve(value, promise));

    /// <summary>`promise_is_resolved(p)` (object.h).</summary>
    public static bool IsResolved(Obj promise) =>
        lean_io_get_task_state(Unsafe.As<PromiseObj>(promise).m_result) == 2;

    /// <summary>`lean_promise_resolve_with_code(status, promise)` (event_loop.cpp).</summary>
    public static void ResolveWithCode(int status, Obj promise)
    {
        Obj res = status == 0 ? UvUtil.ExceptOk(lean_box(0)) : UvUtil.ExceptErr(UvErr.Decode(status));
        Resolve(res, promise);
    }
}

/// <summary>libuv error codes (values of libuv on Linux) and their translation to `IO.Error`.</summary>
internal static unsafe class UvErr
{
    public const int E2BIG = -7, EACCES = -13, EADDRINUSE = -98, EADDRNOTAVAIL = -99, EAFNOSUPPORT = -97,
        EAGAIN = -11, EALREADY = -114, EBADF = -9, EBUSY = -16, ECANCELED = -125, ECHARSET = -4080,
        ECONNABORTED = -103, ECONNREFUSED = -111, ECONNRESET = -104, EDESTADDRREQ = -89, EEXIST = -17,
        EFAULT = -14, EFBIG = -27, EHOSTUNREACH = -113, EINTR = -4, EINVAL = -22, EIO = -5, EISCONN = -106,
        EISDIR = -21, ELOOP = -40, EMFILE = -24, EMSGSIZE = -90, ENAMETOOLONG = -36, ENETDOWN = -100,
        ENETUNREACH = -101, ENFILE = -23, ENOBUFS = -105, ENODEV = -19, ENOENT = -2, ENOMEM = -12,
        ENOPROTOOPT = -92, ENOSPC = -28, ENOSYS = -38, ENOTCONN = -107, ENOTDIR = -20, ENOTEMPTY = -39,
        ENOTSOCK = -88, ENOTSUP = -95, EOVERFLOW = -75, EPERM = -1, EPIPE = -32, EPROTO = -71,
        EPROTONOSUPPORT = -93, EPROTOTYPE = -91, ERANGE = -34, EROFS = -30, ESHUTDOWN = -108, ESPIPE = -29,
        ESRCH = -3, ETIMEDOUT = -110, ETXTBSY = -26, EXDEV = -18, UNKNOWN = -4094, EOF = -4095, ENXIO = -6,
        EMLINK = -31, EHOSTDOWN = -112, ENOTTY = -25, EILSEQ = -84, ESOCKTNOSUPPORT = -94, ENODATA = -61,
        ENOEXEC = -8,
        EAI_ADDRFAMILY = -3000, EAI_AGAIN = -3001, EAI_BADFLAGS = -3002, EAI_CANCELED = -3003, EAI_FAIL = -3004,
        EAI_FAMILY = -3005, EAI_MEMORY = -3006, EAI_NODATA = -3007, EAI_NONAME = -3008, EAI_OVERFLOW = -3009,
        EAI_SERVICE = -3010, EAI_SOCKTYPE = -3011, EAI_BADHINTS = -3013, EAI_PROTOCOL = -3014;

    // (libuv code, errno on Darwin/BSD, uv_strerror message)
    static readonly Dictionary<int, (int mac, string msg)> s_info = new()
    {
        [E2BIG] = (7, "argument list too long"),
        [EACCES] = (13, "permission denied"),
        [EADDRINUSE] = (48, "address already in use"),
        [EADDRNOTAVAIL] = (49, "address not available"),
        [EAFNOSUPPORT] = (47, "address family not supported"),
        [EAGAIN] = (35, "resource temporarily unavailable"),
        [EALREADY] = (37, "connection already in progress"),
        [EBADF] = (9, "bad file descriptor"),
        [EBUSY] = (16, "resource busy or locked"),
        [ECANCELED] = (89, "operation canceled"),
        [ECHARSET] = (4080, "invalid Unicode character"),
        [ECONNABORTED] = (53, "software caused connection abort"),
        [ECONNREFUSED] = (61, "connection refused"),
        [ECONNRESET] = (54, "connection reset by peer"),
        [EDESTADDRREQ] = (39, "destination address required"),
        [EEXIST] = (17, "file already exists"),
        [EFAULT] = (14, "bad address in system call argument"),
        [EFBIG] = (27, "file too large"),
        [EHOSTUNREACH] = (65, "host is unreachable"),
        [EINTR] = (4, "interrupted system call"),
        [EINVAL] = (22, "invalid argument"),
        [EIO] = (5, "i/o error"),
        [EISCONN] = (56, "socket is already connected"),
        [EISDIR] = (21, "illegal operation on a directory"),
        [ELOOP] = (62, "too many symbolic links encountered"),
        [EMFILE] = (24, "too many open files"),
        [EMSGSIZE] = (40, "message too long"),
        [ENAMETOOLONG] = (63, "name too long"),
        [ENETDOWN] = (50, "network is down"),
        [ENETUNREACH] = (51, "network is unreachable"),
        [ENFILE] = (23, "file table overflow"),
        [ENOBUFS] = (55, "no buffer space available"),
        [ENODEV] = (19, "no such device"),
        [ENOENT] = (2, "no such file or directory"),
        [ENOMEM] = (12, "not enough memory"),
        [ENOPROTOOPT] = (42, "protocol not available"),
        [ENOSPC] = (28, "no space left on device"),
        [ENOSYS] = (78, "function not implemented"),
        [ENOTCONN] = (57, "socket is not connected"),
        [ENOTDIR] = (20, "not a directory"),
        [ENOTEMPTY] = (66, "directory not empty"),
        [ENOTSOCK] = (38, "socket operation on non-socket"),
        [ENOTSUP] = (45, "operation not supported on socket"),
        [EOVERFLOW] = (84, "value too large for defined data type"),
        [EPERM] = (1, "operation not permitted"),
        [EPIPE] = (32, "broken pipe"),
        [EPROTO] = (100, "protocol error"),
        [EPROTONOSUPPORT] = (43, "protocol not supported"),
        [EPROTOTYPE] = (41, "protocol wrong type for socket"),
        [ERANGE] = (34, "result too large"),
        [EROFS] = (30, "read-only file system"),
        [ESHUTDOWN] = (58, "cannot send after transport endpoint shutdown"),
        [ESPIPE] = (29, "invalid seek"),
        [ESRCH] = (3, "no such process"),
        [ETIMEDOUT] = (60, "connection timed out"),
        [ETXTBSY] = (26, "text file is busy"),
        [EXDEV] = (18, "cross-device link not permitted"),
        [UNKNOWN] = (4094, "unknown error"),
        [EOF] = (4095, "end of file"),
        [ENXIO] = (6, "no such device or address"),
        [EMLINK] = (31, "too many links"),
        [EHOSTDOWN] = (64, "host is down"),
        [ENOTTY] = (25, "inappropriate ioctl for device"),
        [EILSEQ] = (92, "illegal byte sequence"),
        [ESOCKTNOSUPPORT] = (44, "socket type not supported"),
        [ENODATA] = (96, "no data available"),
        [ENOEXEC] = (8, "exec format error"),
        [EAI_ADDRFAMILY] = (3000, "address family not supported"),
        [EAI_AGAIN] = (3001, "temporary failure"),
        [EAI_BADFLAGS] = (3002, "bad ai_flags value"),
        [EAI_CANCELED] = (3003, "request canceled"),
        [EAI_FAIL] = (3004, "permanent failure"),
        [EAI_FAMILY] = (3005, "ai_family not supported"),
        [EAI_MEMORY] = (3006, "out of memory"),
        [EAI_NODATA] = (3007, "no address"),
        [EAI_NONAME] = (3008, "unknown node or service"),
        [EAI_OVERFLOW] = (3009, "argument buffer overflow"),
        [EAI_SERVICE] = (3010, "service not available for socket type"),
        [EAI_SOCKTYPE] = (3011, "socket type not supported"),
        [EAI_BADHINTS] = (3013, "invalid value for hints"),
        [EAI_PROTOCOL] = (3014, "resolved protocol is unknown"),
    };

    static readonly bool s_bsdErrno = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsFreeBSD()
        || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst();

    /// <summary>`uv_strerror`.</summary>
    public static string StrError(int code) =>
        s_info.TryGetValue(code, out var i) ? i.msg : $"Unknown system error {code}";

    /// <summary>The `errno` value libuv would report on the host (`-code` on Linux).</summary>
    public static uint PosixErrno(int code)
    {
        if (s_bsdErrno && s_info.TryGetValue(code, out var i)) return (uint)i.mac;
        return (uint)(-code);
    }

    // ------------------------------------------------------------------
    // IO.Error constructors (exported from Lean, `Init/System/IOError.lean`)

    static delegate*<uint, Obj, Obj> s_otherError, s_invalidArgument, s_permissionDenied, s_resourceExhausted,
        s_inappropriateType, s_noSuchThing, s_alreadyExists, s_hardwareFault, s_unsatisfiedConstraints,
        s_illegalOperation, s_resourceVanished, s_protocolError, s_timeExpired, s_resourceBusy, s_unsupportedOperation;
    static delegate*<Obj, uint, Obj, Obj> s_interrupted, s_invalidArgumentFile, s_noFileOrDirectory,
        s_permissionDeniedFile, s_resourceExhaustedFile, s_inappropriateTypeFile, s_noSuchThingFile, s_alreadyExistsFile;

    static delegate*<uint, Obj, Obj> F1(ref delegate*<uint, Obj, Obj> cell, string name)
    {
        if (cell == null) cell = (delegate*<uint, Obj, Obj>)LeanExports.Get(name);
        return cell;
    }

    static delegate*<Obj, uint, Obj, Obj> F2(ref delegate*<Obj, uint, Obj, Obj> cell, string name)
    {
        if (cell == null) cell = (delegate*<Obj, uint, Obj, Obj>)LeanExports.Get(name);
        return cell;
    }

    public static Obj MkOtherError(uint errno, string msg) => F1(ref s_otherError, "lean_mk_io_error_other_error")(errno, lean_mk_string(msg));
    public static Obj MkInvalidArgument(uint errno, string msg) => F1(ref s_invalidArgument, "lean_mk_io_error_invalid_argument")(errno, lean_mk_string(msg));
    public static Obj MkInvalidArgumentFile(Obj fname, uint errno, string msg) => F2(ref s_invalidArgumentFile, "lean_mk_io_error_invalid_argument_file")(fname, errno, lean_mk_string(msg));
    public static Obj MkUnsupported(string msg) =>
        F1(ref s_unsupportedOperation, "lean_mk_io_error_unsupported_operation")(PosixErrno(ENOTSUP), lean_mk_string(msg));

    /// <summary>IO error result for features that LeanSharp cannot provide.</summary>
    public static Obj NotSupported(string what) =>
        lean_io_result_mk_error(MkUnsupported(what + " is not supported by LeanSharp"));

    /// <summary>`lean_decode_uv_error(errnum, fname)` (io.cpp). `fname` is borrowed (may be null).</summary>
    public static Obj Decode(int errnum, Obj fname = null)
    {
        uint posix = PosixErrno(errnum);
        Obj details = lean_mk_string(StrError(errnum));
        Obj F() { lean_inc(fname); return fname; }
        switch (errnum)
        {
            case EINTR:
                return F2(ref s_interrupted, "lean_mk_io_error_interrupted")(fname != null ? F() : lean_mk_string(""), posix, details);
            case ELOOP: case ENAMETOOLONG: case EDESTADDRREQ: case EBADF: case EINVAL: case EILSEQ:
            case ENOTCONN: case ENOTSOCK: case ENOEXEC:
                return fname == null ? F1(ref s_invalidArgument, "lean_mk_io_error_invalid_argument")(posix, details)
                    : F2(ref s_invalidArgumentFile, "lean_mk_io_error_invalid_argument_file")(F(), posix, details);
            case ENOENT:
                return F2(ref s_noFileOrDirectory, "lean_mk_io_error_no_file_or_directory")(fname != null ? F() : lean_mk_string(""), posix, details);
            case EACCES: case EROFS: case ECONNABORTED: case EFBIG: case EPERM:
                return fname == null ? F1(ref s_permissionDenied, "lean_mk_io_error_permission_denied")(posix, details)
                    : F2(ref s_permissionDeniedFile, "lean_mk_io_error_permission_denied_file")(F(), posix, details);
            case EMFILE: case ENFILE: case ENOSPC: case E2BIG: case EAGAIN: case EMLINK: case EMSGSIZE:
            case ENOBUFS: case ENOMEM:
                return fname == null ? F1(ref s_resourceExhausted, "lean_mk_io_error_resource_exhausted")(posix, details)
                    : F2(ref s_resourceExhaustedFile, "lean_mk_io_error_resource_exhausted_file")(F(), posix, details);
            case EISDIR: case ENOTDIR:
                return fname == null ? F1(ref s_inappropriateType, "lean_mk_io_error_inappropriate_type")(posix, details)
                    : F2(ref s_inappropriateTypeFile, "lean_mk_io_error_inappropriate_type_file")(F(), posix, details);
            case ENXIO: case EHOSTUNREACH: case ENETUNREACH: case ECONNREFUSED: case ENODATA: case ESRCH:
                return fname == null ? F1(ref s_noSuchThing, "lean_mk_io_error_no_such_thing")(posix, details)
                    : F2(ref s_noSuchThingFile, "lean_mk_io_error_no_such_thing_file")(F(), posix, details);
            case EEXIST: case EISCONN:
                return fname == null ? F1(ref s_alreadyExists, "lean_mk_io_error_already_exists")(posix, details)
                    : F2(ref s_alreadyExistsFile, "lean_mk_io_error_already_exists_file")(F(), posix, details);
            case EIO:
                return F1(ref s_hardwareFault, "lean_mk_io_error_hardware_fault")(posix, details);
            case ENOTEMPTY:
                return F1(ref s_unsatisfiedConstraints, "lean_mk_io_error_unsatisfied_constraints")(posix, details);
            case ENOTTY:
                return F1(ref s_illegalOperation, "lean_mk_io_error_illegal_operation")(posix, details);
            case ECONNRESET: case ENETDOWN: case EPIPE:
                return F1(ref s_resourceVanished, "lean_mk_io_error_resource_vanished")(posix, details);
            case EPROTO: case EPROTONOSUPPORT: case EPROTOTYPE:
                return F1(ref s_protocolError, "lean_mk_io_error_protocol_error")(posix, details);
            case ETIMEDOUT:
                return F1(ref s_timeExpired, "lean_mk_io_error_time_expired")(posix, details);
            case EADDRINUSE: case EBUSY: case ETXTBSY:
                return F1(ref s_resourceBusy, "lean_mk_io_error_resource_busy")(posix, details);
            case EADDRNOTAVAIL: case EAFNOSUPPORT: case ENODEV: case ENOPROTOOPT: case ENOSYS: case ENOTSUP:
            case ERANGE: case ESPIPE: case EXDEV:
                return F1(ref s_unsupportedOperation, "lean_mk_io_error_unsupported_operation")(posix, details);
            default:
                return F1(ref s_otherError, "lean_mk_io_error_other_error")(posix, details);
        }
    }

    /// <summary>`lean_io_result_mk_error(lean_decode_uv_error(code, fname))`.</summary>
    public static Obj IoError(int code, Obj fname = null) => lean_io_result_mk_error(Decode(code, fname));

    /// <summary>`decode_io_error(ENOMEM, nullptr)` style helper for out-of-memory conditions.</summary>
    public static Obj IoErrorNoMem() => IoError(ENOMEM);

    /// <summary>Map a .NET socket error to the libuv code.</summary>
    public static int FromSocketError(SocketError e) => e switch
    {
        SocketError.Success => 0,
        SocketError.AccessDenied => EACCES,
        SocketError.AddressAlreadyInUse => EADDRINUSE,
        SocketError.AddressFamilyNotSupported => EAFNOSUPPORT,
        SocketError.AddressNotAvailable => EADDRNOTAVAIL,
        SocketError.AlreadyInProgress => EALREADY,
        SocketError.ConnectionAborted => ECONNABORTED,
        SocketError.ConnectionRefused => ECONNREFUSED,
        SocketError.ConnectionReset => ECONNRESET,
        SocketError.DestinationAddressRequired => EDESTADDRREQ,
        SocketError.Disconnecting => ESHUTDOWN,
        SocketError.Fault => EFAULT,
        SocketError.HostDown => EHOSTDOWN,
        SocketError.HostNotFound => EAI_NONAME,
        SocketError.HostUnreachable => EHOSTUNREACH,
        SocketError.InProgress => EALREADY,
        SocketError.Interrupted => EINTR,
        SocketError.InvalidArgument => EINVAL,
        SocketError.IsConnected => EISCONN,
        SocketError.MessageSize => EMSGSIZE,
        SocketError.NetworkDown => ENETDOWN,
        SocketError.NetworkReset => ECONNRESET,
        SocketError.NetworkUnreachable => ENETUNREACH,
        SocketError.NoBufferSpaceAvailable => ENOBUFS,
        SocketError.NoData => EAI_NODATA,
        SocketError.NoRecovery => EAI_FAIL,
        SocketError.NotConnected => ENOTCONN,
        SocketError.NotInitialized => EINVAL,
        SocketError.NotSocket => ENOTSOCK,
        SocketError.OperationAborted => ECANCELED,
        SocketError.OperationNotSupported => ENOTSUP,
        SocketError.ProcessLimit => EMFILE,
        SocketError.ProtocolFamilyNotSupported => EAFNOSUPPORT,
        SocketError.ProtocolNotSupported => EPROTONOSUPPORT,
        SocketError.ProtocolOption => ENOPROTOOPT,
        SocketError.ProtocolType => EPROTOTYPE,
        SocketError.Shutdown => EPIPE,
        SocketError.SocketNotSupported => ESOCKTNOSUPPORT,
        SocketError.SystemNotReady => ENETDOWN,
        SocketError.TimedOut => ETIMEDOUT,
        SocketError.TooManyOpenSockets => EMFILE,
        SocketError.TryAgain => EAI_AGAIN,
        SocketError.TypeNotFound => EAI_SERVICE,
        SocketError.VersionNotSupported => ENOTSUP,
        SocketError.WouldBlock => EAGAIN,
        _ => UNKNOWN,
    };

    /// <summary>Map an exception thrown by a .NET API to a libuv code.</summary>
    public static int FromException(Exception ex)
    {
        while (ex is AggregateException ae && ae.InnerException != null) ex = ae.InnerException;
        switch (ex)
        {
            case SocketException se: return FromSocketError(se.SocketErrorCode);
            case ObjectDisposedException: return EBADF;
            case OperationCanceledException: return ECANCELED;
            case OutOfMemoryException: return ENOMEM;
            case UnauthorizedAccessException: return EACCES;
            case DirectoryNotFoundException: return ENOENT;
            case FileNotFoundException: return ENOENT;
            case PathTooLongException: return ENAMETOOLONG;
            case ArgumentException: return EINVAL;
            case PlatformNotSupportedException: return ENOTSUP;
            case InvalidOperationException: return EINVAL;
            case NotSupportedException: return ENOTSUP;
            case IOException: return EIO;
            default: return UNKNOWN;
        }
    }
}

/// <summary>Small object helpers used by the uv code.</summary>
internal static unsafe class UvUtil
{
    /// <summary>`lean_box_uint64`.</summary>
    public static Obj BoxU64(ulong v)
    {
        var r = lean_alloc_ctor(0, 0, 8);
        lean_ctor_set_uint64_s(r, 0, v);
        return r;
    }

    public static ulong UnboxU64(Obj o) => lean_ctor_get_uint64_s(o, 0);

    /// <summary>`mk_except_ok` (`Except.ok` has tag 1).</summary>
    public static Obj ExceptOk(Obj v)
    {
        var r = lean_alloc_ctor(1, 1, 0);
        lean_ctor_set(r, 0, v);
        return r;
    }

    /// <summary>`mk_except_err` (`Except.error` has tag 0).</summary>
    public static Obj ExceptErr(Obj e)
    {
        var r = lean_alloc_ctor(0, 1, 0);
        lean_ctor_set(r, 0, e);
        return r;
    }

    public static Obj IoOkUnit() => lean_io_result_mk_ok(lean_box(0));

    /// <summary>`lean_alloc_external(cls, data)`.</summary>
    public static Obj AllocExternal(ExternalClass cls, object data) =>
        new ExternalObj { m_tag = LeanExternal, m_class = cls, m_data = data };

    public static T ExternalData<T>(Obj o) where T : class => (T)Unsafe.As<ExternalObj>(o).m_data;

    /// <summary>Apply the `foreach` closure `f` of an external class to a child (as the C code does).</summary>
    public static void ForeachChild(Obj f, Obj child)
    {
        if (child == null) return;
        lean_inc(f);
        lean_inc(child);
        lean_dec(lean_apply_1(f, child));
    }

    /// <summary>True if the Lean string contains a NUL byte (`strlen(s) != size - 1`).</summary>
    public static bool HasNul(Obj s) => lean_string_span(s).IndexOf((byte)0) >= 0;

    /// <summary>`mk_embedded_nul_error(str)` (io.cpp).</summary>
    public static Obj EmbeddedNulError(Obj str)
    {
        lean_inc(str);
        return lean_io_result_mk_error(UvErr.MkInvalidArgumentFile(str, UvErr.PosixErrno(UvErr.EINVAL), "string contains NUL bytes"));
    }

    /// <summary>Run a helper command and return its trimmed stdout (null on failure).</summary>
    public static string RunCommand(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public static string TryReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}

public static unsafe partial class LeanRt
{
    /* Std.Internal.UV.Loop.configure (options : Loop.Options) : BaseIO Unit */
    public static Obj lean_uv_event_loop_configure(Obj options)
    {
        // `accumulateIdleTime` and `blockSigProfSignal` only affect libuv's internal polling; there
        // is nothing to configure in the managed implementation.
        _ = lean_ctor_get_uint8_s(options, 0);
        _ = lean_ctor_get_uint8_s(options, 1);
        lean_dec(options);
        return lean_box(0);
    }

    /* Std.Internal.UV.Loop.alive : BaseIO Bool */
    public static byte lean_uv_event_loop_alive()
    {
        // The libuv loop always has its `uv_async_t` handle, so `uv_loop_alive` is always true.
        return 1;
    }
}
