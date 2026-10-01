// Checks for the tasks area: thunks, tasks, promises, cancellation, mutexes, condition variables
// and ST refs.

using System.Diagnostics;
using LeanSharp.Runtime;
using static LeanSharp.Runtime.LeanRt;

unsafe static class Program
{
    static int s_failures;
    static int s_checks;

    static void Check(bool cond, string what)
    {
        s_checks++;
        if (cond) Console.WriteLine("ok   " + what);
        else { s_failures++; Console.WriteLine("FAIL " + what); }
    }

    static Obj Nat(ulong n) => lean_box(n);
    static ulong N(Obj o) => lean_unbox(o);

    // ---------------------------------------------------------------- closures

    static Obj Closure1(delegate*<Obj, Obj> f) => lean_alloc_closure(f, 1, 0);

    static Obj Closure2_1(delegate*<Obj, Obj, Obj> f, Obj a)
    {
        var c = lean_alloc_closure(f, 2, 1);
        lean_closure_set(c, 0, a);
        return c;
    }

    static Obj ConstFn(Obj v, Obj unit) => v;
    static Obj Const(Obj v) => Closure2_1(&ConstFn, v);

    static Obj Add1(Obj x) => Nat(N(x) + 1);

    static Obj SpawnAdd1(Obj x) => lean_task_spawn(Const(Nat(N(x) + 1)), Nat(0));

    static int s_thunkCount;
    static Obj ThunkFn(Obj unit)
    {
        Interlocked.Increment(ref s_thunkCount);
        Thread.Sleep(50);
        return Nat(42);
    }

    static Obj FibTask(Obj n, Obj unit)
    {
        ulong k = N(n);
        if (k < 2) return n;
        Obj t1 = lean_task_spawn(Closure2_1(&FibTask, Nat(k - 1)), Nat(0));
        Obj t2 = lean_task_spawn(Closure2_1(&FibTask, Nat(k - 2)), Nat(0));
        ulong a = N(lean_task_get_own(t1));
        ulong b = N(lean_task_get_own(t2));
        return Nat(a + b);
    }

    static ulong Fib(ulong n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

    // waits for a promise result task (fixed arg) and returns the value inside `some`
    static Obj WaitPromiseFn(Obj resultTask, Obj unit)
    {
        Obj opt = lean_task_get_own(resultTask);
        if (lean_is_scalar(opt)) return Nat(0);
        Obj v = lean_ctor_get(opt, 0);
        lean_inc(v); lean_dec(opt);
        return v;
    }

    static Obj ResolveFn(Obj promise, Obj unit)
    {
        Thread.Sleep(20);
        lean_io_promise_resolve(Nat(7), promise);
        lean_dec(promise);
        return Nat(1);
    }

    static Obj LoopUntilCanceled(Obj unit)
    {
        var sw = Stopwatch.StartNew();
        while (lean_io_check_canceled() == 0)
        {
            if (sw.ElapsedMilliseconds > 10000) return Nat(0);
            Thread.Sleep(1);
        }
        return Nat(1);
    }

    static volatile int s_sawCancel;
    static Obj LoopUntilCanceledFlag(Obj unit)
    {
        var sw = Stopwatch.StartNew();
        while (lean_io_check_canceled() == 0)
        {
            if (sw.ElapsedMilliseconds > 10000) return Nat(0);
            Thread.Sleep(1);
        }
        s_sawCancel = 1;
        return Nat(1);
    }

    static int s_syncTid, s_resolverTid;
    static Obj SyncMapFn(Obj opt)
    {
        s_syncTid = Environment.CurrentManagedThreadId;
        lean_dec(opt);
        return Nat(5);
    }

    static Obj IoAct(Obj w) => Nat(99);
    static Obj IoMapFn(Obj a, Obj w) => Nat(N(a) * 2);
    static Obj IoBindFn(Obj a, Obj w) => lean_task_pure(Nat(N(a) + 1000));

    static Obj SlowFn(Obj unit) { Thread.Sleep(300); return Nat(1); }
    static Obj FastFn(Obj unit) { Thread.Sleep(10); return Nat(2); }

    static Obj PanicFn(Obj unit) => throw new LeanPanicException("boom");
    static Obj ExitFn(Obj unit) { Thread.Sleep(50); throw new LeanExitException(3); }

    static Obj TidFn(Obj unit) => lean_box(lean_io_get_tid());

    // ---------------------------------------------------------------- tests

    static void TestThunks()
    {
        Obj t = lean_mk_thunk(Closure1(&ThunkFn));
        var results = new ulong[8];
        var ths = new Thread[8];
        for (int i = 0; i < 8; i++)
        {
            int j = i;
            ths[i] = new Thread(() => { results[j] = N(lean_thunk_get_own(t)); });
            ths[i].Start();
        }
        foreach (var th in ths) th.Join();
        Check(s_thunkCount == 1, $"thunk evaluated once (count={s_thunkCount})");
        Check(results.All(r => r == 42), "thunk value seen by all threads");
        Check(N(lean_thunk_get_own(t)) == 42 && s_thunkCount == 1, "thunk cached");
        Obj p = lean_thunk_pure(Nat(3));
        Check(N(lean_thunk_get_own(p)) == 3, "thunk pure");
        // exception in thunk: rethrown on every evaluation
        Obj bad = lean_mk_thunk(Closure1(&PanicFn));
        int thrown = 0;
        for (int i = 0; i < 2; i++) { try { lean_thunk_get(bad); } catch (LeanPanicException) { thrown++; } }
        Check(thrown == 2, "thunk exception rethrown on each get");
    }

    static void TestSpawnGet()
    {
        Obj t = lean_task_spawn(Const(Nat(10)), Nat(0));
        Check(N(lean_task_get_own(t)) == 10, "spawn/get");
        Obj p = lean_task_pure(Nat(11));
        Check(N(lean_task_get_own(p)) == 11, "task pure");
        Obj d = lean_task_spawn(Const(Nat(12)), Nat(9));
        Check(N(lean_task_get_own(d)) == 12, "dedicated spawn/get");
        Obj big = lean_task_spawn(Const(Nat(13)), lean_alloc_mpz(System.Numerics.BigInteger.Pow(2, 100)));
        Check(N(lean_task_get_own(big)) == 13, "spawn with huge priority");
        // many small tasks
        var ts = new Obj[10000];
        for (int i = 0; i < ts.Length; i++) ts[i] = lean_task_map(Closure1(&Add1), lean_task_spawn(Const(Nat((ulong)i)), Nat((ulong)(i % 9))), Nat(0), 0);
        ulong sum = 0;
        foreach (var x in ts) sum += N(lean_task_get_own(x));
        ulong expected = 0;
        for (ulong i = 0; i < 10000; i++) expected += i + 1;
        Check(sum == expected, $"10000 spawn+map tasks (sum={sum})");
    }

    static void TestChains()
    {
        Obj t = lean_task_spawn(Const(Nat(0)), Nat(0));
        for (int i = 0; i < 10000; i++) t = lean_task_map(Closure1(&Add1), t, Nat(0), 0);
        Check(N(lean_task_get_own(t)) == 10000, "map chain of 10000");

        Obj s = lean_task_spawn(Const(Nat(0)), Nat(0));
        for (int i = 0; i < 10000; i++) s = lean_task_map(Closure1(&Add1), s, Nat(0), 1);
        Check(N(lean_task_get_own(s)) == 10000, "sync map chain of 10000");

        Obj b = lean_task_pure(Nat(0));
        for (int i = 0; i < 1000; i++) b = lean_task_bind(b, Closure1(&SpawnAdd1), Nat(0), 0);
        Check(N(lean_task_get_own(b)) == 1000, "bind chain of 1000");

        Obj bs = lean_task_spawn(Const(Nat(0)), Nat(0));
        for (int i = 0; i < 1000; i++) bs = lean_task_bind(bs, Closure1(&SpawnAdd1), Nat(0), 1);
        Check(N(lean_task_get_own(bs)) == 1000, "sync bind chain of 1000");
    }

    static void TestBlocking()
    {
        // workers blocked in Task.get: the pool must grow (C: task_manager::wait_for)
        Obj f = lean_task_spawn(Closure2_1(&FibTask, Nat(12)), Nat(0));
        Check(N(lean_task_get_own(f)) == Fib(12), $"fib(12) with nested blocking tasks (workers={LeanTaskManager.NumWorkerThreads})");

        // 64 tasks blocked on a promise that is resolved by a task queued after them
        Obj prom = lean_io_promise_new();
        var ts = new Obj[64];
        for (int i = 0; i < ts.Length; i++)
            ts[i] = lean_task_spawn(Closure2_1(&WaitPromiseFn, lean_io_promise_result_opt(prom)), Nat(0));
        lean_inc(prom);
        Obj resolver = lean_task_spawn(Closure2_1(&ResolveFn, prom), Nat(0));
        ulong sum = 0;
        foreach (var t in ts) sum += N(lean_task_get_own(t));
        Check(sum == 64 * 7, $"64 tasks waiting on a promise resolved by a later task (sum={sum})");
        lean_dec(resolver);
        lean_dec(prom);
    }

    static void TestPromises()
    {
        Obj p = lean_io_promise_new();
        Obj r = lean_io_promise_result_opt(p);
        Check(lean_io_get_task_state(r) == 1, "unresolved promise state = running/promised");
        Obj m = lean_task_map(Closure1(&Add1), lean_io_promise_result_opt(p), Nat(0), 0);
        Check(lean_io_get_task_state(m) == 0, "map on unresolved promise state = waiting");
        lean_dec(m);
        lean_io_promise_resolve(Nat(5), p);
        lean_io_promise_resolve(Nat(6), p); // second resolve has no effect
        Obj opt = lean_task_get_own(r);
        Check(!lean_is_scalar(opt) && N(lean_ctor_get(opt, 0)) == 5, "promise resolve + result?");
        Check(lean_io_get_task_state(lean_io_promise_result_opt(p)) == 2, "resolved promise state = finished");
        Check(promise_is_resolved(p), "promise_is_resolved");
        lean_dec(p);

        Obj p2 = lean_io_promise_new();
        Obj r2 = lean_io_promise_result_opt(p2);
        lean_dec(p2); // dropped without resolving
        Obj opt2 = lean_task_get_own(r2);
        Check(lean_is_scalar(opt2) && N(opt2) == 0, "dropped promise resolves to none");

        // sync map runs in the thread that resolves the promise
        Obj p3 = lean_io_promise_new();
        Obj sm = lean_task_map(Closure1(&SyncMapFn), lean_io_promise_result_opt(p3), Nat(0), 1);
        var th = new Thread(() => { s_resolverTid = Environment.CurrentManagedThreadId; lean_io_promise_resolve(Nat(1), p3); });
        th.Start(); th.Join();
        Check(N(lean_task_get_own(sm)) == 5 && s_syncTid == s_resolverTid, "sync map runs in resolving thread");
        lean_dec(p3);
    }

    static void TestCancel()
    {
        Obj t = lean_task_spawn(Closure1(&LoopUntilCanceled), Nat(0));
        Thread.Sleep(20);
        Check(lean_io_check_canceled() == 0, "check_canceled false outside of tasks");
        lean_io_cancel(t);
        Check(N(lean_task_get_own(t)) == 1, "IO.cancel observed by IO.checkCanceled");

        Obj d = lean_task_spawn(Closure1(&LoopUntilCanceled), Nat(9));
        Thread.Sleep(20);
        lean_io_cancel(d);
        Check(N(lean_task_get_own(d)) == 1, "cancel dedicated task");

        // dropping the last reference to a running task cancels it
        s_sawCancel = 0;
        Obj u = lean_task_spawn(Closure1(&LoopUntilCanceledFlag), Nat(0));
        Thread.Sleep(20);
        lean_dec(u);
        var sw = Stopwatch.StartNew();
        while (s_sawCancel == 0 && sw.ElapsedMilliseconds < 5000) Thread.Sleep(1);
        Check(s_sawCancel == 1, "deactivated task sees cancellation");
    }

    static void TestIoTasks()
    {
        Obj t = lean_io_as_task(Closure1(&IoAct), Nat(0));
        Check(N(lean_io_wait(t)) == 99, "IO.asTask + IO.wait");
        Obj t2 = lean_io_map_task(lean_alloc_closure((delegate*<Obj, Obj, Obj>)&IoMapFn, 2, 0), lean_task_pure(Nat(21)), Nat(0), 0);
        Check(N(lean_io_wait(t2)) == 42, "IO.mapTask");
        Obj t3 = lean_io_bind_task(lean_task_spawn(Const(Nat(1)), Nat(0)), lean_alloc_closure((delegate*<Obj, Obj, Obj>)&IoBindFn, 2, 0), Nat(0), 0);
        Check(N(lean_io_wait(t3)) == 1001, "IO.bindTask");
        Obj slow = lean_task_spawn(Closure1(&SlowFn), Nat(0));
        Obj fast = lean_task_spawn(Closure1(&FastFn), Nat(0));
        Obj lst = MkList(new[] { slow, fast });
        Check(N(lean_io_wait_any(lst)) == 2, "IO.waitAny");
        lean_dec(lst);
        Obj tid = lean_task_spawn(Closure1(&TidFn), Nat(9));
        Check(N(lean_task_get_own(tid)) != lean_io_get_tid(), "IO.getTID differs in dedicated thread");
    }

    static void TestExceptions()
    {
        Obj t = lean_task_spawn(Closure1(&PanicFn), Nat(0));
        bool thrown = false;
        Obj m = lean_task_map(Closure1(&Add1), t, Nat(0), 0);
        lean_inc(t);
        try { lean_task_get_own(t); } catch (LeanPanicException ex) { thrown = ex.Message == "boom"; }
        Check(thrown, "exception in task rethrown by Task.get");
        thrown = false;
        try { lean_task_get_own(m); } catch (LeanPanicException) { thrown = true; }
        Check(thrown, "failure propagates through Task.map");
        // an internal panic in a task ends the program like `exit(1)` (as natively)
        Check(LeanTaskManager.PendingExitCode == 1, "internal panic in a task requests exit code 1");
        LeanTaskManager.ClearPendingExit();

        // exit in a task wakes up a thread blocked on an unrelated promise
        Obj p = lean_io_promise_new();
        Obj e = lean_task_spawn(Closure1(&ExitFn), Nat(0));
        int code = -1;
        try { lean_task_get(lean_io_promise_result_opt(p)); } catch (LeanExitException ex) { code = ex.ExitCode; }
        Check(code == 3 && LeanTaskManager.PendingExitCode == 3, "LeanExitException in a task propagates to blocked main thread");
        LeanTaskManager.ClearPendingExit();
        lean_dec(e);
        lean_dec(p);
    }

    static void TestMutexes()
    {
        Obj mtx = lean_io_basemutex_new();
        Obj cv = lean_io_condvar_new();
        long counter = 0;
        const int per = 20000, nthreads = 4;
        var ths = new Thread[nthreads];
        for (int i = 0; i < nthreads; i++)
        {
            ths[i] = new Thread(() =>
            {
                for (int k = 0; k < per; k++)
                {
                    lean_io_basemutex_lock(mtx);
                    counter++;
                    if (counter == per * nthreads) lean_io_condvar_notify_all(cv);
                    lean_io_basemutex_unlock(mtx);
                }
            });
        }
        long seen = 0;
        var waiter = new Thread(() =>
        {
            lean_io_basemutex_lock(mtx);
            while (counter != per * nthreads) lean_io_condvar_wait(cv, mtx);
            seen = counter;
            lean_io_basemutex_unlock(mtx);
        });
        waiter.Start();
        foreach (var th in ths) th.Start();
        foreach (var th in ths) th.Join();
        Check(waiter.Join(10000) && seen == per * nthreads, $"BaseMutex + Condvar (counter={counter}, seen={seen})");

        lean_io_basemutex_lock(mtx);
        byte tl = 1;
        var th2 = new Thread(() => tl = lean_io_basemutex_try_lock(mtx)); th2.Start(); th2.Join();
        Check(tl == 0, "BaseMutex.tryLock fails while locked");
        lean_io_basemutex_unlock(mtx);
        Check(lean_io_basemutex_try_lock(mtx) == 1, "BaseMutex.tryLock succeeds when unlocked");
        lean_io_basemutex_unlock(mtx);

        // notify_one wakes exactly one waiter
        int woken = 0;
        var ws = new Thread[3];
        for (int i = 0; i < 3; i++)
        {
            ws[i] = new Thread(() =>
            {
                lean_io_basemutex_lock(mtx);
                lean_io_condvar_wait(cv, mtx);
                Interlocked.Increment(ref woken);
                lean_io_basemutex_unlock(mtx);
            });
            ws[i].Start();
        }
        Thread.Sleep(100);
        lean_io_condvar_notify_one(cv);
        Thread.Sleep(100);
        int afterOne = Volatile.Read(ref woken);
        lean_io_condvar_notify_all(cv);
        foreach (var w in ws) w.Join();
        Check(afterOne == 1 && woken == 3, $"Condvar notifyOne/notifyAll (afterOne={afterOne})");

        Obj rm = lean_io_baserecmutex_new();
        lean_io_baserecmutex_lock(rm);
        lean_io_baserecmutex_lock(rm);
        byte rt = 1;
        var th3 = new Thread(() => rt = lean_io_baserecmutex_try_lock(rm)); th3.Start(); th3.Join();
        lean_io_baserecmutex_unlock(rm);
        byte rt2 = 1;
        var th4 = new Thread(() => rt2 = lean_io_baserecmutex_try_lock(rm)); th4.Start(); th4.Join();
        lean_io_baserecmutex_unlock(rm);
        byte rt3 = 0;
        var th5 = new Thread(() => { rt3 = lean_io_baserecmutex_try_lock(rm); if (rt3 == 1) lean_io_baserecmutex_unlock(rm); }); th5.Start(); th5.Join();
        Check(rt == 0 && rt2 == 0 && rt3 == 1 && lean_io_baserecmutex_try_lock(rm) == 1, "BaseRecursiveMutex");
        lean_io_baserecmutex_unlock(rm);

        Obj sm = lean_io_basesharedmutex_new();
        lean_io_basesharedmutex_read(sm);
        byte r2 = 0, w2 = 1;
        var th6 = new Thread(() => { r2 = lean_io_basesharedmutex_try_read(sm); if (r2 == 1) lean_io_basesharedmutex_unlock_read(sm); w2 = lean_io_basesharedmutex_try_write(sm); });
        th6.Start(); th6.Join();
        lean_io_basesharedmutex_unlock_read(sm);
        byte w3 = lean_io_basesharedmutex_try_write(sm);
        byte r3 = 1;
        var th7 = new Thread(() => { r3 = lean_io_basesharedmutex_try_read(sm); });
        th7.Start(); th7.Join();
        lean_io_basesharedmutex_unlock_write(sm);
        lean_io_basesharedmutex_write(sm);
        lean_io_basesharedmutex_unlock_write(sm);
        Check(r2 == 1 && w2 == 0 && w3 == 1 && r3 == 0, "BaseSharedMutex");
    }

    static void TestRefs()
    {
        Obj r = lean_st_mk_ref(Nat(1));
        Check(N(lean_st_ref_get(r)) == 1, "ref get");
        lean_st_ref_put(r, Nat(2));
        Check(N(lean_st_ref_get(r)) == 2, "ref put");
        Check(N(lean_st_ref_swap(r, Nat(3))) == 2 && N(lean_st_ref_get(r)) == 3, "ref swap");
        Check(N(lean_st_ref_take(r)) == 3, "ref take");
        lean_st_ref_put(r, Nat(4));
        Check(N(lean_st_ref_get(r)) == 4, "ref put after take");
        Obj r2 = lean_st_mk_ref(Nat(4));
        Check(lean_st_ref_ptr_eq(r, r) == 1 && lean_st_ref_ptr_eq(r, r2) == 0, "ref ptr_eq");

        // value stored in a multi-threaded ref is marked MT
        Obj mref = lean_st_mk_ref(Nat(0));
        lean_mark_mt(mref);
        Obj arr = MkArray(new[] { Nat(1) });
        lean_st_ref_put(mref, arr);
        Check(lean_is_mt(arr), "value stored in MT ref is marked MT");
        lean_st_ref_put(mref, Nat(0));

        // concurrent take/put increments (Ref.modify)
        const int nthreads = 8, per = 20000;
        var ths = new Thread[nthreads];
        for (int i = 0; i < nthreads; i++)
        {
            ths[i] = new Thread(() =>
            {
                for (int k = 0; k < per; k++)
                {
                    Obj v = lean_st_ref_take(mref);
                    lean_st_ref_put(mref, Nat(N(v) + 1));
                }
            });
        }
        // concurrent readers
        bool readersOk = true;
        var readers = new Thread[2];
        for (int i = 0; i < 2; i++)
        {
            readers[i] = new Thread(() =>
            {
                ulong last = 0;
                for (int k = 0; k < 20000; k++)
                {
                    Obj v = lean_st_ref_get(mref);
                    ulong x = N(v);
                    if (x < last) readersOk = false;
                    last = x;
                    lean_dec(v);
                }
            });
        }
        foreach (var th in ths) th.Start();
        foreach (var th in readers) th.Start();
        foreach (var th in ths) th.Join();
        foreach (var th in readers) th.Join();
        Check(N(lean_st_ref_get(mref)) == nthreads * per && readersOk, $"concurrent take/put/get on MT ref (value={N(lean_st_ref_get(mref))})");

        // concurrent swaps conserve values
        Obj sref = lean_st_mk_ref(Nat(1_000_000));
        lean_mark_mt(sref);
        var got = new List<ulong>[nthreads];
        for (int i = 0; i < nthreads; i++)
        {
            int j = i;
            got[j] = new List<ulong>();
            ths[i] = new Thread(() =>
            {
                for (int k = 0; k < 10000; k++)
                    got[j].Add(N(lean_st_ref_swap(sref, Nat((ulong)(j * 100000 + k + 5000)))));
            });
        }
        foreach (var th in ths) th.Start();
        foreach (var th in ths) th.Join();
        var all = got.SelectMany(x => x).ToList();
        all.Add(N(lean_st_ref_get(sref)));
        var expected = new List<ulong> { 1_000_000 };
        for (int j = 0; j < nthreads; j++) for (int k = 0; k < 10000; k++) expected.Add((ulong)(j * 100000 + k + 5000));
        all.Sort(); expected.Sort();
        Check(all.SequenceEqual(expected), "concurrent swaps conserve values");

        // persistent refs are treated as MT
        Obj pref = lean_st_mk_ref(Nat(0));
        lean_mark_persistent(pref);
        Obj arr2 = MkArray(new[] { Nat(1) });
        lean_st_ref_put(pref, arr2);
        Check(lean_is_mt(arr2) && ReferenceEquals(lean_st_ref_swap(pref, Nat(9)), arr2), "persistent ref is treated as MT");
    }

    static void TestMisc()
    {
        Check(lean_strict_and(1, 0) == 0 && lean_strict_and(1, 1) == 1 && lean_strict_or(0, 0) == 0 && lean_strict_or(0, 1) == 1, "strict and/or");
        Obj a = MkArray(new[] { Nat(1) });
        Check(ReferenceEquals(lean_runtime_mark_multi_threaded(a), a) && lean_is_mt(a), "Runtime.markMultiThreaded");
        Obj b = MkArray(new[] { Nat(1) });
        Check(ReferenceEquals(lean_runtime_mark_persistent(b), b) && lean_is_persistent(b), "Runtime.markPersistent");
        Check(N(lean_runtime_forget(b)) == 0 && N(lean_runtime_hold(b)) == 0, "Runtime.forget/hold");
    }

    static int Main()
    {
        lean_init_task_manager_using(2);
        var sw = Stopwatch.StartNew();
        TestThunks();
        TestSpawnGet();
        TestChains();
        TestBlocking();
        TestPromises();
        TestCancel();
        TestIoTasks();
        TestExceptions();
        TestMutexes();
        TestRefs();
        TestMisc();
        lean_finalize_task_manager();
        // after finalization tasks run synchronously
        Obj t = lean_task_spawn(Const(Nat(8)), Nat(0));
        Check(t.m_rc > 0 && N(lean_task_get_own(t)) == 8, "spawn after finalize runs synchronously");
        Console.WriteLine($"{s_checks - s_failures}/{s_checks} checks passed in {sw.ElapsedMilliseconds} ms (worker threads: {LeanTaskManager.NumWorkerThreads})");
        return s_failures == 0 ? 0 : 1;
    }
}
