#!/bin/sh
# Builds the browser version into out/web/wwwroot with the runtime folder renamed to framework/
# (static hosts such as Claude Artifacts reserve names starting with "_").
set -e
cd "$(dirname "$0")"
rm -rf ../out/web
dotnet publish WorldSim.Web -c Release -o ../out/web
cd ../out/web/wwwroot
find . \( -name "*.br" -o -name "*.gz" \) -delete
mv _framework framework
