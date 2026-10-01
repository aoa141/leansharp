#!/usr/bin/env python3
"""Cross-check of the C# lgz encoder against native leantar.

For a sample of modules: pack with the native `leantar`, pull the lgz stream out of the `.ltar`
container (zstd with the leantar dictionary), and compare it byte for byte with the stream written
by `Check compress`.  usage: cross_lgz.py <native leantar> <dict> <Check.dll> <lib dir> [count]
"""
import os, struct, subprocess, sys, tempfile, random
from compression import zstd

leantar, dict_path, check_dll, lib = sys.argv[1:5]
count = int(sys.argv[5]) if len(sys.argv) > 5 else 40
zd = zstd.ZstdDict(open(dict_path, 'rb').read())

def sections(data):
    """Yields (path, compression, extra paths, payload) of an ltar file."""
    magic = data[:4]; pos = 4
    ver = {b'LTAR': 1, b'LTR2': 2, b'LTR3': 3, b'LTR4': 4}[magic]
    if ver >= 4:
        flag = data[pos]; pos += 1
        if flag == 1: pos += 8
    else: pos += 8
    def cstr():
        nonlocal pos
        e = data.index(b'\0', pos); s = data[pos:e]; pos = e + 1; return s
    def payload(comp):
        nonlocal pos
        if comp in (0, 1, 5):
            n = struct.unpack_from('<Q', data, pos)[0]; pos += 8
            p = data[pos:pos + n]; pos += n; return p
        if comp in (2, 3): pos += 8; return b''
        if comp in (4, 6):
            pos += 32 if comp == 4 else 24
            while True:
                t = data[pos]; pos += 1
                if t == 0: return b''
                if t in (1, 2, 3, 4, 5): pos += 8
        raise ValueError(comp)
    trace = cstr()
    if ver >= 2:
        comp = data[pos]; pos += 1
        yield trace, comp, [], payload(comp)
    while pos < len(data):
        if ver >= 3: pos += 1
        path = cstr()
        if not path:
            cstr(); continue
        comp = data[pos]; pos += 1
        extra = []
        if comp == 5:
            for _ in range(2):
                if ver >= 3: pos += 1
                extra.append(cstr())
        yield path, comp, extra, payload(comp)

mods = []
for d, _, fs in os.walk(lib):
    for f in fs:
        if f.endswith('.olean') and os.path.exists(os.path.join(d, f[:-6] + '.trace')):
            mods.append(os.path.join(d, f[:-6]))
mods.sort(); random.seed(1); random.shuffle(mods)
bad = 0; n = 0
with tempfile.TemporaryDirectory() as tmp:
    for m in mods[:count]:
        rel = os.path.relpath(m, lib)
        files = [rel + '.olean'] + [rel + e for e in ('.olean.server', '.olean.private') if os.path.exists(m + e)]
        ltar = os.path.join(tmp, 'x.ltar')
        subprocess.run([leantar, '-C', lib, ltar, rel + '.trace'] + files, check=True, stdout=subprocess.DEVNULL)
        native = None
        for path, comp, extra, payload in sections(open(ltar, 'rb').read()):
            if comp in (1, 5): native = zstd.decompress(payload, zstd_dict=zd)
        mine = os.path.join(tmp, 'mine.lgz')
        parts = files if len(files) == 3 else files[:1]
        subprocess.run(['dotnet', check_dll, 'compress', mine] + [os.path.join(lib, f) for f in parts], check=True)
        ok = native == open(mine, 'rb').read()
        n += 1
        if not ok:
            bad += 1; print('DIFF', rel, len(native), os.path.getsize(mine))
print(f'{n} modules compared with native leantar, {bad} differ')
sys.exit(1 if bad else 0)
