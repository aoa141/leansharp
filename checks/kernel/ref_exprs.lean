import Lean
open Lean

def a := mkConst `a
def b := mkConst `b
def c := mkConst `c
def f := mkConst `f
def g := mkConst `g
def x := mkFVar ⟨`x⟩
def y := mkFVar ⟨`y⟩
def e1 := mkApp (mkApp f (mkBVar 0)) (mkBVar 1)
def show' (e : Expr) : IO Unit := IO.println s!"{e.dbgToString} | {e.hash}"
#eval show' (e1.instantiate1 a)
#eval show' (e1.instantiate #[a, b])
#eval show' (e1.instantiateRev #[a, b])
#eval show' (e1.instantiateRange 1 2 #[a, b, c])
#eval show' (e1.instantiateRevRange 0 2 #[a, b, c])
#eval show' ((mkApp (mkApp f x) y).abstract #[x, y])
#eval show' ((mkApp (mkApp f x) y).abstractRange 1 #[x, y])
#eval show' ((mkLambda `z .default a (mkApp (mkApp (mkApp g (mkBVar 0)) (mkBVar 1)) (mkBVar 2))).liftLooseBVars 1 3)
#eval show' ((mkApp (mkApp g (mkBVar 3)) (mkBVar 1)).lowerLooseBVars 2 1)
#eval IO.println ((mkLambda `z .default a (mkApp (mkApp g (mkBVar 0)) (mkBVar 2))).hasLooseBVar 1)
#eval IO.println ((mkLambda `z .default a (mkApp (mkApp g (mkBVar 0)) (mkBVar 2))).hasLooseBVar 0)
#eval show' ((mkLambda `y .default a (mkApp (mkConst `h) (mkBVar 1))).instantiate1 (mkBVar 3))
#eval show' (mkForall `a .default (mkConst `Nat) (mkForall `b .implicit (mkSort Level.one) (mkApp (mkConst `P) (mkBVar 1))))
#eval show' (mkForall `x .default (mkConst `Nat) (mkConst `Nat))
#eval show' (mkLambda `x .instImplicit (mkSort (.max (.param `u) (.param `v))) (mkLet `y (mkSort (.succ (.param `u))) (mkRawNatLit 3) (mkApp (mkBVar 0) (mkStrLit "a\"b")) true))
#eval show' (mkProj `Prod 0 (mkMVar ⟨.mkNum `_uniq 7⟩))
#eval show' (mkSort (.imax (.param `u) (.succ (.succ .zero))))
#eval show' (mkConst `List [.param `u, .succ .zero])
#eval show' (mkMData (KVMap.empty.insert `a (DataValue.ofBool true) |>.insert `n (DataValue.ofNat 3)) (mkConst `x))
#eval show' (mkLambda (Name.mkNum `x 1) .strictImplicit (mkConst `Nat) (mkBVar 0))
#eval IO.println (Expr.lt (mkConst `a) (mkConst `b))
#eval IO.println (Expr.lt (mkApp f a) (mkConst `b))
#eval IO.println (Expr.quickLt (mkApp f a) (mkConst `b))
#eval IO.println (Expr.quickLt (mkConst `b) (mkApp f a))
#eval IO.println (Expr.lt (mkSort (.succ .zero)) (mkSort (.param `u)))
#eval IO.println (Expr.eqv (mkLambda `x .default a (mkBVar 0)) (mkLambda `y .implicit a (mkBVar 0)))
#eval IO.println (Expr.equal (mkLambda `x .default a (mkBVar 0)) (mkLambda `y .implicit a (mkBVar 0)))
