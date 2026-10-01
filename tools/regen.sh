#!/bin/sh
# Regenerate the C# translation of the Lean libraries from the IR of a Lean build.
# usage: tools/regen.sh <outdir> [roots...]
#
# The emitter (tools/EmitCSharp/EmitCSharp.lean) is a Lean program. It reads, from a build of the
# Lean libraries in $STAGE, the `.ir` files (lib/lean) and the C files (lib/temp, for the
# initialization order). Two ways to run it:
#
#   with a native Lean build of the pinned commit (the default):
#       LEAN4=~/Repos/lean4 tools/regen.sh gen
#   with LeanSharp only, from a sysroot built by `LeanSharp.Cli build-stdlib`:
#       STAGE=$PWD/artifacts/selfhost tools/regen.sh gen
#     (the sysroot's bin/lean launcher runs the emitter; needs ~14 GB and 2 minutes)
#
# Both give the same output for the same $STAGE. The code generated from a LeanSharp-built
# sysroot differs from the one generated from a native stage 1 build in a few hundred modules,
# because the native stage 1 files are compiled by the older stage 0 compiler; it corresponds to
# a native stage 2.
set -e
LEAN4=${LEAN4:-$HOME/Repos/lean4}
STAGE=${STAGE:-$LEAN4/build/release/stage1}
LEAN=${LEAN:-$STAGE/bin/lean}
OUT=$1; shift
ROOTS=${*:-Init Std Lean Lake LakeMain}
HERE=$(cd "$(dirname "$0")" && pwd)
rm -rf "$OUT"; mkdir -p "$OUT"
"$LEAN" --run "$HERE/EmitCSharp/EmitCSharp.lean" "$STAGE" "$STAGE/lib/temp" "$OUT" $ROOTS 2>&1 | grep -v "missing C file" || true
