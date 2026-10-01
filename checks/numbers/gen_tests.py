#!/usr/bin/env python3
"""Generates differential tests for the numbers area.

Produces
  * ref_tests.lean  -- a Lean program printing one line per test (run with the real Lean toolchain
                       to produce expected.txt: `lean --run ref_tests.lean > expected.txt`)
  * Generated.cs    -- the same tests calling the C# runtime directly.

Usage: python3 gen_tests.py && lean --run ref_tests.lean > expected.txt && dotnet run
"""
import os
import struct

HERE = os.path.dirname(os.path.abspath(__file__))

lean = []   # Lean statements
cs = []     # C# statements

def emit(label, lean_expr, cs_expr):
    """lean_expr must evaluate to a String; cs_expr must evaluate to a string."""
    lean.append(f'  IO.println ("{label} " ++ ({lean_expr}))')
    cs.append(f'        T("{label}", {cs_expr});')

# ------------------------------------------------------------------ Nat
NATS = ["0", "1", "2", "7", "255", "256", "4294967295", "4294967296", "9223372036854775807",
        "9223372036854775808", "18446744073709551615", "18446744073709551616",
        "1000000000000000000000000000000", "340282366920938463463374607431768211457"]

def ln(v): return f"({v} : Nat)"
def cn(v): return f'N("{v}")'

nat_bin = [
    ("add", "{a} + {b}", "lean_nat_add"),
    ("sub", "{a} - {b}", "lean_nat_sub"),
    ("mul", "{a} * {b}", "lean_nat_mul"),
    ("div", "{a} / {b}", "lean_nat_div"),
    ("mod", "{a} % {b}", "lean_nat_mod"),
    ("gcd", "Nat.gcd {a} {b}", "lean_nat_gcd"),
    ("land", "{a} &&& {b}", "lean_nat_land"),
    ("lor", "{a} ||| {b}", "lean_nat_lor"),
    ("xor", "{a} ^^^ {b}", "lean_nat_lxor"),
]
for i, a in enumerate(NATS):
    for j, b in enumerate(NATS):
        for (nm, le, cf) in nat_bin:
            emit(f"nat.{nm}.{i}.{j}", f"toString ({le.format(a=ln(a), b=ln(b))})", f"FN({cf}({cn(a)}, {cn(b)}))")
        emit(f"nat.eq.{i}.{j}", f"toString (decide ({ln(a)} = {ln(b)}))", f"FB(lean_nat_dec_eq({cn(a)}, {cn(b)}))")
        emit(f"nat.le.{i}.{j}", f"toString (decide ({ln(a)} ≤ {ln(b)}))", f"FB(lean_nat_dec_le({cn(a)}, {cn(b)}))")
        emit(f"nat.lt.{i}.{j}", f"toString (decide ({ln(a)} < {ln(b)}))", f"FB(lean_nat_dec_lt({cn(a)}, {cn(b)}))")
SHIFTS = ["0", "1", "3", "31", "63", "64", "65", "100", "200"]
EXPS = ["0", "1", "2", "3", "10", "64", "100"]
for i, a in enumerate(NATS):
    emit(f"nat.log2.{i}", f"toString (Nat.log2 {ln(a)})", f"FN(lean_nat_log2({cn(a)}))")
    emit(f"nat.pred.{i}", f"toString (Nat.pred {ln(a)})", f"FN(lean_nat_pred({cn(a)}))")
    for s in SHIFTS:
        emit(f"nat.shl.{i}.{s}", f"toString ({ln(a)} <<< {ln(s)})", f"FN(lean_nat_shiftl({cn(a)}, {cn(s)}))")
        emit(f"nat.shr.{i}.{s}", f"toString ({ln(a)} >>> {ln(s)})", f"FN(lean_nat_shiftr({cn(a)}, {cn(s)}))")
    for e in EXPS:
        emit(f"nat.pow.{i}.{e}", f"toString ({ln(a)} ^ {ln(e)})", f"FN(lean_nat_pow({cn(a)}, {cn(e)}))")
    for j, m in enumerate(NATS[:9]):
        if m == "0" and i > 4: continue  # b^12345 is too slow to print in Lean for big b
        emit(f"nat.powmod.{i}.{j}", f"toString (Nat.powMod {ln(a)} 12345 {ln(m)})", f"FN(lean_nat_powmod({cn(a)}, N(\"12345\"), {cn(m)}))")
# big shift amount on big value
emit("nat.shr.big", "toString ((2^100 : Nat) >>> (2^70 : Nat))", 'FN(lean_nat_shiftr(N("1267650600228229401496703205376"), N("1180591620717411303424")))')
emit("nat.pow.0big", "toString ((0 : Nat) ^ (2^70 : Nat))", 'FN(lean_nat_pow(N("0"), N("1180591620717411303424")))')
emit("nat.pow.1big", "toString ((1 : Nat) ^ (2^70 : Nat))", 'FN(lean_nat_pow(N("1"), N("1180591620717411303424")))')

# ------------------------------------------------------------------ Int
INTS = ["0", "1", "-1", "7", "-7", "2147483647", "-2147483648", "2147483648", "-2147483649",
        "4294967296", "-4294967296", "9223372036854775807", "-9223372036854775808",
        "100000000000000000000000", "-100000000000000000000000", "-12345678901"]

def li(v): return f"({v} : Int)"
def ci(v): return f'I("{v}")'

int_bin = [
    ("add", "{a} + {b}", "lean_int_add"),
    ("sub", "{a} - {b}", "lean_int_sub"),
    ("mul", "{a} * {b}", "lean_int_mul"),
    ("tdiv", "Int.tdiv {a} {b}", "lean_int_div"),
    ("tmod", "Int.tmod {a} {b}", "lean_int_mod"),
    ("ediv", "{a} / {b}", "lean_int_ediv"),
    ("emod", "{a} % {b}", "lean_int_emod"),
]
for i, a in enumerate(INTS):
    for j, b in enumerate(INTS):
        for (nm, le, cf) in int_bin:
            emit(f"int.{nm}.{i}.{j}", f"toString ({le.format(a=li(a), b=li(b))})", f"FI({cf}({ci(a)}, {ci(b)}))")
        emit(f"int.eq.{i}.{j}", f"toString (decide ({li(a)} = {li(b)}))", f"FB(lean_int_dec_eq({ci(a)}, {ci(b)}))")
        emit(f"int.le.{i}.{j}", f"toString (decide ({li(a)} ≤ {li(b)}))", f"FB(lean_int_dec_le({ci(a)}, {ci(b)}))")
        emit(f"int.lt.{i}.{j}", f"toString (decide ({li(a)} < {li(b)}))", f"FB(lean_int_dec_lt({ci(a)}, {ci(b)}))")
    emit(f"int.neg.{i}", f"toString (- {li(a)})", f"FI(lean_int_neg({ci(a)}))")
    emit(f"int.natAbs.{i}", f"toString (Int.natAbs {li(a)})", f"FN(lean_nat_abs({ci(a)}))")
    emit(f"int.nonneg.{i}", f"toString (decide (0 ≤ {li(a)}))", f"FB(lean_int_dec_nonneg({ci(a)}))")
for i, a in enumerate(NATS):
    emit(f"int.negSucc.{i}", f"toString (Int.negSucc {ln(a)})", f"FI(lean_int_neg_succ_of_nat({cn(a)}))")
    emit(f"int.ofNat.{i}", f"toString (Int.ofNat {ln(a)})", f"FI(lean_nat_to_int({cn(a)}))")

# ------------------------------------------------------------------ fixed width
UT = {  # lean type, cs type, bits
    "uint8": ("UInt8", "byte", 8), "uint16": ("UInt16", "ushort", 16), "uint32": ("UInt32", "uint", 32),
    "uint64": ("UInt64", "ulong", 64), "usize": ("USize", "ulong", 64),
}
ST = {
    "int8": ("Int8", "byte", 8), "int16": ("Int16", "ushort", 16), "int32": ("Int32", "uint", 32),
    "int64": ("Int64", "ulong", 64), "isize": ("ISize", "ulong", 64),
}
UVALS = {
    8: [0, 1, 2, 7, 127, 128, 200, 255],
    16: [0, 1, 7, 255, 256, 32767, 32768, 65535],
    32: [0, 1, 7, 65536, 2147483647, 2147483648, 4294967295],
    64: [0, 1, 7, 4294967296, 9223372036854775807, 9223372036854775808, 18446744073709551615, 9007199254740993],
}
SVALS = {
    8: [0, 1, -1, 7, -7, 127, -128, 100],
    16: [0, 1, -1, -7, 32767, -32768, 1000, 255],
    32: [0, 1, -1, -7, 2147483647, -2147483648, 100000, 33],
    64: [0, 1, -1, -7, 9223372036854775807, -9223372036854775808, 10000000000, 65, -9007199254740993],
}

def cs_lit(ct, bits, v):
    return f"unchecked(({ct}){v % (1 << bits)}UL)"

def fmt_u(ct): return "FU"
def fmt_s(bits): return f"FS{bits}"

for key, (lt, ct, bits) in UT.items():
    vals = UVALS[bits]
    L = lambda v: f"({lt}.ofNat {v})"
    C = lambda v: cs_lit(ct, bits, v)
    ops = [("add", "{a} + {b}", "add"), ("sub", "{a} - {b}", "sub"), ("mul", "{a} * {b}", "mul"),
           ("div", "{a} / {b}", "div"), ("mod", "{a} % {b}", "mod"), ("land", "{a} &&& {b}", "land"),
           ("lor", "{a} ||| {b}", "lor"), ("xor", "{a} ^^^ {b}", "xor"), ("shl", "{a} <<< {b}", "shift_left"),
           ("shr", "{a} >>> {b}", "shift_right")]
    for i, a in enumerate(vals):
        for j, b in enumerate(vals):
            for (nm, le, cf) in ops:
                emit(f"{key}.{nm}.{i}.{j}", f"toString ({le.format(a=L(a), b=L(b))})", f"FU(lean_{key}_{cf}({C(a)}, {C(b)}))")
            emit(f"{key}.eq.{i}.{j}", f"toString (decide ({L(a)} = {L(b)}))", f"FB(lean_{key}_dec_eq({C(a)}, {C(b)}))")
            emit(f"{key}.lt.{i}.{j}", f"toString (decide ({L(a)} < {L(b)}))", f"FB(lean_{key}_dec_lt({C(a)}, {C(b)}))")
            emit(f"{key}.le.{i}.{j}", f"toString (decide ({L(a)} ≤ {L(b)}))", f"FB(lean_{key}_dec_le({C(a)}, {C(b)}))")
        emit(f"{key}.compl.{i}", f"toString (~~~ {L(a)})", f"FU(lean_{key}_complement({C(a)}))")
        emit(f"{key}.neg.{i}", f"toString (- {L(a)})", f"FU(lean_{key}_neg({C(a)}))")
        emit(f"{key}.log2.{i}", f"toString ({lt}.log2 {L(a)})", f"FU(lean_{key}_log2({C(a)}))")
        emit(f"{key}.toNat.{i}", f"toString ({L(a)}.toNat)", f"FN(lean_{key}_to_nat({C(a)}))")
        emit(f"{key}.toFloat.{i}", f"FF ({L(a)}.toFloat)", f"FF(lean_{key}_to_float({C(a)}))")
        emit(f"{key}.toFloat32.{i}", f"FF32 ({L(a)}.toFloat32)", f"FF32(lean_{key}_to_float32({C(a)}))")
        for k2, (lt2, ct2, bits2) in UT.items():
            if k2 == key: continue
            emit(f"{key}.to_{k2}.{i}", f"toString ({L(a)}.to{lt2})", f"FU(lean_{key}_to_{k2}({C(a)}))")
    for i, n in enumerate(NATS):
        emit(f"{key}.ofNat.{i}", f"toString ({lt}.ofNat {ln(n)})", f"FU(lean_{key}_of_nat({cn(n)}))")
        emit(f"{key}.ofNatMk.{i}", f"toString ({lt}.ofBitVec (BitVec.ofNat _ {ln(n)}))", f"FU(lean_{key}_of_nat_mk({cn(n)}))")
    emit(f"{key}.ofBool.t", f"toString (Bool.to{lt} true)", f"FU(lean_bool_to_{key}(1))")
    emit(f"{key}.ofBool.f", f"toString (Bool.to{lt} false)", f"FU(lean_bool_to_{key}(0))")

for key, (lt, ct, bits) in ST.items():
    vals = SVALS[bits]
    L = lambda v: f"({lt}.ofInt ({v}))"
    C = lambda v: cs_lit(ct, bits, v)
    F = f"FS{bits}"
    ops = [("add", "{a} + {b}", "add"), ("sub", "{a} - {b}", "sub"), ("mul", "{a} * {b}", "mul"),
           ("div", "{a} / {b}", "div"), ("mod", "{a} % {b}", "mod"), ("land", "{a} &&& {b}", "land"),
           ("lor", "{a} ||| {b}", "lor"), ("xor", "{a} ^^^ {b}", "xor"), ("shl", "{a} <<< {b}", "shift_left"),
           ("shr", "{a} >>> {b}", "shift_right")]
    for i, a in enumerate(vals):
        for j, b in enumerate(vals):
            for (nm, le, cf) in ops:
                emit(f"{key}.{nm}.{i}.{j}", f"toString ({le.format(a=L(a), b=L(b))})", f"{F}(lean_{key}_{cf}({C(a)}, {C(b)}))")
            emit(f"{key}.eq.{i}.{j}", f"toString (decide ({L(a)} = {L(b)}))", f"FB(lean_{key}_dec_eq({C(a)}, {C(b)}))")
            emit(f"{key}.lt.{i}.{j}", f"toString (decide ({L(a)} < {L(b)}))", f"FB(lean_{key}_dec_lt({C(a)}, {C(b)}))")
            emit(f"{key}.le.{i}.{j}", f"toString (decide ({L(a)} ≤ {L(b)}))", f"FB(lean_{key}_dec_le({C(a)}, {C(b)}))")
        emit(f"{key}.compl.{i}", f"toString (~~~ {L(a)})", f"{F}(lean_{key}_complement({C(a)}))")
        emit(f"{key}.neg.{i}", f"toString (- {L(a)})", f"{F}(lean_{key}_neg({C(a)}))")
        emit(f"{key}.abs.{i}", f"toString ({L(a)}.abs)", f"{F}(lean_{key}_abs({C(a)}))")
        toint = {"int64": "lean_int64_to_int_sint", "isize": "lean_isize_to_int"}.get(key, f"lean_{key}_to_int")
        emit(f"{key}.toInt.{i}", f"toString ({L(a)}.toInt)", f"FI({toint}({C(a)}))")
        emit(f"{key}.toFloat.{i}", f"FF ({L(a)}.toFloat)", f"FF(lean_{key}_to_float({C(a)}))")
        emit(f"{key}.toFloat32.{i}", f"FF32 ({L(a)}.toFloat32)", f"FF32(lean_{key}_to_float32({C(a)}))")
        for k2, (lt2, ct2, bits2) in ST.items():
            if k2 == key: continue
            emit(f"{key}.to_{k2}.{i}", f"toString ({L(a)}.to{lt2})", f"FS{bits2}(lean_{key}_to_{k2}({C(a)}))")
    for i, n in enumerate(NATS):
        emit(f"{key}.ofNat.{i}", f"toString ({lt}.ofNat {ln(n)})", f"{F}(lean_{key}_of_nat({cn(n)}))")
    for i, n in enumerate(INTS):
        emit(f"{key}.ofInt.{i}", f"toString ({lt}.ofInt {li(n)})", f"{F}(lean_{key}_of_int({ci(n)}))")
    emit(f"{key}.ofBool.t", f"toString (Bool.to{lt} true)", f"{F}(lean_bool_to_{key}(1))")

emit("mixhash.1", "toString (mixHash 12345 67890)", "FU(lean_uint64_mix_hash(12345, 67890))")
emit("mixhash.2", "toString (mixHash 18446744073709551615 7)", "FU(lean_uint64_mix_hash(18446744073709551615, 7))")

# ------------------------------------------------------------------ Float
def dbits(x): return struct.unpack("<Q", struct.pack("<d", x))[0]
def fbits(x): return struct.unpack("<I", struct.pack("<f", x))[0]

FLOATS = [0.0, -0.0, 1.0, -1.0, 0.5, 1.5, 2.5, -2.5, 0.1, 1e-7, 0.0078125, 2.5e-6, 123456.789, -987654.321,
          1e10, 1e19, 1.8446744073709552e19, 2e19, 9.3e18, -9.3e18, 255.9, 256.0, -128.9, -129.0, 127.99,
          65535.5, 4294967295.9, -2147483648.5, -2147483649.0, 3.14159, 1e300, 5e-324, 2.2250738585072014e-308,
          1.7976931348623157e308, 0.4999999999999999, 3.5, 1e22, 1e23, 123.4567895]
FB = [dbits(x) for x in FLOATS] + [0x7ff0000000000000, 0xfff0000000000000, 0x7ff8000000000000, 0xfff8000000000001]
F32 = [fbits(x) for x in [0.0, -0.0, 1.0, -1.0, 0.5, 2.5, -2.5, 0.1, 1e-7, 123456.79, 1e10, 1e19, 2e19, 9.3e18,
                          -9.3e18, 255.9, 256.0, -128.9, -129.0, 3.14159, 3.4e38, 1e-45, 1.1754944e-38, 65535.5,
                          4294967040.0, 0.0078125]] + [0x7f800000, 0xff800000, 0x7fc00000, 0xffc00001]

def lf(b): return f"(Float.ofBits {b})"
def cf(b): return f"D({b}UL)"
def lf32(b): return f"(Float32.ofBits {b})"
def cf32(b): return f"F({b}U)"

for i, b in enumerate(FB):
    a, c = lf(b), cf(b)
    emit(f"float.str.{i}", f"FF {a}", f"FF({c})")
    emit(f"float.isnan.{i}", f"toString {a}.isNaN", f"FB(lean_float_isnan({c}))")
    emit(f"float.isinf.{i}", f"toString {a}.isInf", f"FB(lean_float_isinf({c}))")
    emit(f"float.isfinite.{i}", f"toString {a}.isFinite", f"FB(lean_float_isfinite({c}))")
    emit(f"float.frexp.{i}", f"(let p := Float.frExp {a}; FF p.1 ++ \" \" ++ toString p.2)", f"FFrexp(lean_float_frexp({c}))")
    for (nm, t, f) in [("toUInt8", "u", "FU"), ("toUInt16", "u", "FU"), ("toUInt32", "u", "FU"), ("toUInt64", "u", "FU"),
                       ("toUSize", "u", "FU"), ("toInt8", 8, "FS8"), ("toInt16", 16, "FS16"), ("toInt32", 32, "FS32"),
                       ("toInt64", 64, "FS64"), ("toISize", 64, "FS64")]:
        cname = "lean_float_to_" + nm[2:].lower()
        emit(f"float.{nm}.{i}", f"toString {a}.{nm}", f"{f}({cname}({c}))")
    for fn in ["sin", "cos", "tan", "asin", "acos", "atan", "exp", "log", "sqrt", "cbrt", "ceil", "floor", "round", "abs"]:
        cfn = "fabs" if fn == "abs" else fn
        emit(f"float.{fn}.{i}", f"FF (Float.{fn} {a})", f"FF({cfn}({c}))")
    emit(f"float.neg.{i}", f"FF (- {a})", f"FF(lean_float_negate({c}))")
    emit(f"float.toF32.{i}", f"FF32 {a}.toFloat32", f"FF32(lean_float_to_float32({c}))")
    emit(f"float.bits.{i}", f"toString {a}.toBits", f"FU(lean_float_to_bits(lean_float_of_bits({b}UL)))")
    for k, e in enumerate(["0", "1", "-1", "1023", "-1074", "2000", "-2000", "1000000000000000000000000000000", "-1000000000000000000000000000000"]):
        emit(f"float.scaleb.{i}.{k}", f"FF (Float.scaleB {a} ({e} : Int))", f'FF(lean_float_scaleb({c}, I("{e}")))')
BIN = [dbits(x) for x in [0.0, -0.0, 1.0, -2.5, 0.1, 1e300, 5e-324, 3.5, 1e-7]] + [0x7ff0000000000000, 0xfff0000000000000, 0x7ff8000000000000]
for i, b1 in enumerate(BIN):
    for j, b2 in enumerate(BIN):
        a1, a2, c1, c2 = lf(b1), lf(b2), cf(b1), cf(b2)
        for (nm, le, cfn) in [("add", "+", "lean_float_add"), ("sub", "-", "lean_float_sub"), ("mul", "*", "lean_float_mul"), ("div", "/", "lean_float_div")]:
            emit(f"float.{nm}.{i}.{j}", f"FF ({a1} {le} {a2})", f"FF({cfn}({c1}, {c2}))")
        emit(f"float.pow.{i}.{j}", f"FF (Float.pow {a1} {a2})", f"FF(pow({c1}, {c2}))")
        emit(f"float.beq.{i}.{j}", f"toString ({a1} == {a2})", f"FB(lean_float_beq({c1}, {c2}))")
        emit(f"float.le.{i}.{j}", f"toString (decide ({a1} ≤ {a2}))", f"FB(lean_float_decLe({c1}, {c2}))")
        emit(f"float.lt.{i}.{j}", f"toString (decide ({a1} < {a2}))", f"FB(lean_float_decLt({c1}, {c2}))")
        for fn in ["minimum", "maximum", "minimumNumber", "maximumNumber"]:
            cfn = "lean_float_" + {"minimum": "minimum", "maximum": "maximum", "minimumNumber": "minimum_number", "maximumNumber": "maximum_number"}[fn]
            emit(f"float.{fn}.{i}.{j}", f"FF (Float.{fn} {a1} {a2})", f"FF({cfn}({c1}, {c2}))")
        emit(f"float.fma.{i}.{j}", f"FF (Float.fma {a1} {a2} 0.3)", f"FF(fma({c1}, {c2}, 0.3))")

for i, b in enumerate(F32):
    a, c = lf32(b), cf32(b)
    emit(f"f32.str.{i}", f"FF32 {a}", f"FF32({c})")
    emit(f"f32.isnan.{i}", f"toString {a}.isNaN", f"FB(lean_float32_isnan({c}))")
    emit(f"f32.isinf.{i}", f"toString {a}.isInf", f"FB(lean_float32_isinf({c}))")
    emit(f"f32.isfinite.{i}", f"toString {a}.isFinite", f"FB(lean_float32_isfinite({c}))")
    emit(f"f32.frexp.{i}", f"(let p := Float32.frExp {a}; FF32 p.1 ++ \" \" ++ toString p.2)", f"FFrexp32(lean_float32_frexp({c}))")
    for (nm, t, f) in [("toUInt8", "u", "FU"), ("toUInt16", "u", "FU"), ("toUInt32", "u", "FU"), ("toUInt64", "u", "FU"),
                       ("toUSize", "u", "FU"), ("toInt8", 8, "FS8"), ("toInt16", 16, "FS16"), ("toInt32", 32, "FS32"),
                       ("toInt64", 64, "FS64"), ("toISize", 64, "FS64")]:
        cname = "lean_float32_to_" + nm[2:].lower()
        emit(f"f32.{nm}.{i}", f"toString {a}.{nm}", f"{f}({cname}({c}))")
    for fn in ["sin", "cos", "tan", "asin", "acos", "atan", "exp", "log", "sqrt", "cbrt", "ceil", "floor", "round", "abs"]:
        cfn = ("fabs" if fn == "abs" else fn) + "f"
        emit(f"f32.{fn}.{i}", f"FF32 (Float32.{fn} {a})", f"FF32({cfn}({c}))")
    emit(f"f32.neg.{i}", f"FF32 (- {a})", f"FF32(lean_float32_negate({c}))")
    emit(f"f32.toFloat.{i}", f"FF {a}.toFloat", f"FF(lean_float32_to_float({c}))")
    emit(f"f32.bits.{i}", f"toString {a}.toBits", f"FU(lean_float32_to_bits(lean_float32_of_bits({b}U)))")
    for k, e in enumerate(["0", "1", "-1", "127", "-149", "300", "-300", "1000000000000000000000000000000", "-1000000000000000000000000000000"]):
        emit(f"f32.scaleb.{i}.{k}", f"FF32 (Float32.scaleB {a} ({e} : Int))", f'FF32(lean_float32_scaleb({c}, I("{e}")))')
BIN32 = [fbits(x) for x in [0.0, -0.0, 1.0, -2.5, 0.1, 3e38, 1e-45, 3.5]] + [0x7f800000, 0xff800000, 0x7fc00000]
for i, b1 in enumerate(BIN32):
    for j, b2 in enumerate(BIN32):
        a1, a2, c1, c2 = lf32(b1), lf32(b2), cf32(b1), cf32(b2)
        for (nm, le, cfn) in [("add", "+", "lean_float32_add"), ("sub", "-", "lean_float32_sub"), ("mul", "*", "lean_float32_mul"), ("div", "/", "lean_float32_div")]:
            emit(f"f32.{nm}.{i}.{j}", f"FF32 ({a1} {le} {a2})", f"FF32({cfn}({c1}, {c2}))")
        emit(f"f32.pow.{i}.{j}", f"FF32 (Float32.pow {a1} {a2})", f"FF32(powf({c1}, {c2}))")
        emit(f"f32.beq.{i}.{j}", f"toString ({a1} == {a2})", f"FB(lean_float32_beq({c1}, {c2}))")
        emit(f"f32.le.{i}.{j}", f"toString (decide ({a1} ≤ {a2}))", f"FB(lean_float32_decLe({c1}, {c2}))")
        emit(f"f32.lt.{i}.{j}", f"toString (decide ({a1} < {a2}))", f"FB(lean_float32_decLt({c1}, {c2}))")
        for fn in ["minimum", "maximum", "minimumNumber", "maximumNumber"]:
            cfn = "lean_float32_" + {"minimum": "minimum", "maximum": "maximum", "minimumNumber": "minimum_number", "maximumNumber": "maximum_number"}[fn]
            emit(f"f32.{fn}.{i}.{j}", f"FF32 (Float32.{fn} {a1} {a2})", f"FF32({cfn}({c1}, {c2}))")
        emit(f"f32.fma.{i}.{j}", f"FF32 (Float32.fma {a1} {a2} 0.3)", f"FF32(fmaf({c1}, {c2}, 0.3f))")

# ------------------------------------------------------------------ output
CHUNK = 400
with open(os.path.join(HERE, "ref_tests.lean"), "w") as f:
    f.write("-- Generated by gen_tests.py. Run: lean --run ref_tests.lean > expected.txt\n")
    f.write("def FF (x : Float) : String := toString x ++ \" \" ++ toString x.toBits\n")
    f.write("def FF32 (x : Float32) : String := toString x ++ \" \" ++ toString x.toBits\n\n")
    nchunks = (len(lean) + CHUNK - 1) // CHUNK
    for k in range(nchunks):
        f.write(f"def part{k} : IO Unit := do\n")
        f.write("\n".join(lean[k * CHUNK:(k + 1) * CHUNK]) + "\n\n")
    f.write("def main : IO Unit := do\n")
    for k in range(nchunks):
        f.write(f"  part{k}\n")

with open(os.path.join(HERE, "Generated.cs"), "w") as f:
    f.write("// Generated by gen_tests.py -- do not edit.\n")
    f.write("using static LeanSharp.Runtime.LeanRt;\n\n")
    f.write("static partial class Tests\n{\n")
    nchunks = (len(cs) + CHUNK - 1) // CHUNK
    for k in range(nchunks):
        f.write(f"    static void Part{k}()\n    {{\n")
        f.write("\n".join(cs[k * CHUNK:(k + 1) * CHUNK]) + "\n    }\n\n")
    f.write("    static void RunGenerated()\n    {\n")
    for k in range(nchunks):
        f.write(f"        Part{k}();\n")
    f.write("    }\n}\n")

print(f"{len(lean)} tests")
