import Lean
open Lean

universe u
inductive MyNat where | zero | succ (n : MyNat)
inductive MyList (α : Type u) where | nil | cons (head : α) (tail : MyList α)
inductive Tree where | node (cs : MyList Tree)
inductive MyEq {α : Sort u} : (a : α) → (b : α) → Prop where | refl (a : α) : MyEq a a
structure MyProd (α β : Type) where
  fst : α
  snd : β
inductive Vec (α : Type u) : (n : MyNat) → Type u where
  | nil : Vec α MyNat.zero
  | cons {n : MyNat} (h : α) (t : Vec α n) : Vec α (MyNat.succ n)

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

#eval showC ``MyNat
#eval showC ``MyNat.zero
#eval showC ``MyNat.succ
#eval showC ``MyNat.rec
#eval showC ``MyNat.casesOn
#eval showC ``MyList
#eval showC ``MyList.cons
#eval showC ``MyList.rec
#eval showC ``Tree
#eval showC ``Tree.node
#eval showC ``Tree.rec
#eval showC ``Tree.rec_1
#eval showC ``Tree.casesOn
#eval showC ``MyEq
#eval showC ``MyEq.refl
#eval showC ``MyEq.rec
#eval showC ``MyProd
#eval showC ``MyProd.mk
#eval showC ``MyProd.rec
#eval showC ``Vec
#eval showC ``Vec.nil
#eval showC ``Vec.cons
#eval showC ``Vec.rec
#eval showC ``Vec.casesOn
#eval showC ``Eq
#eval showC ``Eq.refl
#eval showC ``Quot
#eval showC ``Quot.mk
#eval showC ``Quot.lift
#eval showC ``Quot.ind
