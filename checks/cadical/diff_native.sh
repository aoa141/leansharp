#!/bin/bash
# Differential test against the native cadical binary: with '--no-arena' the managed port
# follows exactly the same search and must produce byte-identical output and LRAT proofs.
# usage: diff_native.sh "<options>" files...
opts="$1"; shift
D=$(cd "$(dirname "$0")" && pwd)
N=${CADICAL_NATIVE:-$HOME/Repos/lean4/build/release/stage1/bin/cadical}
M="dotnet $D/bin/Release/net10.0/Check.dll cli"
T=$(mktemp -d)
fail=0
for f in "$@"; do b=$(basename $f)
  $N -q $opts $f $T/n.lrat --lrat --binary=false > $T/n.out; nc=$?
  $M -q $opts $f $T/m.lrat --lrat --binary=false > $T/m.out; mc=$?
  if [ $nc != $mc ] || ! cmp -s $T/n.out $T/m.out || ! cmp -s $T/n.lrat $T/m.lrat; then echo "DIFF $b (exit $nc/$mc) $(cmp $T/n.lrat $T/m.lrat | head -1)"; fail=1; fi
done
rm -rf $T; [ $fail = 0 ] && echo "all identical ($opts)"
exit $fail
