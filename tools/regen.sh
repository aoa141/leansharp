#!/bin/sh
# Regenerate the C# translation of the Lean libraries from the IR of a Lean build.
# usage: tools/regen.sh <outdir> [roots...]
set -e
LEAN4=${LEAN4:-$HOME/Repos/lean4}
STAGE=${STAGE:-$LEAN4/build/release/stage1}
OUT=$1; shift
ROOTS=${*:-Init Std Lean Lake LakeMain}
HERE=$(cd "$(dirname "$0")" && pwd)
rm -rf "$OUT"; mkdir -p "$OUT"
"$STAGE/bin/lean" --run "$HERE/EmitCSharp/EmitCSharp.lean" "$STAGE" "$STAGE/lib/temp" "$OUT" $ROOTS 2>&1 | grep -v "missing C file" || true
