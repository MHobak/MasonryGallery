namespace MasonryGallery.Infrastructure.Persistence;

using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

public static class SchemaMigrator
{
    private static readonly string[] _migrations =
    [
        "0001_init.sql",
        "0002_fts_triggers.sql",
        "0003_regen_video_thumbs.sql",
        "0004_reprobe_images.sql",
        "0005_playable_favorites.sql",
        "0006_xmp_sidecars.sql",
        "0007_path_substring_search.sql"
    ];

    /// <summary>The version <see cref="MigrateAsync"/> brings a database to.</summary>
    public static int LatestVersion => _migrations.Length;

    /// <summary>The version a database is at, without changing it; 0 for a new file.</summary>
    public static async Task<int> GetVersionAsync(SqliteConnectionFactory factory)
    {
        using var conn = factory.CreateConnection();
        var hasTable = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SchemaVersion'");
        return hasTable == 0 ? 0 : await conn.QuerySingleOrDefaultAsync<int?>("SELECT MAX(Version) FROM SchemaVersion") ?? 0;
    }

    public static async Task MigrateAsync(SqliteConnectionFactory factory, ILogger logger, CancellationToken ct = default)
    {
        using var conn = factory.CreateConnection();

        await conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL)");
        var versionRow = await conn.QuerySingleOrDefaultAsync<int?>("SELECT MAX(Version) FROM SchemaVersion");
        var current = versionRow ?? 0;

        if (current >= _migrations.Length)
            return;

        logger.LogInformation("Running database migrations from {From} to {To}", current, _migrations.Length);

        using var trans = conn.BeginTransaction();
        for (var i = current + 1; i <= _migrations.Length; i++)
        {
            var migrationNumber = _migrations[i - 1];
            var migrationPath = Path.Combine(AppContext.BaseDirectory, "Migrations", migrationNumber);

            var sql = await File.ReadAllTextAsync(migrationPath, ct);
            await conn.ExecuteAsync(sql, transaction: trans);
            await conn.ExecuteAsync("INSERT INTO SchemaVersion (Version) VALUES (@Version)",
                new { Version = i }, transaction: trans);

            logger.LogInformation("Applied migration {Migration}", migrationNumber);
        }

        await trans.CommitAsync();
    }
}
