import Lean
open Lean

def u : Level := .param `u
def v : Level := .param `v
#eval (Name.anonymous).hash
#eval (`Nat).hash
#eval (`Lean.Expr).hash
#eval (Name.mkNum `x 3).hash
#eval (Name.mkNum (Name.mkStr .anonymous "") 18446744073709551616).hash
#eval ((Level.zero).data : UInt64)
#eval ((Level.succ .zero).data : UInt64)
#eval (u.data : UInt64)
#eval ((Level.max u (.succ .zero)).data : UInt64)
#eval ((Level.imax u v).data : UInt64)
#eval ((Level.mvar ⟨`m⟩).data : UInt64)
#eval ((mkConst `Nat).data : UInt64)
#eval ((mkConst `List [u]).data : UInt64)
#eval ((mkBVar 0).data : UInt64)
#eval ((mkBVar 5).data : UInt64)
#eval ((mkSort (.succ u)).data : UInt64)
#eval ((mkFVar ⟨`x⟩).data : UInt64)
#eval ((mkMVar ⟨`m⟩).data : UInt64)
#eval ((mkRawNatLit 5).data : UInt64)
#eval ((mkStrLit "abc").data : UInt64)
#eval ((mkApp (mkConst `f) (mkBVar 1)).data : UInt64)
#eval ((mkLambda `x .implicit (mkConst `Nat) (mkBVar 0)).data : UInt64)
#eval ((mkForall `x .default (mkConst `Nat) (mkBVar 1)).data : UInt64)
#eval ((mkLet `x (mkConst `Nat) (mkRawNatLit 1) (mkBVar 0) true).data : UInt64)
#eval ((mkProj `Prod 1 (mkBVar 0)).data : UInt64)
#eval ((mkMData {} (mkBVar 2)).data : UInt64)
#eval ((mkMData (KVMap.empty.insert `a (DataValue.ofBool true)) (mkConst `x)).data : UInt64)
