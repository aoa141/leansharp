// Port of 'cadical.cpp' (the stand alone 'cadical' application). The host routes spawns of the
// 'cadical' executable to 'CadicalCli.Main'.

using System.IO.Compression;
using System.Text;

namespace LeanSharp.Runtime.Cadical;

public static class CadicalCli
{
    /// <summary>Run the CaDiCaL command line application. Returns the process exit code
    /// (10 = satisfiable, 20 = unsatisfiable, 0 = unknown, 1 = error).</summary>
    public static int Main(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
        => Main(args, stdin, stdout, stderr, null, default);

    /// <summary>Same as above; relative paths are resolved against 'cwd' (if non-null) and the
    /// solver is terminated asynchronously when 'kill' is cancelled (as on 'SIGKILL'; the result
    /// is then 'c UNKNOWN' with exit code 0 unless the caller already treats the child as killed).</summary>
    public static int Main(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr, string cwd, CancellationToken kill)
    {
        var app = new App(stdin, stdout, stderr) { cwd = cwd, kill = kill };
        try
        {
            return app.main(args);
        }
        catch (CadicalExit e)
        {
            stdout.Flush();
            return e.code;
        }
        catch (CadicalException e)
        {
            stdout.Flush();
            stderr.Write("cadical: fatal error: " + e.Message + "\n");
            stderr.Flush();
            return 134; // 'abort ()'
        }
        finally
        {
            app.cleanup();
        }
    }

    sealed class App
    {
        readonly TextReader stdin;
        readonly TextWriter stdout, stderr;
        Solver solver;
        int time_limit = -1;
        int force_strict_parsing = 1;
        bool force_writing;
        int max_var;
        volatile bool timesup;
        Timer timer;
        public string cwd;
        public CancellationToken kill;

        string R(string path) => cwd == null || path == null || Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);

        public App(TextReader i, TextWriter o, TextWriter e) { stdin = i; stdout = o; stderr = e; }

        public void cleanup()
        {
            timer?.Dispose();
            timer = null;
        }

        void print_usage(bool all = false)
        {
            var w = stdout;
            w.Write("usage: cadical [ <option> ... ] [ <input> [ <proof> ] ]\n\nwhere '<option>' is one of the following common options:\n\n");
            if (!all)
            {
                w.Write("  -h             print this short list of common options\n" +
                        "  --help         print complete list of all options\n" +
                        "  --version      print version\n\n" +
                        "  -n             do not print witness\n" +
                        "  -v             increase verbosity\n" +
                        "  -q             be quiet\n\n" +
                        "  -t <sec>       set wall clock time limit\n");
            }
            else
            {
                w.Write("  -h             print alternatively only a list of common options\n" +
                        "  --help         print this complete list of all options\n" +
                        "  --version      print version\n\n" +
                        "  -n             do not print witness (same as '--no-witness')\n" +
                        "  -v             increase verbosity (see also '--verbose' below)\n" +
                        "  -q             be quiet (same as '--quiet')\n" +
                        "  -t <sec>       set wall clock time limit\n\n" +
                        "Or '<option>' is one of the less common options\n\n" +
                        "  -L<rounds>     run local search initially (default '0' rounds)\n" +
                        "  -O<level>      increase limits by '2^<level>' or '10^<level>'\n" +
                        "  -P<rounds>     initial preprocessing (default '0' rounds)\n\n" +
                        "Note there is no separating space for the options above while the\n" +
                        "following options require a space after the option name:\n\n" +
                        "  -c <limit>     limit the number of conflicts (default unlimited)\n" +
                        "  -d <limit>     limit the number of decisions (default unlimited)\n\n" +
                        "  -o <output>    write simplified CNF in DIMACS format to file\n" +
                        "  -e <extend>    write reconstruction/extension stack to file\n\n" +
                        "  --force | -f   parsing broken DIMACS header and writing proofs\n" +
                        "  --strict       strict parsing (no white space in header)\n\n" +
                        "  -r <sol>       read solution in competition output format\n" +
                        "                 to check consistency of learned clauses\n" +
                        "                 during testing and debugging\n\n" +
                        "  -w <sol>       write result including a potential witness\n" +
                        "                 solution in competition format to the given file\n\n" +
                        "  --colors       force colored output\n" +
                        "  --no-colors    disable colored output to terminal\n" +
                        "  --no-witness   do not print witness (see also '-n' above)\n\n" +
                        "  --build        print build configuration\n" +
                        "  --copyright    print copyright information\n");
                w.Write("\nThere are pre-defined configurations of advanced internal options:\n\n");
                Solver.configurations(w);
                w.Write("\nOr '<option>' is one of the following advanced internal options:\n\n");
                Solver.usage(w);
                w.Write("\nThe internal options have their default value printed in brackets\n" +
                        "after their description.  They can also be used in the form\n" +
                        "'--<name>' which is equivalent to '--<name>=1' and in the form\n" +
                        "'--no-<name>' which is equivalent to '--<name>=0'.  One can also\n" +
                        "use 'true' instead of '1', 'false' instead of '0', as well as\n" +
                        "numbers with positive exponent such as '1e3' instead of '1000'.\n\n" +
                        "Alternatively option values can also be specified in the header\n" +
                        "of the DIMACS file, e.g., 'c --elim=false', or through environment\n" +
                        "variables, such as 'CADICAL_ELIM=false'.  The embedded options in\n" +
                        "the DIMACS file have highest priority, followed by command line\n" +
                        "options and then values specified through environment variables.\n");
            }
            w.Write("\nThe input is read from '<input>' assumed to be in DIMACS format.\n" +
                    "Incremental 'p inccnf' files are supported too with cubes at the end.\n" +
                    "If '<proof>' is given then a DRAT proof is written to that file.\n");
            if (all)
            {
                w.Write("\nIf '<input>' is missing then the solver reads from '<stdin>',\n" +
                        "also if '-' is used as input path name '<input>'.  Similarly,\n\n" +
                        "For incremental files each cube is solved in turn. The solver\n" +
                        "stops at the first satisfied cube if there is one and uses that\n" +
                        "one for the witness to print.  Conflict and decision limits are\n" +
                        "applied to each individual cube solving call while '-P', '-L' and\n" +
                        "'-t' remain global.  Only if all cubes were unsatisfiable the solver\n" +
                        "prints the standard unsatisfiable solution line ('s UNSATISFIABLE').\n\n" +
                        "By default the proof is stored in the binary DRAT format unless\n" +
                        "the option '--no-binary' is specified or the proof is written\n" +
                        "to  '<stdout>' and '<stdout>' is connected to a terminal.\n\n" +
                        "The input is assumed to be compressed if it is given explicitly\n" +
                        "and has a '.gz' suffix.\n");
            }
            w.Flush();
        }

        void print_witness(TextWriter file)
        {
            var sb = new StringBuilder();
            int c = 0, i = 0, tmp;
            do
            {
                if (c == 0) { sb.Append('v'); c = 1; }
                if (i++ == max_var) tmp = 0;
                else tmp = solver.val(i) < 0 ? -i : i;
                string str = " " + tmp.ToString(System.Globalization.CultureInfo.InvariantCulture);
                int l = str.Length;
                if (c + l > 78) { sb.Append("\nv"); c = 1; }
                sb.Append(str);
                c += l;
                if (sb.Length > (1 << 16)) { file.Write(sb.ToString()); sb.Clear(); }
            } while (tmp != 0);
            if (c != 0) sb.Append('\n');
            file.Write(sb.ToString());
        }

        int get(string o) => solver.get(o);
        bool set(string o, int v) => solver.set(o, v);
        bool set(string arg) => solver.set_long_option(arg);
        bool verbose() => get("verbose") != 0 && get("quiet") == 0;

        static bool most_likely_existing_cnf_file(string path)
        {
            if (!CFile.exists(path)) return false;
            foreach (var suffix in new[] { ".dimacs", ".dimacs.gz", ".dimacs.xz", ".dimacs.bz2", ".dimacs.7z",
                                           ".dimacs.lzma", ".cnf", ".cnf.gz", ".cnf.xz", ".cnf.bz2", ".cnf.7z", ".cnf.lzma" })
                if (CUtil.has_suffix(path, suffix)) return true;
            return false;
        }

        void APPERR(string msg) => solver.error(msg);

        void init()
        {
            time_limit = -1;
            force_strict_parsing = 1;
            force_writing = false;
            max_var = 0;
            timesup = false;
            solver = new Solver(1, stdout, stderr);
        }

        static string copyright() => "Copyright (c) 2016-2024 A. Biere, M. Fleury, N. Froleyks, K. Fazekas, F. Pollitt, T. Faller";
        static string authors() => "Armin Biere, Mathias Fleury, Nils Froleyks, Katalin Fazekas, Florian Pollitt, Tobias Faller";
        static string affiliations() => "University of Freiburg, Johannes Kepler University Linz";

        public int main(string[] argv)
        {
            int argc = argv.Length;
            if (argc == 1)
            {
                string arg = argv[0];
                if (arg == "-h") { print_usage(); return 0; }
                else if (arg == "--help") { print_usage(true); return 0; }
                else if (arg == "--version") { stdout.Write(Solver.version() + "\n"); stdout.Flush(); return 0; }
                else if (arg == "--build") { Solver.build(stdout, ""); return 0; }
                else if (arg == "--copyright")
                {
                    stdout.Write(copyright() + "\n" + authors() + "\n" + affiliations() + "\n");
                    stdout.Flush();
                    return 0;
                }
            }

            init();

            string preprocessing_specified = null, optimization_specified = null;
            string read_solution_path = null, write_result_path = null;
            string dimacs_path = null, proof_path = null;
            bool proof_specified = false, dimacs_specified = false;
            int optimize = 0, preprocessing = 0, localsearch = 0;
            string output_path = null, extension_path = null;
            int conflict_limit = -1, decision_limit = -1;
            string conflict_limit_specified = null, decision_limit_specified = null;
            string localsearch_specified = null, time_limit_specified = null;
            bool witness = true, status = true;
            string err;

            for (int i = 0; i < argc; i++)
            {
                string a = argv[i];
                if (a == "-h" || a == "--help" || a == "--build" || a == "--version" || a == "--copyright")
                    APPERR($"can only use '{a}' as single first option");
                else if (a == "-")
                {
                    if (proof_specified) APPERR("too many arguments");
                    else if (!dimacs_specified) dimacs_specified = true;
                    else proof_specified = true;
                }
                else if (a == "-r")
                {
                    if (++i == argc) APPERR("argument to '-r' missing");
                    else if (read_solution_path != null) APPERR($"multiple read solution file options '-r {read_solution_path}' and '-r {argv[i]}'");
                    else read_solution_path = argv[i];
                }
                else if (a == "-w")
                {
                    if (++i == argc) APPERR("argument to '-w' missing");
                    else if (write_result_path != null) APPERR($"multiple solution file options '-w {write_result_path}' and '-w {argv[i]}'");
                    else write_result_path = argv[i];
                }
                else if (a == "-o")
                {
                    if (++i == argc) APPERR("argument to '-o' missing");
                    else if (output_path != null) APPERR($"multiple output file options '-o {output_path}' and '-o {argv[i]}'");
                    else if (!force_writing && most_likely_existing_cnf_file(R(argv[i]))) APPERR($"output file '{argv[i]}' most likely existing CNF (use '-f')");
                    else if (!CFile.writable(R(argv[i]))) APPERR($"output file '{argv[i]}' not writable");
                    else output_path = argv[i];
                }
                else if (a == "-e")
                {
                    if (++i == argc) APPERR("argument to '-e' missing");
                    else if (extension_path != null) APPERR($"multiple extension file options '-e {extension_path}' and '-e {argv[i]}'");
                    else if (!force_writing && most_likely_existing_cnf_file(R(argv[i]))) APPERR($"extension file '{argv[i]}' most likely existing CNF (use '-f')");
                    else if (!CFile.writable(R(argv[i]))) APPERR($"extension file '{argv[i]}' not writable");
                    else extension_path = argv[i];
                }
                else if (CUtil.is_color_option(a)) { }
                else if (CUtil.is_no_color_option(a)) { }
                else if (a == "--witness" || a == "--witness=true" || a == "--witness=1") witness = true;
                else if (a == "-n" || a == "--no-witness" || a == "--witness=false" || a == "--witness=0") witness = false;
                else if (a == "--status" || a == "--status=true" || a == "--status=1") status = true;
                else if (a == "--no-status" || a == "--status=false" || a == "--status=0") status = false;
                else if (a == "--less") APPERR("'--less' without '<stdout>' connected to terminal");
                else if (a == "-c")
                {
                    if (++i == argc) APPERR("argument to '-c' missing");
                    else if (conflict_limit_specified != null) APPERR($"multiple conflict limits '-c {conflict_limit_specified}' and '-c {argv[i]}'");
                    else if (!CUtil.parse_int_str(argv[i], out conflict_limit)) APPERR($"invalid argument in '-c {argv[i]}'");
                    else if (conflict_limit < 0) APPERR("invalid conflict limit");
                    else conflict_limit_specified = argv[i];
                }
                else if (a == "-d")
                {
                    if (++i == argc) APPERR("argument to '-d' missing");
                    else if (decision_limit_specified != null) APPERR($"multiple decision limits '-d {decision_limit_specified}' and '-d {argv[i]}'");
                    else if (!CUtil.parse_int_str(argv[i], out decision_limit)) APPERR($"invalid argument in '-d {argv[i]}'");
                    else if (decision_limit < 0) APPERR("invalid decision limit");
                    else decision_limit_specified = argv[i];
                }
                else if (a == "-t")
                {
                    if (++i == argc) APPERR("argument to '-t' missing");
                    else if (time_limit_specified != null) APPERR($"multiple time limit '-t {time_limit_specified}' and '-t {argv[i]}'");
                    else if (!CUtil.parse_int_str(argv[i], out time_limit)) APPERR($"invalid argument in '-d {argv[i]}'");
                    else if (time_limit < 0) APPERR("invalid time limit");
                    else time_limit_specified = argv[i];
                }
                else if (a == "-q") set("--quiet");
                else if (a == "-v") set("verbose", get("verbose") + 1);
                else if (a == "-f" || a == "--force" || a == "--force=1" || a == "--force=true")
                {
                    force_strict_parsing = 0;
                    force_writing = true;
                }
                else if (a == "--strict" || a == "--strict=1" || a == "--strict=true") force_strict_parsing = 2;
                else if (CUtil.has_prefix(a, "-O"))
                {
                    if (optimization_specified != null) APPERR($"multiple optimization options '{optimization_specified}' and '{a}'");
                    optimization_specified = a;
                    if (!CUtil.parse_int_str(a.Substring(2), out optimize)) APPERR($"invalid optimization option '{a}'");
                    if (optimize < 0 || optimize > 31) APPERR($"invalid argument in '{a}' (expected '0..31')");
                }
                else if (CUtil.has_prefix(a, "-P"))
                {
                    if (preprocessing_specified != null) APPERR($"multiple preprocessing options '{preprocessing_specified}' and '{a}'");
                    preprocessing_specified = a;
                    if (!CUtil.parse_int_str(a.Substring(2), out preprocessing)) APPERR($"invalid preprocessing option '{a}'");
                    if (preprocessing < 0) APPERR($"invalid argument in '{a}' (expected non-negative number)");
                }
                else if (CUtil.has_prefix(a, "-L"))
                {
                    if (localsearch_specified != null) APPERR($"multiple local search options '{localsearch_specified}' and '{a}'");
                    localsearch_specified = a;
                    if (!CUtil.parse_int_str(a.Substring(2), out localsearch)) APPERR($"invalid local search option '{a}'");
                    if (localsearch < 0) APPERR($"invalid argument in '{a}' (expected non-negative number)");
                }
                else if (CUtil.has_prefix(a, "--") && Solver.is_valid_configuration(a.Substring(2)))
                    solver.configure(a.Substring(2));
                else if (set(a)) { }
                else if (a.Length > 0 && a[0] == '-') APPERR($"invalid option '{a}'");
                else if (proof_specified) APPERR("too many arguments");
                else if (dimacs_specified)
                {
                    proof_path = a;
                    proof_specified = true;
                    if (!force_writing && most_likely_existing_cnf_file(R(proof_path)))
                        APPERR($"DRAT proof file '{proof_path}' most likely existing CNF (use '-f')");
                    else if (!CFile.writable(R(proof_path)))
                        APPERR($"DRAT proof file '{proof_path}' not writable");
                }
                else
                {
                    dimacs_specified = true;
                    dimacs_path = a;
                }
            }

            if (dimacs_specified && dimacs_path != null && !CFile.exists(R(dimacs_path)))
                APPERR($"DIMACS input file '{dimacs_path}' does not exist");
            if (read_solution_path != null && !CFile.exists(R(read_solution_path)))
                APPERR($"solution file '{read_solution_path}' does not exist");
            if (dimacs_specified && dimacs_path != null && proof_specified && proof_path != null &&
                dimacs_path == proof_path && dimacs_path != "-")
                APPERR($"DIMACS input file '{dimacs_path}' also specified as DRAT proof file");

            if (read_solution_path != null && get("check") == 0) set("--check");
            if (get("quiet") == 0)
            {
                solver.section("banner");
                solver.message("CaDiCaL SAT Solver");
                solver.message(copyright());
                solver.message(authors());
                solver.message(affiliations());
                solver.message();
                Solver.build(stdout, "c ");
            }
            if (preprocessing > 0 || localsearch > 0 || time_limit >= 0 || conflict_limit >= 0 || decision_limit >= 0)
            {
                solver.section("limit");
                if (preprocessing > 0)
                {
                    solver.message($"enabling {preprocessing} initial rounds of preprocessing (due to '{preprocessing_specified}')");
                    solver.limit("preprocessing", preprocessing);
                }
                if (localsearch > 0)
                {
                    solver.message($"enabling {localsearch} initial rounds of local search (due to '{localsearch_specified}')");
                    solver.limit("localsearch", localsearch);
                }
                if (time_limit >= 0)
                {
                    solver.message($"setting time limit to {time_limit} seconds real time (due to '-t {time_limit_specified}')");
                    timer = new Timer(_ => timesup = true, null, TimeSpan.FromSeconds(time_limit), Timeout.InfiniteTimeSpan);
                    solver.connect_terminator(() => timesup);
                    // (replaced below if a kill token is given)
                }
                if (conflict_limit >= 0)
                {
                    solver.message($"setting conflict limit to {conflict_limit} conflicts (due to '{conflict_limit_specified}')");
                    solver.limit("conflicts", conflict_limit);
                }
                if (decision_limit >= 0)
                {
                    solver.message($"setting decision limit to {decision_limit} decisions (due to '{decision_limit_specified}')");
                    solver.limit("decisions", decision_limit);
                }
            }
            if (kill.CanBeCanceled)
            {
                if (time_limit < 0) solver.connect_terminator(() => kill.IsCancellationRequested);
                else solver.connect_terminator(() => timesup || kill.IsCancellationRequested);
            }
            if (verbose() || proof_specified) solver.section("proof tracing");
            Stream proof_stdout_stream = null;
            if (proof_specified)
            {
                if (proof_path == null)
                {
                    // Proof to '<stdout>' (we can not determine whether it is a terminal).
                    proof_stdout_stream = new TextWriterStream(stdout);
                    solver.message($"writing {(get("binary") != 0 ? "binary" : "non-binary")} proof trace to '<stdout>'");
                    solver.trace_proof(proof_stdout_stream, "<stdout>");
                }
                else if (!solver.trace_proof(R(proof_path)))
                    APPERR($"can not open and write proof trace to '{proof_path}'");
                else
                    solver.message($"writing {(get("binary") != 0 ? "binary" : "non-binary")} proof trace to '{proof_path}'");
            }
            else solver.verbose(1, "will not generate nor write DRAT proof");
            solver.section("parsing input");
            string dimacs_name = dimacs_path ?? "<stdin>";
            string help = dimacs_path == null ? " (use '-h' for a list of common options)" : "";
            solver.message($"reading DIMACS file from '{dimacs_name}'{help}");
            var cube_literals = new Vec<int>();
            bool incremental;
            if (dimacs_path != null)
            {
                Stream s;
                try { s = OpenInput(R(dimacs_path)); }
                catch { s = null; }
                if (s == null) { err = $"failed to read DIMACS file '{dimacs_path}'"; incremental = false; }
                else using (s) err = solver.read_dimacs(s, dimacs_path, out max_var, force_strict_parsing, out incremental, cube_literals);
            }
            else
            {
                using var s = new TextReaderStream(stdin);
                err = solver.read_dimacs(s, dimacs_name, out max_var, force_strict_parsing, out incremental, cube_literals);
            }
            if (err != null) APPERR(err);
            if (read_solution_path != null)
            {
                solver.section("parsing solution");
                solver.message($"reading solution file from '{read_solution_path}'");
                if ((err = solver.read_solution(R(read_solution_path))) != null) APPERR(err);
            }

            solver.section("options");
            if (optimize > 0)
            {
                solver.optimize(optimize);
                solver.message();
            }
            solver.options();

            int res = 0;
            if (incremental)
            {
                bool reporting = get("report") > 1 || get("verbose") > 0;
                if (!reporting) set("report", 0);
                if (!reporting) solver.section("incremental solving");
                int cubes = 0, solved = 0;
                int satisfiable = 0, unsatisfiable = 0, inconclusive = 0;
                for (int k = 0; k < cube_literals.n; k++) if (cube_literals[k] == 0) cubes++;
                if (!reporting)
                {
                    if (cubes != 0) { solver.message($"starting to solve {cubes} cubes"); solver.message(); }
                    else solver.message("no cube to solve");
                }
                var cube = new Vec<int>();
                var failed = new Vec<int>();
                for (int k = 0; k < cube_literals.n; k++)
                {
                    int lit = cube_literals[k];
                    if (lit != 0) cube.push_back(lit);
                    else
                    {
                        cube.reverse();
                        for (int q = 0; q < cube.n; q++) solver.assume(cube[q]);
                        if (solved++ != 0)
                        {
                            if (conflict_limit >= 0) solver.limit("conflicts", conflict_limit);
                            if (decision_limit >= 0) solver.limit("decisions", decision_limit);
                        }
                        res = solver.solve();
                        if (res == 10)
                        {
                            satisfiable++;
                            solver.conclude();
                            break;
                        }
                        else if (res == 20)
                        {
                            unsatisfiable++;
                            solver.conclude();
                            for (int q = 0; q < cube.n; q++) if (solver.failed(cube[q])) failed.push_back(cube[q]);
                            for (int q = 0; q < failed.n; q++) solver.add(-failed[q]);
                            solver.add(0);
                            failed.clear();
                        }
                        else
                        {
                            inconclusive++;
                            if (timesup || kill.IsCancellationRequested) break;
                        }
                        cube.clear();
                    }
                }
                solver.section("incremental summary");
                solver.message($"{solved} cubes solved {CUtil.percent(solved, cubes):F0}%");
                solver.message($"{inconclusive} cubes inconclusive {CUtil.percent(inconclusive, solved):F0}%");
                solver.message($"{unsatisfiable} cubes unsatisfiable {CUtil.percent(unsatisfiable, solved):F0}%");
                solver.message($"{satisfiable} cubes satisfiable {CUtil.percent(satisfiable, solved):F0}%");
                if (inconclusive != 0 && res == 20) res = 0;
            }
            else
            {
                solver.section("solving");
                res = solver.solve();
            }

            if (proof_specified)
            {
                solver.section("closing proof");
                solver.close_proof_trace(get("quiet") == 0);
            }

            if (output_path != null)
            {
                solver.section("writing output");
                solver.message($"writing simplified CNF to DIMACS file '{output_path}'");
                err = solver.write_dimacs(R(output_path), max_var);
                if (err != null) APPERR(err);
            }

            if (extension_path != null)
            {
                solver.section("writing extension");
                solver.message($"writing extension stack to '{extension_path}'");
                err = solver.write_extension(R(extension_path));
                if (err != null) APPERR(err);
            }

            solver.section("result");

            TextWriter write_result_file = stdout;
            StreamWriter result_writer = null;
            if (write_result_path != null)
            {
                try { result_writer = new StreamWriter(R(write_result_path), false, new UTF8Encoding(false)); }
                catch { APPERR($"could not write solution to '{write_result_path}'"); }
                write_result_file = result_writer;
                solver.message($"writing result to '{write_result_path}'");
            }

            if (res == 10)
            {
                if (status) write_result_file.Write("s SATISFIABLE\n");
                if (witness) print_witness(write_result_file);
            }
            else if (res == 20 && status) write_result_file.Write("s UNSATISFIABLE\n");
            else if (status) write_result_file.Write("c UNKNOWN\n");
            write_result_file.Flush();
            result_writer?.Dispose();
            solver.statistics();
            solver.resources();
            solver.section("shutting down");
            solver.message($"exit {res}");
            stdout.Flush();
            return res;
        }

        static Stream OpenInput(string path)
        {
            Stream s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            if (CUtil.has_suffix(path, ".gz"))
            {
                // check the gzip signature, fall back to plain reading otherwise
                int b1 = s.ReadByte(), b2 = s.ReadByte();
                s.Seek(0, SeekOrigin.Begin);
                if (b1 == 0x1f && b2 == 0x8b) return new GZipStream(s, CompressionMode.Decompress);
            }
            return s;
        }
    }

    /// <summary>Byte stream over a 'TextReader' (characters are mapped to bytes by UTF-8).</summary>
    sealed class TextReaderStream : Stream
    {
        readonly TextReader reader;
        readonly char[] cbuf = new char[4096];
        byte[] pending = new byte[0];
        int ppos;
        public TextReaderStream(TextReader r) { reader = r; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (ppos == pending.Length)
            {
                int n = reader.Read(cbuf, 0, cbuf.Length);
                if (n <= 0) return 0;
                pending = Encoding.UTF8.GetBytes(cbuf, 0, n);
                ppos = 0;
            }
            int m = Math.Min(count, pending.Length - ppos);
            Array.Copy(pending, ppos, buffer, offset, m);
            ppos += m;
            return m;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Byte stream writing to a 'TextWriter' (bytes are written as Latin-1 characters).</summary>
    sealed class TextWriterStream : Stream
    {
        readonly TextWriter writer;
        public TextWriterStream(TextWriter w) { writer = w; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => writer.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            var chars = new char[count];
            for (int i = 0; i < count; i++) chars[i] = (char)buffer[offset + i];
            writer.Write(chars);
        }
    }
}
