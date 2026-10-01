#!/usr/bin/env python3
# Generates test CNF instances: pigeonhole formulas and random 3-SAT near the threshold.
import random, sys, os
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "cnf")
os.makedirs(out, exist_ok=True)
def write(name, nvars, clauses):
    with open(os.path.join(out, name), "w") as f:
        f.write(f"p cnf {nvars} {len(clauses)}\n")
        for c in clauses: f.write(" ".join(map(str, c)) + " 0\n")
def php(p, h):
    v = lambda i, j: i * h + j + 1
    cls = [[v(i, j) for j in range(h)] for i in range(p)]
    for j in range(h):
        for a in range(p):
            for b in range(a + 1, p): cls.append([-v(a, j), -v(b, j)])
    write(f"php{p}_{h}.cnf", p * h, cls)
for p in range(2, 10): php(p, p - 1)
php(5, 5)
rng = random.Random(42)
for n in (50, 100, 150, 200):
    for k in range(10):
        m = int(4.26 * n)
        cls = []
        for _ in range(m):
            vs = rng.sample(range(1, n + 1), 3)
            cls.append([x if rng.random() < 0.5 else -x for x in vs])
        write(f"rand3_{n}_{k}.cnf", n, cls)
# small edge cases
write("empty.cnf", 0, [])
write("emptyclause.cnf", 3, [[1, 2], []])
write("units.cnf", 3, [[1], [-1, 2], [-2, 3], [-3]])
write("taut.cnf", 3, [[1, -1, 2], [2, 2, -3], [-2], [3, 1]])
write("sat1.cnf", 5, [[1, 2], [-1, 3], [-3, 4, 5]])
