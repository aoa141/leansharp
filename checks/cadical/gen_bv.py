#!/usr/bin/env python3
"""Generates bit-blasted (bv_decide style) CNF benchmarks via Tseitin encoding.

  mulcomm_N.cnf   a*b == b*a for N-bit array multipliers (UNSAT)
  addassoc_N.cnf  (a+b)+c == a+(b+c) (UNSAT)
  distrib_N.cnf   a*(b+c) == a*b + a*c (UNSAT, hard)
  factor_N.cnf    x*y == p with x,y > 1 (SAT if p composite)
  shiftmul_N.cnf  a*2 == a<<1 (UNSAT)
"""
import os, sys

class CNF:
    def __init__(self):
        self.n = 0
        self.cls = []
        self.t = self.new()
        self.cls.append([self.t])

    def new(self):
        self.n += 1
        return self.n

    def AND(self, a, b):
        if a == -self.t or b == -self.t: return -self.t
        if a == self.t: return b
        if b == self.t: return a
        o = self.new()
        self.cls += [[-o, a], [-o, b], [o, -a, -b]]
        return o

    def OR(self, a, b): return -self.AND(-a, -b)

    def XOR(self, a, b):
        if a == -self.t: return b
        if b == -self.t: return a
        if a == self.t: return -b
        if b == self.t: return -a
        o = self.new()
        self.cls += [[-o, a, b], [-o, -a, -b], [o, -a, b], [o, a, -b]]
        return o

    def bits(self, w): return [self.new() for _ in range(w)]

    def const(self, v, w): return [self.t if (v >> i) & 1 else -self.t for i in range(w)]

    def add(self, a, b):
        c = -self.t
        out = []
        for x, y in zip(a, b):
            s = self.XOR(self.XOR(x, y), c)
            c = self.OR(self.AND(x, y), self.AND(c, self.XOR(x, y)))
            out.append(s)
        return out

    def mul(self, a, b):
        w = len(a)
        acc = [-self.t] * w
        for i in range(w):
            pp = [-self.t] * i + [self.AND(a[j], b[i]) for j in range(w - i)]
            acc = self.add(acc, pp)
        return acc

    def neq(self, a, b):
        diffs = [self.XOR(x, y) for x, y in zip(a, b)]
        self.cls.append(diffs)  # at least one bit differs

    def eq(self, a, b):
        for x, y in zip(a, b):
            self.cls += [[-x, y], [x, -y]]

    def write(self, path):
        with open(path, "w") as f:
            f.write(f"p cnf {self.n} {len(self.cls)}\n")
            for c in self.cls:
                f.write(" ".join(map(str, c)) + " 0\n")

def main(out):
    os.makedirs(out, exist_ok=True)
    for w in (4, 6, 8, 10, 12):
        c = CNF(); a, b = c.bits(w), c.bits(w)
        c.neq(c.mul(a, b), c.mul(b, a)); c.write(f"{out}/mulcomm_{w}.cnf")
    for w in (16, 32, 64):
        c = CNF(); a, b, d = c.bits(w), c.bits(w), c.bits(w)
        c.neq(c.add(c.add(a, b), d), c.add(a, c.add(b, d))); c.write(f"{out}/addassoc_{w}.cnf")
    for w in (4, 5, 6, 7):
        c = CNF(); a, b, d = c.bits(w), c.bits(w), c.bits(w)
        c.neq(c.mul(a, c.add(b, d)), c.add(c.mul(a, b), c.mul(a, d))); c.write(f"{out}/distrib_{w}.cnf")
    for w, p in ((12, 3 * 1021), (14, 89 * 97), (16, 251 * 241), (20, 1021 * 1019)):
        c = CNF(); x, y = c.bits(w), c.bits(w)
        c.eq(c.mul(x, y), c.const(p, w))
        # x > 1 and y > 1: some bit above bit 0 is set
        c.cls.append(x[1:]); c.cls.append(y[1:])
        c.write(f"{out}/factor_{w}.cnf")
    for w in (32, 64):
        c = CNF(); a = c.bits(w)
        c.neq(c.mul(a, c.const(2, w)), [-c.t] + a[:-1]); c.write(f"{out}/shiftmul_{w}.cnf")

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "bv"))
