// Port of 'message.cpp', 'report.cpp' and (a condensed version of) 'stats.cpp'.

using System.Globalization;
using System.Text;

namespace LeanSharp.Runtime.Cadical;

public sealed unsafe partial class Internal
{
    static string F(string fmt, params object[] args) => string.Format(CultureInfo.InvariantCulture, fmt, args);

    public void print_prefix() => output.Write(prefix);

    public void MSG(string msg)
    {
        if (opts.quiet != 0) return;
        output.Write(prefix);
        output.Write(msg);
        output.Write('\n');
        output.Flush();
    }

    public void MSG()
    {
        if (opts.quiet != 0) return;
        output.Write(prefix);
        output.Write('\n');
        output.Flush();
    }

    public void VERBOSE(int lvl, string msg)
    {
        if (opts.quiet != 0 || lvl > opts.verbose) return;
        output.Write(prefix);
        output.Write(msg);
        output.Write('\n');
        output.Flush();
    }

    public void verbose(int lvl)
    {
        if (opts.quiet != 0 || lvl > opts.verbose) return;
        output.Write(prefix);
        output.Write('\n');
        output.Flush();
    }

    public void section(string title)
    {
        if (opts.quiet != 0) return;
        if (stats.sections++ != 0) MSG();
        var sb = new StringBuilder();
        sb.Append(prefix);
        sb.Append("--- [ ").Append(title).Append(" ] ");
        for (int i = title.Length + prefix.Length + 9; i < 78; i++) sb.Append('-');
        sb.Append('\n');
        output.Write(sb.ToString());
        MSG();
    }

    public bool phase_messages_enabled => opts.quiet == 0 && (force_phase_messages || opts.verbose >= 2);

    public void PHASE(string phase, string msg)
    {
        if (!phase_messages_enabled) return;
        output.Write($"{prefix}[{phase}] {msg}\n");
        output.Flush();
    }

    public void PHASE(string phase, long count, string msg)
    {
        if (!phase_messages_enabled) return;
        output.Write($"{prefix}[{phase}-{count}] {msg}\n");
        output.Flush();
    }

    /// <summary>Error output ('stderr').</summary>
    public TextWriter errout = TextWriter.Null;

    public void warning(string msg)
    {
        output.Flush();
        errout.Write("cadical: warning: " + msg + "\n");
        errout.Flush();
    }

    /// <summary>'error' prints a message and exits the process with status 1 in C++. Here it
    /// throws 'CadicalExit' which the CLI catches.</summary>
    public void error(string msg)
    {
        output.Flush();
        errout.Write("cadical: error: " + msg + "\n");
        errout.Flush();
        throw new CadicalExit(1);
    }

    /*------------------------------------------------------------------------*/

    struct ReportEntry
    {
        public string header;
        public string buffer;
        public int pos;
    }

    static ReportEntry MakeReport(string h, int precision, int min, double value)
    {
        var r = new ReportEntry { header = h };
        string s;
        if (precision < 0) s = value.ToString("F" + (-precision - 1), CultureInfo.InvariantCulture);
        else s = value.ToString("F" + precision, CultureInfo.InvariantCulture);
        int width = s.Length;
        if (precision < 0) s += "%";
        if (width < min)
        {
            if (precision < 0) s = value.ToString("F" + (-precision - 1), CultureInfo.InvariantCulture).PadLeft(min) + "%";
            else s = value.ToString("F" + precision, CultureInfo.InvariantCulture).PadLeft(min);
        }
        r.buffer = s;
        return r;
    }

    public void report(char type, int verbose_level = 0)
    {
        if (opts.report == 0) return;
        if (opts.quiet != 0 || verbose_level > opts.verbose) return;
        if (!reported)
        {
            reported = true;
            MSG(F("time measured in {0} time {1}", opts.realtime != 0 ? "real" : "process",
                opts.reportsolve != 0 ? "in solving" : "since initialization"));
        }
        double t = opts.reportsolve != 0 ? solve_time() : time();
        double mb = Resources.current_resident_set_size() / (double)(1L << 20);
        var reports = new ReportEntry[]
        {
            MakeReport("seconds", 2, 5, t),
            MakeReport("MB", 0, 2, mb),
            MakeReport("level", 0, 2, averages.current.level),
            MakeReport("reductions", 0, 1, stats.reductions),
            MakeReport("restarts", 0, 3, stats.restarts),
            MakeReport("conflicts", 0, 4, stats.conflicts),
            MakeReport("redundant", 0, 4, stats.current.redundant),
            MakeReport("trail", -1, 2, CUtil.percent(averages.current.trail_slow, max_var)),
            MakeReport("glue", 0, 1, averages.current.glue_slow),
            MakeReport("irredundant", 0, 4, stats.current.irredundant),
            MakeReport("variables", 0, 3, active()),
            MakeReport("remaining", -1, 2, CUtil.percent(active(), external.max_var)),
        };
        int n = reports.Length;
        if (lim.report == 0)
        {
            output.Write(prefix + "\n");
            int pos = 4;
            for (int i = 0; i < n; i++)
            {
                int len = reports[i].buffer.Length;
                reports[i].pos = pos + (len + 1) / 2;
                pos += len + 1;
            }
            int max_line = pos + 20, nrows = 3;
            var line = new char[max_line];
            for (int start = 0; start < nrows; start++)
            {
                for (int i = 0; i < max_line; i++) line[i] = ' ';
                for (int i = start; i < n; i += nrows)
                {
                    string header = reports[i].header;
                    int len = header.Length;
                    for (int k = -1, j = reports[i].pos - (len + 1) / 2 - 3; k < len; k++, j++)
                        if (j >= 0 && j < max_line) line[j] = k < 0 ? ' ' : header[k];
                }
                int e;
                for (e = max_line - 1; e > 0 && line[e - 1] == ' '; e--) { }
                output.Write(prefix + new string(line, 0, e) + "\n");
            }
            output.Write(prefix + "\n");
            lim.report = 19;
        }
        else lim.report--;
        var sb = new StringBuilder();
        sb.Append(prefix);
        sb.Append(type);
        for (int i = 0; i < n; i++) sb.Append(' ').Append(reports[i].buffer);
        sb.Append('\n');
        output.Write(sb.ToString());
        output.Flush();
    }

    /*------------------------------------------------------------------------*/

    void PRT(bool all, string label, long count, double rel, string what)
    {
        if (label.StartsWith(" ") && !all) return;
        MSG(F("{0,-17}{1,15}   {2,10:F2} {3}", label, count, rel, what));
    }

    public void print_stats()
    {
        if (opts.quiet != 0) return;
        bool all = opts.verbose > 0 || opts.stats != 0;
        double t = solve_time();
        long propagations = stats.propagations.cover + stats.propagations.probe + stats.propagations.search
            + stats.propagations.transred + stats.propagations.vivify + stats.propagations.walk;
        section("statistics");
        if (all || stats.chrono != 0)
            PRT(all, "chronological:", stats.chrono, CUtil.percent(stats.chrono, stats.conflicts), "%  of conflicts");
        PRT(all, "conflicts:", stats.conflicts, CUtil.relative(stats.conflicts, t), "  per second");
        PRT(all, "decisions:", stats.decisions, CUtil.relative(stats.decisions, t), "  per second");
        if (all || stats.all.eliminated != 0)
            PRT(all, "eliminated:", stats.all.eliminated, CUtil.percent(stats.all.eliminated, stats.vars), "%  of all variables");
        if (all || stats.all.fixed_ != 0)
            PRT(all, "fixed:", stats.all.fixed_, CUtil.percent(stats.all.fixed_, stats.vars), "%  of all variables");
        PRT(all, "learned:", stats.learned.clauses, CUtil.percent(stats.learned.clauses, stats.conflicts), "%  per conflict");
        if (all || stats.minimized != 0)
            PRT(all, "minimized:", stats.minimized, CUtil.percent(stats.minimized, stats.learned.literals), "%  learned literals");
        if (all || stats.shrunken != 0)
            PRT(all, "shrunken:", stats.shrunken, CUtil.percent(stats.shrunken, stats.learned.literals), "%  learned literals");
        if (all || stats.probingphases != 0)
            PRT(all, "probingphases:", stats.probingphases, CUtil.relative(stats.conflicts, stats.probingphases), "  interval");
        PRT(all, "propagations:", propagations, CUtil.relative(propagations / 1e6, t), "  millions per second");
        PRT(all, "reduced:", stats.reduced, CUtil.percent(stats.reduced, stats.conflicts), "%  per conflict");
        PRT(all, "rephased:", stats.rephased.total, CUtil.relative(stats.conflicts, stats.rephased.total), "  interval");
        PRT(all, "restarts:", stats.restarts, CUtil.relative(stats.conflicts, stats.restarts), "  interval");
        if (all || stats.subsumed != 0)
            PRT(all, "subsumed:", stats.subsumed, CUtil.percent(stats.subsumed, stats.subchecks), "%  of all clauses");
        if (all || stats.strengthened != 0)
            PRT(all, "strengthened:", stats.strengthened, CUtil.percent(stats.strengthened, stats.subchecks), "%  of all clauses");
        if (all || stats.all.substituted != 0)
            PRT(all, "substituted:", stats.all.substituted, CUtil.percent(stats.all.substituted, stats.vars), "%  of all variables");
        if (all || stats.vivifications != 0)
            PRT(all, "vivified:", stats.vivifysubs + stats.vivifystrs, CUtil.percent(stats.vivifysubs + stats.vivifystrs, stats.vivifychecks), "%  checks");
        MSG();
        MSG(F("seconds are measured in {0} time for solving", opts.realtime != 0 ? "real" : "process"));
    }

    public void print_resource_usage()
    {
        if (opts.quiet != 0) return;
        section("resources");
        ulong m = Resources.maximum_resident_set_size();
        MSG(F("total process time since initialization: {0,12:F2}    seconds", process_time()));
        MSG(F("total real time since initialization:    {0,12:F2}    seconds", real_time()));
        MSG(F("maximum resident set size of process:    {0,12:F2}    MB", m / (double)(1L << 20)));
    }
}

/// <summary>Thrown by 'error' (the C++ code calls 'exit (1)').</summary>
public sealed class CadicalExit : Exception
{
    public readonly int code;
    public CadicalExit(int code) : base("cadical exit " + code) { this.code = code; }
}
