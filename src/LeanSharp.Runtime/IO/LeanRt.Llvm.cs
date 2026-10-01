// Stubs for the LLVM backend bindings (library/llvm.cpp, Lean/Compiler/IR/LLVMBindings.lean).
// LeanSharp has no LLVM backend: all of these are `BaseIO` functions, so they panic.

namespace LeanSharp.Runtime;

public static unsafe partial class LeanRt
{
    const string IoLlvmMessage = "LLVM backend is not supported by LeanSharp";

    static Exception IoLlvmUnsupported(string fn) => lean_internal_panic(IoLlvmMessage + " (" + fn + ")");

    /* initLLVM : IO Unit */
    public static Obj lean_init_llvm() => LeanIOErrors.UserErrorResult(IoLlvmMessage);

    /* emitLLVM (env : Environment) (modName : Name) (filepath : FilePath) : IO Unit */
    public static Obj lean_emit_llvm(Obj env, Obj mod_name, Obj filepath)
    {
        lean_dec(env);
        lean_dec(mod_name);
        lean_dec(filepath);
        return LeanIOErrors.UserErrorResult(IoLlvmMessage);
    }

    /* Lean.LLVM.isDeclaration */
    public static byte llvm_is_declaration(ulong ctx, ulong global) => throw IoLlvmUnsupported("Lean.LLVM.isDeclaration");
    /* Lean.LLVM.countParams */
    public static ulong llvm_count_params(ulong ctx, ulong f) => throw IoLlvmUnsupported("Lean.LLVM.countParams");
    /* Lean.LLVM.getParam */
    public static ulong llvm_get_param(ulong ctx, ulong f, ulong ix) => throw IoLlvmUnsupported("Lean.LLVM.getParam");

    /* Lean.LLVM.addAttributeAtIndex */
    public static Obj lean_llvm_add_attribute_at_index(ulong a0, ulong a1, ulong a2, ulong a3) => throw IoLlvmUnsupported("Lean.LLVM.addAttributeAtIndex");
    /* Lean.LLVM.addCase */
    public static Obj lean_llvm_add_case(ulong a0, ulong a1, ulong a2, ulong a3) => throw IoLlvmUnsupported("Lean.LLVM.addCase");
    /* Lean.LLVM.buildStore */
    public static Obj lean_llvm_build_store(ulong a0, ulong a1, ulong a2, ulong a3) => throw IoLlvmUnsupported("Lean.LLVM.buildStore");
    /* Lean.LLVM.clearInsertionPosition */
    public static Obj lean_llvm_clear_insertion_position(Obj a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.clearInsertionPosition");
    /* Lean.LLVM.disposeModule */
    public static Obj lean_llvm_dispose_module(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.disposeModule");
    /* Lean.LLVM.disposeTargetMachine */
    public static Obj lean_llvm_dispose_target_machine(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.disposeTargetMachine");
    /* Lean.LLVM.getDefaultTargetTriple */
    public static Obj lean_llvm_get_default_target_triple() => lean_mk_string(LeanPlatformTarget);
    /* Lean.LLVM.getFirstInstruction */
    public static Obj lean_llvm_get_first_instruction(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.getFirstInstruction");
    /* Lean.LLVM.getNamedFunction */
    public static Obj lean_llvm_get_named_function(ulong a0, ulong a1, Obj a2) => throw IoLlvmUnsupported("Lean.LLVM.getNamedFunction");
    /* Lean.LLVM.getNamedGlobal */
    public static Obj lean_llvm_get_named_global(ulong a0, ulong a1, Obj a2) => throw IoLlvmUnsupported("Lean.LLVM.getNamedGlobal");
    /* Lean.LLVM.Value.getName */
    public static Obj lean_llvm_get_value_name2(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.Value.getName");
    /* Lean.LLVM.llvmInitializeTargetInfo */
    public static Obj lean_llvm_initialize_target_info() => lean_box(0);
    /* Lean.LLVM.linkModules */
    public static Obj lean_llvm_link_modules(ulong a0, ulong a1, ulong a2) => throw IoLlvmUnsupported("Lean.LLVM.linkModules");
    /* Lean.LLVM.moduleToString */
    public static Obj lean_llvm_module_to_string(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.moduleToString");
    /* Lean.LLVM.positionBuilderAtEnd */
    public static Obj lean_llvm_position_builder_at_end(Obj a0, ulong a1, ulong a2) => throw IoLlvmUnsupported("Lean.LLVM.positionBuilderAtEnd");
    /* Lean.LLVM.positionBuilderBefore */
    public static Obj lean_llvm_position_builder_before(ulong a0, ulong a1, ulong a2) => throw IoLlvmUnsupported("Lean.LLVM.positionBuilderBefore");
    /* Lean.LLVM.printModuletoFile */
    public static Obj lean_llvm_print_module_to_file(ulong a0, ulong a1, Obj a2) => throw IoLlvmUnsupported("Lean.LLVM.printModuletoFile");
    /* Lean.LLVM.printModuletoString */
    public static Obj lean_llvm_print_module_to_string(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.printModuletoString");
    /* Lean.LLVM.setDLLStorageClass */
    public static Obj lean_llvm_set_dll_storage_class(ulong a0, ulong a1, ulong a2) => throw IoLlvmUnsupported("Lean.LLVM.setDLLStorageClass");
    /* Lean.LLVM.setInitializer */
    public static Obj lean_llvm_set_initializer(ulong a0, ulong a1, ulong a2) => throw IoLlvmUnsupported("Lean.LLVM.setInitializer");
    /* Lean.LLVM.setLinkage */
    public static Obj lean_llvm_set_linkage(ulong a0, ulong a1, ulong a2) => throw IoLlvmUnsupported("Lean.LLVM.setLinkage");
    /* Lean.LLVM.setTailCall */
    public static Obj lean_llvm_set_tail_call(ulong a0, ulong a1, byte a2) => throw IoLlvmUnsupported("Lean.LLVM.setTailCall");
    /* Lean.LLVM.setVisibility */
    public static Obj lean_llvm_set_visibility(ulong a0, ulong a1, ulong a2) => throw IoLlvmUnsupported("Lean.LLVM.setVisibility");
    /* Lean.LLVM.targetMachineEmitToFile */
    public static Obj lean_llvm_target_machine_emit_to_file(ulong a0, ulong a1, ulong a2, Obj a3, ulong a4) => throw IoLlvmUnsupported("Lean.LLVM.targetMachineEmitToFile");
    /* Lean.LLVM.verifyModule */
    public static Obj lean_llvm_verify_module(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.verifyModule");
    /* Lean.LLVM.writeBitcodeToFile */
    public static Obj lean_llvm_write_bitcode_to_file(ulong a0, ulong a1, Obj a2) => throw IoLlvmUnsupported("Lean.LLVM.writeBitcodeToFile");
    /* Lean.LLVM.addFunction */
    public static ulong lean_llvm_add_function(ulong a0, ulong a1, Obj a2, ulong a3) => throw IoLlvmUnsupported("Lean.LLVM.addFunction");
    /* Lean.LLVM.addGlobal */
    public static ulong lean_llvm_add_global(ulong a0, ulong a1, Obj a2, ulong a3) => throw IoLlvmUnsupported("Lean.LLVM.addGlobal");
    /* Lean.LLVM.appendBasicBlockInContext */
    public static ulong lean_llvm_append_basic_block_in_context(ulong a0, ulong a1, Obj a2) => throw IoLlvmUnsupported("Lean.LLVM.appendBasicBlockInContext");
    /* Lean.LLVM.arrayType */
    public static ulong lean_llvm_array_type(ulong a0, ulong a1, ulong a2) => throw IoLlvmUnsupported("Lean.LLVM.arrayType");
    /* Lean.LLVM.buildAdd */
    public static ulong lean_llvm_build_add(ulong a0, ulong a1, ulong a2, ulong a3, Obj a4) => throw IoLlvmUnsupported("Lean.LLVM.buildAdd");
    /* Lean.LLVM.buildAlloca */
    public static ulong lean_llvm_build_alloca(ulong a0, ulong a1, ulong a2, Obj a3) => throw IoLlvmUnsupported("Lean.LLVM.buildAlloca");
    /* Lean.LLVM.buildBr */
    public static ulong lean_llvm_build_br(ulong a0, ulong a1, ulong a2) => throw IoLlvmUnsupported("Lean.LLVM.buildBr");
    /* Lean.LLVM.buildCall2 */
    public static ulong lean_llvm_build_call2(ulong a0, ulong a1, ulong a2, ulong a3, Obj a4, Obj a5) => throw IoLlvmUnsupported("Lean.LLVM.buildCall2");
    /* Lean.LLVM.buildCondBr */
    public static ulong lean_llvm_build_cond_br(ulong a0, ulong a1, ulong a2, ulong a3, ulong a4) => throw IoLlvmUnsupported("Lean.LLVM.buildCondBr");
    /* Lean.LLVM.buildGEP2 */
    public static ulong lean_llvm_build_gep2(ulong a0, ulong a1, ulong a2, ulong a3, Obj a4, Obj a5) => throw IoLlvmUnsupported("Lean.LLVM.buildGEP2");
    /* Lean.LLVM.buildGlobalString */
    public static ulong lean_llvm_build_global_string(ulong a0, ulong a1, Obj a2, Obj a3) => throw IoLlvmUnsupported("Lean.LLVM.buildGlobalString");
    /* Lean.LLVM.buildICmp */
    public static ulong lean_llvm_build_icmp(ulong a0, ulong a1, ulong a2, ulong a3, ulong a4, Obj a5) => throw IoLlvmUnsupported("Lean.LLVM.buildICmp");
    /* Lean.LLVM.buildInBoundsGEP2 */
    public static ulong lean_llvm_build_inbounds_gep2(ulong a0, ulong a1, ulong a2, ulong a3, Obj a4, Obj a5) => throw IoLlvmUnsupported("Lean.LLVM.buildInBoundsGEP2");
    /* Lean.LLVM.buildLoad2 */
    public static ulong lean_llvm_build_load2(ulong a0, ulong a1, ulong a2, ulong a3, Obj a4) => throw IoLlvmUnsupported("Lean.LLVM.buildLoad2");
    /* Lean.LLVM.buildMul */
    public static ulong lean_llvm_build_mul(ulong a0, ulong a1, ulong a2, ulong a3, Obj a4) => throw IoLlvmUnsupported("Lean.LLVM.buildMul");
    /* Lean.LLVM.buildNot */
    public static ulong lean_llvm_build_not(ulong a0, ulong a1, ulong a2, Obj a3) => throw IoLlvmUnsupported("Lean.LLVM.buildNot");
    /* Lean.LLVM.buildPtrToInt */
    public static ulong lean_llvm_build_ptr_to_int(ulong a0, ulong a1, ulong a2, ulong a3, Obj a4) => throw IoLlvmUnsupported("Lean.LLVM.buildPtrToInt");
    /* Lean.LLVM.buildRet */
    public static ulong lean_llvm_build_ret(ulong a0, ulong a1, ulong a2) => throw IoLlvmUnsupported("Lean.LLVM.buildRet");
    /* Lean.LLVM.buildSext */
    public static ulong lean_llvm_build_sext(ulong a0, ulong a1, ulong a2, ulong a3, Obj a4) => throw IoLlvmUnsupported("Lean.LLVM.buildSext");
    /* Lean.LLVM.buildSextOrTrunc */
    public static ulong lean_llvm_build_sext_or_trunc(ulong a0, ulong a1, ulong a2, ulong a3, Obj a4) => throw IoLlvmUnsupported("Lean.LLVM.buildSextOrTrunc");
    /* Lean.LLVM.buildSub */
    public static ulong lean_llvm_build_sub(ulong a0, ulong a1, ulong a2, ulong a3, Obj a4) => throw IoLlvmUnsupported("Lean.LLVM.buildSub");
    /* Lean.LLVM.buildSwitch */
    public static ulong lean_llvm_build_switch(ulong a0, ulong a1, ulong a2, ulong a3, ulong a4) => throw IoLlvmUnsupported("Lean.LLVM.buildSwitch");
    /* Lean.LLVM.buildUnreachable */
    public static ulong lean_llvm_build_unreachable(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.buildUnreachable");
    /* Lean.LLVM.buildZext */
    public static ulong lean_llvm_build_zext(ulong a0, ulong a1, ulong a2, ulong a3, Obj a4) => throw IoLlvmUnsupported("Lean.LLVM.buildZext");
    /* Lean.LLVM.constArray */
    public static ulong lean_llvm_const_array(ulong a0, ulong a1, Obj a2) => throw IoLlvmUnsupported("Lean.LLVM.constArray");
    /* Lean.LLVM.constInt */
    public static ulong lean_llvm_const_int(ulong a0, ulong a1, ulong a2, byte a3) => throw IoLlvmUnsupported("Lean.LLVM.constInt");
    /* Lean.LLVM.constPointerNull */
    public static ulong lean_llvm_const_pointer_null(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.constPointerNull");
    /* Lean.LLVM.constString */
    public static ulong lean_llvm_const_string(ulong a0, Obj a1) => throw IoLlvmUnsupported("Lean.LLVM.constString");
    /* Lean.LLVM.countBasicBlocks */
    public static ulong lean_llvm_count_basic_blocks(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.countBasicBlocks");
    /* Lean.LLVM.createBuilderInContext */
    public static ulong lean_llvm_create_builder_in_context(ulong a0) => throw IoLlvmUnsupported("Lean.LLVM.createBuilderInContext");
    /* Lean.LLVM.createContext */
    public static ulong lean_llvm_create_context() => throw IoLlvmUnsupported("Lean.LLVM.createContext");
    /* Lean.LLVM.createMemoryBufferWithContentsOfFile */
    public static ulong lean_llvm_create_memory_buffer_with_contents_of_file(ulong a0, Obj a1) => throw IoLlvmUnsupported("Lean.LLVM.createMemoryBufferWithContentsOfFile");
    /* Lean.LLVM.createModule */
    public static ulong lean_llvm_create_module(ulong a0, Obj a1) => throw IoLlvmUnsupported("Lean.LLVM.createModule");
    /* Lean.LLVM.createStringAttribute */
    public static ulong lean_llvm_create_string_attribute(ulong a0, Obj a1, Obj a2) => throw IoLlvmUnsupported("Lean.LLVM.createStringAttribute");
    /* Lean.LLVM.createTargetMachine */
    public static ulong lean_llvm_create_target_machine(ulong a0, ulong a1, Obj a2, Obj a3, Obj a4) => throw IoLlvmUnsupported("Lean.LLVM.createTargetMachine");
    /* Lean.LLVM.doubleTypeInContext */
    public static ulong lean_llvm_double_type_in_context(ulong a0) => throw IoLlvmUnsupported("Lean.LLVM.doubleTypeInContext");
    /* Lean.LLVM.floatTypeInContext */
    public static ulong lean_llvm_float_type_in_context(ulong a0) => throw IoLlvmUnsupported("Lean.LLVM.floatTypeInContext");
    /* Lean.LLVM.functionType */
    public static ulong lean_llvm_function_type(ulong a0, ulong a1, Obj a2, byte a3) => throw IoLlvmUnsupported("Lean.LLVM.functionType");
    /* Lean.LLVM.getBasicBlockParent */
    public static ulong lean_llvm_get_basic_block_parent(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.getBasicBlockParent");
    /* Lean.LLVM.getEntryBasicBlock */
    public static ulong lean_llvm_get_entry_basic_block(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.getEntryBasicBlock");
    /* Lean.LLVM.getFirstFunction */
    public static ulong lean_llvm_get_first_function(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.getFirstFunction");
    /* Lean.LLVM.getFirstGlobal */
    public static ulong lean_llvm_get_first_global(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.getFirstGlobal");
    /* Lean.LLVM.getInsertBlock */
    public static ulong lean_llvm_get_insert_block(Obj a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.getInsertBlock");
    /* Lean.LLVM.getNextFunction */
    public static ulong lean_llvm_get_next_function(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.getNextFunction");
    /* Lean.LLVM.getNextGlobal */
    public static ulong lean_llvm_get_next_global(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.getNextGlobal");
    /* Lean.LLVM.getTargetFromTriple */
    public static ulong lean_llvm_get_target_from_triple(ulong a0, Obj a1) => throw IoLlvmUnsupported("Lean.LLVM.getTargetFromTriple");
    /* Lean.LLVM.getUndef */
    public static ulong lean_llvm_get_undef(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.getUndef");
    /* Lean.LLVM.intTypeInContext */
    public static ulong lean_llvm_int_type_in_context(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.intTypeInContext");
    /* Lean.LLVM.opaquePointerTypeInContext */
    public static ulong lean_llvm_opaque_pointer_type_in_context(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.opaquePointerTypeInContext");
    /* Lean.LLVM.parseBitcode */
    public static ulong lean_llvm_parse_bitcode(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.parseBitcode");
    /* Lean.LLVM.pointerType */
    public static ulong lean_llvm_pointer_type(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.pointerType");
    /* Lean.LLVM.typeOf */
    public static ulong lean_llvm_type_of(ulong a0, ulong a1) => throw IoLlvmUnsupported("Lean.LLVM.typeOf");
    /* Lean.LLVM.voidType */
    public static ulong lean_llvm_void_type_in_context(ulong a0) => throw IoLlvmUnsupported("Lean.LLVM.voidType");
}
