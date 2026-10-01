#!/bin/bash
# Prints a summary of the managed thread stacks of a running LeanSharp process (needs `dotnet tool install -g dotnet-stack`).
# usage: tools/stacks.sh <pid> [frames per thread]
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet} DOTNET_ROLL_FORWARD=Major PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH
dotnet-stack report -p "$1" | python3 -c '
import re,sys,collections
n=int(sys.argv[1]) if len(sys.argv)>1 else 20
ths=re.split(r"\nThread \(", sys.stdin.read())
c=collections.Counter()
for th in ths[1:]:
    lines=[re.sub(r"\(.*","",l.strip()) for l in th.split("\n")[1:] if l.strip()]
    c["\n   ".join(l[:150] for l in lines[:n])]+=1
for k,v in c.most_common():
    print(v,"threads:\n   "+k+"\n")
' "${2:-20}"
