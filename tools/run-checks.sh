#!/bin/bash
# Builds and runs the per-area check programs of the hand-ported runtime (checks/*).
# They compile the runtime sources directly, so they do not need the generated assembly.
# usage: tools/run-checks.sh [area...]      (default: all areas)
cd "$(dirname "$0")/.."
export PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1
areas=${*:-numbers strings io tasks kernel compact interp uv cadical zstd leantar}
failed=
for c in $areas; do
  if ! dotnet build checks/$c -c Release -v q 2>&1 | grep -q "Build succeeded"; then echo "== $c: BUILD FAILED"; failed="$failed $c"; continue; fi
  args=
  # the cadical check program has several modes: run the FFI tests and the DIMACS test set
  [ "$c" = cadical ] && args="ffi"
  # zstd: cross-checked against Python's `compression.zstd` (needs python >= 3.14); skip the big inputs
  [ "$c" = zstd ] && args="--quick"
  # leantar: round trip of the lgz encoding on the library files of the sysroot
  [ "$c" = leantar ] && args="roundtrip ${LEANSHARP_SYSROOT:-$PWD/artifacts/selfhost}/lib/lean/Init"
  (cd checks/$c && dotnet bin/Release/net10.0/Check.dll $args > ../../artifacts/logs/check-$c.log 2>&1)
  rc=$?
  if [ "$c" = cadical ] && [ $rc = 0 ]; then (cd checks/$c && dotnet bin/Release/net10.0/Check.dll test >> ../../artifacts/logs/check-$c.log 2>&1); rc=$?; fi
  echo "== $c: $([ $rc = 0 ] && echo ok || echo "FAILED ($rc)") -- $(tail -1 artifacts/logs/check-$c.log | cut -c1-100)"
  [ $rc = 0 ] || failed="$failed $c"
done
[ -z "$failed" ] || { echo "failed:$failed"; exit 1; }
