# MasonryGallery

A fast, local desktop gallery for your photos and videos. Point it at a folder and browse everything in a smooth masonry grid. Nothing is uploaded; everything stays on your machine.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Linux: `libwebkit2gtk-4.1` (the window uses WebKit)
- Optional: `ffmpeg`/`ffprobe` for video metadata and thumbnails

## Run it

```bash
dotnet run --project src/MasonryGallery.Host
```

## How to use

1. **Add a library.** Click the folder icon (top right), then type a folder path or press **Browse**. Give it a name and press **Add**.
2. **Wait for the scan.** The folder is scanned (subfolders included). A status pill shows progress; photos appear as thumbnails get made.
   After that the gallery **keeps itself current**: files you add, change, move or delete show up within a few seconds, and every library is fully re-checked every 15 minutes. **Rescan** in the library panel forces a full check.
   If a library's folder is missing or empty (unplugged drive, unmounted share), it is marked **Offline** and its items are kept; when the folder comes back, the library is picked up again within a minute.
3. **Browse.** Scroll the grid. Newest items come first; items on screen get their thumbnails first.
4. **Filter.** Type in the search box to match file and folder names (word prefixes: `bea` finds `beach.jpg`). Use **All / Photos / Videos** and the heart for favorites. **Filters** adds tags, sort order (newest, oldest, recently added, name or **Random**, with **Shuffle** for a new order), library and a date range. Every sort works together with the other filters.
   In the tag box, type part of a tag and pick a suggestion (most used first). Several tags narrow the results (items must have all of them); start with `-` to exclude a tag (`-night`). Click a chip to switch between "has" and "not", **Backspace** in the empty box removes the last one.
5. **Open an item.** Click it (or focus it and press `Enter`). In the viewer:

   | Key | Action |
   |---|---|
   | `←` / `→` | Previous / next |
   | `F` | Favorite |
   | `I` | Details and tags (type a tag, press `Enter`; click any tag to filter by it) |
   | `P` or `F11` | Presentation: full screen, media scaled to fill the screen without cropping |
   | `A` | Autoplay on / off (starts presentation if needed) |
   | `Space` or `K` | Play / pause a video |
   | `J` / `L` or `Shift`+`←` / `→` | Video 5 seconds back / forward |
   | `R` | Loop the video on / off |
   | `+` / `-` / `0` | Zoom in / out / reset (or mouse wheel, double-click; drag to pan) |
   | `O` | Open in the default app |
   | `Esc` | Leave presentation, then close |

   Videos the app can't play get an **Open in default player** button.

   **On a touch screen:**

   | Gesture | Action |
   |---|---|
   | Swipe left / right | Next / previous (the item follows your finger) |
   | Swipe down | Close (in presentation: leave presentation first) |
   | Swipe up | Details and tags |
   | Pinch, double-tap | Zoom an image; drag to pan while zoomed |
   | Double-tap the left / right third of a video | Back / forward 5 seconds; keep tapping to add 5 more |
   | Hold the right / left third of a video | Play forward / rewind at 2× until you let go (rewind steps frame by frame) |
   | Tap (in presentation) | Show the controls |

   The playback bar at the bottom of a video is left to the player. Buttons are bigger on touch screens.

   **Presentation** also starts with `P` on a focused tile. The ▷ button in the top bar starts a **slideshow**: presentation with autoplay on, from the first item, so it follows the current filters and sort (random included). Arrow keys and the on-screen arrows move through the items; the controls and pointer hide when the mouse is still.

   **Autoplay** shows each image for the time set in **Settings → Presentation** (5 seconds by default; a thin bar shows the countdown) and lets each video play to its end, then moves on, starting over after the last item. Pausing autoplay (`A` or its button) keeps the current item on screen; the arrows still work and restart the countdown.

   **Looping:** videos loop in the normal viewer. In presentation they play through so autoplay can move on, unless you turn loop on there with `R` or the loop button.
6. **Manage libraries.** In the folder panel you can hide a library, follow symbolic links, or remove it (this forgets its tags and favorites; your files are never touched). Files that couldn't be read are listed under **Problems**.
7. **More windows.** Press `Ctrl+N` (or the two-squares button) to open another window on the same gallery, with its own library, filters, sort and slideshow. Starting the app again while it runs does the same. Extra windows are marked **Viewer**: they browse, present and edit favorites and tags, and pick up new files within seconds, but scanning, libraries and settings are handled by the first window. If the first window is closed, viewers keep working; nothing is indexed until the app is started again.
8. **Settings** (gear icon):
   - **Worker threads / video processes:** how hard indexing works. *Automatic* suits most machines and uses fewer threads on spinning or network drives.
   - **Thumbnail quality:** applies to thumbnails made from then on.
   - **Preview cache limit:** caps the large lightbox previews, removing the least recently viewed (checked hourly). Grid thumbnails are always kept.
   - **Ignore:** file or folder names to skip, with `*` and `?` wildcards, such as `Screenshots` or `*_edited.jpg`. Saving rescans every library.
   - **FFmpeg folder:** use your own ffmpeg build (after a restart).
   - **Open logs folder:** for bug reports.

Supported files: jpg, jpeg, png, webp, gif, bmp, avif, mp4, mkv, webm, mov, avi, m4v.

**XMP sidecars.** A `photo.jpg.xmp` (digiKam, darktable, gallery-dl, ExifTool) or `photo.xmp` (Lightroom) next to a file is read with it:

- **Tags:** `dc:subject` keywords. Categories come from hierarchical keywords (`lr:hierarchicalSubject`, `digiKam:TagsList`), so `Artist|jane` shows `jane` under **Artist**.
- **Details:** creator, source link, title, description and rating.
- **Date:** the capture date, used when the file has no EXIF date.

Sidecar tags show read-only in the viewer (edit the sidecar to change them). Your own tags are kept separately and survive sidecar changes. Editing, adding or deleting a sidecar is picked up like any other file change; the thumbnail is not remade.

## Where data lives

| | Linux | Windows |
|---|---|---|
| Index (`gallery.db`) and logs | `$XDG_DATA_HOME/MasonryGallery` (default `~/.local/share/MasonryGallery`) | `%APPDATA%\MasonryGallery` |
| Your settings (`settings.json`) | same folder | same folder |
| Thumbnails (cache) | `$XDG_CACHE_HOME/MasonryGallery` (default `~/.cache/MasonryGallery`) | `%LOCALAPPDATA%\MasonryGallery\cache` |

`settings.json` holds only what you changed; the shipped defaults are in `appsettings.json` next to the program. Only the first window indexes; later launches open viewer windows (see step 7).

The thumbnail cache is safe to delete; thumbnails are regenerated on the next start. Deleting the index resets the gallery. Your media is never modified. Data from older builds in `~/.config/MasonryGallery` is moved over on first start.

## Develop

```bash
dotnet build
dotnet test                                   # unit, integration, component and architecture tests
node --test tests/MasonryGallery.UI.Tests/js  # property tests for the masonry layout (layout.js)
dotnet format MasonryGallery.slnx --verify-no-changes
```

Performance (design §2): generate a synthetic 100k-file library once, then check the budgets or run the micro-benchmarks.

```bash
dotnet run -c Release --project tests/MasonryGallery.Benchmarks -- generate /tmp/mg-dataset
dotnet run -c Release --project tests/MasonryGallery.Benchmarks -- budgets /tmp/mg-dataset   # exit 1 if a budget is missed by >20%
dotnet run -c Release --project tests/MasonryGallery.Benchmarks -- micro
```

CI (`.github/workflows/ci.yml`) runs the format check, build, all tests on Ubuntu and Windows, and the budgets on pushes to `main`.

## Build a release

```bash
scripts/fetch-ffmpeg.sh linux-x64   # optional: bundle an LGPL ffmpeg/ffprobe (checksum-verified download)
scripts/publish.sh linux-x64        # self-contained build in artifacts/publish/linux-x64
scripts/package-linux.sh            # AppImage and .deb in artifacts/packages (SKIP_APPIMAGE=1 for the .deb only)
```

Both packages use the system's WebKitGTK 4.1 (`libwebkit2gtk-4.1-0` on Debian/Ubuntu, `webkit2gtk-4.1` on Arch/Fedora) and GStreamer codecs for video. `scripts/publish.sh win-x64` builds for Windows (needs the WebView2 runtime). Bundled components and their licences are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Layout: `Domain` (models) → `Application` (scan pipeline, use cases) → `Infrastructure` (SQLite, Skia thumbnails, file enumeration) → `UI` (Blazor + JS masonry) → `Host` (Photino window + a Kestrel media server on a random loopback port, guarded by a per-launch token). Full design in [design-v2.md](design-v2.md).

## Known limits

Search matches word prefixes, not arbitrary substrings. Grid thumbnail size is fixed at 320 px.

Network folders, and libraries with more subfolders than Linux lets one user watch, aren't watched live; the library panel says so, and their changes show up within 5 minutes instead. To watch large libraries live, raise the limit:

```bash
echo fs.inotify.max_user_watches=524288 | sudo tee /etc/sysctl.d/90-inotify.conf && sudo sysctl --system
```
