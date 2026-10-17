#!/bin/sh
# Builds the browser version into out/web/wwwroot with the runtime folder renamed to framework/
# (static hosts such as Claude Artifacts reserve names starting with "_").
# With the wasm-tools workload installed the build is AOT-compiled (S5-3: about 1.8x faster simulation,
# 4x faster saving, 2x the download); without it, or with NO_AOT=1, it uses the interpreter.
set -e
cd "$(dirname "$0")"
AOT=false
if [ -z "$NO_AOT" ] && dotnet workload list 2>/dev/null | grep -q wasm-tools; then AOT=true; fi
echo "publish-web: AOT=$AOT"
rm -rf ../out/web
dotnet publish WorldSim.Web -c Release -p:RunAOTCompilation=$AOT -o ../out/web
cd ../out/web/wwwroot
find . \( -name "*.br" -o -name "*.gz" \) -delete
mv _framework framework
