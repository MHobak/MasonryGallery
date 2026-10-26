#!/usr/bin/env bash
# Self-contained release build (design §11): no .NET install needed on the target machine.
# ReadyToRun instead of trimming, which would break Blazor's reflection. Includes ffmpeg when
# scripts/fetch-ffmpeg.sh has put a copy in tools/ffmpeg/<rid>/.
#
#   scripts/publish.sh [linux-x64|win-x64]     -> artifacts/publish/<rid>/
set -euo pipefail

RID="${1:-linux-x64}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/artifacts/publish/$RID"

rm -rf "${OUT:?}"
dotnet publish "$ROOT/src/MasonryGallery.Host/MasonryGallery.Host.csproj" \
  -c Release -r "$RID" --self-contained -p:PublishReadyToRun=true -o "$OUT"

if [ -d "$ROOT/tools/ffmpeg/$RID" ]; then
  mkdir -p "$OUT/tools/ffmpeg"
  cp "$ROOT/tools/ffmpeg/$RID"/* "$OUT/tools/ffmpeg/"
  echo "Bundled ffmpeg from tools/ffmpeg/$RID"
else
  echo "No bundled ffmpeg (run scripts/fetch-ffmpeg.sh $RID to include one); videos will use ffmpeg from PATH."
fi
cp "$ROOT/README.md" "$ROOT/THIRD-PARTY-NOTICES.md" "$OUT/"
echo "Published to $OUT"
