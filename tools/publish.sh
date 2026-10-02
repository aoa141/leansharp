#!/bin/bash
# Publishes LeanSharp with ReadyToRun precompilation: the assemblies additionally contain machine
# code produced ahead of time by the .NET SDK, so that almost nothing has to be JIT-compiled at
# startup (`lean --version`: 1.5 s -> 0.4 s; a trivial file: 3.9 s -> 1.9 s).
#
#   usage: tools/publish.sh [rid] [outdir]      (default rid: this machine's; outdir: artifacts/publish)
#
# Output: <outdir>/cli (LeanSharp.Cli) and <outdir>/testrunner (LeanSharp.TestRunner). Run them
# with `dotnet <outdir>/cli/LeanSharp.Cli.dll ...`. The output is specific to the runtime
# identifier (e.g. linux-x64, win-x64, osx-arm64) and needs the same .NET major version at run
# time; if the precompiled code cannot be used, the runtime silently falls back to the JIT.
# A plain `dotnet build` stays pure IL.
set -e
cd "$(dirname "$0")/.."
export PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1
RID=${1:-$(dotnet --info | awk '/^ *RID:/{print $2; exit}')}
OUT=${2:-artifacts/publish}
for p in LeanSharp.Cli:src/LeanSharp.Cli:cli LeanSharp.TestRunner:tests/LeanSharp.TestRunner:testrunner; do
  name=${p%%:*}; rest=${p#*:}; proj=${rest%%:*}; dir=${rest##*:}
  rm -rf "$OUT/$dir"
  dotnet publish "$proj" -c Release -r "$RID" --self-contained false -p:PublishReadyToRun=true -o "$OUT/$dir" \
    | grep -E ' error |warning NETSDK| -> .*/'"$dir"'/$' || true
  [ -f "$OUT/$dir/$name.dll" ] || { echo "publishing $name failed"; exit 1; }
done
dotnet build-server shutdown >/dev/null 2>&1 || true
echo "published for $RID to $OUT"
