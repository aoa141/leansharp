// Port of 'proof.cpp', 'tracer.hpp', 'lrattracer.cpp', 'drattracer.cpp' and the writing part of
// 'file.cpp'.

using System.Text;

namespace LeanSharp.Runtime.Cadical;

/// <summary>Output file used for proof traces (writing only).</summary>
public sealed class CFile
{
    Stream stream;
    readonly byte[] buffer = new byte[1 << 16];
    int pos;
    readonly bool close_stream;
    public readonly string name;
    long _bytes;

    public CFile(Stream s, string name, bool close_stream)
    {
        stream = s; this.name = name; this.close_stream = close_stream;
    }

    public static CFile write(string path)
    {
        try
        {
            var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            return new CFile(fs, path, true);
        }
        catch { return null; }
    }

    public static bool exists(string path)
    {
        try { return System.IO.File.Exists(path); } catch { return false; }
    }

    public static bool writable(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (Directory.Exists(path)) return false;
            if (System.IO.File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                return true;
            }
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            return string.IsNullOrEmpty(dir) || Directory.Exists(dir);
        }
        catch { return false; }
    }

    public bool closed() => stream == null;
    public long bytes() => _bytes;

    public bool put(byte ch)
    {
        if (pos == buffer.Length) flush_buffer();
        buffer[pos++] = ch;
        _bytes++;
        return true;
    }

    public bool put(char ch) => put((byte)ch);

    public bool put(string s)
    {
        foreach (char c in s) put((byte)c);
        return true;
    }

    public bool put(int lit)
    {
        if (lit == 0) return put((byte)'0');
        if (lit == int.MinValue) return put("-2147483648");
        if (lit < 0) { put((byte)'-'); lit = -lit; }
        return put_unsigned((ulong)lit);
    }

    public bool put(long l)
    {
        if (l == 0) return put((byte)'0');
        if (l == long.MinValue) return put("-9223372036854775808");
        if (l < 0) { put((byte)'-'); l = -l; }
        return put_unsigned((ulong)l);
    }

    public bool put(ulong l)
    {
        if (l == 0) return put((byte)'0');
        return put_unsigned(l);
    }

    bool put_unsigned(ulong x)
    {
        Span<byte> tmp = stackalloc byte[24];
        int i = tmp.Length;
        while (x != 0) { tmp[--i] = (byte)('0' + (int)(x % 10)); x /= 10; }
        for (; i < tmp.Length; i++) put(tmp[i]);
        return true;
    }

    void flush_buffer()
    {
        if (pos > 0 && stream != null) stream.Write(buffer, 0, pos);
        pos = 0;
    }

    public void flush()
    {
        flush_buffer();
        stream?.Flush();
    }

    public void close()
    {
        if (stream == null) return;
        flush();
        if (close_stream) stream.Dispose();
        stream = null;
    }
}

/*------------------------------------------------------------------------*/

public abstract class Tracer
{
    public virtual void add_original_clause(ulong id, bool redundant, Vec<int> clause, bool restore = false) { }
    public virtual void add_derived_clause(ulong id, bool redundant, Vec<int> clause, Vec<ulong> chain) { }
    public virtual void delete_clause(ulong id, bool redundant, Vec<int> clause) { }
    public virtual void weaken_minus(ulong id, Vec<int> clause) { }
    public virtual void strengthen(ulong id) { }
    public virtual void report_status(int status, ulong id) { }
    public virtual void finalize_clause(ulong id, Vec<int> clause) { }
    public virtual void begin_proof(ulong id) { }
    public virtual void solve_query() { }
    public virtual void add_assumption(int lit) { }
    public virtual void add_constraint(Vec<int> clause) { }
    public virtual void reset_assumptions() { }
    public virtual void add_assumption_clause(ulong id, Vec<int> clause, Vec<ulong> chain) { }
    public virtual void conclude_unsat(ConclusionType type, Vec<ulong> conclusion) { }
    public virtual void conclude_sat(Vec<int> model) { }
}

public abstract class InternalTracer : Tracer
{
    public virtual void connect_internal(Internal i) { }
}

public abstract class StatTracer : InternalTracer
{
    public virtual void print_stats() { }
}

public abstract class FileTracer : InternalTracer
{
    public abstract bool closed();
    public abstract void close(bool print = false);
    public abstract void flush(bool print = false);
}

public sealed class LratTracer : FileTracer
{
    Internal internal_;
    readonly CFile file;
    readonly bool binary;
    long added, deleted;
    ulong latest_id;
    readonly Vec<ulong> delete_ids = new();

    public LratTracer(Internal i, CFile f, bool b) { internal_ = i; file = f; binary = b; }

    public override void connect_internal(Internal i) { internal_ = i; }

    void put_binary_zero() => file.put((byte)0);

    void put_binary_lit(int lit)
    {
        uint idx = (uint)Math.Abs(lit);
        uint x = 2 * idx + (lit < 0 ? 1u : 0u);
        byte ch;
        while ((x & ~0x7fu) != 0)
        {
            ch = (byte)((x & 0x7f) | 0x80);
            file.put(ch);
            x >>= 7;
        }
        ch = (byte)x;
        file.put(ch);
    }

    void put_binary_id(long id)
    {
        ulong u = (ulong)(id < 0 ? -id : id);
        ulong x = 2 * u + (id < 0 ? 1ul : 0ul);
        byte ch;
        while ((x & ~0x7ful) != 0)
        {
            ch = (byte)((x & 0x7f) | 0x80);
            file.put(ch);
            x >>= 7;
        }
        ch = (byte)x;
        file.put(ch);
    }

    void lrat_add_clause(ulong id, Vec<int> clause, Vec<ulong> chain)
    {
        if (delete_ids.n != 0)
        {
            if (!binary) { file.put(latest_id); file.put(" "); }
            if (binary) file.put((byte)'d');
            else file.put("d ");
            for (int k = 0; k < delete_ids.n; k++)
            {
                ulong did = delete_ids[k];
                if (binary) put_binary_id((long)did);
                else { file.put(did); file.put(" "); }
            }
            if (binary) put_binary_zero();
            else file.put("0\n");
            delete_ids.clear();
        }
        latest_id = id;
        if (binary) { file.put((byte)'a'); put_binary_id((long)id); }
        else { file.put(id); file.put(" "); }
        for (int k = 0; k < clause.n; k++)
        {
            int external_lit = clause[k];
            if (binary) put_binary_lit(external_lit);
            else { file.put(external_lit); file.put((byte)' '); }
        }
        if (binary) put_binary_zero();
        else file.put("0 ");
        for (int k = 0; k < chain.n; k++)
        {
            ulong c = chain[k];
            if (binary) put_binary_id((long)c);
            else { file.put(c); file.put((byte)' '); }
        }
        if (binary) put_binary_zero();
        else file.put("0\n");
    }

    public override void add_derived_clause(ulong id, bool redundant, Vec<int> clause, Vec<ulong> chain)
    {
        if (file.closed()) return;
        lrat_add_clause(id, clause, chain);
        added++;
    }

    public override void delete_clause(ulong id, bool redundant, Vec<int> clause)
    {
        if (file.closed()) return;
        delete_ids.push_back(id);
        deleted++;
    }

    public override void begin_proof(ulong id)
    {
        if (file.closed()) return;
        latest_id = id;
    }

    public override bool closed() => file.closed();

    void print_statistics()
    {
        long bytes = file.bytes();
        long total = added + deleted;
        internal_.MSG($"LRAT {added} added clauses {CUtil.percent(added, total):F2}%");
        internal_.MSG($"LRAT {deleted} deleted clauses {CUtil.percent(deleted, total):F2}%");
        internal_.MSG($"LRAT {bytes} bytes ({bytes / (double)(1 << 20):F2} MB)");
    }

    public override void close(bool print = false)
    {
        file.close();
        if (print)
        {
            internal_.MSG($"LRAT proof file '{file.name}' closed");
            print_statistics();
        }
    }

    public override void flush(bool print = false)
    {
        file.flush();
        if (print)
        {
            internal_.MSG($"LRAT proof file '{file.name}' flushed");
            print_statistics();
        }
    }
}

public sealed class DratTracer : FileTracer
{
    Internal internal_;
    readonly CFile file;
    readonly bool binary;
    long added, deleted;

    public DratTracer(Internal i, CFile f, bool b) { internal_ = i; file = f; binary = b; }

    public override void connect_internal(Internal i) { internal_ = i; }

    void put_binary_zero() => file.put((byte)0);

    void put_binary_lit(int lit)
    {
        uint idx = (uint)Math.Abs(lit);
        uint x = 2u * idx + (lit < 0 ? 1u : 0u);
        byte ch;
        while ((x & ~0x7fu) != 0)
        {
            ch = (byte)((x & 0x7f) | 0x80);
            file.put(ch);
            x >>= 7;
        }
        ch = (byte)x;
        file.put(ch);
    }

    void drat_add_clause(Vec<int> clause)
    {
        if (binary) file.put((byte)'a');
        for (int k = 0; k < clause.n; k++)
        {
            int external_lit = clause[k];
            if (binary) put_binary_lit(external_lit);
            else { file.put(external_lit); file.put((byte)' '); }
        }
        if (binary) put_binary_zero();
        else file.put("0\n");
    }

    void drat_delete_clause(Vec<int> clause)
    {
        if (binary) file.put((byte)'d');
        else file.put("d ");
        for (int k = 0; k < clause.n; k++)
        {
            int external_lit = clause[k];
            if (binary) put_binary_lit(external_lit);
            else { file.put(external_lit); file.put((byte)' '); }
        }
        if (binary) put_binary_zero();
        else file.put("0\n");
    }

    public override void add_derived_clause(ulong id, bool redundant, Vec<int> clause, Vec<ulong> chain)
    {
        if (file.closed()) return;
        drat_add_clause(clause);
        added++;
    }

    public override void delete_clause(ulong id, bool redundant, Vec<int> clause)
    {
        if (file.closed()) return;
        drat_delete_clause(clause);
        deleted++;
    }

    public override bool closed() => file.closed();

    void print_statistics()
    {
        long bytes = file.bytes();
        long total = added + deleted;
        internal_.MSG($"DRAT {added} added clauses {CUtil.percent(added, total):F2}%");
        internal_.MSG($"DRAT {deleted} deleted clauses {CUtil.percent(deleted, total):F2}%");
        internal_.MSG($"DRAT {bytes} bytes ({bytes / (double)(1 << 20):F2} MB)");
    }

    public override void close(bool print = false)
    {
        file.close();
        if (print)
        {
            internal_.MSG($"DRAT proof file '{file.name}' closed");
            print_statistics();
        }
    }

    public override void flush(bool print = false)
    {
        file.flush();
        if (print)
        {
            internal_.MSG($"DRAT proof file '{file.name}' flushed");
            print_statistics();
        }
    }
}

/*------------------------------------------------------------------------*/

public sealed class Proof
{
    readonly Internal internal_;
    readonly Vec<int> clause = new();
    readonly Vec<ulong> proof_chain = new();
    ulong clause_id;
    bool redundant;
    readonly List<Tracer> tracers = new();

    public Proof(Internal i) { internal_ = i; }

    public void connect(Tracer t) => tracers.Add(t);
    public void disconnect(Tracer t) => tracers.RemoveAll(x => x == t);

    void add_literal(int internal_lit)
    {
        int external_lit = internal_.externalize(internal_lit);
        clause.push_back(external_lit);
    }

    void add_literals(Clause c)
    {
        for (int i = 0; i < c.size; i++) add_literal(c.literals[i]);
    }

    void add_literals(Vec<int> c)
    {
        for (int i = 0; i < c.n; i++) add_literal(c[i]);
    }

    public void add_original_clause(ulong id, bool r, Vec<int> c)
    {
        add_literals(c);
        clause_id = id;
        redundant = r;
        add_original_clause();
    }

    public void add_external_original_clause(ulong id, bool r, Vec<int> c, bool restore = false)
    {
        for (int i = 0; i < c.n; i++) clause.push_back(c[i]);
        clause_id = id;
        redundant = r;
        add_original_clause(restore);
    }

    public void delete_external_original_clause(ulong id, bool r, Vec<int> c)
    {
        for (int i = 0; i < c.n; i++) clause.push_back(c[i]);
        clause_id = id;
        redundant = r;
        delete_clause();
    }

    public void add_derived_empty_clause(ulong id, Vec<ulong> chain)
    {
        for (int i = 0; i < chain.n; i++) proof_chain.push_back(chain[i]);
        clause_id = id;
        redundant = false;
        add_derived_clause();
    }

    public void add_derived_unit_clause(ulong id, int internal_unit, Vec<ulong> chain)
    {
        add_literal(internal_unit);
        for (int i = 0; i < chain.n; i++) proof_chain.push_back(chain[i]);
        clause_id = id;
        redundant = false;
        add_derived_clause();
    }

    public void add_derived_clause(Clause c, Vec<ulong> chain)
    {
        add_literals(c);
        for (int i = 0; i < chain.n; i++) proof_chain.push_back(chain[i]);
        clause_id = c.id;
        redundant = c.redundant;
        add_derived_clause();
    }

    public void add_derived_clause(ulong id, bool r, Vec<int> c, Vec<ulong> chain)
    {
        for (int i = 0; i < c.n; i++) add_literal(c[i]);
        for (int i = 0; i < chain.n; i++) proof_chain.push_back(chain[i]);
        clause_id = id;
        redundant = r;
        add_derived_clause();
    }

    public void add_assumption_clause(ulong id, Vec<int> c, Vec<ulong> chain)
    {
        for (int i = 0; i < c.n; i++) clause.push_back(c[i]);
        for (int i = 0; i < chain.n; i++) proof_chain.push_back(chain[i]);
        clause_id = id;
        add_assumption_clause();
    }

    public void add_assumption(int a)
    {
        clause.push_back(a);
        add_assumption();
    }

    public void add_constraint(Vec<int> c)
    {
        for (int i = 0; i < c.n; i++) clause.push_back(c[i]);
        add_constraint();
    }

    public void delete_clause(Clause c)
    {
        clause.clear();
        add_literals(c);
        clause_id = c.id;
        redundant = c.redundant;
        delete_clause();
    }

    public void delete_clause(ulong id, bool r, Vec<int> c)
    {
        add_literals(c);
        clause_id = id;
        redundant = r;
        delete_clause();
    }

    public void weaken_minus(Clause c)
    {
        add_literals(c);
        clause_id = c.id;
        weaken_minus();
    }

    public void weaken_minus(ulong id, Vec<int> c)
    {
        add_literals(c);
        clause_id = id;
        weaken_minus();
    }

    public void weaken_plus(Clause c)
    {
        weaken_minus(c);
        delete_clause(c);
    }

    public void weaken_plus(ulong id, Vec<int> c)
    {
        weaken_minus(id, c);
        delete_clause(id, false, c);
    }

    public void delete_unit_clause(ulong id, int lit)
    {
        add_literal(lit);
        clause_id = id;
        redundant = false;
        delete_clause();
    }

    public void finalize_clause(Clause c)
    {
        add_literals(c);
        clause_id = c.id;
        finalize_clause();
    }

    public void finalize_clause(ulong id, Vec<int> c)
    {
        for (int i = 0; i < c.n; i++) add_literal(c[i]);
        clause_id = id;
        finalize_clause();
    }

    public void finalize_unit(ulong id, int lit)
    {
        add_literal(lit);
        clause_id = id;
        finalize_clause();
    }

    public void finalize_external_unit(ulong id, int lit)
    {
        clause.push_back(lit);
        clause_id = id;
        finalize_clause();
    }

    public void flush_clause(Clause c)
    {
        bool antecedents = internal_.lrat || internal_.frat;
        for (int i = 0; i < c.size; i++)
        {
            int internal_lit = c.literals[i];
            if (internal_.@fixed(internal_lit) < 0)
            {
                if (antecedents)
                {
                    uint uidx = internal_.vlit(-internal_lit);
                    ulong uid = internal_.unit_clauses(uidx);
                    proof_chain.push_back(uid);
                }
                continue;
            }
            add_literal(internal_lit);
        }
        proof_chain.push_back(c.id);
        redundant = c.redundant;
        ulong id = ++internal_.clause_id;
        clause_id = id;
        add_derived_clause();
        delete_clause(c);
        c.id = id;
    }

    public void strengthen_clause(Clause c, int remove, Vec<ulong> chain)
    {
        for (int i = 0; i < c.size; i++)
        {
            int internal_lit = c.literals[i];
            if (internal_lit == remove) continue;
            add_literal(internal_lit);
        }
        ulong id = ++internal_.clause_id;
        clause_id = id;
        redundant = c.redundant;
        for (int i = 0; i < chain.n; i++) proof_chain.push_back(chain[i]);
        add_derived_clause();
        delete_clause(c);
        c.id = id;
    }

    public void otfs_strengthen_clause(Clause c, Vec<int> old, Vec<ulong> chain)
    {
        for (int i = 0; i < c.size; i++) add_literal(c.literals[i]);
        ulong id = ++internal_.clause_id;
        clause_id = id;
        redundant = c.redundant;
        for (int i = 0; i < chain.n; i++) proof_chain.push_back(chain[i]);
        add_derived_clause();
        delete_clause(c.id, c.redundant, old);
        c.id = id;
    }

    public void strengthen(ulong id)
    {
        clause_id = id;
        strengthen();
    }

    void add_original_clause(bool restore = false)
    {
        foreach (var tracer in tracers) tracer.add_original_clause(clause_id, false, clause, restore);
        clause.clear();
        clause_id = 0;
    }

    void add_derived_clause()
    {
        foreach (var tracer in tracers) tracer.add_derived_clause(clause_id, redundant, clause, proof_chain);
        proof_chain.clear();
        clause.clear();
        clause_id = 0;
    }

    void delete_clause()
    {
        foreach (var tracer in tracers) tracer.delete_clause(clause_id, redundant, clause);
        clause.clear();
        clause_id = 0;
    }

    void weaken_minus()
    {
        foreach (var tracer in tracers) tracer.weaken_minus(clause_id, clause);
        clause.clear();
        clause_id = 0;
    }

    void strengthen()
    {
        foreach (var tracer in tracers) tracer.strengthen(clause_id);
        clause_id = 0;
    }

    void finalize_clause()
    {
        foreach (var tracer in tracers) tracer.finalize_clause(clause_id, clause);
        clause.clear();
        clause_id = 0;
    }

    void add_assumption_clause()
    {
        foreach (var tracer in tracers) tracer.add_assumption_clause(clause_id, clause, proof_chain);
        proof_chain.clear();
        clause.clear();
        clause_id = 0;
    }

    void add_assumption()
    {
        foreach (var tracer in tracers) tracer.add_assumption(clause.back());
        clause.clear();
    }

    void add_constraint()
    {
        foreach (var tracer in tracers) tracer.add_constraint(clause);
        clause.clear();
    }

    public void reset_assumptions()
    {
        foreach (var tracer in tracers) tracer.reset_assumptions();
    }

    public void report_status(int status, ulong id)
    {
        foreach (var tracer in tracers) tracer.report_status(status, id);
    }

    public void begin_proof(ulong id)
    {
        foreach (var tracer in tracers) tracer.begin_proof(id);
    }

    public void solve_query()
    {
        foreach (var tracer in tracers) tracer.solve_query();
    }

    public void conclude_unsat(ConclusionType con, Vec<ulong> conclusion)
    {
        foreach (var tracer in tracers) tracer.conclude_unsat(con, conclusion);
    }

    public void conclude_sat(Vec<int> model)
    {
        foreach (var tracer in tracers) tracer.conclude_sat(model);
    }
}

public sealed unsafe partial class Internal
{
    public void new_proof_on_demand()
    {
        if (proof == null) proof = new Proof(this);
    }

    public void resize_unit_clauses_idx()
    {
        long new_vsize = vsize != 0 ? 2L * vsize : 1L + max_var;
        if (unit_clauses_idx.Length < 2 * new_vsize) Array.Resize(ref unit_clauses_idx, (int)(2 * new_vsize));
        else if (unit_clauses_idx.Length > 2 * new_vsize) Array.Resize(ref unit_clauses_idx, (int)(2 * new_vsize));
    }

    public void force_lrat()
    {
        if (lrat) return;
        lrat = true;
    }

    public void connect_proof_tracer(Tracer tracer, bool antecedents, bool finalize_clauses = false)
    {
        new_proof_on_demand();
        if (antecedents) force_lrat();
        if (finalize_clauses) frat = true;
        resize_unit_clauses_idx();
        if (tracer is InternalTracer it) it.connect_internal(this);
        proof.connect(tracer);
        if (tracer is FileTracer ft) file_tracers.Add(ft);
        else if (tracer is StatTracer st) stat_tracers.Add(st);
        else tracers.Add(tracer);
    }

    public bool disconnect_proof_tracer(Tracer tracer)
    {
        bool found = false;
        if (tracer is FileTracer ft) found = file_tracers.Remove(ft);
        else if (tracer is StatTracer st) found = stat_tracers.Remove(st);
        else found = tracers.Remove(tracer);
        if (found) proof.disconnect(tracer);
        return found;
    }

    public void trace(CFile file)
    {
        if (opts.veripb != 0 || opts.idrup != 0 || opts.lidrup != 0)
            throw new CadicalException("VeriPB/IDRUP/LIDRUP proof formats are not supported by this port");
        if (opts.frat != 0)
            throw new CadicalException("FRAT proof format is not supported by this port");
        if (opts.lrat != 0)
        {
            var ft = new LratTracer(this, file, opts.binary != 0);
            connect_proof_tracer(ft, true);
        }
        else
        {
            var ft = new DratTracer(this, file, opts.binary != 0);
            connect_proof_tracer(ft, false);
        }
    }

    public void check()
    {
        // Internal proof checking ('opts.check') is not ported.
    }

    public void close_trace(bool print = false)
    {
        foreach (var tracer in file_tracers) tracer.close(print);
    }

    public void flush_trace(bool print = false)
    {
        foreach (var tracer in file_tracers) tracer.flush(print);
    }
}
