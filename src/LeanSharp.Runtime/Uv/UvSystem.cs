// Port of `runtime/uv/system.cpp` using managed APIs. Information that .NET does not expose is read
// from /proc and /sys on Linux or obtained from standard command line tools (`sysctl`, `ps`,
// `renice`, `vm_stat`, `id`, `dscl`) where no managed API exists.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using static LeanSharp.Runtime.LeanRt;

namespace LeanSharp.Runtime;

internal static class UvSys
{
    static string s_processTitle;
    public static readonly object TitleLock = new();

    public static string ProcessTitle
    {
        get
        {
            lock (TitleLock)
            {
                if (s_processTitle == null)
                {
                    var args = Environment.GetCommandLineArgs();
                    s_processTitle = args.Length > 0 ? args[0] : (Environment.ProcessPath ?? "");
                }
                return s_processTitle;
            }
        }
        set { lock (TitleLock) s_processTitle = value; }
    }

    public static bool IsLinux => OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();
    public static bool IsMac => OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst();

    /// <summary>Fields of /proc/[pid]/stat (field 1 = index 0), handling a `comm` with spaces.</summary>
    public static string[] ProcStat(string pid)
    {
        var text = UvUtil.TryReadFile($"/proc/{pid}/stat");
        if (text == null) return null;
        int rp = text.LastIndexOf(')');
        if (rp < 0) return null;
        var head = text.Substring(0, text.IndexOf(' ') < 0 ? 0 : text.IndexOf(' '));
        var rest = text.Substring(rp + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var r = new string[rest.Length + 2];
        r[0] = head;
        r[1] = "comm";
        Array.Copy(rest, 0, r, 2, rest.Length);
        return r;
    }

    /// <summary>Value (first number) of a `key:` line in /proc files like meminfo or status.</summary>
    public static ulong? ProcKey(string path, string key)
    {
        var text = UvUtil.TryReadFile(path);
        if (text == null) return null;
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith(key + ":", StringComparison.Ordinal)) continue;
            var parts = line.Substring(key.Length + 1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && ulong.TryParse(parts[0], out var v)) return v;
        }
        return null;
    }

    public static ulong TotalMemory()
    {
        if (IsLinux)
        {
            var kb = ProcKey("/proc/meminfo", "MemTotal");
            if (kb != null) return kb.Value * 1024;
        }
        else if (IsMac || OperatingSystem.IsFreeBSD())
        {
            var s = UvUtil.RunCommand("sysctl", "-n", "hw.memsize");
            if (s == null || !ulong.TryParse(s, out _)) s = UvUtil.RunCommand("sysctl", "-n", "hw.physmem");
            if (s != null && ulong.TryParse(s, out var v)) return v;
        }
        var info = GC.GetGCMemoryInfo();
        return (ulong)Math.Max(0, info.TotalAvailableMemoryBytes);
    }

    public static ulong FreeMemory()
    {
        if (IsLinux)
        {
            var kb = ProcKey("/proc/meminfo", "MemAvailable") ?? ProcKey("/proc/meminfo", "MemFree");
            if (kb != null) return kb.Value * 1024;
        }
        else if (IsMac)
        {
            var s = UvUtil.RunCommand("vm_stat");
            if (s != null)
            {
                ulong pageSize = 4096;
                int i = s.IndexOf("page size of ", StringComparison.Ordinal);
                if (i >= 0)
                {
                    var num = new string(s.Substring(i + 13).TakeWhile(char.IsAsciiDigit).ToArray());
                    if (ulong.TryParse(num, out var ps)) pageSize = ps;
                }
                foreach (var line in s.Split('\n'))
                {
                    if (!line.StartsWith("Pages free:", StringComparison.Ordinal)) continue;
                    var num = line.Substring(11).Trim().TrimEnd('.');
                    if (ulong.TryParse(num, out var pages)) return pages * pageSize;
                }
            }
        }
        var info = GC.GetGCMemoryInfo();
        long free = info.TotalAvailableMemoryBytes - info.MemoryLoadBytes;
        // with a GC heap limit the total is the limit, not the machine's memory
        if (free <= 0) free = info.TotalAvailableMemoryBytes - info.HeapSizeBytes;
        return (ulong)Math.Max(0, free);
    }

    /// <summary>`uv_get_constrained_memory` (cgroup limit on Linux; 0 if unknown).</summary>
    public static ulong ConstrainedMemory()
    {
        if (!IsLinux) return 0;
        var v2 = UvUtil.TryReadFile("/sys/fs/cgroup/memory.max");
        if (v2 != null)
        {
            v2 = v2.Trim();
            if (v2 == "max") return ulong.MaxValue;
            if (ulong.TryParse(v2, out var lim)) return lim;
        }
        var v1 = UvUtil.TryReadFile("/sys/fs/cgroup/memory/memory.limit_in_bytes");
        if (v1 != null && ulong.TryParse(v1.Trim(), out var l1))
            return l1 >= 0x7FFFFFFFFFFFF000UL ? ulong.MaxValue : l1;
        return 0;
    }

    public static ulong AvailableMemory()
    {
        ulong constrained = ConstrainedMemory();
        if (constrained == 0 || constrained == ulong.MaxValue) return FreeMemory();
        var cur = UvUtil.TryReadFile("/sys/fs/cgroup/memory.current") ?? UvUtil.TryReadFile("/sys/fs/cgroup/memory/memory.usage_in_bytes");
        if (cur != null && ulong.TryParse(cur.Trim(), out var used))
            return constrained > used ? constrained - used : 0;
        return FreeMemory();
    }

    public static string Machine()
    {
        switch (RuntimeInformation.OSArchitecture)
        {
            case Architecture.X64: return "x86_64";
            case Architecture.X86: return "i686";
            case Architecture.Arm64: return IsLinux ? "aarch64" : "arm64";
            case Architecture.Arm: return "armv7l";
            case Architecture.RiscV64: return "riscv64";
            case Architecture.LoongArch64: return "loongarch64";
            case Architecture.S390x: return "s390x";
            case Architecture.Ppc64le: return "ppc64le";
            default: return RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        }
    }

    /// <summary>(sysname, release, version) as reported by uname(2).</summary>
    public static (string, string, string) Uname()
    {
        if (IsLinux)
        {
            var sys = UvUtil.TryReadFile("/proc/sys/kernel/ostype")?.Trim();
            var rel = UvUtil.TryReadFile("/proc/sys/kernel/osrelease")?.Trim();
            var ver = UvUtil.TryReadFile("/proc/sys/kernel/version")?.Trim();
            if (sys != null && rel != null && ver != null) return (sys, rel, ver);
        }
        if (OperatingSystem.IsWindows())
        {
            var v = Environment.OSVersion.Version;
            return ("Windows_NT", $"{v.Major}.{v.Minor}.{v.Build}", RuntimeInformation.OSDescription);
        }
        {
            var sys = UvUtil.RunCommand("uname", "-s");
            var rel = UvUtil.RunCommand("uname", "-r");
            var ver = UvUtil.RunCommand("uname", "-v");
            if (sys != null && rel != null && ver != null) return (sys, rel, ver);
        }
        // On Linux, OSDescription is "<sysname> <release> <version>".
        var d = RuntimeInformation.OSDescription.Trim();
        var parts = d.Split(' ', 3);
        if (parts.Length == 3) return (parts[0], parts[1], parts[2]);
        if (parts.Length == 2) return (parts[0], parts[1], "");
        return (d, Environment.OSVersion.Version.ToString(), "");
    }

    static readonly object s_ppidLock = new();
    static ulong? s_winPpid;

    public static (bool ok, ulong ppid) ParentPid()
    {
        int pid = Environment.ProcessId;
        if (IsLinux)
        {
            var st = ProcStat("self");
            if (st != null && st.Length > 3 && ulong.TryParse(st[3], out var pp)) return (true, pp);
        }
        if (!OperatingSystem.IsWindows())
        {
            var s = UvUtil.RunCommand("ps", "-o", "ppid=", "-p", pid.ToString());
            if (s != null && ulong.TryParse(s.Trim(), out var pp)) return (true, pp);
        }
        else
        {
            // .NET has no API for it: ask the system's PowerShell once (the parent of a Windows
            // process never changes)
            lock (s_ppidLock)
            {
                if (s_winPpid == null)
                {
                    var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                    var s = UvUtil.RunCommand(ps, "-NoProfile", "-NonInteractive", "-Command",
                        $"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').ParentProcessId");
                    s_winPpid = s != null && ulong.TryParse(s.Trim(), out var pp) ? pp : 0;
                }
                if (s_winPpid > 0) return (true, s_winPpid.Value);
            }
        }
        return (false, 0);
    }

    public static Obj MkCpuInfo(string model, ulong speed, ulong user, ulong nice, ulong sys, ulong idle, ulong irq)
    {
        var times = lean_alloc_ctor(0, 0, 40);
        lean_ctor_set_uint64_s(times, 0, user);
        lean_ctor_set_uint64_s(times, 8, nice);
        lean_ctor_set_uint64_s(times, 16, sys);
        lean_ctor_set_uint64_s(times, 24, idle);
        lean_ctor_set_uint64_s(times, 32, irq);
        var ci = lean_alloc_ctor(0, 2, 8);
        lean_ctor_set(ci, 0, lean_mk_string(model));
        lean_ctor_set(ci, 1, times);
        lean_ctor_set_uint64_s(ci, 0, speed);
        return ci;
    }

    public static List<Obj> CpuInfos()
    {
        var result = new List<Obj>();
        if (IsLinux)
        {
            var stat = UvUtil.TryReadFile("/proc/stat");
            var cpuinfo = UvUtil.TryReadFile("/proc/cpuinfo") ?? "";
            var models = new List<string>();
            var mhz = new List<ulong>();
            foreach (var line in cpuinfo.Split('\n'))
            {
                int c = line.IndexOf(':');
                if (c < 0) continue;
                var key = line.Substring(0, c).Trim();
                var val = line.Substring(c + 1).Trim();
                if (key == "model name" || key == "Processor" || key == "cpu model") models.Add(val);
                else if (key == "cpu MHz" && double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) mhz.Add((ulong)f);
            }
            if (stat != null)
            {
                int idx = 0;
                foreach (var line in stat.Split('\n'))
                {
                    if (!line.StartsWith("cpu", StringComparison.Ordinal) || line.Length < 4 || !char.IsAsciiDigit(line[3])) continue;
                    var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    ulong F(int i) => i < p.Length && ulong.TryParse(p[i], out var v) ? v * 10 : 0; // USER_HZ = 100 → ms
                    string model = idx < models.Count ? models[idx] : (models.Count > 0 ? models[0] : "unknown");
                    ulong speed = UvCpuFreq(idx) ?? (idx < mhz.Count ? mhz[idx] : 0);
                    result.Add(MkCpuInfo(model, speed, F(1), F(2), F(3), F(4), F(6)));
                    idx++;
                }
                if (result.Count > 0) return result;
            }
        }
        string m = "unknown";
        ulong sp = 0;
        if (IsMac || OperatingSystem.IsFreeBSD())
        {
            m = UvUtil.RunCommand("sysctl", "-n", "machdep.cpu.brand_string") ?? UvUtil.RunCommand("sysctl", "-n", "hw.model") ?? "unknown";
            var f = UvUtil.RunCommand("sysctl", "-n", "hw.cpufrequency");
            if (f != null && ulong.TryParse(f, out var hz)) sp = hz / 1000000;
        }
        else if (OperatingSystem.IsWindows())
        {
            m = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
        }
        for (int i = 0; i < Environment.ProcessorCount; i++) result.Add(MkCpuInfo(m, sp, 0, 0, 0, 0, 0));
        return result;
    }

    static ulong? UvCpuFreq(int cpu)
    {
        var s = UvUtil.TryReadFile($"/sys/devices/system/cpu/cpu{cpu}/cpufreq/scaling_cur_freq");
        if (s != null && ulong.TryParse(s.Trim(), out var khz)) return khz / 1000;
        return null;
    }

    /// <summary>Entries of /etc/passwd or /etc/group split at ':'.</summary>
    public static IEnumerable<string[]> ColonFile(string path)
    {
        var text = UvUtil.TryReadFile(path);
        if (text == null) yield break;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            yield return line.TrimEnd('\r').Split(':');
        }
    }

    public static int? GetPriority(int pid)
    {
        if (pid == 0) pid = Environment.ProcessId;
        if (IsLinux)
        {
            var st = ProcStat(pid.ToString());
            if (st == null) return null;
            if (st.Length > 18 && int.TryParse(st[18], out var nice)) return nice;
        }
        if (!OperatingSystem.IsWindows())
        {
            var s = UvUtil.RunCommand("ps", "-o", "nice=", "-p", pid.ToString());
            if (s != null && int.TryParse(s.Trim(), out var nice)) return nice;
            return null;
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.PriorityClass switch
            {
                ProcessPriorityClass.Idle => 19,
                ProcessPriorityClass.BelowNormal => 10,
                ProcessPriorityClass.Normal => 0,
                ProcessPriorityClass.AboveNormal => -7,
                ProcessPriorityClass.High => -14,
                ProcessPriorityClass.RealTime => -20,
                _ => 0,
            };
        }
        catch { return null; }
    }
}

public static unsafe partial class LeanRt
{
    // Std.Internal.UV.System.getProcessTitle : IO String
    public static Obj lean_uv_get_process_title() => lean_io_result_mk_ok(lean_mk_string(UvSys.ProcessTitle));

    // Std.Internal.UV.System.setProcessTitle : @& String → IO Unit
    public static Obj lean_uv_set_process_title(Obj title)
    {
        if (UvUtil.HasNul(title)) return UvUtil.EmbeddedNulError(title);
        // The OS-level process title cannot be changed from managed code; only the value reported by
        // `getProcessTitle` changes.
        UvSys.ProcessTitle = lean_string_to_net(title);
        return UvUtil.IoOkUnit();
    }

    // Std.Internal.UV.System.uptime : IO UInt64
    public static Obj lean_uv_uptime()
    {
        if (UvSys.IsLinux)
        {
            var s = UvUtil.TryReadFile("/proc/uptime");
            if (s != null)
            {
                var p = s.Split(' ');
                if (double.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var up))
                    return lean_io_result_mk_ok(UvUtil.BoxU64((ulong)up));
            }
        }
        else if (UvSys.IsMac || OperatingSystem.IsFreeBSD())
        {
            // "{ sec = 1727000000, usec = 0 } ..."
            var s = UvUtil.RunCommand("sysctl", "-n", "kern.boottime");
            if (s != null)
            {
                int i = s.IndexOf("sec = ", StringComparison.Ordinal);
                if (i >= 0)
                {
                    var num = new string(s.Substring(i + 6).TakeWhile(char.IsAsciiDigit).ToArray());
                    if (long.TryParse(num, out var boot))
                    {
                        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        return lean_io_result_mk_ok(UvUtil.BoxU64((ulong)Math.Max(0, now - boot)));
                    }
                }
            }
        }
        return lean_io_result_mk_ok(UvUtil.BoxU64((ulong)(Environment.TickCount64 / 1000)));
    }

    // Std.Internal.UV.System.osGetPid : IO UInt64
    public static Obj lean_uv_os_getpid() => lean_io_result_mk_ok(UvUtil.BoxU64((ulong)Environment.ProcessId));

    // Std.Internal.UV.System.osGetPpid : IO UInt64
    public static Obj lean_uv_os_getppid()
    {
        var (ok, ppid) = UvSys.ParentPid();
        if (!ok) return UvErr.NotSupported("getting the parent process id");
        return lean_io_result_mk_ok(UvUtil.BoxU64(ppid));
    }

    // Std.Internal.UV.System.cpuInfo : IO (Array CPUInfo)
    public static Obj lean_uv_cpu_info()
    {
        try { return lean_io_result_mk_ok(MkArray(UvSys.CpuInfos())); }
        catch (Exception ex) { return UvErr.IoError(UvErr.FromException(ex)); }
    }

    // Std.Internal.UV.System.cwd : IO String
    public static Obj lean_uv_cwd()
    {
        try
        {
            string d = LeanContext.Proc.Cwd;
            if (d.Length > 1 && (d.EndsWith('/') || (OperatingSystem.IsWindows() && d.EndsWith('\\') && !d.EndsWith(":\\"))))
                d = d.Substring(0, d.Length - 1);
            return lean_io_result_mk_ok(lean_mk_string(d));
        }
        catch (Exception ex) { return UvErr.IoError(UvErr.FromException(ex)); }
    }

    // Std.Internal.UV.System.chdir : @& String → IO Unit
    public static Obj lean_uv_chdir(Obj path)
    {
        if (UvUtil.HasNul(path)) return UvUtil.EmbeddedNulError(path);
        string p = lean_string_to_net(path);
        try
        {
            if (p.Length == 0) return UvErr.IoError(UvErr.ENOENT, path);
            var proc = LeanContext.Proc;
            p = proc.Resolve(p);
            if (File.Exists(p)) return UvErr.IoError(UvErr.ENOTDIR, path);
            if (proc.HasLogicalCwd)
            {
                // an in-process child changes its own (logical) working directory only
                if (!Directory.Exists(p)) return UvErr.IoError(UvErr.ENOENT, path);
                proc.SetCwd(IoRealPath(p) ?? Path.GetFullPath(p));
            }
            else Directory.SetCurrentDirectory(p);
            return UvUtil.IoOkUnit();
        }
        catch (Exception ex)
        {
            return UvErr.IoError(UvErr.FromException(ex), path);
        }
    }

    // Std.Internal.UV.System.osHomedir : IO String
    public static Obj lean_uv_os_homedir()
    {
        string h = LeanContext.Proc.GetEnv(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        if (string.IsNullOrEmpty(h)) h = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(h)) return UvErr.IoError(UvErr.ENOENT, lean_mk_string(""));
        return lean_io_result_mk_ok(lean_mk_string(h));
    }

    // Std.Internal.UV.System.osTmpdir : IO String
    public static Obj lean_uv_os_tmpdir()
    {
        string t;
        if (OperatingSystem.IsWindows())
        {
            t = Path.GetTempPath();
            if (t.Length > 1 && t.EndsWith('\\') && !t.EndsWith(":\\")) t = t.Substring(0, t.Length - 1);
        }
        else
        {
            t = null;
            foreach (var v in new[] { "TMPDIR", "TMP", "TEMP", "TEMPDIR" })
            {
                t = LeanContext.Proc.GetEnv(v);
                if (!string.IsNullOrEmpty(t)) break;
            }
            if (string.IsNullOrEmpty(t)) t = OperatingSystem.IsAndroid() ? "/data/local/tmp" : "/tmp";
            if (t.Length > 1 && t.EndsWith('/')) t = t.Substring(0, t.Length - 1);
        }
        return lean_io_result_mk_ok(lean_mk_string(t));
    }

    // Std.Internal.UV.System.osGetPasswd : IO PasswdInfo
    public static Obj lean_uv_os_get_passwd()
    {
        string user = Environment.UserName;
        ulong? uid = null, gid = null;
        string shell = null, home = null;
        if (!OperatingSystem.IsWindows())
        {
            foreach (var f in UvSys.ColonFile("/etc/passwd"))
            {
                if (f.Length >= 7 && f[0] == user)
                {
                    if (ulong.TryParse(f[2], out var u)) uid = u;
                    if (ulong.TryParse(f[3], out var g)) gid = g;
                    home = f[5];
                    shell = f[6];
                    break;
                }
            }
            if (uid == null)
            {
                var u = UvUtil.RunCommand("id", "-u");
                var g = UvUtil.RunCommand("id", "-g");
                if (u != null && ulong.TryParse(u, out var uu)) uid = uu;
                if (g != null && ulong.TryParse(g, out var gg)) gid = gg;
            }
            if (shell == null && UvSys.IsMac)
            {
                var s = UvUtil.RunCommand("dscl", ".", "-read", "/Users/" + user, "UserShell");
                if (s != null && s.StartsWith("UserShell:", StringComparison.Ordinal)) shell = s.Substring(10).Trim();
            }
            shell ??= LeanContext.Proc.GetEnv("SHELL");
            if (home == null)
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home)) home = null;
            }
        }
        else
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) home = null;
        }
        var r = lean_alloc_ctor(0, 5, 0);
        lean_ctor_set(r, 0, lean_mk_string(user));
        lean_ctor_set(r, 1, uid != null ? lean_mk_option_some(UvUtil.BoxU64(uid.Value)) : lean_box(0));
        lean_ctor_set(r, 2, uid != null && gid != null ? lean_mk_option_some(UvUtil.BoxU64(gid.Value)) : lean_box(0));
        lean_ctor_set(r, 3, shell != null ? lean_mk_option_some(lean_mk_string(shell)) : lean_box(0));
        lean_ctor_set(r, 4, home != null ? lean_mk_option_some(lean_mk_string(home)) : lean_box(0));
        return lean_io_result_mk_ok(r);
    }

    // Std.Internal.UV.System.osGetGroup : UInt64 → IO (Option GroupInfo)
    public static Obj lean_uv_os_get_group(ulong gid)
    {
        if (OperatingSystem.IsWindows()) return UvErr.IoError(UvErr.ENOTSUP);
        foreach (var f in UvSys.ColonFile("/etc/group"))
        {
            if (f.Length >= 3 && ulong.TryParse(f[2], out var g) && g == gid)
            {
                var members = new List<Obj>();
                if (f.Length >= 4)
                    foreach (var m in f[3].Split(',', StringSplitOptions.RemoveEmptyEntries)) members.Add(lean_mk_string(m.Trim()));
                var gi = lean_alloc_ctor(0, 2, 8);
                lean_ctor_set(gi, 0, lean_mk_string(f[0]));
                lean_ctor_set(gi, 1, MkArray(members));
                lean_ctor_set_uint64_s(gi, 0, gid);
                return lean_io_result_mk_ok(lean_mk_option_some(gi));
            }
        }
        return lean_io_result_mk_ok(lean_box(0));
    }

    // Std.Internal.UV.System.osEnviron : IO (Array (String × String))
    public static Obj lean_uv_os_environ()
    {
        var list = new List<Obj>();
        foreach (var e in LeanContext.Proc.EnvironmentSnapshot())
            list.Add(lean_mk_pair(lean_mk_string(e.Key), lean_mk_string(e.Value ?? "")));
        return lean_io_result_mk_ok(MkArray(list));
    }

    // Std.Internal.UV.System.osGetenv : @& String → IO (Option String)
    public static Obj lean_uv_os_getenv(Obj name)
    {
        if (UvUtil.HasNul(name)) return lean_io_result_mk_ok(lean_box(0));
        string n = lean_string_to_net(name);
        if (n.Length == 0 || n.Contains('=')) return lean_io_result_mk_ok(lean_box(0));
        string v = LeanContext.Proc.GetEnv(n);
        return lean_io_result_mk_ok(v == null ? lean_box(0) : lean_mk_option_some(lean_mk_string(v)));
    }

    // Std.Internal.UV.System.osSetenv : @& String → @& String → IO Unit
    public static Obj lean_uv_os_setenv(Obj name, Obj value)
    {
        if (UvUtil.HasNul(name)) return UvUtil.EmbeddedNulError(name);
        if (UvUtil.HasNul(value)) return UvUtil.EmbeddedNulError(value);
        string n = lean_string_to_net(name);
        if (n.Length == 0 || n.Contains('=')) return UvErr.IoError(UvErr.EINVAL);
        try
        {
            // Note: for the OS process, .NET removes the variable when the value is empty.
            LeanContext.Proc.SetEnv(n, lean_string_to_net(value));
            return UvUtil.IoOkUnit();
        }
        catch (Exception ex) { return UvErr.IoError(UvErr.FromException(ex)); }
    }

    // Std.Internal.UV.System.osUnsetenv : @& String → IO Unit
    public static Obj lean_uv_os_unsetenv(Obj name)
    {
        if (UvUtil.HasNul(name)) return UvUtil.EmbeddedNulError(name);
        string n = lean_string_to_net(name);
        if (n.Length == 0 || n.Contains('=')) return UvErr.IoError(UvErr.EINVAL);
        try
        {
            LeanContext.Proc.SetEnv(n, null);
            return UvUtil.IoOkUnit();
        }
        catch (Exception ex) { return UvErr.IoError(UvErr.FromException(ex)); }
    }

    // Std.Internal.UV.System.osGetHostname : IO String
    public static Obj lean_uv_os_gethostname()
    {
        try { return lean_io_result_mk_ok(lean_mk_string(System.Net.Dns.GetHostName())); }
        catch (Exception ex) { return UvErr.IoError(UvErr.FromException(ex)); }
    }

    // Std.Internal.UV.System.osGetPriority : UInt64 → IO Int64
    public static Obj lean_uv_os_getpriority(ulong pid)
    {
        if (pid > int.MaxValue) return UvErr.IoError(UvErr.ESRCH);
        var p = UvSys.GetPriority((int)pid);
        if (p == null) return UvErr.IoError(UvErr.ESRCH);
        return lean_io_result_mk_ok(UvUtil.BoxU64((ulong)(long)p.Value));
    }

    // Std.Internal.UV.System.osSetPriority : UInt64 → Int64 → IO Unit
    public static Obj lean_uv_os_setpriority(ulong pid, ulong priority)
    {
        long prio = (long)priority;
        if (pid > int.MaxValue) return UvErr.IoError(UvErr.ESRCH);
        int ipid = pid == 0 ? Environment.ProcessId : (int)pid;
        if (OperatingSystem.IsWindows())
        {
            if (prio < -20 || prio > 19) return UvErr.IoError(UvErr.EINVAL);
            try
            {
                using var p = Process.GetProcessById(ipid);
                p.PriorityClass = prio < -14 ? ProcessPriorityClass.RealTime : prio < -7 ? ProcessPriorityClass.High
                    : prio < 0 ? ProcessPriorityClass.AboveNormal : prio < 10 ? ProcessPriorityClass.Normal
                    : prio < 19 ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Idle;
                return UvUtil.IoOkUnit();
            }
            catch (ArgumentException) { return UvErr.IoError(UvErr.ESRCH); }
            catch (Exception ex) { return UvErr.IoError(UvErr.FromException(ex)); }
        }
        if (prio > int.MaxValue || prio < int.MinValue) return UvErr.IoError(UvErr.EINVAL);
        try
        {
            var psi = new ProcessStartInfo("renice")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(prio.ToString());
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(ipid.ToString());
            using var proc = Process.Start(psi);
            if (proc == null) return UvErr.NotSupported("setting the process priority");
            proc.StandardOutput.ReadToEnd();
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && !err.Contains("denied", StringComparison.OrdinalIgnoreCase)) return UvUtil.IoOkUnit();
            if (err.Contains("No such process", StringComparison.OrdinalIgnoreCase)) return UvErr.IoError(UvErr.ESRCH);
            if (err.Contains("not permitted", StringComparison.OrdinalIgnoreCase)) return UvErr.IoError(UvErr.EPERM);
            return UvErr.IoError(UvErr.EACCES);
        }
        catch
        {
            return UvErr.NotSupported("setting the process priority");
        }
    }

    // Std.Internal.UV.System.osUname : IO UnameInfo
    public static Obj lean_uv_os_uname()
    {
        var (sys, rel, ver) = UvSys.Uname();
        var r = lean_alloc_ctor(0, 4, 0);
        lean_ctor_set(r, 0, lean_mk_string(sys));
        lean_ctor_set(r, 1, lean_mk_string(rel));
        lean_ctor_set(r, 2, lean_mk_string(ver));
        lean_ctor_set(r, 3, lean_mk_string(UvSys.Machine()));
        return lean_io_result_mk_ok(r);
    }

    // Std.Internal.UV.System.hrtime : IO UInt64
    public static Obj lean_uv_hrtime()
    {
        long ts = Stopwatch.GetTimestamp();
        ulong ns = (ulong)((UInt128)(ulong)ts * 1_000_000_000UL / (ulong)Stopwatch.Frequency);
        return lean_io_result_mk_ok(UvUtil.BoxU64(ns));
    }

    // Std.Internal.UV.System.random : UInt64 → IO (IO.Promise (Except IO.Error ByteArray))
    public static Obj lean_uv_random(ulong size)
    {
        if (size > 0x7FFFFFFFUL) return UvErr.IoError(UvErr.E2BIG);
        if (size > (ulong)Array.MaxLength) return UvErr.IoErrorNoMem();
        Obj promise = UvPromise.New();
        Obj byteArray = lean_alloc_sarray(1, 0, size);
        lean_inc(promise);
        Task.Run(() =>
        {
            Obj res;
            try
            {
                RandomNumberGenerator.Fill(new Span<byte>(lean_sarray_cptr(byteArray), 0, (int)size));
                lean_sarray_set_size(byteArray, size);
                res = UvUtil.ExceptOk(byteArray);
            }
            catch (Exception ex)
            {
                lean_dec(byteArray);
                res = UvUtil.ExceptErr(UvErr.Decode(UvErr.FromException(ex)));
            }
            lock (UvLoop.Lock) UvPromise.Resolve(res, promise);
            lean_dec(promise);
        });
        return lean_io_result_mk_ok(promise);
    }

    // Std.Internal.UV.System.getrusage : IO RUsage
    public static Obj lean_uv_getrusage()
    {
        ulong utime = 0, stime = 0, maxrss = 0, minflt = 0, majflt = 0, nvcsw = 0, nivcsw = 0;
        try
        {
            using var p = Process.GetCurrentProcess();
            utime = (ulong)p.UserProcessorTime.TotalMilliseconds;
            stime = (ulong)p.PrivilegedProcessorTime.TotalMilliseconds;
            maxrss = (ulong)Math.Max(p.PeakWorkingSet64, p.WorkingSet64) / 1024;
        }
        catch { }
        if (UvSys.IsLinux)
        {
            var hwm = UvSys.ProcKey("/proc/self/status", "VmHWM");
            if (hwm != null) maxrss = hwm.Value;
            nvcsw = UvSys.ProcKey("/proc/self/status", "voluntary_ctxt_switches") ?? 0;
            nivcsw = UvSys.ProcKey("/proc/self/status", "nonvoluntary_ctxt_switches") ?? 0;
            var st = UvSys.ProcStat("self");
            if (st != null && st.Length > 11)
            {
                ulong.TryParse(st[9], out minflt);
                ulong.TryParse(st[11], out majflt);
            }
        }
        var r = lean_alloc_ctor(0, 0, 16 * 8);
        var vals = new ulong[] { utime, stime, maxrss, 0, 0, 0, minflt, majflt, 0, 0, 0, 0, 0, 0, nvcsw, nivcsw };
        for (uint i = 0; i < 16; i++) lean_ctor_set_uint64_s(r, i * 8, vals[i]);
        return lean_io_result_mk_ok(r);
    }

    // Std.Internal.UV.System.exePath : IO String
    public static Obj lean_uv_exepath()
    {
        string p = Environment.ProcessPath;
        if (string.IsNullOrEmpty(p)) return UvErr.IoError(UvErr.ENOENT, lean_mk_string(""));
        return lean_io_result_mk_ok(lean_mk_string(p));
    }

    // Std.Internal.UV.System.freeMemory : IO UInt64
    public static Obj lean_uv_get_free_memory() => lean_io_result_mk_ok(UvUtil.BoxU64(UvSys.FreeMemory()));

    // Std.Internal.UV.System.totalMemory : IO UInt64
    public static Obj lean_uv_get_total_memory() => lean_io_result_mk_ok(UvUtil.BoxU64(UvSys.TotalMemory()));

    // Std.Internal.UV.System.constrainedMemory : IO UInt64
    public static Obj lean_uv_get_constrained_memory() => lean_io_result_mk_ok(UvUtil.BoxU64(UvSys.ConstrainedMemory()));

    // Std.Internal.UV.System.availableMemory : IO UInt64
    public static Obj lean_uv_get_available_memory() => lean_io_result_mk_ok(UvUtil.BoxU64(UvSys.AvailableMemory()));
}
