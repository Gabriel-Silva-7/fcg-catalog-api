#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
DOTNET=${DOTNET:-dotnet}
"$DOTNET" restore FCG.Catalog.sln --locked-mode
"$DOTNET" build FCG.Catalog.sln -c Release --no-restore
"$DOTNET" test FCG.Catalog.sln -c Release --no-restore
