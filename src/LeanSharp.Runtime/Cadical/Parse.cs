// Port of 'parse.cpp' and the reading part of 'file.cpp'.

namespace LeanSharp.Runtime.Cadical;

public sealed class CInputFile
{
    readonly Stream stream;
    readonly byte[] buffer = new byte[1 << 16];
    int pos, len;
    public readonly string name;
    public ulong lineno = 1;
    public ulong bytes;
    public const int EOF = -1;

    public CInputFile(Stream s, string name) { stream = s; this.name = name; }

    public int get()
    {
        if (pos == len)
        {
            len = stream.Read(buffer, 0, buffer.Length);
            pos = 0;
            if (len <= 0) { len = 0; return EOF; }
        }
        int res = buffer[pos++];
        if (res == '\n') lineno++;
        bytes++;
        return res;
    }
}

public sealed class Parser
{
    readonly Solver solver;
    readonly Internal internal_;
    readonly External external;
    readonly CInputFile file;
    readonly Vec<int> cubes;

    const int FORCED = 0, RELAXED = 1, STRICT = 2;
    const int EOF = CInputFile.EOF;
    const string cube_token = "unexpected 'a' in CNF";

    public Parser(Solver s, CInputFile f, Vec<int> cubes)
    {
        solver = s; internal_ = s.internal_; external = s.external; file = f; this.cubes = cubes;
    }

    string PER(string msg) => $"{file.name}:{file.lineno}: parse error: {msg}";

    int parse_char() => file.get();

    static bool isdigit(int ch) => ch >= '0' && ch <= '9';
    static bool isspace(int ch) => ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '\v' || ch == '\f';

    string parse_string(string str, char prev)
    {
        foreach (char p in str)
        {
            if (parse_char() == p) prev = p;
            else if (p == ' ') return PER($"expected space after '{prev}'");
            else return PER($"expected '{p}' after '{prev}'");
        }
        return null;
    }

    string parse_positive_int(ref int ch, out int res, string name)
    {
        res = ch - '0';
        while (isdigit(ch = parse_char()))
        {
            int digit = ch - '0';
            if (int.MaxValue / 10 < res || int.MaxValue - digit < 10 * res)
                return PER($"too large '{name}' in header");
            res = 10 * res + digit;
        }
        return null;
    }

    string parse_lit(ref int ch, out int lit, ref int vars, int strict)
    {
        lit = 0;
        if (ch == 'a') return cube_token;
        int sign;
        if (ch == '-')
        {
            if (!isdigit(ch = parse_char())) return PER("expected digit after '-'");
            sign = -1;
        }
        else if (!isdigit(ch)) return PER("expected digit or '-'");
        else sign = 1;
        lit = ch - '0';
        while (isdigit(ch = parse_char()))
        {
            int digit = ch - '0';
            if (int.MaxValue / 10 < lit || int.MaxValue - digit < 10 * lit) return PER("literal too large");
            lit = 10 * lit + digit;
        }
        if (ch == '\r') ch = parse_char();
        if (ch != 'c' && ch != ' ' && ch != '\t' && ch != '\n' && ch != EOF)
            return PER($"expected white space after '{sign * lit}'");
        if (lit > vars)
        {
            if (strict != FORCED) return PER($"literal {sign * lit} exceeds maximum variable {vars}");
            else vars = lit;
        }
        lit *= sign;
        return null;
    }

    public string parse_dimacs(out int vars, int strict, out bool incremental)
    {
        double start = internal_.time();
        bool found_inccnf_header = false;
        int ch, clauses = 0;
        vars = 0;
        incremental = false;
        bool parse_inccnf_too = cubes != null;

        for (; ; )
        {
            ch = parse_char();
            if (strict != STRICT)
                if (ch == ' ' || ch == '\n' || ch == '\t' || ch == '\r') continue;
            if (ch != 'c') break;
            var buf = new System.Text.StringBuilder();
            while ((ch = parse_char()) != '\n')
                if (ch == EOF) return PER("unexpected end-of-file in header comment");
                else if (ch != '\r') buf.Append((char)ch);
            string s = buf.ToString();
            int o = 0;
            while (o < s.Length && s[o] != '-') o++;
            if (o >= s.Length) continue;
            string opt = s.Substring(o);
            internal_.PHASE("parse-dimacs", $"found option '{opt}'");
            solver.set_long_option(opt);
        }

        if (ch != 'p') return PER("expected 'c' or 'p'");

        ch = parse_char();
        if (strict == STRICT)
        {
            if (ch != ' ') return PER("expected space after 'p'");
            ch = parse_char();
        }
        else if (ch != ' ' && ch != '\t') return PER("expected white space after 'p'");
        else
        {
            do ch = parse_char();
            while (ch == ' ' || ch == '\t');
        }

        string err;
        if (ch == 'c')
        {
            if (strict == STRICT)
            {
                err = parse_string("nf ", 'c');
                if (err != null) return err;
                ch = parse_char();
                if (!isdigit(ch)) return PER("expected digit after 'p cnf '");
                err = parse_positive_int(ref ch, out vars, "<max-var>");
                if (err != null) return err;
                if (ch != ' ') return PER($"expected ' ' after 'p cnf {vars}'");
                if (!isdigit(ch = parse_char())) return PER($"expected digit after 'p cnf {vars} '");
                err = parse_positive_int(ref ch, out clauses, "<num-clauses>");
                if (err != null) return err;
                if (ch != '\n') return PER($"expected new-line after 'p cnf {vars} {clauses}'");
            }
            else
            {
                if (parse_char() != 'n') return PER("expected 'n' after 'p c'");
                if (parse_char() != 'f') return PER("expected 'f' after 'p cn'");
                ch = parse_char();
                if (!isspace(ch)) return PER("expected space after 'p cnf'");
                do ch = parse_char();
                while (isspace(ch));
                if (!isdigit(ch)) return PER("expected digit after 'p cnf '");
                err = parse_positive_int(ref ch, out vars, "<max-var>");
                if (err != null) return err;
                if (!isspace(ch)) return PER($"expected space after 'p cnf {vars}'");
                do ch = parse_char();
                while (isspace(ch));
                if (!isdigit(ch)) return PER($"expected digit after 'p cnf {vars} '");
                err = parse_positive_int(ref ch, out clauses, "<num-clauses>");
                if (err != null) return err;
                while (ch != '\n')
                {
                    if (ch != '\r' && !isspace(ch)) return PER($"expected new-line after 'p cnf {vars} {clauses}'");
                    ch = parse_char();
                }
            }
            internal_.MSG($"found 'p cnf {vars} {clauses}' header");
            if (strict != FORCED) solver.reserve(vars);
            internal_.reserve_ids(clauses);
        }
        else if (!parse_inccnf_too) return PER("expected 'c' after 'p '");
        else if (ch == 'i')
        {
            found_inccnf_header = true;
            err = parse_string("nccnf", 'i');
            if (err != null) return err;
            ch = parse_char();
            if (strict == STRICT)
            {
                if (ch != '\n') return PER("expected new-line after 'p inccnf'");
            }
            else
            {
                while (ch != '\n')
                {
                    if (ch != '\r' && !isspace(ch)) return PER("expected new-line after 'p inccnf'");
                    ch = parse_char();
                }
            }
            internal_.MSG("found 'p inccnf' header");
            strict = FORCED;
        }
        else return PER("expected 'c' or 'i' after 'p '");

        int lit = 0, parsed = 0;
        while ((ch = parse_char()) != EOF)
        {
            if (ch == ' ' || ch == '\n' || ch == '\t' || ch == '\r') continue;
            if (ch == 'c')
            {
                while ((ch = parse_char()) != '\n' && ch != EOF) { }
                if (ch == EOF) break;
                continue;
            }
            if (ch == 'a' && found_inccnf_header) break;
            err = parse_lit(ref ch, out lit, ref vars, strict);
            if (err != null) return err;
            if (ch == 'c')
            {
                while ((ch = parse_char()) != '\n')
                    if (ch == EOF) return PER("unexpected end-of-file in comment");
            }
            solver.add(lit);
            if (!found_inccnf_header && lit == 0 && parsed++ >= clauses && strict != FORCED)
                return PER("too many clauses");
        }

        if (lit != 0) return PER("last clause without terminating '0'");
        if (!found_inccnf_header && parsed < clauses && strict != FORCED) return PER("clause missing");

        double end = internal_.time();
        internal_.MSG($"parsed {parsed} clauses in {end - start:F2} seconds {(internal_.opts.realtime != 0 ? "real" : "process")} time");

        start = end;
        long num_cubes = 0;
        if (ch == 'a')
        {
            incremental = true;
            for (; ; )
            {
                ch = parse_char();
                if (ch == ' ' || ch == '\n' || ch == '\t' || ch == '\r') continue;
                if (ch == 'c')
                {
                    while ((ch = parse_char()) != '\n' && ch != EOF) { }
                    if (ch == EOF) break;
                    continue;
                }
                err = parse_lit(ref ch, out lit, ref vars, strict);
                if ((object)err == cube_token) return PER("two 'a' in a row");
                else if (err != null) return err;
                if (ch == 'c')
                {
                    while ((ch = parse_char()) != '\n')
                        if (ch == EOF) return PER("unexpected end-of-file in comment");
                }
                cubes?.push_back(lit);
                if (lit == 0)
                {
                    num_cubes++;
                    for (; ; )
                    {
                        ch = parse_char();
                        if (ch == ' ' || ch == '\n' || ch == '\t' || ch == '\r') continue;
                        if (ch == 'c')
                        {
                            while ((ch = parse_char()) != '\n' && ch != EOF) { }
                            if (ch == EOF) break;
                        }
                        if (ch == EOF) break;
                        if (ch != 'a') return PER("expected 'a' or end-of-file after zero");
                        lit = int.MinValue;
                        break;
                    }
                    if (ch == EOF) break;
                }
            }
            if (lit != 0) return PER("last cube without terminating '0'");
        }
        if (found_inccnf_header)
        {
            end = internal_.time();
            internal_.MSG($"parsed {num_cubes} cubes in {end - start:F2} seconds {(internal_.opts.realtime != 0 ? "real" : "process")} time");
        }
        return null;
    }

    public string parse_solution()
    {
        external.solution = new sbyte[external.max_var + 1];
        int ch;
        for (; ; )
        {
            ch = parse_char();
            if (ch == EOF) return PER("missing 's' line");
            else if (ch == 'c')
            {
                while ((ch = parse_char()) != '\n')
                    if (ch == EOF) return PER("unexpected end-of-file in comment");
            }
            else if (ch == 's') break;
            else return PER("expected 'c' or 's'");
        }
        string err = parse_string(" SATISFIABLE", 's');
        if (err != null) return err;
        if ((ch = parse_char()) == '\r') ch = parse_char();
        if (ch != '\n') return PER("expected new-line after 's SATISFIABLE'");
        int count = 0;
        for (; ; )
        {
            ch = parse_char();
            if (ch != 'v') return PER("expected 'v' at start-of-line");
            if ((ch = parse_char()) != ' ') return PER("expected ' ' after 'v'");
            int lit = 0;
            ch = parse_char();
            do
            {
                if (ch == ' ' || ch == '\t')
                {
                    ch = parse_char();
                    continue;
                }
                int mv = external.max_var;
                err = parse_lit(ref ch, out lit, ref mv, RELAXED);
                if (err != null) return err;
                if (ch == 'c') return PER("unexpected comment");
                if (lit == 0) break;
                if (external.solution[Math.Abs(lit)] != 0) return PER($"variable {Math.Abs(lit)} occurs twice");
                external.solution[Math.Abs(lit)] = (sbyte)CUtil.sign(lit);
                count++;
                if (ch == '\r') ch = parse_char();
            } while (ch != '\n');
            if (lit == 0) break;
        }
        internal_.MSG($"parsed {count} values {CUtil.percent(count, external.max_var):F2}%");
        return null;
    }
}
