// Port of `runtime/uv/signal.cpp` on top of System.Runtime.InteropServices.PosixSignalRegistration.
//
// As with libuv, a registered handler replaces the default action of the signal while it is
// active (the handler cancels the default processing).

using System.Runtime.InteropServices;
using static LeanSharp.Runtime.LeanRt;

namespace LeanSharp.Runtime;

internal sealed class UvSignal
{
    public const int StateInitial = 0, StateRunning = 1, StateFinished = 2;

    public Obj Self;
    public Obj Promise;
    public int Signum;            // native signal number (0 = unsupported)
    public bool Repeating;
    public int State;
    public PosixSignalRegistration Registration;
    public long Generation;

    public static readonly ExternalClass Class = new ExternalClass(Finalize, Foreach);

    static void Finalize(object data)
    {
        var s = (UvSignal)data;
        Obj promise;
        lock (UvLoop.Lock)
        {
            promise = s.Promise;
            s.Promise = null;
            s.StopHandler();
        }
        if (promise != null) lean_dec(promise);
    }

    static void Foreach(object data, Obj f) => UvUtil.ForeachChild(f, ((UvSignal)data).Promise);

    static readonly bool s_bsd = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsFreeBSD()
        || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst();

    /// <summary>Translate the Lean signal number (see `toInt32` in `Std.Async.Signal`) to the native one.</summary>
    public static int ToNative(int signum)
    {
        bool win = OperatingSystem.IsWindows();
        switch (signum)
        {
            case 1: return 1;   // SIGHUP
            case 2: return 2;   // SIGINT
            case 3: return 3;   // SIGQUIT
            case 6: return win ? 22 : 6; // SIGABRT
            case 15: return 15; // SIGTERM
            case 28: return 28; // SIGWINCH
        }
        if (win) return 0;
        switch (signum)
        {
            case 5: return 5;                  // SIGTRAP
            case 10: return s_bsd ? 30 : 10;   // SIGUSR1
            case 12: return s_bsd ? 31 : 12;   // SIGUSR2
            case 14: return 14;                // SIGALRM
            case 17: return s_bsd ? 20 : 17;   // SIGCHLD
            case 18: return s_bsd ? 19 : 18;   // SIGCONT
            case 20: return s_bsd ? 18 : 20;   // SIGTSTP
            case 21: return 21;                // SIGTTIN
            case 22: return 22;                // SIGTTOU
            case 23: return s_bsd ? 16 : 23;   // SIGURG
            case 24: return 24;                // SIGXCPU
            case 25: return 25;                // SIGXFSZ
            case 26: return 26;                // SIGVTALRM
            case 27: return 27;                // SIGPROF
            case 29: return s_bsd ? 23 : 29;   // SIGIO
            case 31: return s_bsd ? 12 : 31;   // SIGSYS
            default: return 0;
        }
    }

    PosixSignal ToPosixSignal()
    {
        int n = Signum;
        if (n == 1) return PosixSignal.SIGHUP;
        if (n == 2) return PosixSignal.SIGINT;
        if (n == 3) return PosixSignal.SIGQUIT;
        if (n == 15) return PosixSignal.SIGTERM;
        if (n == 28) return PosixSignal.SIGWINCH;
        if (n == 21) return PosixSignal.SIGTTIN;
        if (n == 22) return PosixSignal.SIGTTOU;
        if (n == (s_bsd ? 20 : 17)) return PosixSignal.SIGCHLD;
        if (n == (s_bsd ? 19 : 18)) return PosixSignal.SIGCONT;
        if (n == (s_bsd ? 18 : 20)) return PosixSignal.SIGTSTP;
        return (PosixSignal)n; // raw signal number (supported on Unix)
    }

    /// <summary>`uv_signal_start` / `uv_signal_start_oneshot`. Must hold the loop lock. Returns a libuv code.</summary>
    public int StartHandler()
    {
        if (Signum <= 0) return UvErr.EINVAL;
        StopHandler();
        long gen = ++Generation;
        try
        {
            Registration = PosixSignalRegistration.Create(ToPosixSignal(), ctx =>
            {
                ctx.Cancel = true;
                OnSignal(this, gen);
            });
            UvLoop.Root(this);
            return 0;
        }
        catch (Exception ex)
        {
            Registration = null;
            int code = UvErr.FromException(ex);
            return code == UvErr.UNKNOWN ? UvErr.EINVAL : code;
        }
    }

    public void StopHandler()
    {
        Generation++;
        var r = Registration;
        Registration = null;
        UvLoop.Unroot(this);
        try { r?.Dispose(); } catch { }
    }

    /// <summary>`handle_signal_event`.</summary>
    static void OnSignal(UvSignal s, long gen)
    {
        Obj toResolve = null, toRelease = null;
        lock (UvLoop.Lock)
        {
            if (gen != s.Generation || s.State != StateRunning) return;
            if (s.Repeating)
            {
                if (s.Promise != null && !UvPromise.IsResolved(s.Promise)) { toResolve = s.Promise; lean_inc(toResolve); }
            }
            else
            {
                if (s.Promise != null) { toResolve = s.Promise; lean_inc(toResolve); }
                s.StopHandler();
                s.State = StateFinished;
                toRelease = s.Self;
            }
            if (toResolve != null) UvPromise.Resolve(lean_box((ulong)s.Signum), toResolve);
        }
        if (toResolve != null) lean_dec(toResolve);
        if (toRelease != null) lean_dec(toRelease);
    }
}

public static unsafe partial class LeanRt
{
    /* Std.Internal.UV.Signal.mk (signum : Int32) (repeating : Bool) : IO Signal */
    public static Obj lean_uv_signal_mk(uint signum_obj, byte repeating)
    {
        var s = new UvSignal
        {
            Signum = UvSignal.ToNative((int)signum_obj),
            Repeating = repeating != 0,
            State = UvSignal.StateInitial,
        };
        Obj obj = UvUtil.AllocExternal(UvSignal.Class, s);
        lean_mark_mt(obj);
        s.Self = obj;
        return lean_io_result_mk_ok(obj);
    }

    /* Std.Internal.UV.Signal.next (signal : @& Signal) : IO (IO.Promise Int) */
    public static Obj lean_uv_signal_next(Obj obj)
    {
        var s = UvUtil.ExternalData<UvSignal>(obj);
        lock (UvLoop.Lock)
        {
            if (s.State == UvSignal.StateInitial)
            {
                Obj promise = UvPromise.New();
                s.Promise = promise;
                s.State = UvSignal.StateRunning;
                lean_inc(obj);
                lean_inc(promise);
                int result = s.StartHandler();
                if (result != 0)
                {
                    s.State = UvSignal.StateInitial;
                    s.Promise = null;
                    lean_dec(promise);
                    lean_dec(promise);
                    lean_dec(obj);
                    return UvErr.IoError(result);
                }
                return lean_io_result_mk_ok(promise);
            }
            if (s.Repeating && s.State == UvSignal.StateRunning)
            {
                if (s.Promise == null || UvPromise.IsResolved(s.Promise))
                {
                    if (s.Promise != null) lean_dec(s.Promise);
                    s.Promise = UvPromise.New();
                }
                lean_inc(s.Promise);
                return lean_io_result_mk_ok(s.Promise);
            }
            if (s.Promise != null)
            {
                lean_inc(s.Promise);
                return lean_io_result_mk_ok(s.Promise);
            }
            return lean_io_result_mk_ok(UvPromise.New());
        }
    }

    /* Std.Internal.UV.Signal.stop (signal : @& Signal) : IO Unit */
    public static Obj lean_uv_signal_stop(Obj obj)
    {
        var s = UvUtil.ExternalData<UvSignal>(obj);
        Obj promise;
        lock (UvLoop.Lock)
        {
            if (s.State != UvSignal.StateRunning) return lean_io_result_mk_ok(lean_box(0));
            s.StopHandler();
            promise = s.Promise;
            s.Promise = null;
            s.State = UvSignal.StateFinished;
        }
        if (promise != null) lean_dec(promise);
        lean_dec(obj);
        return lean_io_result_mk_ok(lean_box(0));
    }

    /* Std.Internal.UV.Signal.cancel (signal : @& Signal) : IO Unit */
    public static Obj lean_uv_signal_cancel(Obj obj)
    {
        var s = UvUtil.ExternalData<UvSignal>(obj);
        Obj promise = null;
        bool release = false;
        lock (UvLoop.Lock)
        {
            if (s.State == UvSignal.StateRunning && s.Promise != null)
            {
                promise = s.Promise;
                s.Promise = null;
                if (!s.Repeating)
                {
                    s.StopHandler();
                    s.State = UvSignal.StateInitial;
                    release = true;
                }
            }
        }
        if (promise != null) lean_dec(promise);
        if (release) lean_dec(obj);
        return lean_io_result_mk_ok(lean_box(0));
    }
}
