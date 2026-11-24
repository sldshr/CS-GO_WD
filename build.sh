#!/usr/bin/env bash
# Builds the release executables into ./release. Needs the .NET 10 SDK and internet access for NuGet.
# Usage: ./build.sh [--skip-tests]
set -euo pipefail
cd "$(dirname "$0")"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
out=release

if [ "${1:-}" != "--skip-tests" ]; then
    dotnet test tests/Core.Tests/Core.Tests.csproj -c Release
fi

rm -rf "$out"
mkdir -p "$out"

publish() {
    local rid=$1 built=$2 final=$3
    dotnet publish src/App/App.csproj -c Release -r "$rid" --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -o "$out/.$rid"
    mv "$out/.$rid/$built" "$out/$final"
    rm -rf "$out/.$rid"
}

publish win-x64 csgo-workshop.exe csgo-workshop-win-x64.exe
publish linux-x64 csgo-workshop csgo-workshop-linux-x64
chmod +x "$out/csgo-workshop-linux-x64"

ls -lh "$out"
