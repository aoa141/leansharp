// Access to the compiled (C#-translated) Lean code from the runtime.
//
// In C, the IR interpreter finds native code with `dlsym` (`lookup_symbol_in_cur_exe`) on the
// mangled C symbol (`l_Lean_Elab_foo`, `l_Lean_Elab_foo___boxed`, `initialize_Lean_Elab`, ...). In
// LeanSharp, every compiled declaration is a `public static` member named exactly like its C symbol
// in the static class of its module (`LeanSharp.Compiled.M_<mangled module name>`, see
// gen/_LeanModules.cs): functions are methods, constants are fields (initialized by the module
// initializer) or properties (lazily initialized closed terms).
//
// The runtime assembly cannot reference the generated assembly, so the host installs the hooks
// below at startup:
//
//     LeanCompiledCode.ModuleNames         = () => LeanModules.ModuleNames;
//     LeanCompiledCode.ModuleClassResolver = LeanModules.GetModuleClass;
//     LeanCompiledCode.InitializerResolver = m => (nint)LeanModules.GetInitializer(m);
//
// Symbol lookup uses a global index (C symbol -> metadata token) built on first use from the raw
// ECMA-335 metadata of the generated assembly (no reflection objects are created for the index;
// only the members actually looked up are resolved to `MemberInfo`s).

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using LeanSharp.Runtime.Interp;

namespace LeanSharp.Runtime;

public enum CompiledMemberKind : byte { Method = 1, Field = 2, Property = 3 }

/// <summary>A `public static` member of a compiled module class.</summary>
public sealed class CompiledMember
{
    public readonly string Symbol;
    public readonly CompiledMemberKind Kind;
    /// <summary>The method (kind Method), the field (kind Field) or the property getter (kind Property).</summary>
    public readonly MemberInfo Member;

    public CompiledMember(string symbol, CompiledMemberKind kind, MemberInfo member)
    {
        Symbol = symbol; Kind = kind; Member = member;
    }

    public MethodInfo Method => Member as MethodInfo;
    public FieldInfo Field => Member as FieldInfo;
    public override string ToString() => $"{Kind} {Member.DeclaringType?.Name}.{Member.Name}";
}

public static unsafe class LeanCompiledCode
{
    // ------------------------------------------------------------------
    // Hooks (set by the host)

    /// <summary>Names of all compiled modules (`LeanModules.ModuleNames`).</summary>
    public static Func<IReadOnlyList<string>> ModuleNames;
    /// <summary>Module name -> C# class implementing it, or null (`LeanModules.GetModuleClass`).</summary>
    public static Func<string, Type> ModuleClassResolver;
    /// <summary>Module name -> `delegate*&lt;byte, Obj&gt;` initializer as `nint`, or 0 (`LeanModules.GetInitializer`).</summary>
    public static Func<string, nint> InitializerResolver;
    /// <summary>Optional: all module classes (default: `ModuleNames` mapped through `ModuleClassResolver`).</summary>
    public static Func<IEnumerable<Type>> AllModuleClasses;
    /// <summary>Optional override: C symbol of a module initializer (e.g. `initialize_Lean_Elab_Term`,
    /// `meta_initialize_...`) -> `delegate*&lt;byte, Obj&gt;` as `nint`, or 0 if unknown.</summary>
    public static Func<string, nint> InitializerBySymbol;

    /// <summary>Time spent building the symbol index (diagnostics).</summary>
    public static TimeSpan IndexBuildTime { get; private set; }
    /// <summary>Number of indexed members (diagnostics).</summary>
    public static int IndexedMembers { get; private set; }

    static readonly object s_lock = new();
    static SymbolIndex s_index;
    static Dictionary<string, string> s_initStemToModule;
    static readonly Dictionary<string, CompiledMember> s_resolved = new();

    /// <summary>Drop all cached lookups (call after changing the hooks).</summary>
    public static void Reset()
    {
        lock (s_lock)
        {
            s_index = null;
            s_initStemToModule = null;
            s_resolved.Clear();
        }
        IrInterpreter.ResetGlobalCaches();
    }

    /// <summary>Build the symbol index now (e.g. on a background thread at startup).</summary>
    public static void WarmUp() { _ = GetIndex(); }

    static IEnumerable<Type> ModuleClasses()
    {
        if (AllModuleClasses != null) return AllModuleClasses().Where(t => t != null);
        var names = ModuleNames?.Invoke();
        var resolver = ModuleClassResolver;
        if (names == null || resolver == null) return Array.Empty<Type>();
        return names.Select(resolver).Where(t => t != null);
    }

    static SymbolIndex GetIndex()
    {
        var idx = Volatile.Read(ref s_index);
        if (idx != null) return idx;
        lock (s_lock)
        {
            if (s_index != null) return s_index;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            idx = SymbolIndex.Build(ModuleClasses());
            IndexBuildTime = sw.Elapsed;
            IndexedMembers = idx.Count;
            Volatile.Write(ref s_index, idx);
            return idx;
        }
    }

    /// <summary>The `public static` member of a compiled module class whose name is the C symbol
    /// `symbol` (C++ `lookup_symbol_in_cur_exe`), or null.</summary>
    public static CompiledMember Find(string symbol)
    {
        lock (s_resolved)
        {
            if (s_resolved.TryGetValue(symbol, out var m)) return m;
        }
        var r = GetIndex().Lookup(symbol);
        lock (s_resolved) s_resolved[symbol] = r;
        return r;
    }

    /// <summary>The `public static` member named `symbol` declared by the module class `moduleClass`
    /// (a declaration's compiled code always lives in the class of its defining module).</summary>
    internal static CompiledMember Find(string symbol, Type moduleClass) => GetIndex().Lookup(symbol, moduleClass);

    /// <summary>Initializer of the compiled module whose C initialization function is `sym`
    /// (`initialize_<stem>`, `runtime_initialize_<stem>` or `meta_initialize_<stem>`), or null.
    /// LeanSharp modules have a single initializer covering both phases.</summary>
    public static delegate*<byte, Obj> FindModuleInitializer(string sym)
    {
        if (InitializerBySymbol != null) return (delegate*<byte, Obj>)InitializerBySymbol(sym);
        var resolver = InitializerResolver;
        if (resolver == null) return null;
        string s = sym;
        if (s.StartsWith("runtime_", StringComparison.Ordinal)) s = s.Substring("runtime_".Length);
        else if (s.StartsWith("meta_", StringComparison.Ordinal)) s = s.Substring("meta_".Length);
        if (!s.StartsWith("initialize_", StringComparison.Ordinal)) return null;
        string stem = s.Substring("initialize_".Length);
        Dictionary<string, string> map;
        lock (s_lock)
        {
            if (s_initStemToModule == null)
            {
                var m = new Dictionary<string, string>(StringComparer.Ordinal);
                var names = ModuleNames?.Invoke();
                if (names != null)
                    foreach (var n in names) m.TryAdd(LeanNameMangling.ModuleInitStem(n), n);
                s_initStemToModule = m;
            }
            map = s_initStemToModule;
        }
        if (!map.TryGetValue(stem, out var mod)) return null;
        return (delegate*<byte, Obj>)resolver(mod);
    }

    // ------------------------------------------------------------------

    /// <summary>Hash-based index of public static members: FNV-1a of the UTF-8 name -> metadata token.</summary>
    sealed class SymbolIndex
    {
        struct Entry
        {
            public int Token;
            public CompiledMemberKind Kind;
            public int Asm;
            public int NameOffset; // offset in the #Strings heap (metadata path)
            public int Next;
        }

        sealed class AsmInfo
        {
            public Assembly Assembly;
            public Module Module;
            public MetadataReader Reader;
            public byte* StringHeap;
        }

        readonly List<AsmInfo> m_asms = new();
        readonly Dictionary<ulong, int> m_heads = new();
        Entry[] m_entries = new Entry[1024];
        int m_count;
        // reflection fallback (when raw metadata is not available)
        readonly Dictionary<string, CompiledMember> m_byName = new(StringComparer.Ordinal);

        public int Count => m_count + m_byName.Count;

        static ulong Fnv(byte* p)
        {
            ulong h = 14695981039346656037UL;
            for (; *p != 0; p++) { h ^= *p; h *= 1099511628211UL; }
            return h;
        }

        static ulong Fnv(ReadOnlySpan<byte> s)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in s) { h ^= b; h *= 1099511628211UL; }
            return h;
        }

        void Add(int asm, StringHandle name, int token, CompiledMemberKind kind)
        {
            var info = m_asms[asm];
            int off = MetadataTokens.GetHeapOffset(name);
            ulong h = Fnv(info.StringHeap + off);
            if (m_count == m_entries.Length) Array.Resize(ref m_entries, m_count * 2);
            int next = m_heads.TryGetValue(h, out var head) ? head : -1;
            m_entries[m_count] = new Entry { Token = token, Kind = kind, Asm = asm, NameOffset = off, Next = next };
            m_heads[h] = m_count;
            m_count++;
        }

        public static SymbolIndex Build(IEnumerable<Type> classes)
        {
            var idx = new SymbolIndex();
            foreach (var group in classes.Distinct().GroupBy(t => t.Assembly))
            {
                var asm = group.Key;
                var tokens = new HashSet<int>(group.Select(t => t.MetadataToken));
                if (!asm.TryGetRawMetadata(out byte* blob, out int length))
                {
                    foreach (var t in group) idx.AddByReflection(t);
                    continue;
                }
                var reader = new MetadataReader(blob, length);
                var info = new AsmInfo
                {
                    Assembly = asm,
                    Module = asm.ManifestModule,
                    Reader = reader,
                    StringHeap = blob + reader.GetHeapMetadataOffset(HeapIndex.String),
                };
                int ai = idx.m_asms.Count;
                idx.m_asms.Add(info);
                foreach (var th in reader.TypeDefinitions)
                {
                    if (!tokens.Contains(MetadataTokens.GetToken(th))) continue;
                    var td = reader.GetTypeDefinition(th);
                    foreach (var mh in td.GetMethods())
                    {
                        var md = reader.GetMethodDefinition(mh);
                        var a = md.Attributes;
                        if ((a & MethodAttributes.MemberAccessMask) != MethodAttributes.Public || (a & MethodAttributes.Static) == 0) continue;
                        if ((a & MethodAttributes.SpecialName) != 0) continue; // property getters are indexed below
                        idx.Add(ai, md.Name, MetadataTokens.GetToken(mh), CompiledMemberKind.Method);
                    }
                    foreach (var fh in td.GetFields())
                    {
                        var fd = reader.GetFieldDefinition(fh);
                        var a = fd.Attributes;
                        if ((a & FieldAttributes.FieldAccessMask) != FieldAttributes.Public || (a & FieldAttributes.Static) == 0) continue;
                        idx.Add(ai, fd.Name, MetadataTokens.GetToken(fh), CompiledMemberKind.Field);
                    }
                    foreach (var ph in td.GetProperties())
                    {
                        var pd = reader.GetPropertyDefinition(ph);
                        var getter = pd.GetAccessors().Getter;
                        if (getter.IsNil) continue;
                        var gd = reader.GetMethodDefinition(getter);
                        var a = gd.Attributes;
                        if ((a & MethodAttributes.MemberAccessMask) != MethodAttributes.Public || (a & MethodAttributes.Static) == 0) continue;
                        idx.Add(ai, pd.Name, MetadataTokens.GetToken(getter), CompiledMemberKind.Property);
                    }
                }
            }
            return idx;
        }

        readonly HashSet<Type> m_reflected = new();

        void AddByReflection(Type t)
        {
            m_reflected.Add(t);
            const BindingFlags F = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var m in t.GetMethods(F))
                if (!m.IsSpecialName) m_byName.TryAdd(m.Name, new CompiledMember(m.Name, CompiledMemberKind.Method, m));
            foreach (var f in t.GetFields(F))
                m_byName.TryAdd(f.Name, new CompiledMember(f.Name, CompiledMemberKind.Field, f));
            foreach (var p in t.GetProperties(F))
                if (p.GetMethod != null && p.GetMethod.IsPublic)
                    m_byName.TryAdd(p.Name, new CompiledMember(p.Name, CompiledMemberKind.Property, p.GetMethod));
        }

        static bool NameEquals(byte* p, ReadOnlySpan<byte> s)
        {
            for (int i = 0; i < s.Length; i++) if (p[i] != s[i]) return false;
            return p[s.Length] == 0;
        }

        public CompiledMember Lookup(string symbol) => Lookup(symbol, null);

        /// <summary>Member named `symbol`; if `cls` is given, only a member declared by that class.</summary>
        public CompiledMember Lookup(string symbol, Type cls)
        {
            if (m_byName.TryGetValue(symbol, out var r) && (cls == null || r.Member.DeclaringType == cls)) return r;
            if (m_count != 0)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(symbol);
                if (m_heads.TryGetValue(Fnv(utf8), out int i))
                {
                    for (; i >= 0; i = m_entries[i].Next)
                    {
                        ref var e = ref m_entries[i];
                        var info = m_asms[e.Asm];
                        if (cls != null && info.Assembly != cls.Assembly) continue;
                        if (!NameEquals(info.StringHeap + e.NameOffset, utf8)) continue;
                        MemberInfo mi = e.Kind == CompiledMemberKind.Field
                            ? info.Module.ResolveField(e.Token)
                            : info.Module.ResolveMethod(e.Token);
                        if (mi != null && (cls == null || mi.DeclaringType == cls)) return new CompiledMember(symbol, e.Kind, mi);
                    }
                }
            }
            if (cls != null && m_reflected.Contains(cls))
            {
                // reflection fallback when the name is shadowed by another class in `m_byName`
                const BindingFlags F = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
                var fi = cls.GetField(symbol, F);
                if (fi != null) return new CompiledMember(symbol, CompiledMemberKind.Field, fi);
                var pi = cls.GetProperty(symbol, F);
                if (pi?.GetMethod != null) return new CompiledMember(symbol, CompiledMemberKind.Property, pi.GetMethod);
                try
                {
                    var mi = cls.GetMethod(symbol, F);
                    if (mi != null && !mi.IsSpecialName) return new CompiledMember(symbol, CompiledMemberKind.Method, mi);
                }
                catch (AmbiguousMatchException) { }
            }
            return null;
        }
    }
}
