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
4. **Filter.** Type in the search box to match file and folder names (word prefixes: `bea` finds `beach.jpg`). Use **All / Photos / Videos** and the heart for favorites. **Filters** adds sort order, library, tag and a date range.
5. **Open an item.** Click it (or focus it and press `Enter`). In the viewer:

   | Key | Action |
   |---|---|
   | `←` / `→` | Previous / next |
   | `F` | Favorite |
   | `I` | Details and tags (type a tag, press `Enter`) |
   | `+` / `-` / `0` | Zoom in / out / reset (or mouse wheel, double-click; drag to pan) |
   | `O` | Open in the default app |
   | `Esc` | Close |

   Videos the app can't play get an **Open in default player** button.
6. **Manage libraries.** In the folder panel you can hide a library, follow symbolic links, or remove it (this forgets its tags and favorites; your files are never touched). Files that couldn't be read are listed under **Problems**.

Supported files: jpg, jpeg, png, webp, gif, bmp, avif, mp4, mkv, webm, mov, avi, m4v.

## Where data lives

| | Linux | Windows |
|---|---|---|
| Index (`gallery.db`) and logs | `$XDG_DATA_HOME/MasonryGallery` (default `~/.local/share/MasonryGallery`) | `%APPDATA%\MasonryGallery` |
| Thumbnails (cache) | `$XDG_CACHE_HOME/MasonryGallery` (default `~/.cache/MasonryGallery`) | `%LOCALAPPDATA%\MasonryGallery\cache` |

The thumbnail cache is safe to delete; thumbnails are regenerated on the next start. Deleting the index resets the gallery. Your media is never modified. Data from older builds in `~/.config/MasonryGallery` is moved over on first start.

## Develop

```bash
dotnet build
dotnet test
```

Layout: `Domain` (models) → `Application` (scan pipeline, use cases) → `Infrastructure` (SQLite, Skia thumbnails, file enumeration) → `UI` (Blazor + JS masonry) → `Host` (Photino window + a Kestrel media server on a random loopback port, guarded by a per-launch token). Full design in [design-v2.md](design-v2.md).

## Known limits

Search matches word prefixes, not arbitrary substrings. Thumbnail, cache and worker settings are not configurable yet. Nothing yet prevents two instances from opening the same index.

Network folders, and libraries with more subfolders than Linux lets one user watch, aren't watched live; the library panel says so, and their changes show up within 5 minutes instead. To watch large libraries live, raise the limit:

```bash
echo fs.inotify.max_user_watches=524288 | sudo tee /etc/sysctl.d/90-inotify.conf && sudo sysctl --system
```
