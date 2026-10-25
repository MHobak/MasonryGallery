namespace MasonryGallery.Host;

using System.Net;
using System.Security.Cryptography;
using Dapper;
using MasonryGallery.Application.Pipeline;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using MasonryGallery.Infrastructure.Persistence;
using MasonryGallery.Infrastructure.Thumbnails;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Serilog;

/// <summary>
/// Loopback HTTP endpoint the WebView loads thumbnails and originals from (design §7).
/// Requests name media by ID only; every request must carry the per-launch token.
/// </summary>
public sealed class MediaServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string BaseUrl { get; }
    public string Token { get; }

    /// <summary>Fires when the server is shutting down, including on SIGTERM / Ctrl+C.</summary>
    public CancellationToken Stopping => _app.Lifetime.ApplicationStopping;

    private MediaServer(WebApplication app, string baseUrl, string token)
    {
        _app = app;
        BaseUrl = baseUrl;
        Token = token;
    }

    public static async Task<MediaServer> StartAsync(
        SqliteConnectionFactory db, ShardedThumbnailStore thumbs, IThumbnailGenerator generator)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var expected = System.Text.Encoding.ASCII.GetBytes(token);

        // Content root pinned to the app folder so the working directory doesn't matter.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
        // Port 0: the OS picks a free port, so a second instance or another app can't collide with us.
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));

        var app = builder.Build();

        // Any web page the user visits can reach 127.0.0.1; only this app's WebView knows the token.
        app.Use(async (ctx, next) =>
        {
            var given = System.Text.Encoding.ASCII.GetBytes(ctx.Request.Query["t"].ToString());
            if (!CryptographicOperations.FixedTimeEquals(given, expected))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            await next(ctx);
        });

        app.MapGet("/thumb/{id:int}/{size}", async Task<IResult> (int id, string size, HttpContext ctx) =>
        {
            var medium = size == "medium";
            var path = thumbs.GetPath(id, medium ? ThumbnailFormat.Medium : ThumbnailFormat.Small);
            // Medium previews are made on first open (design §6); small ones come from the ingest pipeline.
            if (medium && !File.Exists(path))
                await TryGenerateMediumAsync(id, db, thumbs, generator, ctx.RequestAborted);

            var file = new FileInfo(path);
            // Not generated yet: the tile keeps its placeholder until the next refresh.
            if (!file.Exists)
                return Results.NotFound();

            // Thumbnails are rewritten in place when the source changes, so revalidate instead of caching forever.
            ctx.Response.Headers.CacheControl = "no-cache";
            return TypedResults.PhysicalFile(path, "image/webp",
                lastModified: file.LastWriteTimeUtc,
                entityTag: new EntityTagHeaderValue($"\"{file.LastWriteTimeUtc.Ticks:x}-{file.Length:x}\""));
        });

        app.MapGet("/media/{id:int}", async Task<IResult> (int id) =>
        {
            var (source, status) = await ResolveSourceAsync(id, db);
            if (source is null)
                return Results.StatusCode(status);

            // Streams from disk and answers Range requests, which <video> seeking relies on.
            return TypedResults.PhysicalFile(source.Path, ContentTypeFor(source.Path), enableRangeProcessing: true);
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features
            .GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        return new MediaServer(app, address, token);
    }

    private sealed record Source(string Path, MediaKind Kind, long? DurationMs);

    /// <summary>ID → file on disk, refusing anything that resolves outside its library.</summary>
    private static async Task<(Source? Source, int Status)> ResolveSourceAsync(int id, SqliteConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<(string RootPath, int FollowSymlinks, string RelPath, int Kind, long? DurationMs)>(
            @"SELECT l.RootPath, l.FollowSymlinks, m.RelPath, m.Kind, m.DurationMs
              FROM Media m JOIN Library l ON l.Id = m.LibraryId
              WHERE m.Id = @Id AND m.DeletedAt IS NULL", new { Id = id });
        if (row.RootPath is null)
            return (null, StatusCodes.Status404NotFound);

        var root = Path.GetFullPath(row.RootPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, row.RelPath));
        if (!IsInside(root, fullPath))
            return (null, StatusCodes.Status403Forbidden);
        // Without FollowSymlinks the enumerator never indexes links, so one here was swapped in later.
        if (row.FollowSymlinks == 0 && PassesThroughLink(root, row.RelPath))
            return (null, StatusCodes.Status403Forbidden);
        if (!File.Exists(fullPath))
            return (null, StatusCodes.Status404NotFound);

        return (new Source(fullPath, (MediaKind)row.Kind, row.DurationMs), StatusCodes.Status200OK);
    }

    private static bool PassesThroughLink(string root, string relPath)
    {
        var current = root;
        foreach (var segment in relPath.Split('/'))
        {
            current = Path.Join(current, segment);
            if (new FileInfo(current).LinkTarget is not null)
                return true;
        }
        return false;
    }

    private static async Task TryGenerateMediumAsync(
        int id, SqliteConnectionFactory db, ShardedThumbnailStore thumbs, IThumbnailGenerator generator, CancellationToken ct)
    {
        var (source, _) = await ResolveSourceAsync(id, db);
        if (source is null)
            return;

        var job = new ThumbnailJob(id, source.Path, source.Kind, ThumbnailFormat.Version, source.DurationMs);
        var result = await generator.GenerateMediumAsync(job, ct);
        if (result.IsSuccess)
            await thumbs.SaveAsync(id, ThumbnailFormat.Medium, result.Value!.Webp, ct);
    }

    // Segment-aware: "/photos-private/x" is not inside "/photos". Linux paths are case-sensitive.
    internal static bool IsInside(string root, string fullPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, comparison);
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".avif" => "image/avif",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        _ => "application/octet-stream"
    };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
