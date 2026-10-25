namespace MasonryGallery.Infrastructure.Persistence;

using System.Collections.Frozen;
using Dapper;
using MasonryGallery.Application.Ports;
using MasonryGallery.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

public sealed class SqliteLibraryRepository : ILibraryRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<SqliteLibraryRepository> _logger;

    public SqliteLibraryRepository(SqliteConnectionFactory factory, ILogger<SqliteLibraryRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<Result<Library>> GetByIdAsync(LibraryId id, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = "SELECT * FROM Library WHERE Id = @Id";
        var row = await conn.QuerySingleOrDefaultAsync<Library>(sql, new { Id = id.Value });
        return row is not null ? row : Result<Library>.Failure("Library not found");
    }

    public async Task<Result<Library>> AddAsync(Library library, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = @"INSERT INTO Library (Name, RootPath, Enabled, Status)
                    VALUES (@Name, @RootPath, @Enabled, @Status);
                    SELECT last_insert_rowid()";
        try
        {
            var id = await conn.QuerySingleAsync<int>(sql, new
            {
                library.Name,
                library.RootPath,
                Enabled = library.Enabled ? 1 : 0,
                Status = (int)library.Status
            });
            library.Id = new LibraryId(id);
            return library;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return Result<Library>.Failure("This folder is already a library.");
        }
    }

    public async Task<Result> UpdateAsync(Library library, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = @"UPDATE Library SET
                    Name = @Name, RootPath = @RootPath, Enabled = @Enabled,
                    Status = @Status, FollowSymlinks = @FollowSymlinks,
                    LastScanId = @LastScanId, LastScanUtc = @LastScanUtc
                    WHERE Id = @Id";
        var rows = await conn.ExecuteAsync(sql, new
        {
            Id = library.Id.Value,
            library.Name,
            library.RootPath,
            Enabled = library.Enabled ? 1 : 0,
            Status = (int)library.Status,
            FollowSymlinks = library.FollowSymlinks ? 1 : 0,
            library.LastScanId,
            LastScanUtc = library.LastScanUtc.HasValue ? new DateTimeOffset(library.LastScanUtc.Value).ToUnixTimeMilliseconds() : (long?)null
        });
        return rows > 0 ? Result.Success() : Result.Failure("Library not found");
    }

    public async Task<Result> DeleteAsync(LibraryId id, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        // Media, MediaTag and the FTS rows go with it (ON DELETE CASCADE + the Media_ad trigger).
        var rows = await conn.ExecuteAsync("DELETE FROM Library WHERE Id = @Id", new { Id = id.Value });
        return rows > 0 ? Result.Success() : Result.Failure("Library not found");
    }

    public async Task<Result> SetStatusAsync(LibraryId id, LibraryStatus status, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = "UPDATE Library SET Status = @Status WHERE Id = @Id";
        var rows = await conn.ExecuteAsync(sql, new { Id = id.Value, Status = (int)status });
        return rows > 0 ? Result.Success() : Result.Failure("Library not found");
    }

    public async Task<ReadOnlyCollection<Library>> ListAsync(CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = "SELECT * FROM Library ORDER BY Name";
        var items = (await conn.QueryAsync<Library>(sql)).ToList();
        return items.AsReadOnly();
    }

    public async Task SetLastScanIdAsync(LibraryId id, int scanId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE Library SET LastScanId = MAX(LastScanId, @ScanId) WHERE Id = @Id",
            new { Id = id.Value, ScanId = scanId });
    }

    public async Task<int> GetNextScanIdAsync(LibraryId id, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var sql = "SELECT COALESCE(MAX(LastScanId), 0) + 1 FROM Library WHERE Id = @Id";
        return await conn.QuerySingleAsync<int>(sql, new { Id = id.Value });
    }
}
