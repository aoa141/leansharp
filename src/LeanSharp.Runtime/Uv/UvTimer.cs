// Port of `runtime/uv/timer.cpp` on top of System.Threading.Timer.

using static LeanSharp.Runtime.LeanRt;

namespace LeanSharp.Runtime;

internal sealed class UvTimer
{
    public const int StateInitial = 0, StateRunning = 1, StateFinished = 2;

    public Obj Self;              // the Lean object (`handle->data`)
    public Obj Promise;           // m_promise
    public ulong Timeout;         // m_timeout (ms)
    public bool Repeating;        // m_repeating
    public int State;             // m_state
    public System.Threading.Timer Timer; // the running .NET timer (null when stopped)
    public long Generation;       // incremented on every start/stop, to drop stale callbacks

    public static readonly ExternalClass Class = new ExternalClass(Finalize, Foreach);

    static void Finalize(object data)
    {
        var t = (UvTimer)data;
        Obj promise;
        lock (UvLoop.Lock)
        {
            promise = t.Promise;
            t.Promise = null;
            t.StopTimer();
        }
        if (promise != null) lean_dec(promise);
    }

    static void Foreach(object data, Obj f) => UvUtil.ForeachChild(f, ((UvTimer)data).Promise);

    // Longest due time accepted by System.Threading.Timer.
    const long MaxDue = 0xfffffffeL;

    static long Clamp(ulong ms) => ms > (ulong)MaxDue ? MaxDue : (long)ms;

    /// <summary>`uv_timer_start(timer, cb, timeout, repeat)`. Must hold the loop lock.</summary>
    public void StartTimer(ulong timeout, ulong repeat)
    {
        StopTimer();
        long gen = ++Generation;
        long period = repeat == 0 ? System.Threading.Timeout.Infinite : Math.Max(1L, Clamp(repeat));
        Timer = new System.Threading.Timer(_ => OnFire(this, gen), null, Clamp(timeout), period);
        UvLoop.Root(this);
    }

    /// <summary>`uv_timer_stop`. Must hold the loop lock.</summary>
    public void StopTimer()
    {
        Generation++;
        Timer?.Dispose();
        Timer = null;
        UvLoop.Unroot(this);
    }

    /// <summary>`handle_timer_event`.</summary>
    static void OnFire(UvTimer timer, long gen)
    {
        Obj toResolve = null;
        Obj toRelease = null;
        lock (UvLoop.Lock)
        {
            if (gen != timer.Generation || timer.State != StateRunning) return;
            if (timer.Repeating)
            {
                if (timer.Promise != null && !UvPromise.IsResolved(timer.Promise))
                {
                    toResolve = timer.Promise;
                    lean_inc(toResolve);
                }
            }
            else
            {
                if (timer.Promise != null)
                {
                    toResolve = timer.Promise;
                    lean_inc(toResolve);
                }
                timer.StopTimer();
                timer.State = StateFinished;
                // The loop does not need to keep the timer alive anymore.
                toRelease = timer.Self;
            }
            if (toResolve != null) UvPromise.Resolve(lean_box(0), toResolve);
        }
        if (toResolve != null) lean_dec(toResolve);
        if (toRelease != null) lean_dec(toRelease);
    }
}

public static unsafe partial class LeanRt
{
    /* Std.Internal.UV.Timer.mk (timeout : UInt64) (repeating : Bool) : IO Timer */
    public static Obj lean_uv_timer_mk(ulong timeout, byte repeating)
    {
        var timer = new UvTimer { Timeout = timeout, Repeating = repeating != 0, State = UvTimer.StateInitial };
        Obj obj = UvUtil.AllocExternal(UvTimer.Class, timer);
        lean_mark_mt(obj);
        timer.Self = obj;
        return lean_io_result_mk_ok(obj);
    }

    /* Std.Internal.UV.Timer.next (timer : @& Timer) : IO (IO.Promise Unit) */
    public static Obj lean_uv_timer_next(Obj obj)
    {
        var timer = UvUtil.ExternalData<UvTimer>(obj);
        lock (UvLoop.Lock)
        {
            if (timer.State == UvTimer.StateInitial)
            {
                // setup_timer
                Obj promise = UvPromise.New();
                timer.Promise = promise;
                timer.State = UvTimer.StateRunning;
                // The event loop must keep the timer alive for the duration of the run time.
                lean_inc(obj);
                lean_inc(promise);
                timer.StartTimer(timer.Repeating ? 0 : timer.Timeout, timer.Repeating ? timer.Timeout : 0);
                return lean_io_result_mk_ok(promise);
            }
            if (timer.Repeating && timer.State == UvTimer.StateRunning)
            {
                if (timer.Promise == null || UvPromise.IsResolved(timer.Promise))
                {
                    if (timer.Promise != null) lean_dec(timer.Promise);
                    timer.Promise = UvPromise.New();
                }
                lean_inc(timer.Promise);
                return lean_io_result_mk_ok(timer.Promise);
            }
            if (timer.Promise != null)
            {
                lean_inc(timer.Promise);
                return lean_io_result_mk_ok(timer.Promise);
            }
            // A promise that is never resolved by the timer.
            return lean_io_result_mk_ok(UvPromise.New());
        }
    }

    /* Std.Internal.UV.Timer.reset (timer : @& Timer) : IO Unit */
    public static Obj lean_uv_timer_reset(Obj obj)
    {
        var timer = UvUtil.ExternalData<UvTimer>(obj);
        lock (UvLoop.Lock)
        {
            if (timer.State == UvTimer.StateRunning)
                timer.StartTimer(timer.Timeout, timer.Repeating ? timer.Timeout : 0);
        }
        return lean_io_result_mk_ok(lean_box(0));
    }

    /* Std.Internal.UV.Timer.stop (timer : @& Timer) : IO Unit */
    public static Obj lean_uv_timer_stop(Obj obj)
    {
        var timer = UvUtil.ExternalData<UvTimer>(obj);
        Obj promise;
        lock (UvLoop.Lock)
        {
            if (timer.State != UvTimer.StateRunning) return lean_io_result_mk_ok(lean_box(0));
            timer.StopTimer();
            promise = timer.Promise;
            timer.Promise = null;
            timer.State = UvTimer.StateFinished;
        }
        // Dropping the last reference resolves the promise's task with `none` (outside the lock).
        if (promise != null) lean_dec(promise);
        // The loop does not need to keep the timer alive anymore.
        lean_dec(obj);
        return lean_io_result_mk_ok(lean_box(0));
    }

    /* Std.Internal.UV.Timer.cancel (timer : @& Timer) : IO Unit */
    public static Obj lean_uv_timer_cancel(Obj obj)
    {
        var timer = UvUtil.ExternalData<UvTimer>(obj);
        Obj promise = null;
        bool release = false;
        lock (UvLoop.Lock)
        {
            if (timer.State == UvTimer.StateRunning && timer.Promise != null)
            {
                promise = timer.Promise;
                timer.Promise = null;
                if (!timer.Repeating)
                {
                    timer.StopTimer();
                    timer.State = UvTimer.StateInitial;
                    release = true;
                }
            }
        }
        if (promise != null) lean_dec(promise);
        if (release) lean_dec(obj);
        return lean_io_result_mk_ok(lean_box(0));
    }
}
