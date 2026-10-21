#!/usr/bin/env bash
# Builds the plugin zip into artifacts/ and prints its MD5 for manifest.json.
# Uses a local dotnet 9 SDK if there is one, otherwise the SDK's Docker image.
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${1:-$(sed -n 's:.*<AssemblyVersion>\(.*\)</AssemblyVersion>.*:\1:p' Jellyfin.Plugin.Mediarium/Jellyfin.Plugin.Mediarium.csproj)}"
rm -rf artifacts/publish
if command -v dotnet >/dev/null 2>&1; then
  dotnet publish Jellyfin.Plugin.Mediarium -c Release -o artifacts/publish -p:Version="$VERSION" -nologo
else
  docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
    dotnet publish Jellyfin.Plugin.Mediarium -c Release -o artifacts/publish -p:Version="$VERSION" -nologo
fi
ZIP="artifacts/mediarium-requests_${VERSION}.zip"
rm -f "$ZIP"
(cd artifacts/publish && zip -q -X "../$(basename "$ZIP")" Jellyfin.Plugin.Mediarium.dll)
if command -v md5sum >/dev/null 2>&1; then SUM=$(md5sum "$ZIP" | cut -d' ' -f1); else SUM=$(md5 -q "$ZIP"); fi
echo "$ZIP"
echo "checksum: $SUM"
