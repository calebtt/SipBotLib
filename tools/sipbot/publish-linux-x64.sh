#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="${1:-"$root/dist/sipbot-linux-x64"}"
dotnet publish "$root/tools/sipbot/sipbot.csproj" -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$out"
echo "published $out/sipbot"
