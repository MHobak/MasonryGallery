#!/usr/bin/env bash
# Downloads an LGPL build of ffmpeg + ffprobe for bundling (design §11) into tools/ffmpeg/<rid>/,
# where publish.sh picks it up. Builds from https://github.com/BtbN/FFmpeg-Builds, checked against the
# release's checksums. Without a bundled copy the app uses the ffmpeg on PATH (or the folder set in Settings).
#
#   scripts/fetch-ffmpeg.sh [linux-x64|win-x64]        FFMPEG_SERIES=8.1 picks the release branch
set -euo pipefail

RID="${1:-linux-x64}"
SERIES="${FFMPEG_SERIES:-8.1}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest"

case "$RID" in
  linux-x64) ASSET="ffmpeg-n${SERIES}-latest-linux64-lgpl-${SERIES}.tar.xz"; EXE="" ;;
  win-x64)   ASSET="ffmpeg-n${SERIES}-latest-win64-lgpl-${SERIES}.zip";     EXE=".exe" ;;
  *) echo "Unsupported runtime: $RID (use linux-x64 or win-x64)" >&2; exit 2 ;;
esac

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "Downloading $ASSET"
curl -fL --retry 3 -o "$WORK/$ASSET" "$BASE/$ASSET"
curl -fsSL --retry 3 -o "$WORK/checksums.sha256" "$BASE/checksums.sha256"
( cd "$WORK" && grep "  $ASSET\$" checksums.sha256 | sha256sum -c - )

mkdir -p "$WORK/x"
case "$ASSET" in
  *.tar.xz) tar -xJf "$WORK/$ASSET" -C "$WORK/x" ;;
  *.zip)    unzip -q "$WORK/$ASSET" -d "$WORK/x" ;;
esac
BIN="$(dirname "$(find "$WORK/x" -type f -name "ffmpeg$EXE" | head -1)")"

DEST="$ROOT/tools/ffmpeg/$RID"
mkdir -p "$DEST"
install -m 755 "$BIN/ffmpeg$EXE" "$BIN/ffprobe$EXE" "$DEST/"
# The LGPL asks that the licence travel with the binaries.
find "$WORK/x" -maxdepth 2 -iname 'LICENSE*' -exec cp {} "$DEST/LICENSE-ffmpeg.txt" \; -quit
echo "$ASSET" > "$DEST/SOURCE.txt"
echo "ffmpeg ${SERIES} (LGPL) for $RID is in $DEST"
