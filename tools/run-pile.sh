#!/bin/bash
# Runs one pile of Lean's test suite against LeanSharp, logging to artifacts/logs/<pile>.log,
# results to artifacts/<pile>.tsv, and memory use (every 5 s) to artifacts/logs/<pile>-mem.log.
# usage: tools/run-pile.sh <pile> <workers> [extra TestRunner arguments]
# environment: LEAN4 (default ~/Repos/lean4), LEANSHARP_SYSROOT (default artifacts/selfhost),
#   LEANSHARP_RUNNER (the TestRunner dll; e.g. artifacts/publish/testrunner/LeanSharp.TestRunner.dll
#   after tools/publish.sh, which starts much faster)
cd "$(dirname "$0")/.."
export PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1
export LEANSHARP_SYSROOT=${LEANSHARP_SYSROOT:-$PWD/artifacts/selfhost}
pile=$1; jobs=$2; shift 2
mkdir -p artifacts/logs
(while true; do echo "$(date +%T) $(free -m | awk 'NR==2{printf "used=%s avail=%s ",$3,$7}') $(ps -eo rss,comm --sort=-rss | sed -n 2,5p | tr '\n' ';')" >> artifacts/logs/$pile-mem.log; sleep 5; done) &
W=$!
dotnet "${LEANSHARP_RUNNER:-tests/LeanSharp.TestRunner/bin/Release/net10.0/LeanSharp.TestRunner.dll}" run --tests "${LEAN4:-$HOME/Repos/lean4}/tests" --pile $pile -j $jobs --show-diffs --results artifacts/$pile.tsv "$@" > artifacts/logs/$pile.log 2>&1
echo "rc=$?" >> artifacts/logs/$pile.log
kill $W
