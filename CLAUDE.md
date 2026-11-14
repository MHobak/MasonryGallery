# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

MasonryGallery is a local desktop photo/video gallery (.NET 10, Photino.Blazor window, SQLite). `README.md` documents user-facing behaviour; `design-v2.md` is the authoritative design spec (code comments cite its sections, e.g. "design §5"); `plan.md` is the superseded original.

## Commands

```bash
dotnet run --project src/MasonryGallery.Host          # run the app (Linux needs libwebkit2gtk-4.1; ffmpeg optional)
dotnet build MasonryGallery.slnx -c Release           # warnings are errors (Directory.Build.props)
dotnet format MasonryGallery.slnx --verify-no-changes # CI enforces this on Linux
dotnet test MasonryGallery.slnx -c Release
dotnet test tests/MasonryGallery.Domain.Tests --filter "FullyQualifiedName~SomeTest"   # single test/class
node --test tests/MasonryGallery.UI.Tests/js          # tests for layout.js (no npm install needed)

# Performance budgets (CI runs on main only): generate a 100k-file dataset, then check
dotnet run -c Release --project tests/MasonryGallery.Benchmarks -- generate <dir>
dotnet run -c Release --project tests/MasonryGallery.Benchmarks -- budgets <dir>

scripts/publish.sh [linux-x64|win-x64]   # self-contained ReadyToRun build -> artifacts/publish/<rid>/
scripts/fetch-ffmpeg.sh                  # puts ffmpeg in tools/ffmpeg/<rid>/ for publish.sh to bundle
scripts/package-linux.sh                 # Linux packaging
```

SDK is pinned in `global.json`. Package versions are centralised in `Directory.Packages.props`. Some video tests need `ffmpeg` installed.

## Architecture

Clean/hexagonal layering, enforced by `tests/MasonryGallery.UI.Tests/ArchitectureTests.cs` (NetArchTest) as well as project references:

`Domain ← Application ← Infrastructure / UI ← Host`

- **Domain**: entities (`MediaItem`, `Library`, `Tag`), `Result`; no dependencies.
- **Application**: ports (`Ports/I*.cs`), use cases (`UseCases/`: scan, scheduler, library monitor, queries, cache janitor), and `Pipeline/IngestPipeline` (enumerate → metadata → thumbnail stages). Must not reference SQLite, Dapper, SkiaSharp, ASP.NET or Photino.
- **Infrastructure**: port adapters: SQLite persistence (Dapper), file enumeration/watching, Skia imaging, thumbnails, ffmpeg video, XMP sidecars, settings store. Must not reference UI/Host/ASP.NET.
- **UI**: Razor components (`App`, `Lightbox`, `LibraryPanel`, `SettingsPanel`, `TagFilter`) plus `GalleryService`; talks only to Application. Client-side virtualized masonry layout lives in `wwwroot/js/layout.js` (computed from stored width/height, not from the DOM), and `lightbox.js`.
- **Host**: composition root (`Program.cs`), Photino window, an embedded loopback Kestrel `MediaServer` that serves media by ID with a per-session token and Range support (never by path), `SingleInstanceLock` (a second launch opens another window on the running instance). The first window owns scanning/libraries/settings; extra windows are "Viewer" mode (`AppMode`).

Key design rules worth knowing before changing behaviour:
- Files are stored as `LibraryId` + relative path. Offline libraries (missing/empty folder) are never purged.
- FileSystemWatcher is only a hint (debounced); a periodic reconcile scan is the source of truth.
- Thumbnail cache is versioned (`ThumbnailFormat.Version` → `v{N}` dir); bump it when the format changes.
- Serilog's Kestrel logs are held at Warning because request URLs contain the media token; don't log those URLs.

Tests: xUnit + FluentAssertions per layer under `tests/`; `MasonryGallery.UI.Tests` also has bUnit-style component tests. `InternalsVisibleTo` grants each `X.Tests` project access to `X` internals.
