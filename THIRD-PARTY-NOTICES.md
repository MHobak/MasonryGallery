# Third-party notices

MasonryGallery ships with the components below. Each remains under its own licence.

| Component | Use | Licence |
|---|---|---|
| .NET runtime and ASP.NET Core (self-contained builds) | Runtime, local media server | MIT |
| Photino.Blazor, Photino.NET | Native window and web view host | Apache-2.0 |
| SkiaSharp (with Skia, libjpeg-turbo, libpng, libwebp, zlib) | Image decoding and thumbnails | MIT (SkiaSharp); BSD-3-Clause (Skia); IJG/BSD, libpng, BSD, zlib licences for the codecs |
| Microsoft.Data.Sqlite | Database access | MIT |
| SQLite (via SQLitePCLRaw e_sqlite3) | Index database | Public domain (SQLite); Apache-2.0 (SQLitePCLRaw) |
| Dapper | SQL mapping | Apache-2.0 |
| Serilog, Serilog.Sinks.File, Serilog.Extensions.Hosting | Logging | Apache-2.0 |
| System.Reactive | Event streams | MIT |
| Newtonsoft.Json | ffprobe output parsing | MIT |

The licence texts are at <https://licenses.nuget.org/MIT>, <https://licenses.nuget.org/Apache-2.0>,
<https://opensource.org/license/bsd-3-clause> and <https://www.sqlite.org/copyright.html>.

## FFmpeg

Builds made with `scripts/fetch-ffmpeg.sh` include `ffmpeg` and `ffprobe` from FFmpeg, licensed under the
GNU Lesser General Public License, version 2.1 or later. They are unmodified LGPL builds from
<https://github.com/BtbN/FFmpeg-Builds>; the exact archive is named in `tools/ffmpeg/SOURCE.txt` and the
licence text is in `tools/ffmpeg/LICENSE-ffmpeg.txt`. MasonryGallery runs them as separate programs and does
not link to them. FFmpeg's source code is available at <https://ffmpeg.org/download.html> and, for these
builds, through the FFmpeg-Builds repository. You may replace the bundled programs with your own build, or
point Settings → FFmpeg at another folder.

Builds without a bundled copy use the `ffmpeg` installed on the system, under that copy's licence.

## Libraries used from the system (Linux)

WebKitGTK, GTK and GStreamer are not shipped; they come from the operating system under their own licences
(LGPL-2.1 and others).
