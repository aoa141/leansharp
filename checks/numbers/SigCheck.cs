// Generated from docs/externs-numbers.txt: checks that every extern exists with the exact signature.
using LeanSharp.Runtime;

static unsafe class SigCheck
{
    public static int Run()
    {
        int n = 0;
        { delegate*<float, Obj> f = &LeanRt.lean_float32_frexp; if (f != null) n++; }
        { delegate*<float, Obj> f = &LeanRt.lean_float32_to_string; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_float_array_data; if (f != null) n++; }
        { delegate*<Obj, Obj, double, Obj> f = &LeanRt.lean_float_array_fset; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_float_array_mk; if (f != null) n++; }
        { delegate*<Obj, double, Obj> f = &LeanRt.lean_float_array_push; if (f != null) n++; }
        { delegate*<Obj, Obj, double, Obj> f = &LeanRt.lean_float_array_set; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_float_array_size; if (f != null) n++; }
        { delegate*<Obj, ulong, double, Obj> f = &LeanRt.lean_float_array_uset; if (f != null) n++; }
        { delegate*<double, Obj> f = &LeanRt.lean_float_frexp; if (f != null) n++; }
        { delegate*<double, Obj> f = &LeanRt.lean_float_to_string; if (f != null) n++; }
        { delegate*<ushort, Obj> f = &LeanRt.lean_int16_to_int; if (f != null) n++; }
        { delegate*<uint, Obj> f = &LeanRt.lean_int32_to_int; if (f != null) n++; }
        { delegate*<ulong, Obj> f = &LeanRt.lean_int64_to_int_sint; if (f != null) n++; }
        { delegate*<byte, Obj> f = &LeanRt.lean_int8_to_int; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_int_add; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_int_div; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_int_div_exact; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_int_ediv; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_int_emod; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_int_mod; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_int_mul; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_int_neg; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_int_neg_succ_of_nat; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_int_sub; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_internal_enable_debug; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_internal_get_build_type; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_internal_get_default_max_heartbeat; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_internal_get_default_max_memory; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_internal_get_option_overrides; if (f != null) n++; }
        { delegate*<byte, Obj> f = &LeanRt.lean_internal_set_exit_on_panic; if (f != null) n++; }
        { delegate*<ulong, Obj> f = &LeanRt.lean_internal_set_max_heartbeat; if (f != null) n++; }
        { delegate*<ulong, Obj> f = &LeanRt.lean_internal_set_max_memory; if (f != null) n++; }
        { delegate*<ulong, Obj> f = &LeanRt.lean_internal_set_thread_stack_size; if (f != null) n++; }
        { delegate*<ulong, Obj> f = &LeanRt.lean_isize_to_int; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_nat_abs; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_add; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_div; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_div_exact; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_gcd; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_land; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_nat_log2; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_lor; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_lxor; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_mod; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_mul; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_pow; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj, Obj> f = &LeanRt.lean_nat_powmod; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_nat_pred; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_shiftl; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_shiftr; if (f != null) n++; }
        { delegate*<Obj, Obj, Obj> f = &LeanRt.lean_nat_sub; if (f != null) n++; }
        { delegate*<Obj, Obj> f = &LeanRt.lean_nat_to_int; if (f != null) n++; }
        { delegate*<ushort, Obj> f = &LeanRt.lean_uint16_to_nat; if (f != null) n++; }
        { delegate*<uint, Obj> f = &LeanRt.lean_uint32_to_nat; if (f != null) n++; }
        { delegate*<ulong, Obj> f = &LeanRt.lean_uint64_to_nat; if (f != null) n++; }
        { delegate*<byte, Obj> f = &LeanRt.lean_uint8_to_nat; if (f != null) n++; }
        { delegate*<ulong, Obj> f = &LeanRt.lean_usize_to_nat; if (f != null) n++; }
        { delegate*<byte, byte> f = &LeanRt.lean_bool_to_int8; if (f != null) n++; }
        { delegate*<byte, byte> f = &LeanRt.lean_bool_to_uint8; if (f != null) n++; }
        { delegate*<float, float, byte> f = &LeanRt.lean_float32_beq; if (f != null) n++; }
        { delegate*<float, float, byte> f = &LeanRt.lean_float32_decLe; if (f != null) n++; }
        { delegate*<float, float, byte> f = &LeanRt.lean_float32_decLt; if (f != null) n++; }
        { delegate*<float, byte> f = &LeanRt.lean_float32_isfinite; if (f != null) n++; }
        { delegate*<float, byte> f = &LeanRt.lean_float32_isinf; if (f != null) n++; }
        { delegate*<float, byte> f = &LeanRt.lean_float32_isnan; if (f != null) n++; }
        { delegate*<float, byte> f = &LeanRt.lean_float32_to_int8; if (f != null) n++; }
        { delegate*<float, byte> f = &LeanRt.lean_float32_to_uint8; if (f != null) n++; }
        { delegate*<double, double, byte> f = &LeanRt.lean_float_beq; if (f != null) n++; }
        { delegate*<double, double, byte> f = &LeanRt.lean_float_decLe; if (f != null) n++; }
        { delegate*<double, double, byte> f = &LeanRt.lean_float_decLt; if (f != null) n++; }
        { delegate*<double, byte> f = &LeanRt.lean_float_isfinite; if (f != null) n++; }
        { delegate*<double, byte> f = &LeanRt.lean_float_isinf; if (f != null) n++; }
        { delegate*<double, byte> f = &LeanRt.lean_float_isnan; if (f != null) n++; }
        { delegate*<double, byte> f = &LeanRt.lean_float_to_int8; if (f != null) n++; }
        { delegate*<double, byte> f = &LeanRt.lean_float_to_uint8; if (f != null) n++; }
        { delegate*<ushort, ushort, byte> f = &LeanRt.lean_int16_dec_eq; if (f != null) n++; }
        { delegate*<ushort, ushort, byte> f = &LeanRt.lean_int16_dec_le; if (f != null) n++; }
        { delegate*<ushort, ushort, byte> f = &LeanRt.lean_int16_dec_lt; if (f != null) n++; }
        { delegate*<ushort, byte> f = &LeanRt.lean_int16_to_int8; if (f != null) n++; }
        { delegate*<uint, uint, byte> f = &LeanRt.lean_int32_dec_eq; if (f != null) n++; }
        { delegate*<uint, uint, byte> f = &LeanRt.lean_int32_dec_le; if (f != null) n++; }
        { delegate*<uint, uint, byte> f = &LeanRt.lean_int32_dec_lt; if (f != null) n++; }
        { delegate*<uint, byte> f = &LeanRt.lean_int32_to_int8; if (f != null) n++; }
        { delegate*<ulong, ulong, byte> f = &LeanRt.lean_int64_dec_eq; if (f != null) n++; }
        { delegate*<ulong, ulong, byte> f = &LeanRt.lean_int64_dec_le; if (f != null) n++; }
        { delegate*<ulong, ulong, byte> f = &LeanRt.lean_int64_dec_lt; if (f != null) n++; }
        { delegate*<ulong, byte> f = &LeanRt.lean_int64_to_int8; if (f != null) n++; }
        { delegate*<byte, byte> f = &LeanRt.lean_int8_abs; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_add; if (f != null) n++; }
        { delegate*<byte, byte> f = &LeanRt.lean_int8_complement; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_dec_eq; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_dec_le; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_dec_lt; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_div; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_land; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_lor; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_mod; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_mul; if (f != null) n++; }
        { delegate*<byte, byte> f = &LeanRt.lean_int8_neg; if (f != null) n++; }
        { delegate*<Obj, byte> f = &LeanRt.lean_int8_of_int; if (f != null) n++; }
        { delegate*<Obj, byte> f = &LeanRt.lean_int8_of_nat; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_shift_left; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_shift_right; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_sub; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_int8_xor; if (f != null) n++; }
        { delegate*<Obj, Obj, byte> f = &LeanRt.lean_int_dec_eq; if (f != null) n++; }
        { delegate*<Obj, Obj, byte> f = &LeanRt.lean_int_dec_le; if (f != null) n++; }
        { delegate*<Obj, Obj, byte> f = &LeanRt.lean_int_dec_lt; if (f != null) n++; }
        { delegate*<Obj, byte> f = &LeanRt.lean_int_dec_nonneg; if (f != null) n++; }
        { delegate*<Obj, byte> f = &LeanRt.lean_internal_get_default_verbose; if (f != null) n++; }
        { delegate*<Obj, byte> f = &LeanRt.lean_internal_has_address_sanitizer; if (f != null) n++; }
        { delegate*<Obj, byte> f = &LeanRt.lean_internal_has_llvm_backend; if (f != null) n++; }
        { delegate*<Obj, byte> f = &LeanRt.lean_internal_is_debug; if (f != null) n++; }
        { delegate*<Obj, byte> f = &LeanRt.lean_internal_is_multi_thread; if (f != null) n++; }
        { delegate*<Obj, byte> f = &LeanRt.lean_internal_is_stage0; if (f != null) n++; }
        { delegate*<ulong, ulong, byte> f = &LeanRt.lean_isize_dec_eq; if (f != null) n++; }
        { delegate*<ulong, ulong, byte> f = &LeanRt.lean_isize_dec_le; if (f != null) n++; }
        { delegate*<ulong, ulong, byte> f = &LeanRt.lean_isize_dec_lt; if (f != null) n++; }
        { delegate*<ulong, byte> f = &LeanRt.lean_isize_to_int8; if (f != null) n++; }
        { delegate*<Obj, Obj, byte> f = &LeanRt.lean_nat_dec_eq; if (f != null) n++; }
        { delegate*<Obj, Obj, byte> f = &LeanRt.lean_nat_dec_le; if (f != null) n++; }
        { delegate*<Obj, Obj, byte> f = &LeanRt.lean_nat_dec_lt; if (f != null) n++; }
        { delegate*<ushort, ushort, byte> f = &LeanRt.lean_uint16_dec_eq; if (f != null) n++; }
        { delegate*<ushort, ushort, byte> f = &LeanRt.lean_uint16_dec_le; if (f != null) n++; }
        { delegate*<ushort, ushort, byte> f = &LeanRt.lean_uint16_dec_lt; if (f != null) n++; }
        { delegate*<ushort, byte> f = &LeanRt.lean_uint16_to_uint8; if (f != null) n++; }
        { delegate*<uint, uint, byte> f = &LeanRt.lean_uint32_dec_eq; if (f != null) n++; }
        { delegate*<uint, uint, byte> f = &LeanRt.lean_uint32_dec_le; if (f != null) n++; }
        { delegate*<uint, uint, byte> f = &LeanRt.lean_uint32_dec_lt; if (f != null) n++; }
        { delegate*<uint, byte> f = &LeanRt.lean_uint32_to_uint8; if (f != null) n++; }
        { delegate*<ulong, ulong, byte> f = &LeanRt.lean_uint64_dec_eq; if (f != null) n++; }
        { delegate*<ulong, ulong, byte> f = &LeanRt.lean_uint64_dec_le; if (f != null) n++; }
        { delegate*<ulong, ulong, byte> f = &LeanRt.lean_uint64_dec_lt; if (f != null) n++; }
        { delegate*<ulong, byte> f = &LeanRt.lean_uint64_to_uint8; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_add; if (f != null) n++; }
        { delegate*<byte, byte> f = &LeanRt.lean_uint8_complement; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_dec_eq; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_dec_le; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_dec_lt; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_div; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_land; if (f != null) n++; }
        { delegate*<byte, byte> f = &LeanRt.lean_uint8_log2; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_lor; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_mod; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_mul; if (f != null) n++; }
        { delegate*<byte, byte> f = &LeanRt.lean_uint8_neg; if (f != null) n++; }
        { delegate*<Obj, byte> f = &LeanRt.lean_uint8_of_nat; if (f != null) n++; }
        { delegate*<Obj, byte> f = &LeanRt.lean_uint8_of_nat_mk; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_shift_left; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_shift_right; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_sub; if (f != null) n++; }
        { delegate*<byte, byte, byte> f = &LeanRt.lean_uint8_xor; if (f != null) n++; }
        { delegate*<ulong, ulong, byte> f = &LeanRt.lean_usize_dec_eq; if (f != null) n++; }
        { delegate*<ulong, ulong, byte> f = &LeanRt.lean_usize_dec_le; if (f != null) n++; }
        { delegate*<ulong, ulong, byte> f = &LeanRt.lean_usize_dec_lt; if (f != null) n++; }
        { delegate*<ulong, byte> f = &LeanRt.lean_usize_to_uint8; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.acos; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.asin; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.atan; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.cbrt; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.ceil; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.cos; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.exp; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.fabs; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.floor; if (f != null) n++; }
        { delegate*<double, double, double, double> f = &LeanRt.fma; if (f != null) n++; }
        { delegate*<float, double> f = &LeanRt.lean_float32_to_float; if (f != null) n++; }
        { delegate*<double, double, double> f = &LeanRt.lean_float_add; if (f != null) n++; }
        { delegate*<Obj, Obj, double> f = &LeanRt.lean_float_array_fget; if (f != null) n++; }
        { delegate*<Obj, Obj, double> f = &LeanRt.lean_float_array_get; if (f != null) n++; }
        { delegate*<Obj, ulong, double> f = &LeanRt.lean_float_array_uget; if (f != null) n++; }
        { delegate*<double, double, double> f = &LeanRt.lean_float_div; if (f != null) n++; }
        { delegate*<double, double, double> f = &LeanRt.lean_float_maximum; if (f != null) n++; }
        { delegate*<double, double, double> f = &LeanRt.lean_float_maximum_number; if (f != null) n++; }
        { delegate*<double, double, double> f = &LeanRt.lean_float_minimum; if (f != null) n++; }
        { delegate*<double, double, double> f = &LeanRt.lean_float_minimum_number; if (f != null) n++; }
        { delegate*<double, double, double> f = &LeanRt.lean_float_mul; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.lean_float_negate; if (f != null) n++; }
        { delegate*<ulong, double> f = &LeanRt.lean_float_of_bits; if (f != null) n++; }
        { delegate*<double, Obj, double> f = &LeanRt.lean_float_scaleb; if (f != null) n++; }
        { delegate*<double, double, double> f = &LeanRt.lean_float_sub; if (f != null) n++; }
        { delegate*<ushort, double> f = &LeanRt.lean_int16_to_float; if (f != null) n++; }
        { delegate*<uint, double> f = &LeanRt.lean_int32_to_float; if (f != null) n++; }
        { delegate*<ulong, double> f = &LeanRt.lean_int64_to_float; if (f != null) n++; }
        { delegate*<byte, double> f = &LeanRt.lean_int8_to_float; if (f != null) n++; }
        { delegate*<ulong, double> f = &LeanRt.lean_isize_to_float; if (f != null) n++; }
        { delegate*<ushort, double> f = &LeanRt.lean_uint16_to_float; if (f != null) n++; }
        { delegate*<uint, double> f = &LeanRt.lean_uint32_to_float; if (f != null) n++; }
        { delegate*<ulong, double> f = &LeanRt.lean_uint64_to_float; if (f != null) n++; }
        { delegate*<byte, double> f = &LeanRt.lean_uint8_to_float; if (f != null) n++; }
        { delegate*<ulong, double> f = &LeanRt.lean_usize_to_float; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.log; if (f != null) n++; }
        { delegate*<double, double, double> f = &LeanRt.pow; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.round; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.sin; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.sqrt; if (f != null) n++; }
        { delegate*<double, double> f = &LeanRt.tan; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.acosf; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.asinf; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.atanf; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.cbrtf; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.ceilf; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.cosf; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.expf; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.fabsf; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.floorf; if (f != null) n++; }
        { delegate*<float, float, float, float> f = &LeanRt.fmaf; if (f != null) n++; }
        { delegate*<float, float, float> f = &LeanRt.lean_float32_add; if (f != null) n++; }
        { delegate*<float, float, float> f = &LeanRt.lean_float32_div; if (f != null) n++; }
        { delegate*<float, float, float> f = &LeanRt.lean_float32_maximum; if (f != null) n++; }
        { delegate*<float, float, float> f = &LeanRt.lean_float32_maximum_number; if (f != null) n++; }
        { delegate*<float, float, float> f = &LeanRt.lean_float32_minimum; if (f != null) n++; }
        { delegate*<float, float, float> f = &LeanRt.lean_float32_minimum_number; if (f != null) n++; }
        { delegate*<float, float, float> f = &LeanRt.lean_float32_mul; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.lean_float32_negate; if (f != null) n++; }
        { delegate*<uint, float> f = &LeanRt.lean_float32_of_bits; if (f != null) n++; }
        { delegate*<float, Obj, float> f = &LeanRt.lean_float32_scaleb; if (f != null) n++; }
        { delegate*<float, float, float> f = &LeanRt.lean_float32_sub; if (f != null) n++; }
        { delegate*<double, float> f = &LeanRt.lean_float_to_float32; if (f != null) n++; }
        { delegate*<ushort, float> f = &LeanRt.lean_int16_to_float32; if (f != null) n++; }
        { delegate*<uint, float> f = &LeanRt.lean_int32_to_float32; if (f != null) n++; }
        { delegate*<ulong, float> f = &LeanRt.lean_int64_to_float32; if (f != null) n++; }
        { delegate*<byte, float> f = &LeanRt.lean_int8_to_float32; if (f != null) n++; }
        { delegate*<ulong, float> f = &LeanRt.lean_isize_to_float32; if (f != null) n++; }
        { delegate*<ushort, float> f = &LeanRt.lean_uint16_to_float32; if (f != null) n++; }
        { delegate*<uint, float> f = &LeanRt.lean_uint32_to_float32; if (f != null) n++; }
        { delegate*<ulong, float> f = &LeanRt.lean_uint64_to_float32; if (f != null) n++; }
        { delegate*<byte, float> f = &LeanRt.lean_uint8_to_float32; if (f != null) n++; }
        { delegate*<ulong, float> f = &LeanRt.lean_usize_to_float32; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.logf; if (f != null) n++; }
        { delegate*<float, float, float> f = &LeanRt.powf; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.roundf; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.sinf; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.sqrtf; if (f != null) n++; }
        { delegate*<float, float> f = &LeanRt.tanf; if (f != null) n++; }
        { delegate*<byte, uint> f = &LeanRt.lean_bool_to_int32; if (f != null) n++; }
        { delegate*<byte, uint> f = &LeanRt.lean_bool_to_uint32; if (f != null) n++; }
        { delegate*<float, uint> f = &LeanRt.lean_float32_to_bits; if (f != null) n++; }
        { delegate*<float, uint> f = &LeanRt.lean_float32_to_int32; if (f != null) n++; }
        { delegate*<float, uint> f = &LeanRt.lean_float32_to_uint32; if (f != null) n++; }
        { delegate*<double, uint> f = &LeanRt.lean_float_to_int32; if (f != null) n++; }
        { delegate*<double, uint> f = &LeanRt.lean_float_to_uint32; if (f != null) n++; }
        { delegate*<ushort, uint> f = &LeanRt.lean_int16_to_int32; if (f != null) n++; }
        { delegate*<uint, uint> f = &LeanRt.lean_int32_abs; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_int32_add; if (f != null) n++; }
        { delegate*<uint, uint> f = &LeanRt.lean_int32_complement; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_int32_div; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_int32_land; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_int32_lor; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_int32_mod; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_int32_mul; if (f != null) n++; }
        { delegate*<uint, uint> f = &LeanRt.lean_int32_neg; if (f != null) n++; }
        { delegate*<Obj, uint> f = &LeanRt.lean_int32_of_int; if (f != null) n++; }
        { delegate*<Obj, uint> f = &LeanRt.lean_int32_of_nat; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_int32_shift_left; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_int32_shift_right; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_int32_sub; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_int32_xor; if (f != null) n++; }
        { delegate*<ulong, uint> f = &LeanRt.lean_int64_to_int32; if (f != null) n++; }
        { delegate*<byte, uint> f = &LeanRt.lean_int8_to_int32; if (f != null) n++; }
        { delegate*<Obj, uint> f = &LeanRt.lean_internal_get_believer_trust_level; if (f != null) n++; }
        { delegate*<Obj, uint> f = &LeanRt.lean_internal_get_hardware_concurrency; if (f != null) n++; }
        { delegate*<ulong, uint> f = &LeanRt.lean_isize_to_int32; if (f != null) n++; }
        { delegate*<ushort, uint> f = &LeanRt.lean_uint16_to_uint32; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_uint32_add; if (f != null) n++; }
        { delegate*<uint, uint> f = &LeanRt.lean_uint32_complement; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_uint32_div; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_uint32_land; if (f != null) n++; }
        { delegate*<uint, uint> f = &LeanRt.lean_uint32_log2; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_uint32_lor; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_uint32_mod; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_uint32_mul; if (f != null) n++; }
        { delegate*<uint, uint> f = &LeanRt.lean_uint32_neg; if (f != null) n++; }
        { delegate*<Obj, uint> f = &LeanRt.lean_uint32_of_nat; if (f != null) n++; }
        { delegate*<Obj, uint> f = &LeanRt.lean_uint32_of_nat_mk; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_uint32_shift_left; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_uint32_shift_right; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_uint32_sub; if (f != null) n++; }
        { delegate*<uint, uint, uint> f = &LeanRt.lean_uint32_xor; if (f != null) n++; }
        { delegate*<ulong, uint> f = &LeanRt.lean_uint64_to_uint32; if (f != null) n++; }
        { delegate*<byte, uint> f = &LeanRt.lean_uint8_to_uint32; if (f != null) n++; }
        { delegate*<ulong, uint> f = &LeanRt.lean_usize_to_uint32; if (f != null) n++; }
        { delegate*<byte, ulong> f = &LeanRt.lean_bool_to_int64; if (f != null) n++; }
        { delegate*<byte, ulong> f = &LeanRt.lean_bool_to_isize; if (f != null) n++; }
        { delegate*<byte, ulong> f = &LeanRt.lean_bool_to_uint64; if (f != null) n++; }
        { delegate*<byte, ulong> f = &LeanRt.lean_bool_to_usize; if (f != null) n++; }
        { delegate*<float, ulong> f = &LeanRt.lean_float32_to_int64; if (f != null) n++; }
        { delegate*<float, ulong> f = &LeanRt.lean_float32_to_isize; if (f != null) n++; }
        { delegate*<float, ulong> f = &LeanRt.lean_float32_to_uint64; if (f != null) n++; }
        { delegate*<float, ulong> f = &LeanRt.lean_float32_to_usize; if (f != null) n++; }
        { delegate*<double, ulong> f = &LeanRt.lean_float_to_bits; if (f != null) n++; }
        { delegate*<double, ulong> f = &LeanRt.lean_float_to_int64; if (f != null) n++; }
        { delegate*<double, ulong> f = &LeanRt.lean_float_to_isize; if (f != null) n++; }
        { delegate*<double, ulong> f = &LeanRt.lean_float_to_uint64; if (f != null) n++; }
        { delegate*<double, ulong> f = &LeanRt.lean_float_to_usize; if (f != null) n++; }
        { delegate*<ushort, ulong> f = &LeanRt.lean_int16_to_int64; if (f != null) n++; }
        { delegate*<ushort, ulong> f = &LeanRt.lean_int16_to_isize; if (f != null) n++; }
        { delegate*<uint, ulong> f = &LeanRt.lean_int32_to_int64; if (f != null) n++; }
        { delegate*<uint, ulong> f = &LeanRt.lean_int32_to_isize; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_int64_abs; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_int64_add; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_int64_complement; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_int64_div; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_int64_land; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_int64_lor; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_int64_mod; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_int64_mul; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_int64_neg; if (f != null) n++; }
        { delegate*<Obj, ulong> f = &LeanRt.lean_int64_of_int; if (f != null) n++; }
        { delegate*<Obj, ulong> f = &LeanRt.lean_int64_of_nat; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_int64_shift_left; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_int64_shift_right; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_int64_sub; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_int64_to_isize; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_int64_xor; if (f != null) n++; }
        { delegate*<byte, ulong> f = &LeanRt.lean_int8_to_int64; if (f != null) n++; }
        { delegate*<byte, ulong> f = &LeanRt.lean_int8_to_isize; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_isize_abs; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_isize_add; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_isize_complement; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_isize_div; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_isize_land; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_isize_lor; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_isize_mod; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_isize_mul; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_isize_neg; if (f != null) n++; }
        { delegate*<Obj, ulong> f = &LeanRt.lean_isize_of_int; if (f != null) n++; }
        { delegate*<Obj, ulong> f = &LeanRt.lean_isize_of_nat; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_isize_shift_left; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_isize_shift_right; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_isize_sub; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_isize_to_int64; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_isize_xor; if (f != null) n++; }
        { delegate*<ushort, ulong> f = &LeanRt.lean_uint16_to_uint64; if (f != null) n++; }
        { delegate*<ushort, ulong> f = &LeanRt.lean_uint16_to_usize; if (f != null) n++; }
        { delegate*<uint, ulong> f = &LeanRt.lean_uint32_to_uint64; if (f != null) n++; }
        { delegate*<uint, ulong> f = &LeanRt.lean_uint32_to_usize; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_uint64_add; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_uint64_complement; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_uint64_div; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_uint64_land; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_uint64_log2; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_uint64_lor; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_uint64_mix_hash; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_uint64_mod; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_uint64_mul; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_uint64_neg; if (f != null) n++; }
        { delegate*<Obj, ulong> f = &LeanRt.lean_uint64_of_nat; if (f != null) n++; }
        { delegate*<Obj, ulong> f = &LeanRt.lean_uint64_of_nat_mk; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_uint64_shift_left; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_uint64_shift_right; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_uint64_sub; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_uint64_to_usize; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_uint64_xor; if (f != null) n++; }
        { delegate*<byte, ulong> f = &LeanRt.lean_uint8_to_uint64; if (f != null) n++; }
        { delegate*<byte, ulong> f = &LeanRt.lean_uint8_to_usize; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_usize_add; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_usize_complement; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_usize_div; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_usize_land; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_usize_log2; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_usize_lor; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_usize_mod; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_usize_mul; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_usize_neg; if (f != null) n++; }
        { delegate*<Obj, ulong> f = &LeanRt.lean_usize_of_nat; if (f != null) n++; }
        { delegate*<Obj, ulong> f = &LeanRt.lean_usize_of_nat_mk; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_usize_shift_left; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_usize_shift_right; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_usize_sub; if (f != null) n++; }
        { delegate*<ulong, ulong> f = &LeanRt.lean_usize_to_uint64; if (f != null) n++; }
        { delegate*<ulong, ulong, ulong> f = &LeanRt.lean_usize_xor; if (f != null) n++; }
        { delegate*<byte, ushort> f = &LeanRt.lean_bool_to_int16; if (f != null) n++; }
        { delegate*<byte, ushort> f = &LeanRt.lean_bool_to_uint16; if (f != null) n++; }
        { delegate*<float, ushort> f = &LeanRt.lean_float32_to_int16; if (f != null) n++; }
        { delegate*<float, ushort> f = &LeanRt.lean_float32_to_uint16; if (f != null) n++; }
        { delegate*<double, ushort> f = &LeanRt.lean_float_to_int16; if (f != null) n++; }
        { delegate*<double, ushort> f = &LeanRt.lean_float_to_uint16; if (f != null) n++; }
        { delegate*<ushort, ushort> f = &LeanRt.lean_int16_abs; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_int16_add; if (f != null) n++; }
        { delegate*<ushort, ushort> f = &LeanRt.lean_int16_complement; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_int16_div; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_int16_land; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_int16_lor; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_int16_mod; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_int16_mul; if (f != null) n++; }
        { delegate*<ushort, ushort> f = &LeanRt.lean_int16_neg; if (f != null) n++; }
        { delegate*<Obj, ushort> f = &LeanRt.lean_int16_of_int; if (f != null) n++; }
        { delegate*<Obj, ushort> f = &LeanRt.lean_int16_of_nat; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_int16_shift_left; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_int16_shift_right; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_int16_sub; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_int16_xor; if (f != null) n++; }
        { delegate*<uint, ushort> f = &LeanRt.lean_int32_to_int16; if (f != null) n++; }
        { delegate*<ulong, ushort> f = &LeanRt.lean_int64_to_int16; if (f != null) n++; }
        { delegate*<byte, ushort> f = &LeanRt.lean_int8_to_int16; if (f != null) n++; }
        { delegate*<ulong, ushort> f = &LeanRt.lean_isize_to_int16; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_uint16_add; if (f != null) n++; }
        { delegate*<ushort, ushort> f = &LeanRt.lean_uint16_complement; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_uint16_div; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_uint16_land; if (f != null) n++; }
        { delegate*<ushort, ushort> f = &LeanRt.lean_uint16_log2; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_uint16_lor; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_uint16_mod; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_uint16_mul; if (f != null) n++; }
        { delegate*<ushort, ushort> f = &LeanRt.lean_uint16_neg; if (f != null) n++; }
        { delegate*<Obj, ushort> f = &LeanRt.lean_uint16_of_nat; if (f != null) n++; }
        { delegate*<Obj, ushort> f = &LeanRt.lean_uint16_of_nat_mk; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_uint16_shift_left; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_uint16_shift_right; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_uint16_sub; if (f != null) n++; }
        { delegate*<ushort, ushort, ushort> f = &LeanRt.lean_uint16_xor; if (f != null) n++; }
        { delegate*<uint, ushort> f = &LeanRt.lean_uint32_to_uint16; if (f != null) n++; }
        { delegate*<ulong, ushort> f = &LeanRt.lean_uint64_to_uint16; if (f != null) n++; }
        { delegate*<byte, ushort> f = &LeanRt.lean_uint8_to_uint16; if (f != null) n++; }
        { delegate*<ulong, ushort> f = &LeanRt.lean_usize_to_uint16; if (f != null) n++; }
        return n;
    }
}
