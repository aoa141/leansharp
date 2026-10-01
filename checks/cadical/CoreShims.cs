// Shims for functions that Core references but that live in other areas (the check project only
// compiles Core + Cadical).
using System.Text;
namespace LeanSharp.Runtime;

public static unsafe partial class LeanRt
{
    public static Obj lean_mk_string_from_bytes_lossy(ReadOnlySpan<byte> s) =>
        lean_mk_string(Encoding.UTF8.GetString(s));
}
