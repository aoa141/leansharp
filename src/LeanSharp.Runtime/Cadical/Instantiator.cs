// Port of 'instantiate.hpp' (shared between 'elim.cpp' and 'instantiate.cpp').
namespace LeanSharp.Runtime.Cadical;

public sealed class Instantiator
{
    public struct Candidate
    {
        public int lit;
        public int size;
        public long negoccs;
        public Clause clause;
        public Candidate(int l, Clause c, int s, long n) { lit = l; size = s; negoccs = n; clause = c; }
    }
    public readonly Vec<Candidate> candidates = new();
    public void candidate(int l, Clause c, int s, long n) => candidates.push_back(new Candidate(l, c, s, n));
    public bool any() => !candidates.empty();
}
