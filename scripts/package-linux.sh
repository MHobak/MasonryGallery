#!/usr/bin/env bash
# Linux packages from artifacts/publish/linux-x64 (run scripts/publish.sh first): an AppImage and a .deb
# (design §11). Both use the system's WebKitGTK 4.1 rather than bundling it: it must match the host's
# graphics stack. Video codecs come from GStreamer (gst-plugins-good/bad, gst-libav).
#
#   VERSION=0.1.0 scripts/package-linux.sh            -> artifacts/packages/
#   SKIP_APPIMAGE=1 ...                               only the .deb (no appimagetool download)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$ROOT/artifacts/publish/linux-x64"
OUT="$ROOT/artifacts/packages"
VERSION="${VERSION:-0.1.0}"
NAME=masonrygallery
MAINTAINER="${MAINTAINER:-$(git -C "$ROOT" config user.name) <$(git -C "$ROOT" config user.email)>}"

[ -x "$APP/MasonryGallery.Host" ] || { echo "Run scripts/publish.sh linux-x64 first." >&2; exit 1; }
mkdir -p "$OUT"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# ── AppImage ──
if [ -z "${SKIP_APPIMAGE:-}" ]; then
APPDIR="$WORK/AppDir"
mkdir -p "$APPDIR/usr/lib/$NAME"
cp -a "$APP/." "$APPDIR/usr/lib/$NAME/"
cp "$ROOT/packaging/linux/$NAME.desktop" "$APPDIR/"
cp "$ROOT/packaging/$NAME.svg" "$APPDIR/$NAME.svg"
ln -s "$NAME.svg" "$APPDIR/.DirIcon"
cat > "$APPDIR/AppRun" <<'RUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/masonrygallery/MasonryGallery.Host" "$@"
RUN
chmod +x "$APPDIR/AppRun"

TOOL="$ROOT/tools/cache/appimagetool-x86_64.AppImage"
if [ ! -x "$TOOL" ]; then
  mkdir -p "$(dirname "$TOOL")"
  curl -fL --retry 3 -o "$TOOL" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$TOOL"
fi
# Extract-and-run: works without FUSE (containers, CI).
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" --no-appstream "$APPDIR" "$OUT/MasonryGallery-$VERSION-x86_64.AppImage"
fi

# ── .deb ──
DEB="$WORK/deb"
mkdir -p "$DEB/DEBIAN" "$DEB/opt/$NAME" "$DEB/usr/bin" "$DEB/usr/share/applications" "$DEB/usr/share/icons/hicolor/scalable/apps"
cp -a "$APP/." "$DEB/opt/$NAME/"
ln -s "/opt/$NAME/MasonryGallery.Host" "$DEB/usr/bin/$NAME"
cp "$ROOT/packaging/linux/$NAME.desktop" "$DEB/usr/share/applications/"
cp "$ROOT/packaging/$NAME.svg" "$DEB/usr/share/icons/hicolor/scalable/apps/"
cat > "$DEB/DEBIAN/control" <<CONTROL
Package: $NAME
Version: $VERSION
Section: graphics
Priority: optional
Architecture: amd64
Maintainer: $MAINTAINER
Depends: libwebkit2gtk-4.1-0, libgtk-3-0
Recommends: ffmpeg, gstreamer1.0-plugins-good, gstreamer1.0-plugins-bad, gstreamer1.0-libav
Installed-Size: $(du -sk "$DEB/opt" | cut -f1)
Description: Photo and video gallery with a masonry layout
 Browses folders of photos and videos in a fast virtualized grid, keeps them
 current as files change, and reads tags from XMP sidecars.
CONTROL

DEBFILE="$OUT/${NAME}_${VERSION}_amd64.deb"
if command -v dpkg-deb >/dev/null; then
  dpkg-deb --root-owner-group --build "$DEB" "$DEBFILE"
else
  # Same format by hand (debian-binary, control and data tarballs in an ar archive) where dpkg isn't installed.
  ( cd "$DEB/DEBIAN" && tar --owner=0 --group=0 -cJf "$WORK/control.tar.xz" . )
  ( cd "$DEB" && tar --owner=0 --group=0 --exclude=./DEBIAN -cJf "$WORK/data.tar.xz" . )
  echo "2.0" > "$WORK/debian-binary"
  rm -f "$DEBFILE"
  ( cd "$WORK" && ar rc "$DEBFILE" debian-binary control.tar.xz data.tar.xz )
fi
ls -lh "$OUT"
