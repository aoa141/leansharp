import Lean
open Lean

universe u
inductive MyNat where | zero | succ (n : MyNat)
mutual
inductive Even : Type where | zero | succ (o : Odd)
inductive Odd : Type where | succ (e : Even)
end
inductive W where | sup (f : MyNat → W)
inductive Le : (n : MyNat) → (m : MyNat) → Prop where
  | refl (n : MyNat) : Le n n
  | step (n m : MyNat) (h : Le n m) : Le n (MyNat.succ m)
inductive MyAnd (a b : Prop) : Prop where | intro (left : a) (right : b)
inductive MyFalse : Prop

def showC (n : Name) : MetaM Unit := do
  let ci ← getConstInfo n
  IO.println s!"{n} : {ci.type.dbgToString} | {ci.type.hash} | lps={ci.levelParams}"
  match ci with
  | .recInfo r =>
    IO.println s!"  all={r.all} np={r.numParams} ni={r.numIndices} nmot={r.numMotives} nmin={r.numMinors} k={r.k}"
    for rule in r.rules do
      IO.println s!"  rule {rule.ctor} {rule.nfields} : {rule.rhs.dbgToString} | {rule.rhs.hash}"
  | .inductInfo i =>
    IO.println s!"  np={i.numParams} ni={i.numIndices} all={i.all} ctors={i.ctors} nested={i.numNested} rec={i.isRec} refl={i.isReflexive}"
  | .ctorInfo c =>
    IO.println s!"  induct={c.induct} cidx={c.cidx} np={c.numParams} nf={c.numFields}"
  | .defnInfo d =>
    IO.println s!"  value: {d.value.dbgToString} | {d.value.hash} hints={d.hints.getHeightEx} abbrev={d.hints.isAbbrev} safe={d.safety == .safe}"
  | _ => pure ()

#eval showC ``Even
#eval showC ``Odd
#eval showC ``Even.succ
#eval showC ``Odd.succ
#eval showC ``Even.rec
#eval showC ``Odd.rec
#eval showC ``Even.casesOn
#eval showC ``W
#eval showC ``W.sup
#eval showC ``W.rec
#eval showC ``Le
#eval showC ``Le.refl
#eval showC ``Le.step
#eval showC ``Le.rec
#eval showC ``Le.casesOn
#eval showC ``MyAnd
#eval showC ``MyAnd.intro
#eval showC ``MyAnd.rec
#eval showC ``MyFalse
#eval showC ``MyFalse.rec
#eval showC ``MyFalse.casesOn
