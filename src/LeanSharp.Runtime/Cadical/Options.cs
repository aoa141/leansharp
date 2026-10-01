// Port of 'options.hpp' / 'options.cpp' / 'config.cpp'.

namespace LeanSharp.Runtime.Cadical;

public sealed class OptionInfo
{
    public readonly string name;
    public readonly int def, lo, hi;
    public readonly int optimizable;
    public readonly bool preprocessing;
    public readonly bool resettable;
    public readonly string description;
    public readonly string defStr, loStr, hiStr;
    public readonly int index;

    public OptionInfo(string name, int def, int lo, int hi, int optimizable, bool preprocessing, bool resettable,
                      string description, string defStr, string loStr, string hiStr, int index)
    {
        this.name = name; this.def = def; this.lo = lo; this.hi = hi; this.optimizable = optimizable;
        this.preprocessing = preprocessing; this.resettable = resettable; this.description = description;
        this.defStr = defStr; this.loStr = loStr; this.hiStr = hiStr; this.index = index;
    }
}

public sealed partial class Options
{
    /// <summary>Static table used for name lookups (default of 'report' is the library default 0).</summary>
    public static readonly OptionInfo[] s_table = MakeTable(0);
    static readonly Dictionary<string, OptionInfo> s_byName = BuildIndex();

    static Dictionary<string, OptionInfo> BuildIndex()
    {
        var d = new Dictionary<string, OptionInfo>(StringComparer.Ordinal);
        foreach (var o in s_table) d[o.name] = o;
        return d;
    }

    /// <summary>Per instance table (the default of 'report' depends on library vs. stand alone usage).</summary>
    public readonly OptionInfo[] table;
    readonly Internal internal_;

    public Options(Internal internal_, int reportdefault)
    {
        this.internal_ = internal_;
        table = MakeTable(reportdefault);
        foreach (var o in table) val(o.index) = o.def;
        // environment overrides
        foreach (var o in table)
        {
            string key = "CADICAL_" + o.name.ToUpperInvariant();
            string v = null;
            try { v = Environment.GetEnvironmentVariable(key); } catch { }
            if (v == null) continue;
            if (!CUtil.parse_int_str(v, out int x)) continue;
            if (x < o.lo) x = o.lo;
            if (x > o.hi) x = o.hi;
            val(o.index) = x;
        }
    }

    public static OptionInfo has(string name) => name != null && s_byName.TryGetValue(name, out var o) ? o : null;

    public static bool parse_long_option(string arg, out string name, out int value)
    {
        name = null; value = 0;
        if (arg.Length < 2 || arg[0] != '-' || arg[1] != '-') return false;
        bool has_no_prefix = arg.Length >= 5 && arg[2] == 'n' && arg[3] == 'o' && arg[4] == '-';
        int offset = has_no_prefix ? 5 : 2;
        string rest = arg.Substring(offset);
        int pos = rest.IndexOf('=');
        name = pos >= 0 ? rest.Substring(0, pos) : rest;
        // C++ truncates at an embedded NUL as well
        int nul = name.IndexOf('\0');
        if (nul >= 0) name = name.Substring(0, nul);
        if (has(name) == null) return false;
        if (pos < 0) value = has_no_prefix ? 0 : 1;
        else
        {
            if (!CUtil.parse_int_str(rest.Substring(pos + 1), out value)) return false;
        }
        return true;
    }

    void set(OptionInfo o, int new_val)
    {
        ref int v = ref val(o.index);
        if (v == new_val) return;
        if (new_val < o.lo) new_val = o.lo;
        if (new_val > o.hi) new_val = o.hi;
        v = new_val;
    }

    public bool set(string name, int v)
    {
        var o = has(name);
        if (o == null) return false;
        set(table[o.index], v);
        return true;
    }

    public int get(string name)
    {
        var o = has(name);
        return o != null ? val(o.index) : 0;
    }

    public void print()
    {
        var internal_ = this.internal_;
        int different = 0;
        bool verbose_ = verbose != 0;
        foreach (var o in table)
        {
            int N = val(o.index);
            if (N != o.def) different++;
            if (verbose_ || N != o.def)
            {
                if (o.lo == 0 && o.hi == 1)
                {
                    string buffer = "--" + o.name + "=" + (N != 0 ? "true" : "false");
                    internal_.MSG(string.Format("  {0,-30} ({1} default '{2}')", buffer,
                        N == o.def ? "same as" : "different from", o.def != 0 ? "true" : "false"));
                }
                else
                {
                    string buffer = "--" + o.name + "=" + N;
                    internal_.MSG(string.Format("  {0,-30} ({1} default '{2}')", buffer,
                        N == o.def ? "same as" : "different from", o.defStr));
                }
            }
        }
        if (different == 0) internal_.MSG("all options are set to their default value");
    }

    public static void usage(TextWriter w)
    {
        foreach (var o in s_table)
        {
            if (o.lo == 0 && o.hi == 1)
                w.Write(string.Format("  {0,-26} {1} [{2}]\n", "--" + o.name + "=bool", o.description, o.def != 0 ? "true" : "false"));
            else
                w.Write(string.Format("  {0,-26} {1} [{2}]\n", "--" + o.name + "=" + o.loStr + ".." + o.hiStr, o.description, o.defStr));
        }
    }

    public void optimize(int v)
    {
        if (v < 0) return;
        const int max_val = 31;
        if (v > max_val) v = max_val;
        long factor2 = 1;
        for (int i = 0; i < v && factor2 <= int.MaxValue; i++) factor2 *= 2;
        long factor10 = 1;
        for (int i = 0; i < v && factor10 <= int.MaxValue; i++) factor10 *= 10;
        uint increased = 0;
        foreach (var o in table)
        {
            if (o.optimizable == 0) continue;
            long factor = o.optimizable == 1 ? factor2 : factor10;
            long new_val = factor * (long)o.def;
            if (new_val > o.hi) new_val = o.hi;
            if (new_val == o.def) continue;
            val(o.index) = (int)new_val;
            increased++;
        }
        if (increased != 0) internal_.MSG($"optimization mode '-O{v}' increased {increased} limits");
    }

    public void disable_preprocessing()
    {
        foreach (var o in table)
        {
            if (!o.preprocessing) continue;
            if (val(o.index) == 0) continue;
            val(o.index) = 0;
        }
    }

    public static bool is_preprocessing_option(string name)
    {
        var o = has(name);
        return o != null && o.preprocessing;
    }

    public void reset_default_values()
    {
        foreach (var o in table)
        {
            if (!o.resettable) continue;
            if (val(o.index) == o.def) continue;
            val(o.index) = o.def;
        }
    }

    public void copy(Options other)
    {
        foreach (var o in table)
        {
            int N = val(o.index);
            if (N != o.def) other.val(o.index) = N;
        }
    }
}

/// <summary>'config.cpp'</summary>
public static class Config
{
    static readonly string[] configs = { "default", "plain", "sat", "unsat" };
    static readonly string[] descriptions = {
        "set default advanced internal options",
        "disable all internal preprocessing options",
        "set internal options to target satisfiable instances",
        "set internal options to target unsatisfiable instances",
    };
    static readonly (string, int)[] sat_config = { ("elimreleff", 10), ("stabilizeonly", 1), ("subsumereleff", 60) };
    static readonly (string, int)[] unsat_config = { ("stabilize", 0), ("walk", 0) };

    public static bool has(string name) => Array.IndexOf(configs, name) >= 0;

    public static bool set(Options opts, string name)
    {
        switch (name)
        {
            case "default": opts.reset_default_values(); return true;
            case "plain": opts.disable_preprocessing(); return true;
            case "sat": foreach (var (n, v) in sat_config) opts.set(n, v); return true;
            case "unsat": foreach (var (n, v) in unsat_config) opts.set(n, v); return true;
        }
        return false;
    }

    public static void usage(TextWriter w)
    {
        for (int i = 0; i < configs.Length; i++)
            w.Write(string.Format("  {0,-14} {1}\n", "--" + configs[i], descriptions[i]));
    }

    public static string[] all => configs;
}
