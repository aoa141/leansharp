#!/bin/bash
# Cross-check of the managed leantar against the native one, on real modules.
#   usage: checks/leantar/cross_ltar.sh <native leantar> <lib dir with .trace/.olean/... files> [count]
# For each sampled module:
#   native pack  -> managed unpack   must reproduce the files
#   managed pack -> native unpack    must reproduce the files
#   managed pack -> managed unpack   must reproduce the files
# (The trace file is compared as JSON-equivalent text: unpacking rewrites it.)
set -u
NATIVE=$1; LIB=$(cd "$2" && pwd); COUNT=${3:-25}
HERE=$(cd "$(dirname "$0")" && pwd)
export PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1
MINE="dotnet $HERE/bin/Release/net10.0/Check.dll leantar"
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
fail=0; n=0; nsz=0; msz=0
mods=$(cd "$LIB" && find . -name '*.trace' | sed 's|^\./||; s|\.trace$||' | sort | awk 'NR % 97 == 1' | head -n "$COUNT")
for m in $mods; do
  files=""
  for e in olean olean.server olean.private ilean ir ir.sig; do [ -f "$LIB/$m.$e" ] && files="$files $m.$e"; done
  [ -f "$LIB/$m.olean" ] || continue
  n=$((n+1))
  $NATIVE -C "$LIB" "$TMP/native.ltar" "$m.trace" $files || { echo "native pack failed: $m"; fail=$((fail+1)); continue; }
  $MINE -C "$LIB" "$TMP/mine.ltar" "$m.trace" $files || { echo "managed pack failed: $m"; fail=$((fail+1)); continue; }
  nsz=$((nsz + $(stat -c%s "$TMP/native.ltar"))); msz=$((msz + $(stat -c%s "$TMP/mine.ltar")))
  for combo in "native.ltar:$MINE:a" "mine.ltar:$NATIVE:b" "mine.ltar:$MINE:c"; do
    ltar=${combo%%:*}; rest=${combo#*:}; tool=${rest%:*}; tag=${rest##*:}
    out="$TMP/out-$tag"; rm -rf "$out"; mkdir -p "$out"
    $tool -C "$out" -x "$TMP/$ltar" || { echo "unpack failed ($tag): $m"; fail=$((fail+1)); continue; }
    for f in $files; do
      cmp -s "$LIB/$f" "$out/$f" || { echo "DIFF ($tag): $f"; fail=$((fail+1)); }
    done
    [ -f "$out/$m.trace" ] || { echo "no trace ($tag): $m"; fail=$((fail+1)); }
  done
  # both tools must write the same trace file from the same archive
  cmp -s "$TMP/out-b/$m.trace" "$TMP/out-c/$m.trace" || { echo "trace differs between native and managed unpack: $m"; fail=$((fail+1)); }
  # comments
  $MINE -C "$LIB" "$TMP/c.ltar" "$m.trace" -c "hello world" $m.ilean -c second || fail=$((fail+1))
  [ "$($NATIVE -k "$TMP/c.ltar")" = "$($MINE -k "$TMP/c.ltar")" ] || { echo "comments differ: $m"; fail=$((fail+1)); }
done
echo "$n modules, $fail failures; archive bytes: native $nsz, managed $msz ($(( msz * 100 / (nsz > 0 ? nsz : 1) ))%)"
[ $fail = 0 ]
